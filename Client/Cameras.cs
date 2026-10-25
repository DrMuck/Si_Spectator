using HarmonyLib;
using Il2Cpp;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SiSpectator;

// Camera modes. Free = the vanilla FreeFlyCameraController, which turns itself on for a player with no unit, no RTS mode and
// a locked cursor (API_NOTES §5). Every other mode sets EnableMovement = false (FreeFly then only copies the pose into its
// targets each frame) and places Game.CurrentCamera from a postfix on FreeFlyCameraController.OrderedLateUpdate: after the
// soldier animation and camera updates (priority 100), before culling and the crosshair (API_NOTES §6). Soft follow only:
// no unit ownership, no Unit.CurrentUnit, no StrategyMode, nothing sent.
public sealed partial class ClientMod
{
    // Auto = the followed player's own view as far as the game replicates it (see AutoPose).
    internal enum CamMode { Free, Auto, FollowFps, Shoulder, Orbit, Overview, Fixed }
    private static readonly CamMode[] ModeOrder = { CamMode.Free, CamMode.Auto, CamMode.FollowFps, CamMode.Shoulder, CamMode.Orbit, CamMode.Overview, CamMode.Fixed };
    private CamMode mode = CamMode.Free, lastFollow = CamMode.Auto;
    private bool movementTouched, nearTouched;
    private float savedNear = -1;
    private int lastDriveFrame = -1;
    private bool haveRot; private Quaternion smoothRot = Quaternion.identity; private Vector3 smoothPos;
    // orbit
    private float orbitYaw, orbitPitch = 15f, orbitDistance = 8f;
    // overview (StrategyMode formula: CameraPitch = Lerp(10, 75, pct^0.4), height 4..300 m, StrategyMode.cs:127-133 / 410-412 / 779-784)
    private const float OverviewMinHeight = 4f, OverviewMaxHeight = 300f, OverviewPitchPower = 0.4f;
    private float overviewPct = .5f, overviewYaw;
    private Vector3 overviewFocus; private bool overviewTracks;
    private string cameraNote = "";

    [HarmonyPatch(typeof(FreeFlyCameraController), nameof(FreeFlyCameraController.OrderedLateUpdate))]
    private static class CameraDrive
    {
        private static void Prefix(FreeFlyCameraController __instance)
        {
            if (!Active || __instance == null) return;
            try
            {
                bool free = instance.mode == CamMode.Free;
                if (__instance.EnableMovement != free) { __instance.EnableMovement = free; instance.movementTouched = true; }
            }
            catch (Exception error) { instance.Error(error); }
        }
        private static void Postfix(FreeFlyCameraController __instance)
        {
            if (!Active) return;
            try { instance.DriveCamera(__instance); } catch (Exception error) { instance.Error(error); }
        }
    }
    // Fallback when no free-fly controller ticks (none in the scene, or disabled): drive from MelonLoader's LateUpdate.
    private void CameraFallback()
    {
        // the postfix ran this frame or the last one: the controller ticks, no second drive (it would double the smoothing)
        if (!Active || lastDriveFrame >= Time.frameCount - 1) return;
        try { DriveCamera(null); } catch (Exception error) { Error(error); }
    }

    private void EnterCameras()
    {
        mode = CamMode.Free; haveRot = false; cameraNote = ""; overviewTracks = false;
        var free = FreeFlyCameraController.Instance;
        if (free != null && !free.EnableMovement) { free.EnableMovement = true; movementTouched = true; }
    }
    private void LeaveCameras()
    {
        RestoreNearClip();
        var free = FreeFlyCameraController.Instance;
        if (movementTouched && free != null) free.EnableMovement = true;
        movementTouched = false;
        mode = CamMode.Free; haveRot = false;
    }
    private string CameraDiagnostics()
    {
        try
        {
            var cam = Game.CurrentCamera; var free = FreeFlyCameraController.Instance;
            if (cam == null) return "No Game.CurrentCamera yet.";
            if (free == null) return "No FreeFlyCameraController in this scene: Free mode is unavailable, our modes drive the camera from LateUpdate.";
            bool same = free.transform.Pointer == cam.transform.Pointer;
            bool sameCam = free.Camera != null && free.Camera.Pointer == cam.Pointer;
            return "Runtime check 1: FreeFly.transform " + (same ? "IS" : "is NOT") + " Game.CurrentCamera.transform, FreeFly.Camera " + (sameCam ? "IS" : "is NOT")
                + " the current camera (FreeFly active " + free.IsActive + ", EnableMovement " + free.EnableMovement + ", cursor " + (Cursor.visible ? "visible" : "hidden") + ").";
        }
        catch (Exception error) { return "camera diagnostics failed: " + error.Message; }
    }

