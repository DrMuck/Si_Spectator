using HarmonyLib;
using Il2Cpp;
using Il2CppSilica;
using Il2CppSilica.UI.Menus;
using UnityEngine;

namespace SiSpectator;

// IMGUI panel (Si_ModUI look: dark surface tinted by nothing - a spectator has no team -, accent line, same font sizes):
// header with mode / fog / target, the team-grouped player list (click = follow), camera mode / fog / tags buttons and
// "Return to game" (chat line "/spectate off" through the normal chat path). While it is open the cursor is free and the
// game gets no clicks or input (ModUI's IsAnyMenuOpened / GetPreferredInput / GetControlsEnabled patches + a disabled
// EventSystem over native menus). Cameras keep running underneath (GetCameraEnabled is NOT patched here).
public sealed partial class ClientMod
{
    private bool panelOpen, drawingEnabled = true;
    private double releaseUntil, nextDrawRetry;
    private CursorLockMode priorLock; private bool priorCursor;
    private Vector2 listScroll;
    private GUIStyle bodyStyle, titleStyle, smallStyle, buttonStyle, tabStyle, surfaceStyle;
    private Texture2D surfaceTexture;
    private int appearanceFont;
    private readonly Color accent = new(.6f, .88f, 1f, 1f), surface = new(.10f, .12f, .15f, .96f);
    private const float Contrast = .35f;
    private bool BlocksGameplay => panelOpen || Now < releaseUntil;

