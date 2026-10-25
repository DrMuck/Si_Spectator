using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppSilica.UI.Menus;
using UnityEngine;

namespace SiSpectator;

// Finding, registering or creating the LargeMap menu for a spectator.
//
// Live test 2 (1.0.7): MenuManager._menus held 7 menus (InGameMenu, FeedbackMenu, StrategyFactionsMenu, RoleScreen,
// BattleLayoutsScreen, NextMapMenu, EndScreen) and no LargeMap; Resources.FindObjectsOfTypeAll found 4 LargeMap objects,
// none classified as a scene object. Menu.Awake (Silica.UI.Menus/Menu.cs:50-56) is what registers a menu, and Awake only
// runs once the object is active in the hierarchy, so a LargeMap under an inactive UICollection (UIManager.SwitchUIMode
// turns every collection off in UIMode None) is never registered.
//
// Order: (1) a registered instance in _menus; (2) a scene instance (classified by Scene.IsValid, scene name, or by
// sharing its root with MenuManager / a registered menu) - SetActive(true) so Awake registers it, or AddMenu<LargeMap>;
// (3) if nothing registered results: a clone ("LargeMap (Spectator)") of the best candidate (scene object first, else a
// prefab whose name contains "Strategy" / "MP", else the first) instantiated under the parent of a registered menu
// (InGameMenu preferred, else MenuManager.Instance.transform). Its Awake registers it. The clone lives as long as we
// spectate: on leave it is closed, removed from MenuManager (RemoveMenu<LargeMap>) and destroyed.
// Everything is logged: one "LargeMap candidates" line listing each object, and the path taken on every M press.
public sealed partial class ClientMod
{
    private int largeMapSearches; private bool largeMapDiagLogged;
    private LargeMap spawnedMap;

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

    // Makes a registered LargeMap exist (registering or creating one) and returns it; notes collects every attempt.
    private LargeMap EnsureLargeMap(ref string notes)
    {
        var map = FindLargeMap();
        if (map != null) return map;
        var manager = MenuManager.Instance;
        if (manager == null) { notes += " no MenuManager;"; return null; }
        List<MapCandidate> candidates;
        try { candidates = MapCandidates(); }
        catch (Exception e) { notes += " candidate search threw " + e.GetType().Name + ";"; candidates = new List<MapCandidate>(); }
        notes += " candidates " + candidates.Count + " (" + candidates.Count(c => c.InScene) + " in scene);";
        // (2) scene instances: activate so Awake registers, else AddMenu
        foreach (var c in candidates.Where(c => c.InScene))
        {
            try
            {
                if (!c.Map.gameObject.activeSelf) { c.Map.gameObject.SetActive(true); notes += " SetActive(true) on '" + c.Map.gameObject.name + "' (inHierarchy " + c.Map.gameObject.activeInHierarchy + ");"; }
                if (!IsRegistered(c.Map) && c.Map.gameObject.activeInHierarchy) { manager.AddMenu<LargeMap>(c.Map); notes += " AddMenu<LargeMap>;"; }
                if (IsRegistered(c.Map) && c.Map.gameObject.activeInHierarchy) { notes += " scene instance registered;"; return largeMap = c.Map; }
            }
            catch (Exception e) { notes += " scene instance threw " + e.GetType().Name + ";"; }
        }
        // (3) clone under the parent of a registered menu
        var source = candidates.FirstOrDefault(c => c.InScene)
            ?? candidates.FirstOrDefault(c => { string n = c.Map.gameObject.name; return n.IndexOf("Strategy", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("MP", StringComparison.Ordinal) >= 0; })
            ?? candidates.FirstOrDefault();
        if (source == null) { notes += " nothing to clone;"; return null; }
        Transform parent = null;
        var registered = RegisteredMenus();
        var inGame = registered.FirstOrDefault(m => m.GetIl2CppType().Name == "InGameMenu") ?? registered.FirstOrDefault();
        try { parent = inGame != null ? inGame.transform.parent : null; } catch { }
        if (parent == null) parent = manager.transform;
        try
        {
            var cloneObj = UnityEngine.Object.Instantiate(source.Map.gameObject.Cast<UnityEngine.Object>(), parent, false);
            var cloneGo = cloneObj == null ? null : cloneObj.TryCast<GameObject>();
            if (cloneGo == null) { notes += " Instantiate returned no GameObject;"; return null; }
            cloneGo.name = "LargeMap (Spectator)";
            if (!cloneGo.activeSelf) cloneGo.SetActive(true);
            var clone = cloneGo.GetComponent<LargeMap>();
            if (clone == null) { notes += " clone has no LargeMap;"; UnityEngine.Object.Destroy(cloneGo); return null; }
            spawnedMap = clone;
            if (!IsRegistered(clone)) { try { manager.AddMenu<LargeMap>(clone); notes += " AddMenu<LargeMap>(clone);"; } catch (Exception e) { notes += " AddMenu(clone) threw " + e.GetType().Name + ";"; } }
            notes += " cloned '" + source.Map.gameObject.name + "' (" + (source.InScene ? "scene" : "asset") + ") under " + ObjectPath(parent.gameObject) + ", registered " + IsRegistered(clone) + ";";
            Write("LargeMap clone created from '" + source.Map.gameObject.name + "' under " + ObjectPath(parent.gameObject) + ", registered " + IsRegistered(clone) + ".");
            return largeMap = clone;
        }
        catch (Exception e) { notes += " clone threw " + e.GetType().Name + ": " + e.Message + ";"; return null; }
    }

    // Returns the path that worked or "FAILED ... (attempts)". Open: ensure a registered map, OpenMenu(Menu), last
    // resort OpenMenu<LargeMap>(out). Close: CloseMenu(Menu), fallback CloseMenu<LargeMap>().
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
        var map = EnsureLargeMap(ref notes);
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
        return how + (map != null && spawnedMap != null && map.Pointer == spawnedMap.Pointer ? " on the Spectator clone" : "") + (notes.Trim().Length > 0 ? " (" + notes.Trim() + ")" : "");
    }

    // On leave: close, unregister and destroy our clone so vanilla code never meets a dead menu in MenuManager.
    private void DestroySpawnedMap()
    {
        var clone = spawnedMap; spawnedMap = null;
        if (clone == null) return;
        try
        {
            var manager = MenuManager.Instance;
            if (manager != null)
            {
                try { if (SafeIsOpen(clone)) manager.CloseMenu(clone); } catch { }
                try { manager.RemoveMenu<LargeMap>(clone); } catch { }
            }
            if (largeMap != null && largeMap.Pointer == clone.Pointer) largeMap = null;
            UnityEngine.Object.Destroy(clone.gameObject);
            Write("LargeMap clone removed.");
        }
        catch (Exception error) { Error(error); }
    }
}
