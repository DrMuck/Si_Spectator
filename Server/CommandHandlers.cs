using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using SilicaAdminMod;
using SiSpectator.Logic;

namespace SiSpectator
{
    public sealed partial class SpectatorMod
    {
        // ------------------------------------------------------------------ player commands

        public static void Command_Play(Player caller, string args)
        {
            try { HandleSpectate(caller, new SpectateCommand { Action = SpectateAction.Off }); }
            catch (Exception ex) { MelonLogger.Error("/play: " + ex); Reply(caller, "error: " + ex.GetBaseException().Message); }
        }

        public static void Command_Spectate(Player caller, string args)
        {
            try { HandleSpectate(caller, SpectateCommand.Parse(args)); }
            catch (Exception ex) { MelonLogger.Error("/spectate: " + ex); Reply(caller, "error: " + ex.GetBaseException().Message); }
        }

        private static void HandleSpectate(Player caller, SpectateCommand cmd)
        {
            if (caller == null) { Reply(null, "/spectate is a player command"); return; }
            ulong id = GameApi.SteamOf(caller);
            bool spectating = State.IsSpectating(id);
            switch (cmd.Action)
            {
                case SpectateAction.Invalid: Reply(caller, cmd.Error); return;
                case SpectateAction.Help: Reply(caller, SpectateCommand.Usage); return;
                case SpectateAction.Status: Reply(caller, StatusLine(caller)); return;
                case SpectateAction.Toggle:
                    if (spectating) goto case SpectateAction.Off;
                    goto case SpectateAction.On;
                case SpectateAction.On:
                {
                    if (spectating) { Reply(caller, "You are already spectating. /play to pick a faction."); SendState(caller); return; }
                    if (!Enter(caller, null, out string reason)) { Reply(caller, reason); return; }
                    Reply(caller, State.HasClient(id) ? "Spectating. /play or /spectate off to return." : "Spectating (team-less). Install the Si_Spectator client mod for cameras. /play to return.");
                    return;
                }
                case SpectateAction.Off:
                    if (!spectating) { Reply(caller, "You are not spectating."); return; }
                    Leave(caller, null);
                    Reply(caller, "Spectating ended. Pick a faction to play.");
                    return;
                case SpectateAction.View:
                {
                    if (!spectating) { Reply(caller, "Only while spectating (/spectate on)."); return; }
                    var target = GameApi.FindPlayer(cmd.Arg, out string why);
                    if (target == null) { Reply(caller, why); return; }
                    if (target == caller) { Reply(caller, "You cannot view yourself."); return; }
                    if (GameApi.TeamOf(target) == null) { Reply(caller, GameApi.NameOf(target) + " is not on a team."); return; }
                    if (!State.HasClient(id)) { Reply(caller, "View needs the Si_Spectator client mod."); return; }
                    if (!Net.Send(caller, Protocol.View(target.PlayerID.m_ID, target.PlayerChannel))) { Reply(caller, "Could not reach your client."); return; }
                    Reply(caller, "Viewing " + GameApi.NameOf(target) + ".");
                    return;
                }
                case SpectateAction.Fow:
                {
                    if (!spectating) { Reply(caller, "Only while spectating (/spectate on)."); return; }
                    string value = "all";
                    if (cmd.Arg != "all")
                    {
                        var team = GameApi.FogTeam(cmd.Arg);
                        if (team == null) { Reply(caller, cmd.Arg + " is not in this match."); return; }
                        value = team.TeamShortName ?? "";
                        if (!Protocol.ValidTeam(value) || value == "all") { Reply(caller, "Team name '" + value + "' cannot be sent."); return; }
                    }
                    State.SetFog(id, cmd.Arg);
                    if (!State.HasClient(id)) { Reply(caller, "Fog perspective needs the Si_Spectator client mod."); return; }
                    if (!Net.Send(caller, Protocol.Fog(value))) { Reply(caller, "Could not reach your client."); return; }
                    Reply(caller, "Fog perspective: " + (cmd.Arg == "all" ? "everything" : value) + ".");
                    return;
                }
            }
        }

        private static string StatusLine(Player p)
        {
            ulong id = GameApi.SteamOf(p);
            string me = State.IsSpectating(id) ? "You are spectating" : Permitted(p) ? "You may spectate" : "You may not spectate";
            string client = State.HasClient(id) ? "client " + State.ClientVersion(id) : "no client mod";
            return me + " (" + client + "). " + SlotsText() + (Cfg.Enabled ? "" : " Spectator mode is OFF.");
        }

        private static string SlotsText() => State.Count + (Cfg.MaxSpectators > 0 ? "/" + Cfg.MaxSpectators : "") + " spectating.";

        // ------------------------------------------------------------------ admin command

        public static void Command_Admin(Player caller, string args)
        {
            try { HandleAdmin(caller, SpectatorAdminCommand.Parse(args)); }
            catch (Exception ex) { MelonLogger.Error("!spectator: " + ex); Reply(caller, "error: " + ex.GetBaseException().Message); }
        }