    [HarmonyPatch(typeof(MenuManager), nameof(MenuManager.IsAnyMenuOpened))]
    private static class MenuFocus { private static void Postfix(ref bool __result) { if (instance?.BlocksGameplay == true) __result = true; } }
    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetPreferredInput))]
    private static class InputFocus { private static bool Prefix(ref GameInput.InputMap __result) { if (instance?.BlocksGameplay != true) return true; __result = GameInput.InputMap.UI; return false; } }
    [HarmonyPatch(typeof(Game), nameof(Game.GetControlsEnabled))]
    private static class ControlFocus { private static bool Prefix(ref bool __result) { if (instance?.BlocksGameplay != true) return true; __result = false; return false; } }

    private static bool NativeMenuBlocking() => Game.IsPauseMenuActive || Game.InMainMenu;
    private static string OverlayName()
    {
        try { var current = MenuManager.Instance == null ? null : MenuManager.Instance.GetCurrent(); return current == null ? "" : current.GetIl2CppType().Name; }
        catch { return "?"; }
    }
    private UnityEngine.EventSystems.EventSystem pausedEventSystem;
    private void BlockNativeClicks(bool block)
    {
        try
        {
            if (block)
            {
                if (pausedEventSystem != null || OverlayName().Length == 0) return;
                var system = UnityEngine.EventSystems.EventSystem.current;
                if (system == null) return;
                system.enabled = false; pausedEventSystem = system;
            }
            else if (pausedEventSystem != null)
            {
                try { pausedEventSystem.enabled = true; } catch { }
                pausedEventSystem = null;
            }
        }
        catch (Exception error) { pausedEventSystem = null; Error(error); }
    }
    private void OpenPanel()
    {
        if (!Active || panelOpen) return;
        if (hideOverlays) { hideOverlays = false; }
        if (!drawingEnabled) { ResetAppearance(); drawingEnabled = true; nextDrawRetry = 0; }
        if (NativeMenuBlocking() || NativeTextInput()) { Write("Panel not opened: " + (NativeTextInput() ? "text input" : "native menu") + "."); return; }
        priorCursor = Cursor.visible; priorLock = Cursor.lockState;
        panelOpen = true;
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        try { GameInput.UpdateInput(); } catch { }
        BlockNativeClicks(true);
        Write("Panel opened" + (OverlayName().Length > 0 ? " over " + OverlayName() : "") + ".");
    }
    private void ClosePanel()
    {
        BlockNativeClicks(false);
        if (!panelOpen) return;
        panelOpen = false; releaseUntil = Now + .2;
        try
        {
            if (!Application.isFocused) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            else if (!NativeMenuBlocking() && !NativeTextInput())
            {
                bool show = GameMode.CurrentGameMode == null ? priorCursor : GameMode.CurrentGameMode.GetShowCursor();
                Cursor.visible = show; Cursor.lockState = show ? CursorLockMode.None : (priorLock == CursorLockMode.None ? CursorLockMode.Locked : priorLock);
            }
            GameInput.UpdateInput();
        }
        catch { }
        Write("Panel closed.");
    }
    private void PanelLateUpdate()
    {
        try
        {
            if (!panelOpen) return;
            if (!Active || !Application.isFocused || NativeMenuBlocking() || NativeTextInput()) { ClosePanel(); return; }
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            if (OverlayName().Length > 0) BlockNativeClicks(true); else BlockNativeClicks(false);
        }
        catch (Exception error) { ClosePanel(); Error(error); }
    }

    // ---- appearance (Si_ModUI palette for a team-less player) ----
    private void ResetAppearance()
    {
        if (surfaceTexture != null) UnityEngine.Object.Destroy(surfaceTexture);
        if (whiteTexture != null) UnityEngine.Object.Destroy(whiteTexture);
        surfaceTexture = whiteTexture = null;
        bodyStyle = titleStyle = smallStyle = buttonStyle = tabStyle = surfaceStyle = tagStyle = tagSmallStyle = null;
    }
    private void Appearance()
    {
        int font = Math.Clamp(fontSize?.Value ?? 17, 14, 26);
        if (bodyStyle != null && surfaceTexture != null && font == appearanceFont) return;
        if (surfaceTexture != null) UnityEngine.Object.Destroy(surfaceTexture);
        appearanceFont = font;
        surfaceTexture = Solid(Color.white);
        surfaceStyle = new GUIStyle(); surfaceStyle.normal.background = surfaceTexture;
        bodyStyle = Text(font);
        titleStyle = Text(font + 3); titleStyle.fontStyle = FontStyle.Bold; titleStyle.normal.textColor = new Color(1f, .97f, .87f, 1);
        smallStyle = Text(Math.Max(13, font - 3)); smallStyle.normal.textColor = new Color(.86f, .88f, .86f, 1);
        buttonStyle = Text(font); buttonStyle.alignment = TextAnchor.MiddleLeft; buttonStyle.padding = new RectOffset(10, 8, 4, 4); buttonStyle.wordWrap = false;
        buttonStyle.normal.background = buttonStyle.hover.background = buttonStyle.active.background = buttonStyle.focused.background = surfaceTexture;
        buttonStyle.hover.textColor = buttonStyle.active.textColor = buttonStyle.focused.textColor = Color.white;
        tabStyle = Text(Math.Max(13, font - 2)); tabStyle.alignment = TextAnchor.MiddleCenter; tabStyle.fontStyle = FontStyle.Bold; tabStyle.padding = new RectOffset(4, 4, 3, 3); tabStyle.wordWrap = false;
        tabStyle.normal.background = tabStyle.hover.background = tabStyle.active.background = tabStyle.focused.background = surfaceTexture;
        tabStyle.hover.textColor = tabStyle.active.textColor = tabStyle.focused.textColor = Color.white;
    }
    private static Texture2D Solid(Color color)
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = UnityEngine.HideFlags.HideAndDontSave };
        texture.SetPixel(0, 0, color); texture.Apply(); return texture;
    }
    private static GUIStyle Text(int size)
    {
        var style = new GUIStyle { font = GUI.skin.font, fontSize = size, wordWrap = true, richText = false };
        style.normal.textColor = new Color(.98f, .98f, .95f, 1); return style;
    }
    private void Surface(Rect rect, Color color) { var old = GUI.color; try { GUI.color = color; GUI.Box(rect, "", surfaceStyle); } finally { GUI.color = old; } }
    private bool Button(Rect rect, string text, bool selected = false, bool allowed = true, GUIStyle style = null)
    {
        var oldColor = GUI.color; bool oldEnabled = GUI.enabled;
        try
        {
            bool hover = rect.Contains(Event.current.mousePosition);
            GUI.enabled = oldEnabled && allowed;
            var c = Color.Lerp(surface, accent, selected ? Math.Min(.78f, Contrast + .3f) : hover ? Math.Min(.7f, Contrast + .15f) : Contrast);
            GUI.color = new Color(c.r, c.g, c.b, 1);
            return GUI.Button(rect, text, style ?? buttonStyle) && allowed;
        }
        finally { GUI.color = oldColor; GUI.enabled = oldEnabled; }
    }

    // ---- rendering ----
    public override void OnGUI()
    {
        if (!Active || Game.InMainMenu || hideOverlays) return;
        if (!drawingEnabled) { if (Now < nextDrawRetry) return; ResetAppearance(); drawingEnabled = true; }
        if (Screen.width < 200 || Screen.height < 100) return;
        try
        {
            Appearance();
            if (Event.current.type == EventType.Repaint && tagsOn) DrawTags();
            if (panelOpen)
            {
                bool before = GUI.enabled;
                GUI.enabled = before && Application.isFocused && !NativeMenuBlocking() && !NativeTextInput() && Cursor.visible;
                try { RenderPanel(); } finally { GUI.enabled = before; }
            }
            else if (showHud.Value && Event.current.type == EventType.Repaint) RenderHudLine();
        }
        catch (Exception error) { ClosePanel(); drawingEnabled = false; nextDrawRetry = Now + 2; Error(error); }
    }
    private string TargetLabel()
    {
        if (target == null) return aiTarget != null ? AiLabel(aiTarget) + (Alive(aiTarget) ? "" : " - destroyed") : "no target";
        string unit = TargetUnit() is Unit u ? UnitName(u) : (CommanderSafe(target) ? "commander" : "no unit");
        return SafeName(target) + " (" + (target.Team != null ? TeamLabel(target.Team) + ", " : "") + unit + ")";
    }
    private static bool CommanderSafe(Player p) { try { return p != null && p.IsCommander; } catch { return false; } }
    private void RenderHudLine()
    {
        string text = "SPECTATING  " + mode + (IsFollowMode(mode) || mode == CamMode.Overview ? ": " + TargetLabel() : "") + (cameraNote.Length > 0 ? "  [" + cameraNote + "]" : "")
            + "   |   Fog: " + TeamLabel(fogTeam) + "   |   " + kPanel + " panel, " + kCamera + " camera, " + kFog + " fog, " + kPrev + "/" + kNext + " players";
        if (HasTarget && Now < selectHintUntil && !IsFollowMode(mode)) text = "Selected " + TargetDesc() + " - click again or press " + kFollow + " to follow";
        float width = Math.Min(Screen.width - 24, smallStyle.CalcSize(new GUIContent(text)).x + 28);
        var rect = new Rect((Screen.width - width) / 2, 10, width, 28);
        Surface(rect, new Color(surface.r, surface.g, surface.b, .8f)); Surface(new Rect(rect.x, rect.y, rect.width, 2), accent);
        GUI.Label(new Rect(rect.x + 14, rect.y + 5, rect.width - 28, 22), text, smallStyle);
    }
    private void RenderPanel()
    {
        float width = Math.Min(460, Screen.width - 24), height = Math.Min(Screen.height - 24, 760);
        var rect = new Rect(12, Math.Max(12, (Screen.height - height) / 2), width, height);
        var ev = Event.current;
        if (ev.type == EventType.MouseDown && GUI.enabled && !rect.Contains(ev.mousePosition)) { ev.Use(); ClosePanel(); return; }
        Surface(rect, surface); Surface(new Rect(rect.x + 12, rect.y, rect.width - 24, 2), accent);
        float x = rect.x + 14, y = rect.y + 10, w = rect.width - 28;
        GUI.Label(new Rect(x, y, 220, 32), "SPECTATOR", titleStyle);
        GUI.Label(new Rect(x + 150, y + 7, w - 230, 24), "server " + (serverVersion.Length > 0 ? serverVersion : "?") + ", client " + Version, smallStyle);
        if (Button(new Rect(rect.xMax - 92, y, 78, 30), "CLOSE", style: tabStyle)) { ClosePanel(); return; }
        y += 38;
        GUI.Label(new Rect(x, y, w, 22), "Camera: " + mode + (cameraNote.Length > 0 ? "  (" + cameraNote + ")" : ""), smallStyle); y += 22;
        GUI.Label(new Rect(x, y, w, 22), "Target: " + TargetLabel(), smallStyle); y += 22;
        GUI.Label(new Rect(x, y, w, 22), "Fog perspective: " + TeamLabel(fogTeam), smallStyle); y += 28;

        // camera modes, 3 per row
        float bw = (w - 16) / 3, bh = 30;
        bool anyoneToFollow = HasTarget || OrderedPlayers().Any(p => Alive(p.ControlledUnit));
        for (int i = 0; i < ModeOrder.Length; i++)
        {
            var m = ModeOrder[i];
            var r = new Rect(x + (i % 3) * (bw + 8), y + (i / 3) * (bh + 6), bw, bh);
            bool needsTarget = IsFollowMode(m) && !anyoneToFollow;
            if (Button(r, m.ToString(), mode == m, !needsTarget, tabStyle))
            {
                if (IsFollowMode(m) && !HasTarget) PickFirstTarget();
                SetMode(m, "panel");
            }
        }
        y += 2 * (bh + 6) + 6;
        // fog perspective
        var teams = MatchTeams();
        int count = teams.Count + 1;
        float fw = (w - 8 * (count - 1)) / count;
        if (Button(new Rect(x, y, fw, bh), "All", fogTeam == null, true, tabStyle)) SetFog(null, "panel");
        for (int i = 0; i < teams.Count; i++)
            if (Button(new Rect(x + (i + 1) * (fw + 8), y, fw, bh), TeamLabel(teams[i]), fogTeam != null && fogTeam.Pointer == teams[i].Pointer, true, tabStyle)) SetFog(teams[i], "panel");
        y += bh + 8;
        float hw = (w - 8) / 2;
        if (Button(new Rect(x, y, hw, bh), "Name tags: " + (tagsOn ? "ON" : "OFF"), tagsOn, true, tabStyle)) { tagsOn = !tagsOn; Write("Name tags " + (tagsOn ? "on" : "off") + " (panel)."); }
        if (Button(new Rect(x + hw + 8, y, hw, bh), "Hide overlays (" + kHide + ")", false, true, tabStyle)) { hideOverlays = true; ClosePanel(); Write("Overlays hidden (panel)."); return; }
        y += bh + 8;
        bool ai = followAiUnits.Value;
        if (Button(new Rect(x, y, hw, bh), "AI units: " + (ai ? "ON" : "OFF"), ai, true, tabStyle))
        {
            followAiUnits.Value = ai = !ai;
            Write("AI unit picking " + (ai ? "on" : "off") + " (panel).");
            if (!ai && aiTarget != null) { ClearTarget(); if (IsFollowMode(mode)) SetMode(CamMode.Free, "AI units off"); else if (mode == CamMode.Overview) overviewTracks = false; }
        }
        if (Button(new Rect(x + hw + 8, y, hw, bh), "AI tags: " + (tagsForAi.Value ? "ON" : "OFF"), tagsForAi.Value, ai, tabStyle))
        { tagsForAi.Value = !tagsForAi.Value; Write("AI tags " + (tagsForAi.Value ? "on" : "off") + " (panel)."); }
        y += bh + 12;

        // player list grouped by team
        float footer = 88;
        var listRect = new Rect(x, y, w, rect.yMax - footer - y);
        RenderPlayerList(listRect, teams);
        y = rect.yMax - footer + 8;
        if (Button(new Rect(x, y, w, 34), "Return to game  (/spectate off)", false, true, tabStyle)) ReturnToGame();
        GUI.Label(new Rect(x, y + 40, w, 40), "Keys: " + kPanel + " panel, " + kCamera + " camera, " + kFog + " fog, " + kTags + " tags, " + kHide + " hide, " + kPrev + "/" + kNext + " players, "
            + kFollow + " / 2nd click follow, Esc = Free", smallStyle);
    }
    private void RenderPlayerList(Rect rect, List<Team> teams)
    {
        var players = OrderedPlayers();
        float row = 30, header = 26, total = 0;
        foreach (var t in teams) total += header + players.Count(p => p.Team.Pointer == t.Pointer) * (row + 4) + 6;
        if (players.Count == 0) total = 40;
        listScroll = GUI.BeginScrollView(rect, listScroll, new Rect(0, 0, rect.width - 20, Math.Max(total, 1)), false, total > rect.height);
        try
        {
            float y = 0, w = rect.width - 24;
            if (players.Count == 0) GUI.Label(new Rect(4, 4, w, 30), "No players on a team.", smallStyle);
            foreach (var team in teams)
            {
                var members = players.Where(p => p.Team.Pointer == team.Pointer).ToList();
                var old = smallStyle.normal.textColor;
                smallStyle.normal.textColor = Color.Lerp(team.TeamColor, Color.white, .35f);
                GUI.Label(new Rect(2, y + 2, w, header), TeamLabel(team).ToUpperInvariant() + "  (" + members.Count + ")", smallStyle);
                smallStyle.normal.textColor = old;
                y += header;
                foreach (var p in members)
                {
                    var unit = p.ControlledUnit;
                    bool alive = Alive(unit);
                    string hp = alive && Health(unit) is float h && h >= 0 ? "  " + Mathf.RoundToInt(h * 100) + "%" : "";
                    string label = (CommanderSafe(p) ? "[CMD] " : "") + SafeName(p) + "  -  " + (alive ? UnitName(unit) : CommanderSafe(p) ? "commanding" : "no unit") + hp;
                    bool selected = target != null && target.Pointer == p.Pointer;
                    if (Button(new Rect(0, y, w, row), label, selected))
                    {
                        SelectTarget(p, "panel");
                        if (alive) SetMode(IsFollowMode(mode) ? mode : lastFollow, "panel list");
                        else if (CommanderSafe(p)) SetMode(CamMode.Overview, "panel list (commander)");
                    }
                    y += row + 4;
                }
                y += 6;
            }
        }
        finally { GUI.EndScrollView(); }
    }
    private void ReturnToGame()
    {
        try
        {
            var p = Player.CurrentPlayer;
            if (p == null) return;
            p.SendChatMessage("/spectate off", false);
            Write("Return to game: sent /spectate off.");
            ClosePanel();
        }
        catch (Exception error) { Error(error); }
    }
}
