using HarmonyLib;
using Il2Cpp;
using Il2CppDebugTools;
using Il2CppSilica.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SiSpectator;

// Entering / leaving: the faction screen is kept away while spectating (API_NOTES §9a), the team / role select actions are
// swallowed, Escape steps back to Free, and our keys. Everything here is inert unless Active.
public sealed partial class ClientMod
{
    private bool factionLogged, screenLogged;

    // Runs before MP_Strategy.HandleUI and after UpdateGameLoop in the same frame (GameMode.Update), so the forced
    // SELECT_TEAM for a team-less player is overridden before any UI decision. With NONE the cursor locks
    // (GetShowCursor false) and the vanilla free-fly camera gets input.
    [HarmonyPatch(typeof(MP_Strategy), nameof(MP_Strategy.HandleUI))]
    private static class FactionMenu
    {
        private static void Prefix(MP_Strategy __instance)
        {
            if (!Active || __instance == null) return;
            try { instance.SuppressFactionMenu(__instance); } catch (Exception error) { instance.Error(error); }
        }
    }
    private void SuppressFactionMenu(MP_Strategy mode)
    {
        if (mode.ShowTeamSelectMenu != GameModeExt.ETeamSelect.NONE)
        {
            if (!factionLogged) { factionLogged = true; Write("Faction menu suppressed (was " + mode.ShowTeamSelectMenu + ")."); }
            mode.ShowTeamSelectMenu = GameModeExt.ETeamSelect.NONE;
        }
        // The battle-layout vote (INIT, no versus mode yet) stays: a spectator may vote. Every other strategy screen that
        // belongs to team / role / respawn choice is closed; end screen and map vote stay.
        if (mode.MissionState == GameModeExt.EMissionState.INIT && mode.TeamsVersus == GameModeExt.ETeamsVersus.NONE) return;
        var handler = StrategyHandler.Instance;
        if (handler == null) return;
        var screen = handler.CurrentStrategyScreen;
        if (screen == StrategyScreenType.Faction || screen == StrategyScreenType.Loadout || screen == StrategyScreenType.Respawn)
        {
            StrategyHandler.SwitchTo(StrategyScreenType.None, null);
            if (!screenLogged) { screenLogged = true; Write("Closed strategy screen " + screen + " (" + mode.MissionState + ")."); }
        }
    }
    // On leave the vanilla loop brings the faction screen back by itself once the round runs (UpdateGameLoop forces
    // SELECT_TEAM for a team-less player). Before the round it would stay NONE, so it is set back here.
    private void RestoreFactionMenu()
    {
        factionLogged = screenLogged = false;
        var p = Player.CurrentPlayer;
        if (p == null || p.Team != null || Game.InMainMenu) return;
        var mode = GameMode.CurrentGameMode == null ? null : GameMode.CurrentGameMode.TryCast<MP_Strategy>();
        if (mode != null && mode.ShowTeamSelectMenu == GameModeExt.ETeamSelect.NONE) mode.ShowTeamSelectMenu = GameModeExt.ETeamSelect.SELECT_TEAM;
    }

    // The "Select Team" / "Select Role" actions would reopen the screens; a spectator leaves with "Return to game" instead.
    [HarmonyPatch(typeof(MP_Strategy), nameof(MP_Strategy.TeamSelectActionPerformed))]
    private static class TeamSelectAction { private static bool Prefix() { if (!Active) return true; instance.Write("Team select action swallowed (spectating)."); return false; } }
    [HarmonyPatch(typeof(MP_Strategy), nameof(MP_Strategy.RoleSelectActionPerformed))]
    private static class RoleSelectAction { private static bool Prefix() { if (!Active) return true; instance.Write("Role select action swallowed (spectating)."); return false; } }

    // Escape: our panel closes first; while following (any mode but Free) Escape returns to Free; in Free the game opens
    // the pause menu as usual. With the ModUI panel open ModUI's own Escape prefix handles it (GetCameraEnabled is false).
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.OnEscape))]
    private static class Escape
    {
        private static bool Prefix()
        {
            if (!Active) return true;
            try
            {
                if (instance.panelOpen) { instance.ClosePanel(); return false; }
                if (instance.KeysBlocked(out _) || MenuOpen()) return true;
                if (instance.mode != CamMode.Free) { instance.SetMode(CamMode.Free, "Escape"); return false; }
            }
            catch (Exception error) { instance.Error(error); }
            return true;
        }
    }
    private static bool MenuOpen()
    {
        try { var m = Il2CppSilica.UI.Menus.MenuManager.Instance; return m != null && m.GetCurrent() != null; } catch { return false; }
    }

    private static bool NativeTextInput() => UIManager.TextInputActive || DebugConsole.IsOpen;
    // Our keys are ignored while text is typed (chat, console), in the pause menu or main menu, and while the Si_ModUI
    // panel is open. ModUI is detected by Game.GetCameraEnabled() returning false while our own panel is closed: ModUI's
    // client patches exactly that method while its panel is up (and vanilla only returns false during a benchmark).
    private bool KeysBlocked(out string why)
    {
        why = NativeTextInput() ? "text input" : Game.IsPauseMenuActive ? "pause menu" : Game.InMainMenu ? "main menu"
            : !panelOpen && !Game.GetCameraEnabled() ? "ModUI panel" : null;
        return why != null;
    }
    private static bool Pressed(Keyboard keyboard, Key key)
    {
        if (key == Key.None) return false;
        var control = keyboard[key];
        return control != null && control.wasPressedThisFrame;
    }
    private void HandleKeys()
    {
        if (!Application.isFocused) return;
        var keyboard = Keyboard.current;
        if (keyboard == null || KeysBlocked(out _)) return;
        if (Pressed(keyboard, kPanel)) { if (panelOpen) ClosePanel(); else OpenPanel(); }
        if (Pressed(keyboard, kHide)) { hideOverlays = !hideOverlays; if (hideOverlays && panelOpen) ClosePanel(); Write("Overlays " + (hideOverlays ? "hidden" : "shown") + "."); }
        if (Pressed(keyboard, kTags)) { tagsOn = !tagsOn; Write("Name tags " + (tagsOn ? "on" : "off") + "."); }
        if (Pressed(keyboard, kCamera)) CycleMode();
        if (Pressed(keyboard, kFog)) CycleFog();
        if (Pressed(keyboard, kNext)) StepTarget(+1);
        if (Pressed(keyboard, kPrev)) StepTarget(-1);
        if (Pressed(keyboard, kFollow)) FollowSelected("key " + kFollow);
        if (!panelOpen && clickPicking.Value && !Cursor.visible)
        {
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) ClickPick();
        }
    }
}