    private void CycleMode()
    {
        int i = Array.IndexOf(ModeOrder, mode);
        for (int step = 1; step <= ModeOrder.Length; step++)
        {
            var next = ModeOrder[(i + step) % ModeOrder.Length];
            if (IsFollowMode(next) && !HasTarget && !PickFirstTarget()) continue;   // nobody to follow: skip follow modes
            SetMode(next, "key " + kCamera);
            return;
        }
    }
    private static bool IsFollowMode(CamMode m) => m == CamMode.Auto || m == CamMode.FollowFps || m == CamMode.Shoulder || m == CamMode.Orbit;
    internal void SetMode(CamMode next, string why)
    {
        if (next == mode) return;
        var cam = Game.CurrentCamera;
        if (mode == CamMode.FollowFps || mode == CamMode.Auto) RestoreNearClip();
        haveRot = false;
        if (cam != null)
        {
            var e = cam.transform.rotation.eulerAngles;
            if (next == CamMode.Orbit) { orbitYaw = e.y; orbitPitch = 15f; orbitDistance = DefaultOrbitDistance(TargetUnit()); }
            if (next == CamMode.Overview) EnterOverview(cam);
            if (next == CamMode.FollowFps) ApplyNearClip(cam);
        }
        if (IsFollowMode(next)) lastFollow = next;
        mode = next; cameraNote = "";
        Write("Camera " + next + " (" + why + ")" + (HasTarget ? ", target " + TargetDesc() : "") + ".");
    }
    private void ApplyNearClip(Camera cam)
    {
        if (cam == null) return;
        if (!nearTouched) { savedNear = cam.nearClipPlane; nearTouched = true; }
        float wanted = Math.Max(savedNear, Math.Clamp(fpsNearClip.Value, 0.01f, 2f));
        if (cam.nearClipPlane != wanted) cam.nearClipPlane = wanted;
    }
    private void RestoreNearClip()
    {
        if (!nearTouched) return;
        nearTouched = false;
        try { var cam = Game.CurrentCamera; if (cam != null && savedNear > 0) cam.nearClipPlane = savedNear; } catch { }
    }

