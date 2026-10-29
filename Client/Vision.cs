using Il2Cpp;
using UnityEngine;

namespace SiSpectator;

// Vision modes (thermal / alien / chroma) for the spectator. Purely local rendering, no packets, no Unit.CurrentUnit.
//   VisionModeManager.CycleVisionModes(Unit forUnit) (VisionModeManager.cs:30, both branches) cycles VisionMode.VisionModes
//   skipping modes where !mode.IsCurrentlyValid(forUnit) (VisionMode.cs:66): false without WorldLight.AllowVisionModes,
//   true for a null unit, else unit.GetVisionModeValid(mode) = SupportedVisionModeTypes.Contains(type)
//   (Unit.cs 0947:64/1047, 106:64/1074). The vanilla Q handler only acts with Unit.CurrentUnit set, and
//   ValidateVisionMode(null) keeps any mode, so our choice is not undone while spectating.
// Key KeyVision (Q): following a unit -> that unit's modes (exactly the player's own Q); Free / Overview without a
// target -> all modes + off. On a target change a mode the new unit does not support is switched off; on leave / team
// join / scene load the mode is reset to off (SelectVisionMode(null, null)).
// Camera roll: the free-fly reads "Camera Roll" (Q / E, Freecam map, FreeFlyCameraController.cs:147) inside
// UpdateCamera. Patching the generic GameInput.ReadValue<float> is not reliable in Il2Cpp, so in Free mode our
// OrderedLateUpdate postfix strips the roll afterwards: CurrentRoll = 0, TargetRot / camera rotation / the
// stick-to-object ReferenceLocalRot keep yaw and pitch with roll 0. Net effect: Q / E never roll the free cam.
public sealed partial class ClientMod
{
    private static string VisionLabel(VisionMode mode)
    {
        if (mode == null) return "off";
        try
        {
            return mode.VisionModeType switch
            {
                EVisionModeTypes.COMBAT_VISION => "Thermal",
                EVisionModeTypes.ALIEN_VISION => "Alien",
                EVisionModeTypes.CHROMA_VISION => "Chroma",
                _ => mode.VisionModeType.ToString(),
            };
        }
        catch { return "?"; }
    }
    private static VisionMode ShownVisionMode()
    {
        try
        {
            var m = VisionModeManager.Instance;
            if (m == null) return null;
            return m.TargetVisionMode ?? m.CurrentVisionMode;
        }
        catch { return null; }
    }
    private string VisionStatus() => VisionLabel(ShownVisionMode());

    private void CycleVision(string why)
    {
        try
        {
            if (VisionModeManager.Instance == null) { Write("Vision: no VisionModeManager in this scene."); return; }
            Unit forUnit = IsFollowMode(mode) || mode == CamMode.Fixed ? TargetUnit() : null;
            if (forUnit != null)
            {
                var supported = forUnit.SupportedVisionModeTypes;
                if (supported == null || supported.Count == 0) { Write("Vision: " + UnitName(forUnit) + " has no vision modes."); return; }
            }
            var next = VisionModeManager.CycleVisionModes(forUnit);
            Write("Vision mode: " + VisionLabel(next) + " (" + why + (forUnit != null ? ", modes of " + UnitName(forUnit) : ", all modes") + ").");
        }
        catch (Exception error) { Error(error); }
    }
    // After a target change: switch off a mode the new unit does not support (no auto-on).
    private void VisionTargetChanged()
    {
        try
        {
            var current = ShownVisionMode();
            var unit = TargetUnit();
            if (current == null || unit == null) return;
            if (current.IsCurrentlyValid(unit)) return;
            VisionModeManager.SelectVisionMode(null, null);
            Write("Vision mode: off (" + UnitName(unit) + " does not support " + VisionLabel(current) + ").");
        }
        catch (Exception error) { Error(error); }
    }
    private void ResetVision(string why)
    {
        try
        {
            if (ShownVisionMode() == null) return;
            VisionModeManager.SelectVisionMode(null, null);
            Write("Vision mode: off (" + why + ").");
        }
        catch { }
    }
    // Free mode only: remove whatever roll the free-fly applied this frame (Q / E are ours / unused while spectating).
    private void StripFreeRoll(FreeFlyCameraController free)
    {
        if (free == null || !free.IsActive) return;
        if (free.CurrentRoll == 0f) return;
        free.CurrentRoll = 0f;
        Quaternion NoRoll(Quaternion q) { var e = q.eulerAngles; return Quaternion.Euler(e.x, e.y, 0f); }
        free.TargetRot = NoRoll(free.TargetRot);
        free.transform.rotation = NoRoll(free.transform.rotation);
        if (free.ReferenceTransform != null) free.ReferenceLocalRot = NoRoll(free.ReferenceLocalRot);
    }
}
