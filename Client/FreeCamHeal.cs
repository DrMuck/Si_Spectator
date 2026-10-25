using Il2Cpp;
using Il2CppSilica.UI;
using Il2CppSilica.UI.Menus;
using UnityEngine;

namespace SiSpectator;

// Free cam self-heal. Live test 2: spectating straight from the commander seat left the free cam dead ("FreeFly active
// False, cursor visible"). FreeFlyCameraController refuses while StrategyMode.InStrategyMode (FreeFlyCameraController.cs
// OrderedLateUpdate), and the client only leaves RTS mode in MP_Strategy.SetCommander when CurrentPlayer.Team still
// equals the seat's team (106:1589-1596) - after the team was set to null first, it stays on. CursorManager also keeps
// the cursor visible while InStrategyMode (CursorManager.DoesGameRequireCursor).
//   - InStrategyMode while spectating: StrategyMode.ToggleStrategyMode(false). Safe: it only sends
//     SendPlayerSelectUnit when ENABLING with a unit (StrategyMode.cs 0947:562-572), and DirectControl(null) (which
//     sends) only runs when InDirectControl - in that case we do not call it and only log.
//   - Cursor visible with no menu, no text input, no pause / main menu, our panel and the large map closed, cameras
//     enabled and the game mode not asking for a cursor: lock and hide it.
// Runs every LateUpdate while spectating (cheap checks), each heal logged once per entry. A "Free cam blocked:" line is
// logged once per entry when Free mode has had no active free-fly controller for more than 1 s.
public sealed partial class ClientMod
{
    private bool healStrategyLogged, healCursorLogged, freeBlockedLogged, lastCameraEnabled = true;
    private double freeBlockedSince = -1;

    private void ResetFreeCamHeal() { healStrategyLogged = healCursorLogged = freeBlockedLogged = false; freeBlockedSince = -1; lastCameraEnabled = true; }

    private void FreeCamLateUpdate()
    {
        if (!Active) return;
        bool cameraEnabled = Game.GetCameraEnabled();
        if (cameraEnabled && !lastCameraEnabled) Write("ModUI panel closed: free cam self-heal check.");
        lastCameraEnabled = cameraEnabled;
        HealFreeCam(cameraEnabled);
        var free = FreeFlyCameraController.Instance;
        bool quiet = !panelOpen && !LargeMapOpen() && !KeysBlocked(out _);
        if (mode == CamMode.Free && free != null && !free.IsActive && quiet)
        {
            if (freeBlockedSince < 0) freeBlockedSince = Now;
            else if (Now - freeBlockedSince > 1 && !freeBlockedLogged) { freeBlockedLogged = true; Write("Free cam blocked: " + FreeCamState()); }
        }
        else freeBlockedSince = -1;
    }
    private void HealFreeCam(bool cameraEnabled)
    {
        try
        {
            if (StrategyMode.InStrategyMode)
            {
                if (!StrategyMode.InDirectControl)
                {
                    StrategyMode.ToggleStrategyMode(false, false);
                    if (!healStrategyLogged) { healStrategyLogged = true; Write("Self-heal: left the RTS (strategy) mode the commander seat left behind; now InStrategyMode " + StrategyMode.InStrategyMode + "."); }
                }
                else if (!healStrategyLogged) { healStrategyLogged = true; Write("Self-heal skipped: InStrategyMode with a direct-control unit (leaving it would send a unit request)."); }
            }
        }
        catch (Exception error) { Error(error); }
        try
        {
            if (!Cursor.visible || !cameraEnabled || panelOpen || !Application.isFocused) return;
            if (NativeTextInput() || Game.IsPauseMenuActive || Game.InMainMenu || LargeMapOpen() || StrategyMode.InStrategyMode) return;
            var menu = MenuManager.Instance == null ? null : MenuManager.Instance.GetCurrent();
            if (menu != null) return;
            if (GameMode.CurrentGameMode != null && GameMode.CurrentGameMode.GetShowCursor()) return;
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
            if (!healCursorLogged) { healCursorLogged = true; Write("Self-heal: cursor was visible with no menu open; locked and hidden."); }
        }
        catch (Exception error) { Error(error); }
    }
    private string FreeCamState()
    {
        var parts = new List<string>();
        void Add(string name, Func<string> value) { try { parts.Add(name + "=" + value()); } catch (Exception e) { parts.Add(name + "=<" + e.GetType().Name + ">"); } }
        Add("InStrategyMode", () => StrategyMode.InStrategyMode.ToString());
        Add("InDirectControl", () => StrategyMode.InDirectControl.ToString());
        Add("CurrentUnit", () => Unit.CurrentUnit == null ? "null" : UnitName(Unit.CurrentUnit));
        Add("Cinematic", () => CinematicCamera.IsPlaying.ToString());
        Add("CameraEnabled", () => Game.GetCameraEnabled().ToString());
        Add("ControlsEnabled", () => Game.GetControlsEnabled().ToString());
        Add("GameModeShowCursor", () => GameMode.CurrentGameMode == null ? "no mode" : GameMode.CurrentGameMode.GetShowCursor().ToString());
        Add("Cursor", () => (Cursor.visible ? "visible" : "hidden") + "/" + Cursor.lockState);
        Add("Menu", () => { var m = MenuManager.Instance == null ? null : MenuManager.Instance.GetCurrent(); return m == null ? "none" : m.GetIl2CppType().Name; });
        Add("TextInput", () => UIManager.TextInputActive.ToString());
        Add("UIMode", () => UIManager.Instance == null ? "no UIManager" : UIManager.Instance.CurrentMode.ToString());
        Add("EnableMovement", () => FreeFlyCameraController.Instance == null ? "no FreeFly" : FreeFlyCameraController.Instance.EnableMovement.ToString());
        return string.Join(", ", parts);
    }
}
