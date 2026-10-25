using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppSilica;
using Il2CppSilica.UI;
using Il2CppSilica.UI.Menus;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SiSpectator;

// Large map (M), large minimap (Tab) and the map faction filter while spectating.
//
// Vanilla wiring (identical in 0.9.47 and 1.0.6):
//   - LargeMapOpener (Silica.UI.Menus/LargeMapOpener.cs:22-53, handler at :39) subscribes to the Global "Map" action in OnEnable and
//     toggles the LargeMap menu (no team check). Where the component sits in the HUD is not visible in code; if it is
//     disabled for a player with no unit (UIMode None), M does nothing. So: in LateUpdate, when "Map" was performed this
//     frame and the LargeMap open state did not change since the last LateUpdate, we toggle it ourselves through
//     MenuManager.OpenMenu(Menu) / CloseMenu(Menu) (the non-generic overloads, MenuManager.cs:130 / 198). Finding or
//     creating the LargeMap instance: LargeMapAccess.cs. Every M press we handle is logged with the path or FAILED.
//   - The large minimap lives under UIManager.MinimapCanvas, which UIManager.SwitchUIMode turns OFF in UIMode.None
//     (UIManager.cs:310-323), the mode a team-less free-flying player is in; MinimapToggle (MinimapToggle.cs:41-54) also
//     requires UIMode FPS. So while spectating we switch the canvas on ourselves and drive MinimapToggle's _minimap /
//     _largeMinimap from the Global "Large Minimap" action (held = large). Restored on leave.
//   - Both maps get their unit / structure icons from TargetMapIconDisplay.GetData() =
//     Target.ProcessTargetsForTeam(Player.CurrentPlayer.Team, ...) (TargetMapIconDisplay.cs:35-39). For a team-less
//     player that returns EVERY active target (Target.cs:215-252, no visibility check), and MapIconDisplay rebuilds its
//     icons from that list every frame (MapIconDisplay.cs:29-75: icons missing from the list are released next frame).
//     A postfix on ProcessTargetsForTeam (team argument null, spectating only) therefore filters both maps at once:
//       faction filter: only targets of the chosen team;  fog perspective X: only targets visible / static-visible
//       under X (IsVisible / IsStaticVisible, which our UpdateVisibility prefix sets).
//     Filtered icons come back on the next frame when the filter changes; no extra refresh is needed. The map fog
//     texture follows the fog perspective through our GetCurrentFogTeam postfix (LargeMap.cs:83-89 reads
//     FogOfWar.CurrentTeamFogTexture every frame; the minimap gets it from FogOfWar.ShowFog).
public sealed partial class ClientMod
{
    private Team mapFilter;   // null = All; reset on every spectate entry
    private LargeMap largeMap; private double largeMapSearchAt;
    private InputAction mapAction, minimapAction;
    private bool mapOpenAtLastLate, ourMapLogged, vanillaMapLogged;
    private MinimapToggle minimapToggle; private double minimapSearchAt;
    private bool minimapTouched, minimapLogged, minimapOnLogged;

    private void ResetMapRefs()
    {
        largeMap = null; minimapToggle = null; mapAction = minimapAction = null; minimapTouched = false;
        largeMapSearchAt = minimapSearchAt = 0; mapOpenAtLastLate = false; largeMapSearches = 0; largeMapDiagLogged = false; ourMapLogged = vanillaMapLogged = false;
    }

