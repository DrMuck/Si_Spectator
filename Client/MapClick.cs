using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSilica.UI;
using Il2CppSilica.UI.Controls;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SiSpectator;

// Large map: click a unit icon -> follow that unit (Orbit = free third-person camera) and close the map; unit-cap rows for the faction chosen in
// the map toolbar ("Show: <faction>").
//
// Icons: the map's TargetMapIconDisplay creates one MapIcon per target with RelatedObject = target.Owner
// (TargetMapIconDisplay.cs:43, both branches) under Map.IconParent (Map.cs:19). Hit test = pure geometry on the active
// MapIcons (RectTransform world corners; the UI canvases are screen-space overlay, so world corners are pixels - checked
// and logged once), no raycast target needed. Only clicks inside Map.Viewport (Map.cs:38) count, never on our toolbar.
// Ping: the vanilla Ping action is the middle mouse button (Global map); PingManager.HandlePingAction returns early
// without Unit.CurrentUnit / StrategyMode (PingManager.cs:152-157), i.e. always for a spectator. As a hard guarantee a
// prefix on NetworkLayer.SendPlayerPing (0947:1581 / 106:1780) drops any ping send while spectating.
// The Tab minimap is not clickable: it is a HUD shown while the cursor is locked, where a left click is already the 3D
// click-to-follow.
public sealed partial class ClientMod
{
    private Rect mapToolbarRect;           // IMGUI coordinates (y down), set while the toolbar is drawn
    private bool iconSpaceLogged, pingDropLogged;

    [HarmonyPatch(typeof(NetworkLayer), nameof(NetworkLayer.SendPlayerPing))]
    private static class NoPing
    {
        private static bool Prefix()
        {
            if (!Active) return true;
            if (instance != null && !instance.pingDropLogged) { instance.pingDropLogged = true; instance.Write("Ping send dropped (spectators never ping)."); }
            return false;
        }
    }

