using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using SiSpectator.Logic;

namespace SiSpectator
{
    // Thin layer over the game and over optional community mods. Everything that differs between the Steam branches
    // (SetCommander protected in 1.0.6 / private in 0.9.47, RPC_SynchCommander likewise) and every other mod's internals
    // (Si_CommManagement, Si_EloMatchmaking) go through reflection with a type lookup by name, skipped when absent.
    internal static class GameApi
    {
        // ---- identity / lookup --------------------------------------------------------------------------------
        internal static ulong SteamOf(Player p)
        {
            try { return p == null ? 0 : p.PlayerID.m_ID; } catch { return 0; }
        }

        internal static string NameOf(Player p)
        {
            try { return p == null ? "?" : (p.PlayerName ?? "?"); } catch { return "?"; }
        }

        internal static bool IsServerPlayer(Player p)
        {
            try { return p != null && p == NetworkGameServer.GetServerPlayer(); } catch { return false; }
        }

        internal static List<Player> Connected()
        {
            var res = new List<Player>();
            try
            {
                foreach (var p in Player.Players)
                    if (p != null && !IsServerPlayer(p)) res.Add(p);
            }
            catch { }
            return res;
        }

        internal static Player BySteam(ulong steamId) => steamId == 0 ? null : Connected().FirstOrDefault(p => SteamOf(p) == steamId);

        internal static Player FindPlayer(string query, out string why) => NameMatch.Find(Connected(), p => NameOf(p), query, out why);

        internal static bool IsAdmin(Player p)
        {
            try { return p != null && SilicaAdminMod.PlayerInterface.IsAdmin(p); } catch { return false; }
        }

        internal static Team TeamOf(Player p)
        {
            try { return p == null ? null : p.Team; } catch { return null; }
        }

        internal static string TeamLabel(Team t)
        {
            try { return t == null ? "no team" : (t.TeamShortName ?? t.name); } catch { return "?"; }
        }

        // ---- teams of the match (fog perspective) ----------------------------------------------------------------
        /// <summary>Teams with an active strategy setup in the current versus mode (falls back to every playable team).</summary>
        internal static List<Team> ActiveTeams()
        {
            var res = new List<Team>();
            try
            {
                if (GameMode.CurrentGameMode is MP_Strategy strat && strat.TeamSetups != null)
                {
                    foreach (var setup in strat.TeamSetups)
                        if (setup != null && setup.Team != null && strat.GetTeamSetupActive(setup)) res.Add(setup.Team);
                }
            }
            catch (Exception ex) { SpectatorMod.ReportOnce("teams", ex); }
            return res;
        }