        private static void HandleAdmin(Player caller, SpectatorAdminCommand cmd)
        {
            switch (cmd.Action)
            {
                case AdminAction.Invalid: Reply(caller, cmd.Error); return;
                case AdminAction.Help: Reply(caller, SpectatorAdminCommand.Usage); return;
                case AdminAction.Status:
                    Reply(caller, "Mode " + (Cfg.Enabled ? "ON" : "OFF") + ", allow all " + (Cfg.AllowAll ? "on" : "off") + ", admins " + (Cfg.AllowAdmins ? "yes" : "no")
                                  + ", " + SlotsText() + " Allowed list: " + Cfg.AllowedList.Count + ". Transport " + (Net.Enabled ? "on" : "off") + ".");
                    return;
                case AdminAction.List: List(caller); return;
                case AdminAction.Reload:
                    Cfg = SpectatorConfig.Load();
                    Reply(caller, "Config reloaded (" + Cfg.AllowedList.Count + " allowed, max " + MaxText() + ").");
                    ResendAllowed();
                    return;
                case AdminAction.All:
                    Cfg.AllowAll = cmd.On; Cfg.Save();
                    Reply(caller, "Everyone may spectate: " + (cmd.On ? "ON" : "OFF") + ".");
                    LogAdmin(caller, "all " + (cmd.On ? "on" : "off"));
                    ResendAllowed();
                    return;
                case AdminAction.Max:
                    Cfg.MaxSpectators = cmd.Number; Cfg.Save();
                    Reply(caller, "Spectator slots: " + MaxText() + (State.Count > cmd.Number && cmd.Number > 0 ? " (" + State.Count + " spectating now, nobody is removed)" : "") + ".");
                    LogAdmin(caller, "max " + cmd.Number);
                    return;
                case AdminAction.Allow:
                {
                    var target = GameApi.FindPlayer(cmd.Arg, out string why);
                    if (target == null) { Reply(caller, why); return; }
                    bool added = Cfg.AllowedList.Add(GameApi.SteamOf(target), GameApi.NameOf(target));
                    Cfg.Save();
                    Reply(caller, GameApi.NameOf(target) + (added ? " may now spectate." : " was already allowed."));
                    if (added) { Reply(target, "An admin allowed you to spectate: /spectate"); SendState(target); LogAdmin(caller, "allow " + GameApi.NameOf(target)); }
                    return;
                }
                case AdminAction.Deny:
                {
                    // the allowed list first (works for offline players), then online players
                    var entry = NameMatch.Find(Cfg.AllowedList.Entries.Select(e => new AllowedJson { SteamId = e.Key, Name = e.Value }), e => e.Name, cmd.Arg, out string why);
                    ulong sid = entry?.SteamId ?? 0;
                    string name = entry?.Name ?? "";
                    if (entry == null)
                    {
                        var online = GameApi.FindPlayer(cmd.Arg, out string why2);
                        if (online == null) { Reply(caller, why + " on the allowed list"); return; }
                        sid = GameApi.SteamOf(online); name = GameApi.NameOf(online);
                    }
                    bool removed = Cfg.AllowedList.Remove(sid);
                    Cfg.Save();
                    var p = GameApi.BySteam(sid);
                    Reply(caller, removed ? name + " removed from the allowed list" + (State.IsSpectating(sid) ? " (still spectating; !spectator kick " + name + " to end it)." : ".") : name + " was not on the allowed list.");
                    if (removed) { if (p != null) SendState(p); LogAdmin(caller, "deny " + name); }
                    return;
                }
                case AdminAction.Force:
                {
                    if (caller == null) { Reply(null, "force needs an admin player (the slot cap override is logged with the admin's name)"); return; }
                    var target = GameApi.FindPlayer(cmd.Arg, out string why);
                    if (target == null) { Reply(caller, why); return; }
                    if (State.IsSpectating(GameApi.SteamOf(target))) { Reply(caller, GameApi.NameOf(target) + " is already spectating."); return; }
                    if (!Enter(target, caller, out string reason)) { Reply(caller, "Could not: " + reason); return; }
                    Reply(caller, GameApi.NameOf(target) + " is now spectating.");
                    if (target != caller) Reply(target, "An admin put you into spectator mode. /play to return.");
                    LogAdmin(caller, "force " + GameApi.NameOf(target));
                    return;
                }
                case AdminAction.Kick:
                {
                    var target = SpectatorByName(cmd.Arg, out string why);
                    if (target == null) { Reply(caller, why); return; }
                    Leave(target, caller);
                    Reply(caller, GameApi.NameOf(target) + " is back on the faction screen.");
                    if (target != caller) Reply(target, "An admin ended your spectator mode. Pick a faction.");
                    LogAdmin(caller, "kick " + GameApi.NameOf(target));
                    return;
                }
            }
        }

        private static Player SpectatorByName(string query, out string why)
        {
            var spectators = State.Spectators.Select(GameApi.BySteam).Where(p => p != null).ToList();
            var p = NameMatch.Find(spectators, GameApi.NameOf, query, out why);
            if (p == null && spectators.Count == 0) why = "nobody is spectating";
            else if (p == null) why += " among the spectators";
            return p;
        }

        private static void List(Player caller)
        {
            var specs = State.Spectators.Select(GameApi.BySteam).Where(p => p != null).Select(GameApi.NameOf).ToList();
            Reply(caller, "Spectating (" + specs.Count + "): " + (specs.Count == 0 ? "nobody" : string.Join(", ", specs)));
            var allowed = Cfg.AllowedList.Entries.Select(e => (e.Value.Length > 0 ? e.Value : "?") + (GameApi.BySteam(e.Key) == null ? " (offline)" : "")).ToList();
            Reply(caller, "Allowed (" + allowed.Count + (Cfg.AllowAll ? ", allow all ON" : "") + (Cfg.AllowAdmins ? ", admins always" : "") + "): " + (allowed.Count == 0 ? "none" : string.Join(", ", allowed)));
        }

        private static string MaxText() => Cfg.MaxSpectators > 0 ? Cfg.MaxSpectators.ToString() : "unlimited";

        private static void LogAdmin(Player caller, string what) => MelonLogger.Msg("admin " + (caller != null ? GameApi.NameOf(caller) : "console") + ": !spectator " + what);

        // the "allowed" bit of S changed for everyone with a client (all on/off, reload)
        private static void ResendAllowed()
        {
            foreach (var p in GameApi.Connected())
                if (State.HasClient(GameApi.SteamOf(p))) SendState(p);
        }
    }
}
