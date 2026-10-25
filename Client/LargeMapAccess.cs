using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppSilica.UI.Menus;
using UnityEngine;

namespace SiSpectator;

// Finding the LargeMap menu for a spectator.
//
// The LargeMap (with the minimap canvas, the CursorManager-bearing UIManager, the LargeMapOpener) lives in the per-faction
// UI prefab (Team.UIPrefab: UIManagerSol / UIManagerCentauri / UIManagerAlien / UIManagerGM) that TeamUI instantiates when
// the local player joins a team (TeamUI.cs, identical in both branches). A team-less player has none of it. While
// spectating, SpectatorUi.cs instantiates one faction UI ourselves; its LargeMap registers in MenuManager through
// Menu.Awake (Menu.cs:50-56). Here we only look the registered LargeMap up and open / close it through MenuManager.
// Diagnostics: one "LargeMap diagnostics" + one "LargeMap candidates" line per scene, and the path (or FAILED with every
// attempt) on every M press we handle.
public sealed partial class ClientMod
{
    private int largeMapSearches; private bool largeMapDiagLogged;

    private sealed class MapCandidate { public LargeMap Map; public bool InScene; public string Info; }

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
            if (menus == null || menu == null) return false;
            foreach (var m in menus.Values) if (m != null && m.Pointer == menu.Pointer) return true;
        }
        catch { }
        return false;
    }
    private static List<Menu> RegisteredMenus()
    {
        var list = new List<Menu>();
        try
        {
            var menus = MenuManager.Instance == null ? null : MenuManager.Instance._menus;
            if (menus != null) foreach (var m in menus.Values) if (m != null) list.Add(m);
        }
        catch { }
        return list;
    }
    // Roots of objects that certainly live in a loaded scene: MenuManager and every registered menu.
    private static List<IntPtr> KnownSceneRoots()
    {
        var roots = new List<IntPtr>();
        try { if (MenuManager.Instance != null) roots.Add(MenuManager.Instance.transform.root.Pointer); } catch { }
        foreach (var m in RegisteredMenus()) { try { roots.Add(m.transform.root.Pointer); } catch { } }
        return roots;
    }
    private List<MapCandidate> MapCandidates()
    {
        var list = new List<MapCandidate>();
        var roots = KnownSceneRoots();
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<LargeMap>());
        for (int i = 0; all != null && i < all.Length; i++)
        {
            var map = all[i] == null ? null : all[i].TryCast<LargeMap>();
            if (map == null) continue;
            var go = map.gameObject;
            string sceneName = "?", valid = "?", flags = "?";
            bool inScene = false;
            try { var scene = go.scene; sceneName = scene.name ?? ""; bool v = scene.IsValid(); valid = v ? "valid" : "invalid"; inScene |= v || sceneName.Length > 0; }
            catch (Exception e) { valid = "error " + e.GetType().Name; }
            try { flags = go.hideFlags.ToString(); } catch { }
            try { if (roots.Contains(go.transform.root.Pointer)) inScene = true; } catch { }
            list.Add(new MapCandidate
            {
                Map = map, InScene = inScene,
                Info = "'" + go.name + "' scene='" + sceneName + "' " + valid + " flags=" + flags + " " + (inScene ? "SCENE" : "asset") + " registered=" + IsRegistered(map) + " path=" + ObjectPath(go)
            });
        }
        return list;
    }

    // Finds a usable LargeMap (registered first). Does not create anything; SetLargeMap does that on an M press.
    private LargeMap FindLargeMap()
    {
        if (largeMap != null) return largeMap;
        if (Now < largeMapSearchAt) return null;
        largeMapSearchAt = Now + 2; largeMapSearches++;
        foreach (var m in RegisteredMenus()) { var found = m.TryCast<LargeMap>(); if (found != null) { largeMap = found; break; } }
        if (!largeMapDiagLogged && (largeMap != null || largeMapSearches >= 3))
        {
            largeMapDiagLogged = true;
            var names = RegisteredMenus().Select(m => m.GetIl2CppType().Name).ToList();
            Write("LargeMap diagnostics: MenuManager " + (MenuManager.Instance == null ? "missing" : "present") + ", _menus count " + names.Count + " [" + string.Join(",", names) + "], registered LargeMap " + (largeMap != null)
                + (largeMap != null ? " at " + ObjectPath(largeMap.gameObject) + ", IsOpen " + SafeIsOpen(largeMap) : ""));
            try { LogCandidates(); } catch (Exception e) { Write("LargeMap candidates: search failed: " + e.GetType().Name + " " + e.Message); }
        }
        return largeMap;
    }
    private void LogCandidates()
    {
        var candidates = MapCandidates();
        Write("LargeMap candidates (" + candidates.Count + "): " + (candidates.Count == 0 ? "none" : string.Join(" | ", candidates.Select(c => c.Info))));
    }

    // Returns the path that worked or "FAILED ... (attempts)". Open: OpenMenu(Menu) on the registered map, last resort
    // OpenMenu<LargeMap>(out). Close: CloseMenu(Menu), fallback CloseMenu<LargeMap>().
    private string SetLargeMap(bool open)
    {
        var manager = MenuManager.Instance;
        if (manager == null) return "FAILED: no MenuManager";
        string notes = "";
        if (!open)
        {
            var current = largeMap ?? FindLargeMap();
            try { if (current != null && manager.CloseMenu(current)) return "CloseMenu(Menu)"; } catch (Exception e) { notes += " CloseMenu(Menu) threw " + e.GetType().Name + ";"; }
            try { if (manager.CloseMenu<LargeMap>()) return "CloseMenu<LargeMap>()"; } catch (Exception e) { notes += " CloseMenu<LargeMap>() threw " + e.GetType().Name + ";"; }
            return "FAILED to close (" + notes.Trim() + ")";
        }
        largeMapSearchAt = 0;
        var map = FindLargeMap();
        if (map == null) notes += " no registered LargeMap (spectator UI " + (spectatorUi != null ? "present" : "missing") + ");";
        string how = null;
        if (map != null)
        {
            try { if (manager.OpenMenu(map)) how = "OpenMenu(Menu)"; else notes += " OpenMenu(Menu) false (IsOpen " + SafeIsOpen(map) + ", registered " + IsRegistered(map) + ");"; }
            catch (Exception e) { notes += " OpenMenu(Menu) threw " + e.GetType().Name + ";"; }
        }
        if (how == null)
        {
            try { if (manager.OpenMenu<LargeMap>(out var opened)) { how = "OpenMenu<LargeMap>(out)"; if (opened != null) largeMap = map = opened; } else notes += " OpenMenu<LargeMap> false;"; }
            catch (Exception e) { notes += " OpenMenu<LargeMap> threw " + e.GetType().Name + ";"; }
        }
        if (how == null) return "FAILED to open (" + notes.Trim() + ")";
        try
        {
            // A team-less spectator never picks a spawn: keep the respawn part of the map hidden.
            if (map != null && map.RespawnMenu != null && map.RespawnMenu.gameObject.activeSelf) map.RespawnMenu.gameObject.SetActive(false);
            var unit = TargetUnit(); var cam = Game.CurrentCamera;
            Vector3 focus = unit != null ? unit.transform.position : cam != null ? cam.transform.position : Vector3.zero;
            if (map != null && map._map != null) map._map.FocusOnWorldPosition(focus);
        }
        catch { }
        return how + (notes.Trim().Length > 0 ? " (" + notes.Trim() + ")" : "");
    }
}