        /// <summary>sol | cent | alien -> the active team of that faction (TeamShortName prefix), null when it is not in the match.</summary>
        internal static Team FogTeam(string canonical)
        {
            string prefix = canonical == "sol" ? "Sol" : canonical == "cent" ? "Cent" : canonical == "alien" ? "Alien" : null;
            if (prefix == null) return null;
            return ActiveTeams().FirstOrDefault(t => (t.TeamShortName ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        // ---- commander seat --------------------------------------------------------------------------------------
        private static Type _modeType;
        private static MethodInfo _setCommander, _synchCommander;

        private static void ResolveCommanderMethods(Type modeType)
        {
            if (modeType == _modeType) return;
            _modeType = modeType;
            _setCommander = AccessTools.Method(modeType, "SetCommander", new[] { typeof(Team), typeof(Player) });
            _synchCommander = AccessTools.Method(modeType, "RPC_SynchCommander", new[] { typeof(Team), typeof(bool) })
                           ?? AccessTools.Method(modeType, "RPC_SynchCommander", new[] { typeof(Team) });
            SpectatorMod.Dbg("reflection on " + modeType.Name + ": SetCommander=" + (_setCommander != null) + " RPC_SynchCommander=" + (_synchCommander != null) + "(" + (_synchCommander?.GetParameters().Length ?? 0) + ")");
        }

        /// <summary>
        /// Frees every commander seat the player holds: SetCommander(team, null) + RPC_SynchCommander(team, kicked: false),
        /// the pair vanilla runs for an eliminated team (no mutiny popup, the AI takeover countdown starts). Must run before
        /// the team is cleared: a team change does not free the seat, and a team-less ex-commander who disconnects would
        /// leave a ghost commander. Returns the names of the freed teams.
        /// </summary>
        internal static List<string> DemoteCommander(Player p)
        {
            var freed = new List<string>();
            var mode = GameMode.CurrentGameMode;
            if (mode == null || p == null) return freed;
            var teams = new List<Team>();
            try
            {
                if (mode is MP_Strategy strat && strat.TeamSetups != null)
                {
                    foreach (var setup in strat.TeamSetups)
                        if (setup != null && setup.Team != null && setup.Commander == p) teams.Add(setup.Team);
                }
                else if (p.Team != null && p.IsCommander) teams.Add(p.Team);
            }
            catch (Exception ex) { SpectatorMod.ReportOnce("seat-scan", ex); }
            if (teams.Count == 0) return freed;

            ResolveCommanderMethods(mode.GetType());
            if (_setCommander == null || _synchCommander == null)
            {
                MelonLogger.Warning("SetCommander/RPC_SynchCommander not found on " + mode.GetType().Name + "; " + NameOf(p) + " keeps the commander seat of " + string.Join(", ", teams.Select(TeamLabel)));
                return freed;
            }
            foreach (var team in teams)
            {
                try
                {
                    _setCommander.Invoke(mode, new object[] { team, null });
                    var ps = _synchCommander.GetParameters();
                    var args = new object[ps.Length];
                    args[0] = team;
                    for (int i = 1; i < ps.Length; i++) args[i] = ps[i].ParameterType == typeof(bool) ? (object)false : (ps[i].HasDefaultValue ? ps[i].DefaultValue : null);
                    _synchCommander.Invoke(mode, args);
                    freed.Add(TeamLabel(team));
                }
                catch (Exception ex) { MelonLogger.Warning("freeing the commander seat of " + TeamLabel(team) + " failed: " + ex.GetBaseException().Message); }
            }
            return freed;
        }

        // ---- applicant pools ------------------------------------------------------------------------------------
        private static FieldInfo _lottery;
        private static bool _lotteryLooked;

        /// <summary>Vanilla MP_Strategy.CommanderLottery (private in both branches): remove the player from every team's list.</summary>
        internal static int RemoveFromVanillaLottery(Player p)
        {
            int n = 0;
            try
            {
                if (!(GameMode.CurrentGameMode is MP_Strategy strat)) return 0;
                if (!_lotteryLooked) { _lotteryLooked = true; _lottery = AccessTools.Field(typeof(MP_Strategy), "CommanderLottery"); }
                if (_lottery?.GetValue(strat) is IDictionary dict)
                    foreach (var list in dict.Values) if (list is List<Player> l) n += l.RemoveAll(x => x == p);
            }
            catch (Exception ex) { SpectatorMod.ReportOnce("lottery", ex); }
            return n;
        }

        private static FieldInfo _commApplicants;
        private static bool _commLooked;

        /// <summary>Si_CommManagement's public static List&lt;Player&gt;[] CommanderApplications.commanderApplicants (optional mod).</summary>
        internal static int RemoveFromCommMgmt(Player p)
        {
            if (!_commLooked)
            {
                _commLooked = true;
                var t = FindType("Si_CommanderManagement.CommanderApplications", "Si_CommManagement.CommanderApplications");
                _commApplicants = t?.GetField("commanderApplicants", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                SpectatorMod.Dbg("Si_CommManagement " + (t == null ? "not loaded" : "found, commanderApplicants=" + (_commApplicants != null)));
            }
            int n = 0;
            if (_commApplicants == null) return 0;
            try
            {
                if (_commApplicants.GetValue(null) is List<Player>[] arr)
                    foreach (var list in arr) if (list != null) n += list.RemoveAll(x => x == p);
            }
            catch (Exception ex) { SpectatorMod.ReportOnce("commmgmt", ex); }
            return n;
        }

        private static FieldInfo _eloReserve;
        private static bool _eloLooked;

        /// <summary>Si_EloMatchmaking's !reserve queue (private static List&lt;Reservation&gt; EloMod._reserve, items keyed by string Steam).</summary>
        internal static int RemoveFromEloReserve(Player p)
        {
            if (!_eloLooked)
            {
                _eloLooked = true;
                var t = FindType("Si_EloMatchmaking.EloMod");
                _eloReserve = t?.GetField("_reserve", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                SpectatorMod.Dbg("Si_EloMatchmaking " + (t == null ? "not loaded" : "found, _reserve=" + (_eloReserve != null)));
            }
            if (_eloReserve == null) return 0;
            int n = 0;
            try
            {
                if (!(_eloReserve.GetValue(null) is IList list)) return 0;
                string steam = SteamOf(p).ToString();
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var item = list[i];
                    if (item == null) continue;
                    var steamField = item.GetType().GetField("Steam");
                    if (steamField != null && (steamField.GetValue(item) as string) == steam) { list.RemoveAt(i); n++; }
                }
                if (n > 0)
                    foreach (var item in list)   // a mirrored swap partner loses its approval, like Elo's own Drop(moved: false)
                    {
                        var partner = item?.GetType().GetField("Partner");
                        if (partner != null && (partner.GetValue(item) as string) == steam) partner.SetValue(item, null);
                    }
            }
            catch (Exception ex) { SpectatorMod.ReportOnce("elo", ex); }
            return n;
        }

        internal static Type FindType(params string[] fullNames)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var n in fullNames)
                {
                    try { var t = asm.GetType(n, false); if (t != null) return t; } catch { }
                }
            }
            return null;
        }
    }
}