    // ---- large map ----
    internal bool LargeMapOpen()
    {
        try { var map = FindLargeMap(); return map != null && map.IsOpen; } catch { return false; }
    }
    private static bool ActionPerformed(ref InputAction action, string name, Key fallback)
    {
        try
        {
            action ??= GameInput.GetAction(GameInput.InputMap.Global, name);
            if (action != null) return action.WasPerformedThisFrame();
        }
        catch { action = null; }
        var keyboard = Keyboard.current;
        return keyboard != null && keyboard[fallback] != null && keyboard[fallback].wasPressedThisFrame;
    }
    private static bool ActionHeld(ref InputAction action, string name, Key fallback)
    {
        try
        {
            action ??= GameInput.GetAction(GameInput.InputMap.Global, name);
            if (action != null) return action.IsPressed();
        }
        catch { action = null; }
        var keyboard = Keyboard.current;
        return keyboard != null && keyboard[fallback] != null && keyboard[fallback].isPressed;
    }
    private void MapLateUpdate()
    {
        if (!Active) { mapOpenAtLastLate = false; return; }
        bool open = LargeMapOpen();
        try
        {
            bool pickSpawn = GameMode.CurrentGameMode != null && GameMode.CurrentGameMode.GetPickSpawn();
            if (ActionPerformed(ref mapAction, "Map", Key.M) && Application.isFocused && !panelOpen && !pickSpawn && !KeysBlocked(out _))
            {
                if (open == mapOpenAtLastLate)
                {
                    string how = SetLargeMap(!open);
                    open = LargeMapOpen();
                    ourMapLogged = true;
                    Write((how.StartsWith("FAILED", StringComparison.Ordinal) ? "Map key handled by Spectator: " : "Map key handled by Spectator: ") + how + ", large map now " + (open ? "open" : "closed") + ".");
                }
                else if (!vanillaMapLogged) { vanillaMapLogged = true; Write("Map key handled by the vanilla LargeMapOpener."); }
            }
        }
        catch (Exception error) { Error(error); }
        mapOpenAtLastLate = open;
        UpdateMinimap();
    }
    // ---- minimap (Tab) ----
    private MinimapToggle FindMinimapToggle(GameObject canvas)
    {
        if (minimapToggle != null) return minimapToggle;
        if (Now < minimapSearchAt) return null;
        minimapSearchAt = Now + 2;
        try { minimapToggle = canvas.GetComponentInChildren<MinimapToggle>(true); } catch { minimapToggle = null; }
        if (minimapToggle == null && !minimapLogged) { minimapLogged = true; Write("MinimapToggle not found under UIManager.MinimapCanvas: Tab (large minimap) unavailable."); }
        return minimapToggle;
    }
    private static void SetActive(GameObject go, bool on) { if (go != null && go.activeSelf != on) go.SetActive(on); }
    private void UpdateMinimap()
    {
        try
        {
            var ui = UIManager.Instance;
            var canvas = ui == null ? null : ui.MinimapCanvas;
            if (canvas == null) { if (Active && !minimapOnLogged && spectatorUi != null) { minimapOnLogged = true; Write("Minimap: " + (ui == null ? "no UIManager.Instance" : "UIManager has no MinimapCanvas") + " although the spectator UI exists."); } return; }
            if (ui.CurrentMode != UIMode.None) { minimapTouched = false; return; }   // vanilla owns the canvas in every other UI mode
            bool usable = Active && !hideOverlays && !NativeMenuBlocking() && !LargeMapOpen() && Application.isFocused;
            bool large = usable && !NativeTextInput() && !panelOpen && ActionHeld(ref minimapAction, "Large Minimap", Key.Tab);
            bool small = usable && showMinimap.Value;
            if (small || large)
            {
                if (!canvas.activeSelf)
                {
                    canvas.SetActive(true); minimapTouched = true;
                    if (!minimapOnLogged) { minimapOnLogged = true; Write("Minimap canvas switched on (" + ObjectPath(canvas) + ", now " + (canvas.activeInHierarchy ? "visible" : "still inactive in hierarchy") + ")."); }
                }
                var toggle = FindMinimapToggle(canvas);
                if (toggle != null) { SetActive(toggle._minimap, small && !large); SetActive(toggle._largeMinimap, large); }
            }
            else if (minimapTouched) RestoreMinimap();
        }
        catch (Exception error) { Error(error); }
    }
    private void RestoreMinimap()
    {
        if (!minimapTouched) return;
        minimapTouched = false;
        try
        {
            if (minimapToggle != null) { SetActive(minimapToggle._minimap, true); SetActive(minimapToggle._largeMinimap, false); }
            var ui = UIManager.Instance;
            if (ui != null && ui.MinimapCanvas != null && ui.CurrentMode == UIMode.None) ui.MinimapCanvas.SetActive(false);
        }
        catch { }
    }