    // Screen rectangle (pixels, origin bottom-left like Mouse.position) of a UI element.
    private bool ScreenRect(RectTransform rt, out Rect rect)
    {
        rect = default;
        if (rt == null) return false;
        var corners = new Il2CppStructArray<Vector3>(4);
        rt.GetWorldCorners(corners);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue; bool flat = true;
        for (int i = 0; i < 4; i++)
        {
            Vector3 c = corners[i];
            if (Math.Abs(c.z) > 1f) flat = false;
            minX = Math.Min(minX, c.x); minY = Math.Min(minY, c.y); maxX = Math.Max(maxX, c.x); maxY = Math.Max(maxY, c.y);
        }
        if (!flat)
        {
            // camera-space canvas: project through the current camera (logged once; overlay is expected)
            var cam = Game.CurrentCamera;
            if (cam == null) return false;
            minX = minY = float.MaxValue; maxX = maxY = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 s = cam.WorldToScreenPoint(corners[i]);
                minX = Math.Min(minX, s.x); minY = Math.Min(minY, s.y); maxX = Math.Max(maxX, s.x); maxY = Math.Max(maxY, s.y);
            }
        }
        if (!iconSpaceLogged) { iconSpaceLogged = true; Write("Map hit test: UI space " + (flat ? "screen-space overlay (corners = pixels)" : "camera space (projected)") + "."); }
        rect = Rect.MinMaxRect(minX, minY, maxX, maxY);
        return true;
    }

    // Called from MapLateUpdate while the large map is open.
    private void MapClickLateUpdate()
    {
        if (!Active || panelOpen || NativeTextInput() || !Application.isFocused) return;
        var mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;
        Vector2 p = mouse.position.ReadValue();
        if (mapToolbarRect.width > 0 && mapToolbarRect.Contains(new Vector2(p.x, Screen.height - p.y))) return;   // our toolbar
        var lm = largeMap;
        var map = lm == null ? null : lm._map;
        if (map == null) return;
        if (map.Viewport != null && ScreenRect(map.Viewport, out var viewport) && !viewport.Contains(p)) return;   // legend, scoreboard, ...
        Transform root = map.IconParent != null ? map.IconParent : map.transform;
        var icons = root.GetComponentsInChildren<MapIcon>(false);
        MapIcon best = null; float bestScore = float.MaxValue; const float tolerance = 12f;
        for (int i = 0; icons != null && i < icons.Length; i++)
        {
            var icon = icons[i];
            if (icon == null || icon.RelatedObject == null || !ScreenRect(icon.RectTransform, out var r)) continue;
            float d = Vector2.Distance(r.center, p);
            bool inside = new Rect(r.x - 4, r.y - 4, r.width + 8, r.height + 8).Contains(p);
            if (!inside && d > tolerance) continue;
            if (icon.RelatedObject.TryCast<Unit>() == null) d += 1000f;   // prefer units over structures under the same point
            if (d < bestScore) { bestScore = d; best = icon; }
        }
        if (best == null) { Write("Map click: no unit icon under the pointer."); return; }
        var unit = best.RelatedObject.TryCast<Unit>();
        if (unit == null) { Write("Map click: '" + UnitName(best.RelatedObject) + "' is not a unit."); return; }
        if (!Alive(unit)) return;
        var me = Player.CurrentPlayer;
        var player = unit.ControlledBy;
        if (player != null)
        {
            if (player.Team == null || (me != null && player.Pointer == me.Pointer)) return;
            SelectTarget(player, "map click");
        }
        else
        {
            if (!AiPicking) { Write("Map click: '" + UnitName(unit) + "' is an AI unit and AI units are off."); return; }
            SelectAi(unit, "map click");
        }
        SetMode(CamMode.Orbit, "map click");
        string how = SetLargeMap(false);
        Write("Map closed after map click (" + how + ").");
    }

    // ---- unit-cap rows for the faction in "Show: <faction>" ----
    private readonly List<RTSUnitCapPanel> capPanels = new();
    private Team capTeam; private double capNextAt;

    // Collect the panels of our UI instance once, keep their own component off (its Init / UpdatePanel read the static
    // Team.CurrentTeam, null for us) and hide them until a faction is chosen.
    private void HideUnitCapPanels()
    {
        if (unitCapsHidden || spectatorUi == null) return;
        unitCapsHidden = true; capPanels.Clear(); capTeam = null;
        try
        {
            var panels = spectatorUi.GetComponentsInChildren<RTSUnitCapPanel>(true);
            for (int i = 0; panels != null && i < panels.Length; i++)
            {
                var p = panels[i];
                if (p == null) continue;
                p.enabled = false;
                if (p.gameObject.activeSelf) p.gameObject.SetActive(false);
                capPanels.Add(p);
            }
            Write("Unit cap rows: " + capPanels.Count + " RTSUnitCapPanel found, hidden until a faction is shown on the map.");
        }
        catch (Exception error) { Error(error); }
    }
    // While the large map is open: Show = All -> rows hidden; Show = <faction> -> the faction's real caps, every ~0.5 s.
    private void UnitCapLateUpdate()
    {
        if (capPanels.Count == 0) return;
        var team = mapFilter;
        bool switched = (team == null) != (capTeam == null) || (team != null && capTeam != null && team.Pointer != capTeam.Pointer);
        if (!switched && Now < capNextAt) return;
        capNextAt = Now + .5;
        if (team == null)
        {
            if (switched) { foreach (var p in capPanels) if (p != null && p.gameObject.activeSelf) p.gameObject.SetActive(false); capTeam = null; Write("Unit cap rows: hidden (Show: All)."); }
            return;
        }
        int entries = 0;
        foreach (var panel in capPanels)
        {
            if (panel == null) continue;
            try
            {
                if (!panel.gameObject.activeSelf) panel.gameObject.SetActive(true);
                if (panel.enabled) panel.enabled = false;
                entries += ApplyCaps(panel, team, switched);
            }
            catch (Exception error) { Error(error); }
        }
        if (switched) { capTeam = team; Write("Unit cap rows: " + TeamLabel(team) + " (" + entries + " entries)."); }
    }
    // RTSUnitCapPanel.Init (icons) + UpdatePanel (label "<current>/<max>", chart fill) for the given team, read-only.
    private static int ApplyCaps(RTSUnitCapPanel panel, Team team, bool icons)
    {
        var list = panel._unitCapEntries;
        if (list == null) return 0;
        Color numberColor = panel._currentNumberColor;
        int n = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            if (entry == null) continue;
            n++;
            var cap = team.GetUnitTypeCapEntryForType(entry.CapType);
            if (icons && entry.CapIcon != null && cap != null) entry.CapIcon.sprite = cap.Icon;
            int current = cap != null ? cap.Current : 0, max = cap != null ? cap.Max : 0;
            string text = "<color=#" + ColorUtility.ToHtmlStringRGB(numberColor) + ">" + current + "</color>";
            if (entry.CapLabel != null) entry.CapLabel.text = max > 0 ? text + "/" + max : text;
            if (entry.CapChart != null) entry.CapChart.fillAmount = Mathf.Clamp01(max > 0 ? (float)current / max : 0f);
        }
        return n;
    }
}
