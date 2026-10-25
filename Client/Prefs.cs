using MelonLoader;
using UnityEngine.InputSystem;

namespace SiSpectator;

// MelonPreferences category [Si_Spectator] in UserData\MelonPreferences.cfg. Key names are UnityEngine.InputSystem.Key
// names (I, C, LeftBracket, F7 ...) or a single character ([, ], /). An unknown name falls back to the default.
// Defaults avoid vanilla bindings that are live for a team-less player: P = "Vote No" (Voting map), N = "Voice Channel
// Switch" (Global map). C / F are Unit-map actions (no unit = no effect), [ / ] only exist in the Commander / Cheats maps.
public sealed partial class ClientMod
{
    private MelonPreferences_Entry<string> keyPanel, keyCamera, keyFog, keyTags, keyHide, keyPrev, keyNext, keyFollow;
    private MelonPreferences_Entry<float> tagRange, followSmoothing, fpsNearClip, orbitSensitivity;
    private MelonPreferences_Entry<bool> showTags, showHud, clickPicking, followAiUnits, tagsForAi;
    private MelonPreferences_Entry<int> fontSize;
    private Key kPanel = Key.I, kCamera = Key.C, kFog = Key.F, kTags = Key.K, kHide = Key.H, kPrev = Key.LeftBracket, kNext = Key.RightBracket, kFollow = Key.G;

    private void CreatePrefs()
    {
        var c = MelonPreferences.CreateCategory("Si_Spectator");
        keyPanel = c.CreateEntry("KeyPanel", "I", description: "Open / close the spectator panel (not P: P is the vanilla 'Vote No' key in map / surrender votes)");
        keyCamera = c.CreateEntry("KeyCameraMode", "C", description: "Cycle camera mode: Free, FollowFps, Shoulder, Orbit, Overview, Fixed");
        keyFog = c.CreateEntry("KeyFog", "F", description: "Cycle fog perspective: All, then every team of the match");
        keyTags = c.CreateEntry("KeyTags", "K", description: "Name tags on / off (not N: N is the vanilla voice-channel switch)");
        keyHide = c.CreateEntry("KeyHideOverlays", "H", description: "Hide every overlay of this mod (tags, HUD line, panel)");
        keyPrev = c.CreateEntry("KeyPrevPlayer", "LeftBracket", description: "Previous player (team order)");
        keyNext = c.CreateEntry("KeyNextPlayer", "RightBracket", description: "Next player (team order)");
        keyFollow = c.CreateEntry("KeyFollow", "G", description: "Follow the selected player (same as a second click on them)");
        tagRange = c.CreateEntry("TagRange", 300f, description: "Name tags beyond this distance (m) are hidden in Free and Overview");
        followSmoothing = c.CreateEntry("FollowSmoothing", 12f, description: "Follow-camera rotation smoothing rate (1/s); 0 = none, higher = snappier");
        fpsNearClip = c.CreateEntry("FpsNearClip", 0.3f, description: "Near clip plane (m) in FollowFps so the followed soldier's own head does not block the view");
        orbitSensitivity = c.CreateEntry("OrbitSensitivity", 0.15f, description: "Degrees per mouse count in Orbit");
        showTags = c.CreateEntry("ShowTags", true, description: "Name tags at start");
        showHud = c.CreateEntry("ShowHudLine", true, description: "One-line status at the top of the screen while spectating");
        clickPicking = c.CreateEntry("ClickPicking", true, description: "Left click selects the player under the crosshair, a second click follows");
        fontSize = c.CreateEntry("FontSize", 17, description: "Panel font size (14..26), same scale as Si_ModUI");
        followAiUnits = c.CreateEntry("FollowAiUnits", true, description: "Clicks may also select AI-controlled units (follow cameras work on them; [ / ] still step over players only)");
        tagsForAi = c.CreateEntry("TagsForAi", false, description: "Small tags (unit name in team colour) over AI units within TagRange; needs FollowAiUnits");
        // Pre-release 0.1.0 builds (2026-10-25 morning) saved P / N as defaults; move exactly those values to the new
        // defaults once. A key the user chose on purpose (anything else) is kept.
        var prefsVersion = c.CreateEntry("PrefsVersion", 1, description: "Internal: settings layout version", is_hidden: true);
        if (prefsVersion.Value < 2)
        {
            if (string.Equals(keyPanel.Value?.Trim(), "P", StringComparison.OrdinalIgnoreCase)) keyPanel.Value = "I";
            if (string.Equals(keyTags.Value?.Trim(), "N", StringComparison.OrdinalIgnoreCase)) keyTags.Value = "K";
            prefsVersion.Value = 2;
        }
        ReadKeys();
        tagsOn = showTags.Value;
    }
    private void ReadKeys()
    {
        kPanel = ParseKey(keyPanel.Value, Key.I); kCamera = ParseKey(keyCamera.Value, Key.C); kFog = ParseKey(keyFog.Value, Key.F);
        kTags = ParseKey(keyTags.Value, Key.K); kHide = ParseKey(keyHide.Value, Key.H); kPrev = ParseKey(keyPrev.Value, Key.LeftBracket);
        kNext = ParseKey(keyNext.Value, Key.RightBracket); kFollow = ParseKey(keyFollow.Value, Key.G);
    }
    private static Key ParseKey(string text, Key fallback)
    {
        text = (text ?? "").Trim();
        switch (text)
        {
            case "[": return Key.LeftBracket;
            case "]": return Key.RightBracket;
            case "/": return Key.Slash;
            case "\\": return Key.Backslash;
            case ";": return Key.Semicolon;
            case ",": return Key.Comma;
            case ".": return Key.Period;
            case "-": return Key.Minus;
            case "=": return Key.Equals;
        }
        if (text.Length == 1 && char.IsDigit(text[0])) text = "Digit" + text;
        return Enum.TryParse(text, true, out Key key) && key != Key.None && key != Key.Escape ? key : fallback;
    }
    private string KeySummary() => "panel " + kPanel + ", camera " + kCamera + ", fog " + kFog + ", tags " + kTags + ", hide " + kHide
        + ", prev/next " + kPrev + "/" + kNext + ", follow " + kFollow + " or 2nd click, Escape = back to Free";
    private float Smoothing => Math.Clamp(followSmoothing?.Value ?? 12f, 0f, 60f);
    private float TagRange => Math.Clamp(tagRange?.Value ?? 300f, 20f, 5000f);
}
