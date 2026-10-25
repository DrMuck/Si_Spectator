using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace SiSpectator
{
    // Si_AFKManager counts every team-less player as AFK (+1 minute per check while a round runs) and kicks at its limit
    // when the server is nearly full. A postfix on its OnUpdate removes spectators from the private static AFKTracker
    // list, so their count never gets past 1. Type found by name; nothing happens when the mod is not installed.
    internal static class Guards
    {
        private static FieldInfo _tracker;
        private static PropertyInfo _trackerPlayer;
        private static bool _failed;

        internal static void InstallAfk(HarmonyLib.Harmony harmony)
        {
            var type = GameApi.FindType("Si_AFKManager.AwayFromKeyboard");
            if (type == null) { SpectatorMod.Dbg("Si_AFKManager not loaded: no AFK guard needed"); return; }
            _tracker = AccessTools.Field(type, "AFKTracker");
            var onUpdate = AccessTools.Method(type, "OnUpdate", Type.EmptyTypes);
            if (_tracker == null || onUpdate == null || onUpdate.DeclaringType != type)
            {
                MelonLogger.Warning("Si_AFKManager found but AFKTracker/OnUpdate not (" + (_tracker != null) + "/" + (onUpdate != null) + "): spectators may be AFK-kicked on a nearly full server");
                return;
            }
            harmony.Patch(onUpdate, postfix: new HarmonyMethod(typeof(Guards).GetMethod(nameof(AfkPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            MelonLogger.Msg("Si_AFKManager guard installed (spectators are kept out of its AFK tracker)");
        }

        private static void AfkPostfix()
        {
            if (_failed || SpectatorMod.State.Count == 0) return;
            try
            {
                if (!(_tracker.GetValue(null) is IList list)) return;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var entry = list[i];
                    if (entry == null) continue;
                    if (_trackerPlayer == null) _trackerPlayer = entry.GetType().GetProperty("Player");
                    var player = _trackerPlayer?.GetValue(entry) as Player;
                    if (player == null || !SpectatorMod.State.IsSpectating(GameApi.SteamOf(player))) continue;
                    list.RemoveAt(i);
                    SpectatorMod.Dbg("AFK tracker: removed spectator " + GameApi.NameOf(player));
                }
            }
            catch (Exception ex) { _failed = true; MelonLogger.Warning("AFK guard disabled after an error: " + ex.GetBaseException().Message); }
        }
    }
}
