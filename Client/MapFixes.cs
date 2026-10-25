using HarmonyLib;
using Il2Cpp;
using Il2CppSilica.UI;
using UnityEngine;

namespace SiSpectator;

// Fixes for the faction UI we instantiate while spectating (SpectatorUi.cs). All local, all inert unless spectating.
//
//  1. "NO TEAM / MAP CONCEALED": ScoreBoard.LateUpdate (Silica.UI/ScoreBoard.cs:48-63, on the LargeMap's "Match" object)
//     does mapHider.SetActive(GameMode.CurrentGameMode.GetConcealMap()), and GameMode.GetConcealMap() is
//     `return !Team.CurrentTeam;` (GameMode.cs 0947:154 / 106:176; no override in either decompile or in the 1.0.7
//     interop). Postfix: false while spectating. The map fog then comes from FogOfWar.CurrentTeamFogTexture, which our
//     GetCurrentFogTeam postfix steers: Fog = All -> GetTeamFogTexture(null) = null -> no fog layer
//     (FogOfWar.cs 0947:263-268); Fog = X -> X's fog texture.
//  2. Minimap: MinimapCanvas sits under the "FPS" UICollection (UIs/FPS/Canvas, Minimap), which UIManager.SwitchUIMode
//     only activates in UIMode FPS; in UIMode None it also switches MinimapCanvas off on every mode switch
//     (UIManager.cs 0947:306-410 / 106:356-460). A spectator never leaves None. So while spectating we move the canvas
//     one level up (UIs/FPS -> UIs, still inside our UI instance, destroyed with it) and re-run our minimap logic in
//     a postfix on SwitchUIMode, so the None branch never leaves it off.
//  3. The map's unit-cap rows (RTSUnitCapPanel): driven by MapClick.cs for the faction chosen in "Show:".
public sealed partial class ClientMod
{
    private bool concealLogged, minimapReparented, minimapPathLogged, unitCapsHidden;

    private void ResetMapFixes() { minimapReparented = minimapPathLogged = unitCapsHidden = false; }

    [HarmonyPatch(typeof(GameMode), nameof(GameMode.GetConcealMap))]
    private static class ConcealMap
    {
        private static void Postfix(ref bool __result)
        {
            if (!__result || !Active) return;
            __result = false;
            if (instance != null && !instance.concealLogged) { instance.concealLogged = true; instance.Write("Map concealment off for the spectator (GameMode.GetConcealMap -> false)."); }
        }
    }

    [HarmonyPatch(typeof(UIManager), nameof(UIManager.SwitchUIMode))]
    private static class UiModeSwitch
    {
        private static void Postfix(UIManager __instance)
        {
            if (!Active || instance?.spectatorUi == null || __instance == null) return;
            try { instance.UpdateMinimap(); } catch (Exception error) { instance.Error(error); }
        }
    }

    private bool InSpectatorUi(GameObject go)
    {
        try { return spectatorUi != null && go != null && go.transform.IsChildOf(spectatorUi.transform); } catch { return false; }
    }
    // UIs/FPS/Canvas, Minimap -> UIs/Canvas, Minimap (only inside our own UI instance).
    private void EnsureMinimapReparented(GameObject canvas)
    {
        if (minimapReparented || !InSpectatorUi(canvas)) return;
        minimapReparented = true;
        try
        {
            var collection = canvas.transform.parent;
            var target = collection == null ? null : collection.parent;
            if (target == null) { Write("Minimap: no grandparent to move the canvas to (" + ObjectPath(canvas) + ")."); return; }
            canvas.transform.SetParent(target, false);
            minimapToggle = null; minimapSearchAt = 0;
        }
        catch (Exception error) { Error(error); }
    }
    private void LogMinimapPathOnce(GameObject canvas)
    {
        if (minimapPathLogged) return;
        minimapPathLogged = true;
        Write("Minimap canvas: " + ObjectPath(canvas) + ", activeInHierarchy " + canvas.activeInHierarchy + (minimapReparented ? " (moved out of the FPS collection)" : "") + ".");
    }
}
