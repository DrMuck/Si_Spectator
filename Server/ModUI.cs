using System;
using System.Linq;
using MelonLoader;

namespace SiSpectator
{
    // Si_ModUI export: mirrors the chat commands only (chat stays fully usable without the UI). Names only, never Steam
    // IDs, in any label or value: the deny / kick choices send the player's name and the server resolves it.
    public sealed partial class SpectatorMod
    {
        private static string _cache; private static string _cacheKey = "";

        public static string ModUIData()
        {
            try
            {
                var spectators = State.Spectators.Select(GameApi.BySteam).Where(p => p != null).Select(GameApi.NameOf).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var allowed = Cfg.AllowedList.Entries.Select(e => e.Value).Where(n => n.Length > 0).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                string key = Cfg.Enabled + "|" + Cfg.AllowAll + "|" + Cfg.AllowAdmins + "|" + Cfg.MaxSpectators + "|" + string.Join("\u0001", spectators) + "|" + string.Join("\u0001", allowed);
                if (_cache != null && key == _cacheKey) return _cache;

                string who = Cfg.AllowAll ? "everyone" : (Cfg.AllowAdmins ? "admins and " : "") + "players an admin allowed";
                var x = ModUI.ModUIExport.New()
                    .Enabled(Cfg.Enabled)
                    .Info("Spectator mode " + (Cfg.Enabled ? "ON" : "OFF") + " | " + spectators.Count + (Cfg.MaxSpectators > 0 ? "/" + Cfg.MaxSpectators : "") + " spectating | allowed: " + who)
                    .Info("A spectator has no team. Picking a faction (or /play) ends spectating. Cameras need the Si_Spectator client mod.")
                    .OnOff("/spectate", "Spectate", "Leave your team and watch the match without a unit. OFF returns you to the faction screen.", admin: false, on: false)
                    .Picker("/spectate view", "View player", "Your Si_Spectator client follows the chosen player. Only while spectating.", admin: false, arg: "player")
                    .Switch("/spectate fow", "Fog perspective", "Show what one team can see, or everything. Only while spectating, needs the client mod.", admin: false, current: "all",
                            ("all", "ALL"), ("sol", "SOL"), ("cent", "CENTAURI"), ("alien", "ALIEN"))
                    .Report("/spectate status", "Status", "Whether you may spectate, the slots and your client mod.")
                    .Picker("!spectator allow", "Allow player", "Adds the player to the allowed list (saved in the config).", admin: true, arg: "player");
                if (allowed.Count > 0)
                    x.Picker("!spectator deny", "Remove allowed player", "Removes the player from the allowed list. A running spectator stays until kicked.", admin: true, arg: "choice",
                             allowed.Select(n => (n, n)).ToArray());
                x.Picker("!spectator force", "Force into spectator", "Takes the player out of the team (frees a commander seat) into spectator mode. Ignores the slot cap.", admin: true, arg: "player");
                if (spectators.Count > 0)
                    x.Picker("!spectator kick", "End spectating", "Sends the spectator back to the faction screen.", admin: true, arg: "choice",
                             spectators.Select(n => (n, n)).ToArray());
                x.OnOff("!spectator all", "Everyone may spectate", "Off: only admins and allowed players.", admin: true, on: Cfg.AllowAll)
                 .Value("!spectator max", "Spectator slots", "0 = unlimited. Forced entries ignore it.", admin: true, current: Cfg.MaxSpectators)
                 .Report("!spectator list", "List", "Current spectators and the allowed list.", admin: true)
                 .Action("!spectator reload", "Reload config", "Re-read Si_Spectator_Config.json.", admin: true);
                _cache = x.Build(); _cacheKey = key;
                return _cache;
            }
            catch (Exception ex) { MelonLogger.Warning("ModUIData: " + ex.Message); return null; }
        }

        // This player's own switch values: spectating on/off and the last fog choice.
        public static string ModUIPlayerData(ulong steamId)
        {
            try
            {
                bool on = State.IsSpectating(steamId);
                var p = GameApi.BySteam(steamId);
                return ModUI.ModUIExport.ForPlayer()
                    .Value("/spectate", on ? "on" : "off")
                    .Value("/spectate fow", on ? State.FogOf(steamId) : "all")
                    .Table("You", new[] { "Spectating", "Allowed", "Client mod" },
                           new[] { on ? "yes" : "no", p != null && Permitted(p) ? "yes" : "no", State.HasClient(steamId) ? State.ClientVersion(steamId) : "none" })
                    .Build();
            }
            catch (Exception ex) { MelonLogger.Warning("ModUIPlayerData: " + ex.Message); return null; }
        }
    }
}
