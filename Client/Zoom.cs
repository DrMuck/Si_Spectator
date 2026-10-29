using HarmonyLib;
using Il2Cpp;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SiSpectator;

// Scroll-wheel zoom in the follow views (Auto / FollowFps / Shoulder), like the player's own ZoomManager.
//   Unit.ZoomLevels (Unit.cs:68, both branches; InitZoom 0947:1138 / 106:1165 inserts 1 at index 0) are FOV multipliers;
//   the vanilla ZoomManager.ZoomBy (ZoomManager.cs:93) only serves Unit.CurrentUnit, so this is our own copy: clamped
//   index (no wrap), wheel up = +1 = next level (the "Zoom" composite: scroll up positive), lerp at dt*20, zoom sound
//   ObjectInfo.ZoomInSFX / ZoomOutSFX (0947:110/113, 106:128/131) with pitch clamp(index/count*0.15+1, 1, 10), local.
// FOV route: in our follow views the free-fly controller is NOT active (EnableMovement = false -> Deactivate), so
// Game.UpdateCameraFOV (0947:655 / 106:689, called every frame from GameManager) would set CameraDefaultFOV (60).
// A prefix on Game.UpdateCameraFOV sets Game.CurrentCamera.fieldOfView = base * level (lerped) and skips the original
// while a follow view with a target is active; base = GameSettings.m_VideoSettings.m_FPSCameraFOV (VideoSettings.cs:29;
// 60 if unavailable), i.e. the follow views now use the player's FPS FOV setting even at level 0 (no jump when the first
// zoom step starts). Outside the follow views the prefix is inert and vanilla restores its FOV on the next frame.
// Near clip is a separate camera property, unaffected.
public sealed partial class ClientMod
{
    private int zoomIndex; private float zoomFov = -1; private IntPtr zoomUnit; private bool zoomSingleLogged;

    private bool ZoomView(out Unit unit)
    {
        unit = null;
        if (!Active || !(mode == CamMode.Auto || mode == CamMode.FollowFps || mode == CamMode.Shoulder)) return false;
        unit = TargetUnit();
        return unit != null;
    }
    private static float BaseFov()
    {
        try { var s = GameSettings.Instance; if (s != null && s.m_VideoSettings != null && s.m_VideoSettings.m_FPSCameraFOV > 1f) return s.m_VideoSettings.m_FPSCameraFOV; } catch { }
        return 60f;
    }
    private float ZoomLevel(Unit unit)
    {
        try
        {
            var levels = unit.ZoomLevels;
            if (levels == null || levels.Count == 0) return 1f;
            float l = levels[Math.Clamp(zoomIndex, 0, levels.Count - 1)];
            return l > 0.01f ? l : 1f;
        }
        catch { return 1f; }
    }
    // Reset when the unit / player changes or the view leaves the follow modes.
    private void ZoomTrack()
    {
        if (!ZoomView(out var unit)) { if (zoomIndex != 0 || zoomUnit != IntPtr.Zero) { zoomIndex = 0; zoomUnit = IntPtr.Zero; } zoomFov = -1; return; }
        if (unit.Pointer != zoomUnit) { zoomUnit = unit.Pointer; zoomIndex = 0; zoomSingleLogged = false; }
    }
    // From HandleKeys (map closed, keys not blocked): the wheel steps through the followed unit's levels.
    private void ZoomInput()
    {
        ZoomTrack();
        if (!ZoomView(out var unit) || !GameInputAllowed()) return;
        var mouse = Mouse.current;
        if (mouse == null) return;
        float notches = ScrollNotches(mouse);
        if (notches == 0) return;
        int diff = notches > 0 ? 1 : -1;
        var levels = unit.ZoomLevels;
        int count = levels == null ? 0 : levels.Count;
        if (count <= 1) { if (!zoomSingleLogged) { zoomSingleLogged = true; Write("Zoom: " + UnitName(unit) + " has a single zoom level."); } return; }
        int next = Math.Clamp(zoomIndex + diff, 0, count - 1);
        if (next == zoomIndex) return;
        zoomIndex = next;
        try
        {
            var info = unit.ObjectInfo;
            var sfx = info == null ? null : diff > 0 ? info.ZoomInSFX : info.ZoomOutSFX;
            float pitch = Mathf.Clamp((float)next / count * 0.15f + 1f, 1f, 10f);
            if (sfx != null) AudioEffectManager.RequestAudioEffect(sfx, Vector3.zero, null, null, 1f, pitch, 1f, false, null, false);
        }
        catch (Exception error) { Error(error); }
        Write("Zoom: " + UnitName(unit) + " level " + next + "/" + (count - 1) + " (x" + ZoomFactorText(unit) + ").");
    }
    private string ZoomFactorText(Unit unit) => (1f / ZoomLevel(unit)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    private string ZoomHudSuffix(Unit unit) => zoomIndex > 0 && ZoomView(out var u) && u.Pointer == unit.Pointer ? "   zoom ×" + ZoomFactorText(unit) : "";
    private void ZoomReset() { zoomIndex = 0; zoomUnit = IntPtr.Zero; zoomFov = -1; try { Game.UpdateCameraFOV(true); } catch { } }

    [HarmonyPatch(typeof(Game), nameof(Game.UpdateCameraFOV))]
    private static class FovOverride
    {
        private static bool Prefix()
        {
            if (instance == null || !Active) return true;
            try
            {
                if (!instance.ZoomView(out var unit)) return true;
                var cam = Game.CurrentCamera;
                if (cam == null) return true;
                float wanted = BaseFov() * instance.ZoomLevel(unit);
                if (instance.zoomFov < 0) instance.zoomFov = cam.fieldOfView;
                instance.zoomFov = Mathf.Lerp(instance.zoomFov, wanted, Mathf.Clamp01(Time.unscaledDeltaTime * 20f));
                if (Math.Abs(instance.zoomFov - wanted) < 0.01f) instance.zoomFov = wanted;
                if (cam.fieldOfView != instance.zoomFov) cam.fieldOfView = instance.zoomFov;
                return false;
            }
            catch (Exception error) { instance.Error(error); return true; }
        }
    }
}
