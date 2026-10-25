using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json;
using SiSpectator.Logic;

namespace SiSpectator
{
    /// <summary>One designated spectator, persisted as {"steamId": ..., "name": "..."}. The name is informational.</summary>
    public sealed class AllowedJson
    {
        [JsonProperty("steamId")] public ulong SteamId;
        [JsonProperty("name")] public string Name = "";
    }

    /// <summary>
    /// UserData/cfg/Spectator/Si_Spectator_Config.json (the server's cfg/ folder, next to the other mods). New mod, so
    /// there is no older folder to migrate.
    /// </summary>
    public sealed class SpectatorConfig
    {
        public bool Enabled = true;            // master switch: off = nobody can enter (not even forced)
        public bool AllowAll = false;          // everyone may /spectate
        public bool AllowAdmins = true;        // Admin Mod admins may always /spectate
        public List<AllowedJson> Allowed = new List<AllowedJson>();   // designated by !spectator allow
        public int MaxSpectators = 4;          // slots, 0 = unlimited; !spectator force ignores it
        public bool Announce = true;           // all-chat line when someone enters / leaves spectator mode
        public bool Debug = false;             // verbose log lines

        [JsonIgnore] public AllowedList AllowedList = new AllowedList();

        public static string Path => System.IO.Path.Combine(MelonEnvironment.UserDataDirectory, "cfg", "Spectator", "Si_Spectator_Config.json");

        [JsonIgnore] public RulesSettings Rules => new RulesSettings { Enabled = Enabled, AllowAll = AllowAll, AllowAdmins = AllowAdmins, MaxSpectators = MaxSpectators };

        public static SpectatorConfig Load()
        {
            var cfg = new SpectatorConfig();
            try
            {
                if (File.Exists(Path))
                {
                    cfg = JsonConvert.DeserializeObject<SpectatorConfig>(File.ReadAllText(Path)) ?? new SpectatorConfig();
                    cfg.Sanitize();
                    MelonLogger.Msg("Config loaded from " + Path);
                }
                else
                {
                    cfg.Sanitize();
                    cfg.Save();
                    MelonLogger.Msg("Default config written to " + Path);
                }
            }
            catch (Exception ex)
            {
                cfg = new SpectatorConfig();
                cfg.Sanitize();
                MelonLogger.Warning("Config load failed (" + ex.Message + "), using defaults (the file is left untouched)");
            }
            return cfg;
        }

        public void Save()
        {
            try
            {
                Allowed = AllowedList.Entries.Select(e => new AllowedJson { SteamId = e.Key, Name = e.Value }).ToList();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                File.WriteAllText(Path, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch (Exception ex) { MelonLogger.Warning("Config save failed: " + ex.Message); }
        }

        private void Sanitize()
        {
            if (MaxSpectators < 0) MaxSpectators = 0;
            if (MaxSpectators > SpectatorRules.MaxSlots) MaxSpectators = SpectatorRules.MaxSlots;
            AllowedList = new AllowedList((Allowed ?? new List<AllowedJson>()).Where(a => a != null && a.SteamId != 0)
                                          .Select(a => new KeyValuePair<ulong, string>(a.SteamId, a.Name ?? "")));
            Allowed = AllowedList.Entries.Select(e => new AllowedJson { SteamId = e.Key, Name = e.Value }).ToList();
        }
    }
}