    private void DriveCamera(FreeFlyCameraController free)
    {
        lastDriveFrame = Time.frameCount;
        if (mode == CamMode.Free || mode == CamMode.Fixed) return;   // Fixed: EnableMovement is off, nothing moves the camera
        if (!Game.GetCameraEnabled()) return;                       // Si_ModUI panel open: freeze like the vanilla cameras
        if (LargeMapOpen()) return;                                 // large map open: the camera keeps still
        var cam = Game.CurrentCamera;
        if (cam == null) return;
        float dt = Math.Clamp(Time.unscaledDeltaTime, 0f, .1f);
        Vector3 pos; Quaternion rot;
        bool placed = mode switch
        {
            CamMode.Auto => AutoPose(dt, cam, out pos, out rot),
            CamMode.FollowFps => FollowPose(dt, false, out pos, out rot),
            CamMode.Shoulder => FollowPose(dt, true, out pos, out rot),
            CamMode.Orbit => OrbitPose(dt, out pos, out rot),
            CamMode.Overview => OverviewPose(dt, out pos, out rot),
            _ => Hold(out pos, out rot),
        };
        if (!placed) return;   // target dead / respawning: hold the last pose
        if (!IsFinite(pos) || !IsFinite(rot)) return;
        cam.transform.SetPositionAndRotation(pos, rot);
        if (free != null && free.transform.Pointer == cam.transform.Pointer) { free.TargetPos = pos; free.TargetRot = rot; }   // seamless hand-back to Free
    }
    private static bool Hold(out Vector3 pos, out Quaternion rot) { pos = Vector3.zero; rot = Quaternion.identity; return false; }
    private static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    private static bool IsFinite(Quaternion q) => float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w) && (q.x != 0 || q.y != 0 || q.z != 0 || q.w != 0);
    private Quaternion Smooth(Quaternion wanted, float dt)
    {
        float k = Smoothing;
        if (!haveRot || k <= 0) { haveRot = true; smoothRot = wanted; return wanted; }
        smoothRot = Quaternion.Slerp(smoothRot, wanted, 1f - Mathf.Exp(-k * dt));
        return smoothRot;
    }

    // ---- follow (FollowFps / Shoulder) ----
    private bool FollowPose(float dt, bool shoulder, out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero; rot = Quaternion.identity;
        var unit = TargetUnit();
        if (unit == null) { cameraNote = WaitingNote(); return false; }
        cameraNote = "";
        // Driver: ControlledUnit is the vehicle. Gunner / passenger: the soldier stays ControlledUnit, the view is the
        // compartment owner's camera (UnitCompartment.cs:416-446, Soldier.cs:2061-2066).
        Unit carrier = unit.TryCast<Vehicle>() != null ? unit : CompartmentOwner(unit);
        if (carrier != null && carrier.Camera != null)
        {
            var vehicleCam = carrier.Camera.TryCast<VehicleCameraController>();
            if (vehicleCam != null) return VehiclePose(unit, carrier, vehicleCam, dt, out pos, out rot);
            pos = carrier.Camera.transform.position;   // plain seat camera (turret): sit on its anchor
            rot = Smooth(LookRotation(AimDirection(unit, pos)), dt);
            return true;
        }
        Vector3 anchor = EyeAnchor(unit, out bool soldierEye);
        Vector3 dir = AimDirection(unit, anchor);
        rot = Smooth(LookRotation(dir), dt);
        if (!shoulder) { pos = anchor; return true; }
        // the game's own third person: CameraReference.position + camera.TransformVector(0.5, -0.1, -1.5) (Soldier.cs:2081)
        Vector3 basePos = soldierEye ? HeadAnchor(unit) : anchor;
        Vector3 wanted = basePos + rot * new Vector3(.5f, -.1f, -1.5f);
        pos = Collide(basePos, wanted, .2f);
        return true;
    }
    // ---- auto (the player's own view) ----
    // Soldier: the first / third person choice is m_bTarget3rdPerson, toggled locally by "Camera Cycle"
    //   (Soldier.cs 0947:1722-1726 / 106:1770-1774) and NOT part of OnSendNetUpdate / OnSendNetInit (0947:773-863,
    //   106 same): a remote client cannot know it. Auto uses what a fresh player sees: first person
    //   (m_bTarget3rdPerson defaults to false, 0947:381 / 106:389).
    // Alien (CreatureDecapod): the owner sends Is3rdPerson + m_Camera3rdAngles + m_CameraLocalPosition in every
    //   ObjectUpdate (0947:940-950 / 106:985-995) and other clients store them (0947:1017-1026 / 106:1062-1071), with
    //   m_CameraLocalPosition = zero for first person. Auto shows exactly that view: CameraReference + local offset,
    //   rotation Euler(m_Camera3rdAngles.y + freelook.y, m_Camera3rdAngles.x + freelook.x, 0) like UpdateCamera.
    // Vehicles, gunners, passengers: the vehicle orbit (no first person choice there).
    private bool AutoPose(float dt, Camera cam, out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero; rot = Quaternion.identity;
        var unit = TargetUnit();
        if (unit == null) { cameraNote = WaitingNote(); RestoreNearClip(); return false; }
        bool carried = unit.TryCast<Vehicle>() != null || CompartmentOwner(unit) != null;
        var creature = carried ? null : unit.TryCast<CreatureDecapod>();
        if (creature != null && creature.CameraReference != null)
        {
            Vector3 local = creature.m_CameraLocalPosition;
            if (local != Vector3.zero)
            {
                RestoreNearClip();
                Vector2 a = creature.m_Camera3rdAngles, free = creature.m_FreeLookAngles;
                pos = creature.CameraReference.transform.position + local;
                rot = Smooth(Quaternion.Euler(Mathf.Clamp(a.y + free.y, -90f, 90f), a.x + free.x, 0f), dt);
                cameraNote = "view: player's third person";
                return true;
            }
            ApplyNearClip(cam);
            bool ok = FollowPose(dt, false, out pos, out rot);
            cameraNote = "view: player's first person";
            return ok;
        }
        if (carried) { RestoreNearClip(); bool v = FollowPose(dt, false, out pos, out rot); cameraNote = v ? "view: vehicle" : cameraNote; return v; }
        ApplyNearClip(cam);
        bool placed = FollowPose(dt, false, out pos, out rot);
        if (placed) cameraNote = unit.TryCast<Soldier>() != null ? "view: default first person, player's choice not networked" : "view: default";
        return placed;
    }

    private static Unit CompartmentOwner(Unit unit)
    {
        var compartment = unit.InCompartment;
        return compartment == null ? null : compartment.OwnerUnit;
    }
    // First-person eye: soldier CameraReference.TransformPoint(HumanHandsAnimator.WeaponViewOffset) (Soldier.cs:2082),
    // alien CameraReference (CreatureDecapod.cs:2361), else the unit camera anchor, else the top of the bounds.
    private static Vector3 EyeAnchor(Unit unit, out bool soldierEye)
    {
        soldierEye = false;
        var soldier = unit.TryCast<Soldier>();
        if (soldier != null && soldier.CameraReference != null)
        {
            soldierEye = true;
            var hands = soldier.HumanHandsAnimator;
            return soldier.CameraReference.transform.TransformPoint(hands != null ? hands.WeaponViewOffset : Vector3.zero);
        }
        var creature = unit.TryCast<CreatureDecapod>();
        if (creature != null && creature.CameraReference != null) return creature.CameraReference.transform.position;
        if (unit.Camera != null) return unit.Camera.transform.position;
        return unit.WorldPhysicalCenter + new Vector3(0, unit.PhysicalBounds.extents.y, 0);
    }
    private static Vector3 HeadAnchor(Unit unit)
    {
        var soldier = unit.TryCast<Soldier>();
        return soldier != null && soldier.CameraReference != null ? soldier.CameraReference.transform.position : EyeAnchor(unit, out _);
    }
    // The followed player's crosshair point: UnitAimAt.AimAtPosition is computed by the owner from its camera ray and
    // replicated at full precision (UnitAimAt.cs:90-126). Fallback: the networked view direction.
    private static Vector3 AimDirection(Unit unit, Vector3 from)
    {
        try
        {
            var aim = unit.UnitAimAt;
            if (aim != null)
            {
                Vector3 d = aim.AimAtPosition - from;
                if (aim.AimAtPosition != Vector3.zero && d.sqrMagnitude > .25f) return d;
            }
        }
        catch { }
        Vector3 view = unit.GetViewDirection();
        return view.sqrMagnitude > 1e-4f ? view : unit.transform.forward;
    }
    private static Quaternion LookRotation(Vector3 dir) => dir.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(dir.normalized, Vector3.up);
    // VehicleCameraController orbit from its public fields (VehicleCameraController.cs:60-155): pivot = anchor +
    // (LocalTransform ?? anchor).TransformVector(LocalOffset) + WorldOffset, camera DistanceMax back along the view,
    // shortened by a sphere cast. View rotation from the aimer's UnitAimAt (driver = the vehicle, gunner = the soldier).
    private bool VehiclePose(Unit aimer, Unit carrier, VehicleCameraController vc, float dt, out Vector3 pos, out Quaternion rot)
    {
        var anchor = vc.transform;
        var local = vc.LocalTransform != null ? vc.LocalTransform : anchor;
        Vector3 pivot = anchor.position + local.TransformVector(vc.LocalOffset) + vc.WorldOffset;
        Vector3 aimFrom = pivot;
        Vector3 dir = AimDirection(aimer, aimFrom);
        if (aimer.Pointer != carrier.Pointer && (aimer.UnitAimAt == null || aimer.UnitAimAt.AimAtPosition == Vector3.zero)) dir = AimDirection(carrier, aimFrom);
        rot = Smooth(LookRotation(dir), dt);
        float distance = Math.Max(vc.DistanceMin, vc.DistanceMax);
        pos = Collide(pivot, pivot + rot * Vector3.back * distance, Math.Max(.1f, vc.CollisionRadius), vc.DistanceMin);
        return true;
    }
    // Sphere cast from the pivot to the wanted camera position; the camera stops in front of the first hit (terrain,
    // structures; units and debris ignored like the game's own cameras).
    private static Vector3 Collide(Vector3 from, Vector3 to, float radius, float minDistance = 0f)
    {
        Vector3 delta = to - from; float length = delta.magnitude;
        if (length < 1e-3f) return to;
        try
        {
            if (Physics.SphereCast(from, radius, delta / length, out RaycastHit hit, length, GamePhysics.TRACELAYERMASK_IGNOREDEBRISANDUNITS, QueryTriggerInteraction.Ignore))
                return from + delta / length * Math.Max(minDistance, hit.distance);
        }
        catch { }
        return to;
    }

    // ---- orbit ----
    private static float DefaultOrbitDistance(Unit unit)
    {
        if (unit == null) return 8f;
        try { return Math.Clamp(unit.PhysicalBounds.extents.magnitude * 3.5f, 4f, 40f); } catch { return 8f; }
    }
    private bool OrbitPose(float dt, out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero; rot = Quaternion.identity;
        var unit = TargetUnit();
        if (unit == null) { cameraNote = WaitingNote(); return false; }
        cameraNote = "";
        if (GameInputAllowed())
        {
            var mouse = Mouse.current;
            if (mouse != null)
            {
                Vector2 delta = mouse.delta.ReadValue();
                float s = Math.Clamp(orbitSensitivity.Value, .01f, 2f);
                orbitYaw += delta.x * s; orbitPitch = Math.Clamp(orbitPitch - delta.y * s, -80f, 85f);
                float notches = ScrollNotches(mouse);
                if (notches != 0) orbitDistance = Math.Clamp(orbitDistance * Mathf.Pow(.88f, notches), 2f, 120f);
            }
        }
        Vector3 pivot = unit.WorldPhysicalCenter;
        smoothPos = haveRot ? Vector3.Lerp(smoothPos, pivot, 1f - Mathf.Exp(-20f * dt)) : pivot;
        rot = Quaternion.Euler(orbitPitch, orbitYaw, 0f); haveRot = true;
        pos = Collide(smoothPos, smoothPos + rot * Vector3.back * orbitDistance, .3f, 1f);
        return true;
    }
    private static float ScrollNotches(Mouse mouse)
    {
        float y = mouse.scroll.ReadValue().y;
        if (Math.Abs(y) < 1e-3f) return 0;
        return Math.Abs(y) >= 20f ? y / 120f : y;   // Windows reports 120 per notch on older Input System versions, 1 on newer
    }
    // Mouse / keyboard steer our cameras only with the cursor locked and nothing typed (our panel frees the cursor).
    private bool GameInputAllowed() => Application.isFocused && !Cursor.visible && !panelOpen && !KeysBlocked(out _);

    // ---- overview ----
    private void EnterOverview(Camera cam)
    {
        var unit = TargetUnit();
        overviewYaw = cam.transform.rotation.eulerAngles.y;
        overviewTracks = unit != null || CommanderHq(target) != null;
        if (unit != null) overviewFocus = unit.transform.position;
        else if (CommanderHq(target) is Structure hq) overviewFocus = hq.transform.position;
        else
        {
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            overviewFocus = Physics.Raycast(ray, out RaycastHit hit, 2000f, GamePhysics.TRACELAYERMASK_IGNOREDEBRISANDUNITS, QueryTriggerInteraction.Ignore) ? hit.point : cam.transform.position;
        }
    }
    private bool OverviewPose(float dt, out Vector3 pos, out Quaternion rot)
    {
        if (overviewTracks)
        {
            var unit = TargetUnit();
            if (unit != null) overviewFocus = unit.transform.position;
            else if (CommanderHq(target) is Structure hq) overviewFocus = hq.transform.position;
        }
        if (GameInputAllowed())
        {
            var keyboard = Keyboard.current; var mouse = Mouse.current;
            if (mouse != null)
            {
                float notches = ScrollNotches(mouse);
                if (notches != 0) overviewPct = Math.Clamp(overviewPct - notches * .05f, 0f, 1f);
            }
            if (keyboard != null)
            {
                Vector2 move = Vector2.zero;
                if (keyboard.wKey.isPressed) move.y += 1; if (keyboard.sKey.isPressed) move.y -= 1;
                if (keyboard.dKey.isPressed) move.x += 1; if (keyboard.aKey.isPressed) move.x -= 1;
                if (keyboard.qKey.isPressed) overviewYaw -= 90f * dt; if (keyboard.eKey.isPressed) overviewYaw += 90f * dt;
                if (keyboard.spaceKey.isPressed) overviewPct = Math.Clamp(overviewPct + .6f * dt, 0f, 1f);
                if (keyboard.leftCtrlKey.isPressed) overviewPct = Math.Clamp(overviewPct - .6f * dt, 0f, 1f);
                if (move != Vector2.zero)
                {
                    overviewTracks = false;   // panning detaches from the target until it is selected again
                    var yawRot = Quaternion.Euler(0, overviewYaw, 0);
                    float speed = (20f + OverviewDistance() * 1.2f) * (keyboard.leftShiftKey.isPressed ? 3f : 1f);
                    overviewFocus += yawRot * new Vector3(move.x, 0, move.y).normalized * speed * dt;
                }
            }
        }
        float pitch = Mathf.Lerp(10f, 75f, Mathf.Pow(overviewPct, OverviewPitchPower));
        rot = Quaternion.Euler(pitch, overviewYaw, 0f);
        Vector3 ground = GroundPoint(overviewFocus);
        Vector3 wanted = ground + rot * Vector3.back * OverviewDistance();
        Vector3 below = GroundPoint(wanted);
        if (wanted.y < below.y + OverviewMinHeight) wanted.y = below.y + OverviewMinHeight;
        if (!haveRot) { smoothPos = wanted; smoothRot = rot; haveRot = true; }
        float k = 1f - Mathf.Exp(-8f * dt);
        smoothPos = Vector3.Lerp(smoothPos, wanted, k); smoothRot = Quaternion.Slerp(smoothRot, rot, k);
        pos = smoothPos; rot = smoothRot;
        cameraNote = overviewTracks ? "" : "free pan";
        return true;
    }
    // StrategyMode.MoveCameraToPosition: d = Lerp(Max, Lerp(Min, Max, pct), TerrainHeightPct) / sin(pitch), with
    // TerrainHeightPct = 1 - Clamp01((pct - 0.5) / 0.5).
    private float OverviewDistance()
    {
        float pitch = Mathf.Lerp(10f, 75f, Mathf.Pow(overviewPct, OverviewPitchPower));
        float terrainPct = 1f - Mathf.Clamp01((overviewPct - .5f) / .5f);
        float height = Mathf.Lerp(OverviewMaxHeight, Mathf.Lerp(OverviewMinHeight, OverviewMaxHeight, overviewPct), terrainPct);
        return height / Math.Max(.05f, Mathf.Sin(pitch * Mathf.Deg2Rad));
    }
    private static Vector3 GroundPoint(Vector3 at)
    {
        try
        {
            if (Physics.Raycast(new Vector3(at.x, at.y + 3000f, at.z), Vector3.down, out RaycastHit hit, 6000f, GamePhysics.TRACELAYERMASK_IGNOREDEBRISANDUNITS, QueryTriggerInteraction.Ignore))
                return hit.point;
        }
        catch { }
        return at;
    }

    public override void OnLateUpdate()
    {
        PanelLateUpdate();
        CameraFallback();
        try { FreeCamLateUpdate(); } catch (Exception error) { Error(error); }
        try { MapLateUpdate(); } catch (Exception error) { Error(error); }
        try { UpdateHover(); } catch (Exception error) { Error(error); }
    }
}
