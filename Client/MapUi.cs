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
//     MenuManager.OpenMenu(Menu) / CloseMenu(Menu) (the non-generic overloads, MenuManager.cs:130 / 198) on the
//     instance registered in MenuManager._menus. Whichever path acts is logged once.
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
    private bool minimapTouched, minimapLogged;

    private void ResetMapRefs()
    {
        largeMap = null; minimapToggle = null; mapAction = minimapAction = null; minimapTouched = false;
        largeMapSearchAt = minimapSearchAt = 0; mapOpenAtLastLate = false; largeMapSearches = 0; largeMapDiagLogged = false; ourMapLogged = vanillaMapLogged = false;
    }

    // ---- large map ----
    // The LargeMap instance: first from MenuManager._menus (where Menu.Awake registers it, Menu.cs:50-56), then -
    // because the live test showed the dictionary walk finding nothing - from Resources.FindObjectsOfTypeAll, preferring
    // a scene object over a prefab asset. One diagnostic line per scene says what was found and where.
    private int largeMapSearches; private bool largeMapDiagLogged;
    private LargeMap FindLargeMap()
    {
        if (largeMap != null) return largeMap;
        if (Now < largeMapSearchAt) return null;
        largeMapSearchAt = Now + 2; largeMapSearches++;
        LargeMap fromDict = null, fromScene = null; int dictCount = -1, assets = 0; string dictInfo = "";
        try
        {
            var manager = MenuManager.Instance;
            var menus = manager == null ? null : manager._menus;
            if (menus != null)
            {
                dictCount = menus.Count;
                var names = new List<string>();
                foreach (var menu in menus.Values)
                {
                    if (menu == null) continue;
                    if (names.Count < 16) names.Add(menu.GetIl2CppType().Name);
                    var found = menu.TryCast<LargeMap>();
                    if (found != null && fromDict == null) fromDict = found;
                }
                dictInfo = string.Join(",", names);
            }
        }
        catch (Exception error) { dictInfo = "enumeration failed: " + error.GetType().Name + " " + error.Message; }
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<LargeMap>());
            for (int i = 0; all != null && i < all.Length; i++)
            {
                var candidate = all[i] == null ? null : all[i].TryCast<LargeMap>();
                if (candidate == null) continue;
                bool inScene = false;
                try { inScene = candidate.gameObject.scene.IsValid(); } catch { }
                if (inScene) { if (fromScene == null) fromScene = candidate; }
                else assets++;
            }
        }
        catch (Exception error) { if (!largeMapDiagLogged) Write("LargeMap search (FindObjectsOfTypeAll) failed: " + error.GetType().Name + " " + error.Message); }
        largeMap = fromDict ?? fromScene;
        if (!largeMapDiagLogged && (largeMap != null || largeMapSearches >= 3))
        {
            largeMapDiagLogged = true;
            Write("LargeMap diagnostics: MenuManager " + (MenuManager.Instance == null ? "missing" : "present") + ", _menus count " + dictCount + " [" + dictInfo + "], in dictionary " + (fromDict != null)
                + ", scene instance " + (fromScene != null) + ", prefab/asset copies " + assets
                + (largeMap != null ? ", path " + ObjectPath(largeMap.gameObject) + ", IsOpen " + SafeIsOpen(largeMap) + ", registered " + IsRegistered(largeMap) : ", NOT FOUND"));
        }
        return largeMap;
    }
    private static bool SafeIsOpen(Menu menu) { try { return menu.IsOpen; } catch { return false; } }
    // "Root(on/on)/Child(off/off)/LargeMap(on/off)": name(activeSelf/activeInHierarchy) per level.
    private static string ObjectPath(GameObject go)
    {
        try
        {
            var parts = new List<string>();
            for (var t = go.transform; t != null && parts.Count < 12; t = t.parent)
                parts.Insert(0, t.gameObject.name + "(" + (t.gameObject.activeSelf ? "on" : "off") + "/" + (t.gameObject.activeInHierarchy ? "on" : "off") + ")");
            return string.Join("/", parts);
        }
        catch { return "?"; }
    }
    private static bool IsRegistered(Menu menu)
    {
        try
        {
            var menus = MenuManager.Instance == null ? null : MenuManager.Instance._menus;
            if (menus == null) return false;
            foreach (var m in menus.Values) if (m != null && m.Pointer == menu.Pointer) return true;
        }
        catch { }
        return false;
    }
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
                    if (!ourMapLogged || how.StartsWith("FAILED", StringComparison.Ordinal))
                    { ourMapLogged = true; Write("Map key handled by Spectator (the vanilla LargeMapOpener did not react): " + how + ", large map now " + (open ? "open" : "closed") + "."); }
                }
                else if (!vanillaMapLogged) { vanillaMapLogged = true; Write("Map key handled by the vanilla LargeMapOpener."); }
            }
        }
        catch (Exception error) { Error(error); }
        mapOpenAtLastLate = open;
        UpdateMinimap();
    }
    // Returns which path worked (for the log). Open: OpenMenu(Menu); if the map is not registered (its Awake never ran),
    // AddMenu<LargeMap>(map) - or SetActive(true) so Awake registers it - and OpenMenu(Menu) again; last resort the
    // generic OpenMenu<LargeMap>(out _). Close: CloseMenu(Menu), fallback CloseMenu<LargeMap>().
    private string SetLargeMap(bool open)
    {
        largeMapSearchAt = 0;
        var map = FindLargeMap(); var manager = MenuManager.Instance;
        if (manager == null) return "FAILED: no MenuManager";
        if (!open)
        {
            try { if (map != null && manager.CloseMenu(map)) return "CloseMenu(Menu)"; } catch (Exception e) { Write("CloseMenu(Menu) threw " + e.GetType().Name + ": " + e.Message); }
            try { if (manager.CloseMenu<LargeMap>()) return "CloseMenu<LargeMap>()"; } catch (Exception e) { Write("CloseMenu<LargeMap>() threw " + e.GetType().Name + ": " + e.Message); }
            return "FAILED: no close path worked";
        }
        string how = null, notes = "";
        if (map != null)
        {
            try { if (manager.OpenMenu(map)) how = "OpenMenu(Menu)"; } catch (Exception e) { notes += " OpenMenu(Menu) threw " + e.GetType().Name + ";"; }
            if (how == null && !SafeIsOpen(map) && !IsRegistered(map))
            {
                try { manager.AddMenu<LargeMap>(map); notes += " AddMenu<LargeMap> called, registered " + IsRegistered(map) + ";"; }
                catch (Exception e) { notes += " AddMenu<LargeMap> threw " + e.GetType().Name + ";"; }
                if (!IsRegistered(map))
                {
                    try { if (!map.gameObject.activeSelf) { map.gameObject.SetActive(true); notes += " SetActive(true), registered " + IsRegistered(map) + ";"; } }
                    catch (Exception e) { notes += " SetActive threw " + e.GetType().Name + ";"; }
                }
                try { if (manager.OpenMenu(map)) how = "registered + OpenMenu(Menu)"; } catch (Exception e) { notes += " OpenMenu(Menu) #2 threw " + e.GetType().Name + ";"; }
            }
        }
        if (how == null)
        {
            try { if (manager.OpenMenu<LargeMap>(out var opened)) { how = "OpenMenu<LargeMap>(out)"; if (largeMap == null && opened != null) largeMap = opened; } }
            catch (Exception e) { notes += " OpenMenu<LargeMap> threw " + e.GetType().Name + ";"; }
        }
        if (how == null)
            return "FAILED to open (map " + (map == null ? "not found" : ObjectPath(map.gameObject) + ", IsOpen " + SafeIsOpen(map) + ", registered " + IsRegistered(map)) + ";" + notes + ")";
        try
        {
            var shown = largeMap ?? map;
            var unit = TargetUnit(); var cam = Game.CurrentCamera;
            Vector3 focus = unit != null ? unit.transform.position : cam != null ? cam.transform.position : Vector3.zero;
            if (shown != null && shown._map != null) shown._map.FocusOnWorldPosition(focus);
        }
        catch { }
        return how + (notes.Length > 0 ? " (" + notes.Trim() + ")" : "");
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
            if (canvas == null) return;
            if (ui.CurrentMode != UIMode.None) { minimapTouched = false; return; }   // vanilla owns the canvas in every other UI mode
            bool usable = Active && !hideOverlays && !NativeMenuBlocking() && !LargeMapOpen() && Application.isFocused;
            bool large = usable && !NativeTextInput() && !panelOpen && ActionHeld(ref minimapAction, "Large Minimap", Key.Tab);
            bool small = usable && showMinimap.Value;
            if (small || large)
            {
                if (!canvas.activeSelf) { canvas.SetActive(true); minimapTouched = true; }
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
