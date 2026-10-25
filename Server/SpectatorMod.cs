/*
 Si_Spectator (server) - team-less spectator mode for Silica multiplayer.

   /spectate [on|off|status]        toggle / enter / leave / show (alias /spec)
   /play                            = /spectate off
   /spectate view <player>          ask your Si_Spectator client to follow that player (packet V)
   /spectate fow <all|sol|cent|alien>  ask your client to show that team's fog perspective (packet F)
   !spectator allow|deny|force|kick <player> | list | all on|off | max <n> | reload | status   (Power.Teams)

 A spectator is remembered by Steam ID, has no team, no unit, no commander seat. Picking a faction (faction screen,
 Elo !reserve, BTB !team) ends spectating. The client mod (Client/) turns the team-less view into cameras.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using SilicaAdminMod;
using SiSpectator.Logic;
using UnityEngine;

[assembly: MelonInfo(typeof(SiSpectator.SpectatorMod), "Spectator", SiSpectator.SpectatorMod.Version, "DrMuck")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]
[assembly: MelonPlatformDomain(MelonPlatformDomainAttribute.CompatibleDomains.MONO)]
[assembly: MelonOptionalDependencies("Admin Mod")]

namespace SiSpectator
{
    public sealed partial class SpectatorMod : MelonMod
    {
        public const string Version = "0.1.0";
        private const string Tag = "<color=#9FD7FF>[Spectator]</color> ";

        internal static SpectatorConfig Cfg = new SpectatorConfig();
        internal static readonly SpectatorState State = new SpectatorState();
        private static float _resendAt = -1;
        private static readonly HashSet<string> _reported = new HashSet<string>();

        // ------------------------------------------------------------------ lifecycle

        public override void OnInitializeMelon()
        {
            Cfg = SpectatorConfig.Load();
            try { Net.Install(HarmonyInstance); }
            catch (Exception ex) { MelonLogger.Error("network patch failed (no transport, no hardening): " + ex); }
        }

        public override void OnLateInitializeMelon()
        {
            try { Guards.InstallAfk(HarmonyInstance); }
            catch (Exception ex) { MelonLogger.Warning("AFK guard not installed: " + ex.GetBaseException().Message); }
            Subscribe();
            if (!RegisteredMelons.Any(m => m.Info.Name == "Admin Mod"))
            {
                MelonLogger.Warning("Admin Mod not found: no chat commands registered");
                return;
            }
            try
            {
                // Player commands have no console alias, so RegisterPlayerCommand is safe. The admin command goes straight
                // into the Admin Mod's list (Si_Spawn pattern): RegisterAdminCommand would also add a "sam_spectator"
                // console key and throw on a duplicate. No installed mod uses spectate/spec/play/spectator (checked 2026-10-25).
                HelperMethods.RegisterPlayerCommand("spectate", Command_Spectate, true);
                HelperMethods.RegisterPlayerCommand("spec", Command_Spectate, true);
                HelperMethods.RegisterPlayerCommand("play", Command_Play, true);
                AdminMethods.AdminCommands.Add(new AdminCommand
                {
                    AdminCommandText = "spectator",
                    AdminCallback = Command_Admin,
                    AdminPower = Power.Teams,
                    AdminCommandDescription = "Spectator mode. Usage: " + SpectatorAdminCommand.Usage
                });
                MelonLogger.Msg("Si_Spectator v" + Version + " loaded: /spectate /spec /play, !spectator (Power.Teams); transport " + (Net.Enabled ? "on" : "OFF") + ", hardening " + (Net.HardeningOn ? "on" : "OFF") + "; config " + SpectatorConfig.Path);
            }
            catch (Exception ex) { MelonLogger.Error("command registration failed: " + ex); }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // Game.ExitLevel runs GameEvents.Clear(): subscribe again for every level. Spectators stay (by Steam ID) and get
            // S|on again once their client is in the new scene (their own hello after the load answers too).
            Subscribe();
            if (State.Count > 0) _resendAt = Time.realtimeSinceStartup + 6f;
        }

        public override void OnDeinitializeMelon()
        {
            try { GameEvents.OnPlayerChangedTeam -= OnPlayerChangedTeam; GameEvents.OnPlayerLeft -= OnPlayerLeft; GameEvents.OnGameRestart -= OnGameRestart; } catch { }
        }

        private static void Subscribe()
        {
            try
            {
                GameEvents.OnPlayerChangedTeam -= OnPlayerChangedTeam; GameEvents.OnPlayerChangedTeam += OnPlayerChangedTeam;
                GameEvents.OnPlayerLeft -= OnPlayerLeft; GameEvents.OnPlayerLeft += OnPlayerLeft;
                GameEvents.OnGameRestart -= OnGameRestart; GameEvents.OnGameRestart += OnGameRestart;
            }
            catch (Exception ex) { ReportOnce("subscribe", ex); }
        }

        public override void OnUpdate()
        {
            if (_resendAt < 0 || Time.realtimeSinceStartup < _resendAt) return;
            _resendAt = -1;
            try
            {
                foreach (ulong id in State.Spectators)
                {
                    var p = GameApi.BySteam(id);
                    if (p != null) SendState(p);
                }
            }
            catch (Exception ex) { ReportOnce("resend", ex); }
        }

        // ------------------------------------------------------------------ game events

        private static void OnPlayerChangedTeam(Player player, Team oldTeam, Team newTeam)
        {
            try
            {
                if (player == null || newTeam == null) return;
                ulong id = GameApi.SteamOf(player);
                if (!State.OnTeamChanged(id, true)) return;
                MelonLogger.Msg(GameApi.NameOf(player) + " joined " + GameApi.TeamLabel(newTeam) + ": spectating ended");
                SendState(player);
                if (Cfg.Announce) Announce(GameApi.NameOf(player) + " stopped spectating and joined " + GameApi.TeamLabel(newTeam) + ".");
            }
            catch (Exception ex) { ReportOnce("team-change", ex); }
        }

        private static void OnPlayerLeft(Player player)
        {
            try
            {
                ulong id = GameApi.SteamOf(player);
                if (id != 0 && State.Forget(id)) Dbg(GameApi.NameOf(player) + " left the server while spectating");
            }
            catch (Exception ex) { ReportOnce("player-left", ex); }
        }

        private static void OnGameRestart(GameMode mode)
        {
            // ClearGame sets every team to null (no event for us to act on); clients re-open the faction screen.
            if (State.Count > 0) _resendAt = Time.realtimeSinceStartup + 3f;
        }

        // ------------------------------------------------------------------ transport

        internal static void OnPacket(NetworkID sender, string body)
        {
            if (!Protocol.TrySplit(body, out char kind, out _)) { Dbg("malformed packet ignored"); return; }
            Player player = null;
            try { player = Player.FindPlayer(sender); } catch { }
            if (player == null) return;
            ulong id = sender.m_ID;
            switch (kind)
            {
                case 'H':
                    if (!Protocol.TryHello(body, out string version)) { Dbg("bad hello from " + GameApi.NameOf(player)); return; }
                    bool first = !State.HasClient(id);
                    if (!State.AcceptHello(id, Time.realtimeSinceStartup, version)) return;
                    if (first) MelonLogger.Msg("client " + version + " hello from " + GameApi.NameOf(player) + (State.IsSpectating(id) ? " (spectating)" : ""));
                    SendState(player);
                    return;
                case 'K':
                    return;   // commander camera relay: phase 2
                default:
                    Dbg("unknown message '" + kind + "' from " + GameApi.NameOf(player) + " ignored");
                    return;
            }
        }

        internal static bool Permitted(Player p)
        {
            ulong id = GameApi.SteamOf(p);
            return SpectatorRules.IsPermitted(Cfg.Rules, GameApi.IsAdmin(p), Cfg.AllowedList.Contains(id));
        }

        internal static void SendState(Player p)
        {
            if (p == null) return;
            Net.Send(p, Protocol.State(State.IsSpectating(GameApi.SteamOf(p)), Permitted(p), Version));
        }

        // ------------------------------------------------------------------ enter / leave

        /// <summary>Puts the player into spectator mode. <paramref name="by"/> = the admin for a forced entry, else null.</summary>
        internal static bool Enter(Player p, Player by, out string reason)
        {
            reason = "";
            ulong id = GameApi.SteamOf(p);
            if (id == 0) { reason = "player has no Steam ID"; return false; }
            bool forced = by != null;
            var r = SpectatorRules.CanEnter(Cfg.Rules, GameApi.IsAdmin(p), Cfg.AllowedList.Contains(id), State.Count, State.IsSpectating(id), forced);
            if (!r.Ok) { reason = r.Reason; return false; }
            if (forced && SpectatorRules.ForceExceedsCap(Cfg.Rules, State.Count))
                MelonLogger.Msg(GameApi.NameOf(by) + " forced " + GameApi.NameOf(p) + " past the slot cap (" + State.Count + "/" + Cfg.MaxSpectators + ")");

            Team oldTeam = GameApi.TeamOf(p);
            // 1. free a commander seat first: a team change does not, and a team-less ex-commander leaving the server
            //    would leave a ghost commander.
            var freed = GameApi.DemoteCommander(p);
            // 2. applicant pools of the vanilla lottery and the optional community mods.
            int pools = GameApi.RemoveFromVanillaLottery(p) + GameApi.RemoveFromCommMgmt(p) + GameApi.RemoveFromEloReserve(p);
            // 3. the vanilla eliminated-team sequence (MP_Strategy.CheckPlayersOnEliminatedTeam); null round-trips on both branches.
            try
            {
                if (oldTeam != null || p.ControlledUnit != null)
                {
                    NetworkLayer.SendPlayerSelectUnit(p, null);
                    p.ControlledUnit = null;
                    p.Team = null;
                    NetworkLayer.SendPlayerSelectTeam(p, null);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error("removing " + GameApi.NameOf(p) + " from " + GameApi.TeamLabel(oldTeam) + " failed: " + ex);
                reason = "could not take you out of your team (" + ex.GetBaseException().Message + ")";
                return false;
            }
            // 4. remember, tell the client, announce.
            State.Enter(id);
            if (Cfg.AllowedList.Rename(id, GameApi.NameOf(p))) Cfg.Save();   // keep the stored name current
            SendState(p);
            MelonLogger.Msg(GameApi.NameOf(p) + " is spectating (" + (forced ? "forced by " + GameApi.NameOf(by) : "own request") + ", was " + GameApi.TeamLabel(oldTeam)
                            + (freed.Count > 0 ? ", commander seat freed: " + string.Join(", ", freed) : "") + (pools > 0 ? ", removed from " + pools + " applicant list(s)" : "") + ")");
            if (Cfg.Announce) Announce(GameApi.NameOf(p) + " is now spectating.");
            return true;
        }

        internal static bool Leave(Player p, Player by)
        {
            ulong id = GameApi.SteamOf(p);
            if (!State.Leave(id)) return false;
            SendState(p);
            MelonLogger.Msg(GameApi.NameOf(p) + " left spectator mode" + (by != null ? " (kicked by " + GameApi.NameOf(by) + ")" : ""));
            if (Cfg.Announce) Announce(GameApi.NameOf(p) + " stopped spectating.");
            return true;
        }

        // ------------------------------------------------------------------ helpers

        internal static void Reply(Player p, string msg)
        {
            if (p == null) { MelonLogger.Msg(msg); return; }
            try { HelperMethods.SendChatMessageToPlayer(p, Tag + msg); } catch (Exception ex) { ReportOnce("reply", ex); }
        }

        internal static void Announce(string msg)
        {
            try { HelperMethods.SendChatMessageToAll(Tag + msg); } catch (Exception ex) { ReportOnce("announce", ex); }
        }

        internal static void Dbg(string msg)
        {
            if (Cfg != null && Cfg.Debug) MelonLogger.Msg("[debug] " + msg);
        }

        internal static void ReportOnce(string key, Exception ex)
        {
            if (_reported.Add(key)) MelonLogger.Warning(key + ": " + ex.GetBaseException().Message + " (further errors of this kind are not logged)");
        }
    }
}
