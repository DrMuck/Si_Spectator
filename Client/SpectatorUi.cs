using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppSilica.UI.Menus;
using UnityEngine;

namespace SiSpectator;

// The faction UI for a team-less spectator.
//
// TeamUI (TeamUI.cs, identical in 0.9.47 and 1.0.6) is a scene component (".../GameplayUIBase/UI"). On every local team
// change it destroys its _currentUIInstance and, for a non-null team, instantiates team.UIPrefab (Team.cs 0947:292 /
// 106:270) under itself. That prefab (UIManagerSol / UIManagerCentauri / UIManagerAlien / UIManagerGM) carries the
// UIManager (UIManager.Instance is set in its Awake / OnEnable), the minimap canvas, the LargeMap + LargeMapOpener and the
// shared HUD canvas. A team-less player therefore has no UIManager, no minimap and no large map at all.
//
// While spectating, ~0.5 s after S|on (and again after a scene-load re-entry), when TeamUI has no instance: instantiate
// the UIPrefab of the team named by the setting SpectatorUiFaction (default "Sol"; fallback the first team with a prefab)
// under the TeamUI transform, exactly as TeamUI would. It is purely local UI. Destroyed (deactivated first, so the
// UIManager's OnDisable clears UIManager.Instance and unsubscribes at once) on leave, on any non-null team of the local
// player - in a prefix on TeamUI.PlayerTeamChanged, i.e. BEFORE TeamUI instantiates the real one - on scene load and on
// disconnect. Never two UI managers.
public sealed partial class ClientMod
{
    private GameObject spectatorUi;
    private double spectatorUiDueAt = -1;
    private bool spectatorUiTried;

    private void ScheduleSpectatorUi() { spectatorUiDueAt = Now + .5; spectatorUiTried = false; minimapOnLogged = false; ResetMapFixes(); }

    // 4 Hz from OnUpdate while Active.
    private void PollSpectatorUi()
    {
        if (spectatorUi != null || spectatorUiTried || spectatorUiDueAt < 0 || Now < spectatorUiDueAt) return;
        spectatorUiTried = true;
        try { CreateSpectatorUi(); } catch (Exception error) { Error(error); Write("Spectator UI: instantiation failed: " + error.GetType().Name + " " + error.Message); }
    }
    private TeamUI FindTeamUi()
    {
        TeamUI fallback = null;
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TeamUI>());
        for (int i = 0; all != null && i < all.Length; i++)
        {
            var t = all[i] == null ? null : all[i].TryCast<TeamUI>();
            if (t == null) continue;
            bool inScene = false;
            try { inScene = t.gameObject.scene.IsValid(); } catch { }
            if (t.gameObject.activeInHierarchy) return t;
            if (inScene && fallback == null) fallback = t;
        }
        return fallback;
    }
    private void CreateSpectatorUi()
    {
        var teamUi = FindTeamUi();
        if (teamUi != null && teamUi._currentUIInstance != null) { Write("Spectator UI: TeamUI already has an instance (" + teamUi._currentUIInstance.name + "), not adding one."); return; }
        Transform parent = teamUi != null ? teamUi.transform : null;
        if (parent == null)
        {
            var inGame = RegisteredMenus().FirstOrDefault(m => m.GetIl2CppType().Name == "InGameMenu");
            try { parent = inGame != null ? inGame.transform.parent : null; } catch { }
        }
        if (parent == null) { Write("Spectator UI: no TeamUI and no InGameMenu parent found; no faction UI (no minimap / large map)."); return; }
        var team = PickUiTeam();
        if (team == null) { Write("Spectator UI: no team with a UIPrefab."); return; }
        var prefab = team.UIPrefab;
        var created = UnityEngine.Object.Instantiate(prefab.Cast<UnityEngine.Object>(), parent, false);
        spectatorUi = created == null ? null : created.TryCast<GameObject>();
        if (spectatorUi == null) { Write("Spectator UI: Instantiate returned nothing."); return; }
        largeMap = null; largeMapSearchAt = 0; minimapToggle = null; minimapSearchAt = 0;
        ResetMapFixes();
        Write("Spectator UI: instantiated " + prefab.name + " (team " + TeamLabel(team) + ") under " + ObjectPath(parent.gameObject) + (teamUi == null ? " (no TeamUI found, InGameMenu parent used)" : "")
            + "; UIManager.Instance " + (Il2CppSilica.UI.UIManager.Instance != null ? "set" : "null") + ".");
        HideUnitCapPanels();
    }
    private Team PickUiTeam()
    {
        string wanted = (spectatorUiFaction?.Value ?? "Sol").Trim();
        Team first = null;
        var teams = Team.Teams;
        for (int i = 0; teams != null && i < teams.Count; i++)
        {
            var t = teams[i];
            if (t == null) continue;
            GameObject prefab = null;
            try { prefab = t.UIPrefab; } catch { }
            if (prefab == null) continue;
            if (first == null) first = t;
            if (wanted.Length > 0 && ((t.TeamShortName ?? "").StartsWith(wanted, StringComparison.OrdinalIgnoreCase) || (t.TeamName ?? "").StartsWith(wanted, StringComparison.OrdinalIgnoreCase)
                || (t.TeamName ?? "").IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0))
                return t;
        }
        return first;
    }
    private void DestroySpectatorUi(string why)
    {
        spectatorUiDueAt = -1;
        var ui = spectatorUi; spectatorUi = null;
        if (ui == null) return;   // Unity null: already gone with its scene
        try
        {
            try { var map = largeMap; if (map != null && SafeIsOpen(map) && MenuManager.Instance != null) MenuManager.Instance.CloseMenu(map); } catch { }
            ui.SetActive(false);   // OnDisable now: UIManager.Instance cleared, its event subscriptions removed
            UnityEngine.Object.Destroy(ui);
            largeMap = null; minimapToggle = null; minimapTouched = false;
            Write("Spectator UI: destroyed (" + why + ").");
        }
        catch (Exception error) { Error(error); }
    }

    // Before TeamUI creates the real faction UI for a local team change, ours goes - so there is never a second UIManager.
    [HarmonyPatch(typeof(TeamUI), nameof(TeamUI.PlayerTeamChanged))]
    private static class TeamUiChange
    {
        private static void Prefix(Player __0, Team __2)
        {
            try
            {
                if (instance?.spectatorUi == null || __2 == null || __0 == null) return;
                var me = Player.CurrentPlayer;
                if (me == null || __0.Pointer != me.Pointer) return;
                instance.DestroySpectatorUi("local player joined team " + __2.TeamShortName);
            }
            catch (Exception error) { instance?.Error(error); }
        }
    }
}