    // ---- icon filter (both maps) ----
    [HarmonyPatch(typeof(Target), nameof(Target.ProcessTargetsForTeam))]
    private static class MapIconFilter
    {
        private static void Postfix(Team __0, Il2CppSystem.Collections.Generic.List<Target> __1, ref int __result)
        {
            if (!Active || __0 != null || __1 == null) return;
            try { instance.FilterTargets(__1, ref __result); } catch (Exception error) { instance.Error(error); }
        }
    }
    private void FilterTargets(Il2CppSystem.Collections.Generic.List<Target> list, ref int count)
    {
        var filter = mapFilter; var fog = fogTeam;
        if (filter == null && fog == null) return;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var t = list[i];
            bool keep = t != null;
            if (keep && filter != null) { var team = t.Team; keep = team != null && team.Pointer == filter.Pointer; }
            if (keep && fog != null) keep = t.IsVisible || t.IsStaticVisible;
            if (!keep) list.RemoveAt(i);
        }
        count = list.Count;
    }
    private void SetMapFilter(Team team, string why)
    {
        bool same = (team == null && mapFilter == null) || (team != null && mapFilter != null && team.Pointer == mapFilter.Pointer);
        mapFilter = team;
        if (!same) Write("Map shows " + (team == null ? "all factions" : TeamLabel(team) + " only") + " (" + why + ").");
    }

    // Two rows of buttons: "Show:" (map faction filter) and "Fog:" (fog perspective). Used on the large map toolbar and in
    // the panel. Only our own buttons consume clicks; the map underneath keeps its input.
    private float RenderFilterRows(float x, float y, float w, List<Team> teams)
    {
        float bh = 30, label = 58;
        int count = teams.Count + 1;
        float bw = (w - label - 8 * (count - 1)) / count;
        GUI.Label(new Rect(x, y + 5, label, 24), "Show:", smallStyle);
        if (Button(new Rect(x + label, y, bw, bh), "All", mapFilter == null, true, tabStyle)) SetMapFilter(null, "button");
        for (int i = 0; i < teams.Count; i++)
            if (Button(new Rect(x + label + (i + 1) * (bw + 8), y, bw, bh), TeamLabel(teams[i]), mapFilter != null && mapFilter.Pointer == teams[i].Pointer, true, tabStyle)) SetMapFilter(teams[i], "button");
        y += bh + 6;
        GUI.Label(new Rect(x, y + 5, label, 24), "Fog:", smallStyle);
        if (Button(new Rect(x + label, y, bw, bh), "All", fogTeam == null, true, tabStyle)) SetFog(null, "button");
        for (int i = 0; i < teams.Count; i++)
            if (Button(new Rect(x + label + (i + 1) * (bw + 8), y, bw, bh), TeamLabel(teams[i]), fogTeam != null && fogTeam.Pointer == teams[i].Pointer, true, tabStyle)) SetFog(teams[i], "button");
        return y + bh;
    }
    private void RenderMapToolbar()
    {
        var teams = MatchTeams();
        float w = Math.Min(Screen.width - 24, 120 + (teams.Count + 1) * 120);
        var rect = new Rect((Screen.width - w) / 2, 8, w, 88);
        Surface(rect, new Color(surface.r, surface.g, surface.b, .88f)); Surface(new Rect(rect.x, rect.y, rect.width, 2), accent);
        bool before = GUI.enabled;
        GUI.enabled = before && Application.isFocused && Cursor.visible && !NativeTextInput();
        try { RenderFilterRows(rect.x + 10, rect.y + 10, rect.width - 20, teams); } finally { GUI.enabled = before; }
    }
}
