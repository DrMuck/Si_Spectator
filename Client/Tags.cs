using Il2Cpp;
using UnityEngine;

namespace SiSpectator;

// IMGUI name tags over every player-controlled unit: name in team colour (1-px shadow), unit name, HP bar. Anchor = the
// vanilla RTSPlayerName formula plus 0.4 m: WorldPhysicalCenter + (0, PhysicalBounds.extents.y + 0.4, 0) through
// Game.CurrentCamera.WorldToScreenPoint, y flipped for IMGUI. Hidden when the unit is not visible under the current fog
// perspective (BaseGameObject.IsVisible), beyond TagRange in Free / Overview, and for the unit we sit in (FollowFps).
// The team HQ of a player commander gets a "CMD <name>" tag.
public sealed partial class ClientMod
{
    private bool tagsOn = true, hideOverlays;
    private GUIStyle tagStyle, tagSmallStyle, fillStyle;
    private Texture2D whiteTexture;
    private const float HardTagRange = 1500f;

    private void EnsureTagStyles()
    {
        if (tagStyle != null && whiteTexture != null && fillStyle != null) return;
        if (whiteTexture == null) whiteTexture = Solid(Color.white);
        // GUI.DrawTexture is stripped in this Il2Cpp build (NotSupportedException "Method unstripping failed"); a box
        // with a white background under GUI.color is the Si_ModUI way (Si_ModUI\Client\Ui.cs Surface()).
        fillStyle = new GUIStyle(); fillStyle.normal.background = whiteTexture;
        tagStyle = new GUIStyle { font = GUI.skin.font, fontSize = 15, alignment = TextAnchor.LowerCenter, richText = false, wordWrap = false, fontStyle = FontStyle.Bold };
        tagSmallStyle = new GUIStyle { font = GUI.skin.font, fontSize = 12, alignment = TextAnchor.UpperCenter, richText = false, wordWrap = false };
    }
    private void DrawTags()
    {
        var cam = Game.CurrentCamera;
        if (cam == null) return;
        EnsureTagStyles();
        Vector3 camPos = cam.transform.position;
        float range = mode == CamMode.Free || mode == CamMode.Overview ? Math.Min(TagRange, HardTagRange) : HardTagRange;
        var followed = TargetUnit();
        var me = Player.CurrentPlayer;
        var players = Player.Players;
        for (int i = 0; players != null && i < players.Count; i++)
        {
            var p = players[i];
            if (p == null || p.Team == null || (me != null && p.Pointer == me.Pointer)) continue;
            var unit = p.ControlledUnit;
            if (!Alive(unit)) continue;
            if (mode == CamMode.FollowFps && followed != null && unit.Pointer == followed.Pointer) continue;
            if (fogTeam != null && !unit.IsVisible) continue;
            Vector3 anchor = unit.WorldPhysicalCenter + new Vector3(0, unit.PhysicalBounds.extents.y + .4f, 0);
            float dist = Vector3.Distance(camPos, anchor);
            if (dist > range) continue;
            bool selected = target != null && p.Pointer == target.Pointer;
            string info = UnitName(unit);
            float hp = Health(unit);
            DrawTag(cam, anchor, dist, range, SafeName(p), p.Team.TeamColor, info, hp, selected, IsHovered(unit));
        }
        if (AiPicking) DrawAiTags(cam, camPos, followed);
        // commander tags on the HQ
        for (int i = 0; players != null && i < players.Count; i++)
        {
            var p = players[i];
            if (p == null || p.Team == null) continue;
            bool commander; try { commander = p.IsCommander; } catch { commander = false; }
            if (!commander) continue;
            var hq = HqOf(p.Team);
            if (hq == null || (fogTeam != null && !hq.IsVisible)) continue;
            Vector3 anchor = hq.WorldPhysicalCenter + new Vector3(0, hq.PhysicalBounds.extents.y + .4f, 0);
            float dist = Vector3.Distance(camPos, anchor);
            if (dist > Math.Max(range, 600f)) continue;
            DrawTag(cam, anchor, dist, Math.Max(range, 600f), "CMD " + SafeName(p), p.Team.TeamColor, TeamLabel(p.Team) + " HQ", Health(hq), target != null && p.Pointer == target.Pointer, false);
        }
    }
    // AI units: unit name in team colour, no player name, no HP bar, always within TagRange. With TagsForAi off only the
    // selected AI target gets its tag.
    private void DrawAiTags(Camera cam, Vector3 camPos, Unit followed)
    {
        bool all = tagsForAi?.Value ?? false;
        bool hoverAi = hoverUnit != null && hoverPlayer == null && Alive(hoverUnit);
        if (!all && aiTarget == null && !hoverAi) return;
        float range = TagRange;
        foreach (var team in all ? MatchTeams() : new List<Team>())
        {
            var units = team.Units;
            for (int i = 0; units != null && i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.ControlledBy != null || !Alive(u)) continue;
                if (aiTarget != null && u.Pointer == aiTarget.Pointer) continue;   // drawn below as the selection
                if (hoverAi && u.Pointer == hoverUnit.Pointer) continue;          // drawn below as the hover
                DrawAiTag(cam, camPos, u, team, range, false, followed);
            }
        }
        if (aiTarget != null && Alive(aiTarget) && !(hoverAi && aiTarget.Pointer == hoverUnit.Pointer)) DrawAiTag(cam, camPos, aiTarget, aiTarget.Team, Math.Max(range, HardTagRange), true, followed);
        if (hoverAi) DrawAiTag(cam, camPos, hoverUnit, hoverUnit.Team, Math.Max(range, HardTagRange), aiTarget != null && aiTarget.Pointer == hoverUnit.Pointer, followed, true);
    }
    private void DrawAiTag(Camera cam, Vector3 camPos, Unit u, Team team, float range, bool selected, Unit followed, bool hovered = false)
    {
        if (mode == CamMode.FollowFps && followed != null && u.Pointer == followed.Pointer) return;
        if (fogTeam != null && !u.IsVisible) return;
        Vector3 anchor = u.WorldPhysicalCenter + new Vector3(0, u.PhysicalBounds.extents.y + .4f, 0);
        float dist = Vector3.Distance(camPos, anchor);
        if (dist > range) return;
        Vector3 s = cam.WorldToScreenPoint(anchor);
        if (s.z <= 0) return;
        float x = s.x, y = Screen.height - s.y;
        if (x < -100 || x > Screen.width + 100 || y < -40 || y > Screen.height + 40) return;
        float scale = Math.Clamp(1f - dist / Math.Max(range, 1f) * .4f, .6f, 1f);
        tagSmallStyle.fontSize = Math.Max(9, (int)(12 * scale));
        string name = UnitName(u);
        if (name.Length == 0) name = "AI";
        Color color = team != null ? Color.Lerp(team.TeamColor, Color.white, .25f) : Color.white;
        if (hovered)
        {
            tagSmallStyle.fontSize = Math.Max(11, (int)(14 * scale));
            color = Color.Lerp(color, Color.white, .45f);
            name += "  \u00b7  click: follow";
            Outline(new Rect(x - 130, y - 18 * scale, 260, 18 * scale), name, tagSmallStyle);
        }
        Shadowed(new Rect(x - 130, y - 18 * scale, 260, 18 * scale), selected ? "> " + name + " <" : name, tagSmallStyle, color);
    }
    // Thin box around a centred label (hover highlight).
    private void Outline(Rect rect, string text, GUIStyle style)
    {
        float tw = Math.Min(rect.width, style.CalcSize(new GUIContent(text)).x + 12), th = rect.height + 4;
        var box = new Rect(rect.center.x - tw / 2, rect.y - 2, tw, th);
        var c = new Color(1, 1, 1, .9f);
        Fill(new Rect(box.x, box.y, box.width, 1), c); Fill(new Rect(box.x, box.yMax - 1, box.width, 1), c);
        Fill(new Rect(box.x, box.y, 1, box.height), c); Fill(new Rect(box.xMax - 1, box.y, 1, box.height), c);
    }
    // Centre dot (2-3 px, team-neutral, 1-px shadow): the point a left click picks from.
    private void DrawCrosshairDot()
    {
        EnsureTagStyles();
        float cx = Mathf.Round(Screen.width * .5f), cy = Mathf.Round(Screen.height * .5f);
        Fill(new Rect(cx - 2, cy - 2, 5, 5), new Color(0, 0, 0, .75f));
        Fill(new Rect(cx - 1, cy - 1, 3, 3), hoverUnit != null ? new Color(1f, .95f, .55f, 1) : new Color(1, 1, 1, .95f));
    }
    // Small own HUD while following (the vanilla HUD stays off, see docs\CLIENT.md "Player HUD"): name, unit, HP bar at
    // the bottom centre, and a marker on the followed player's networked aim point.
    private void DrawFollowHud()
    {
        var unit = TargetUnit(); var cam = Game.CurrentCamera;
        if (unit == null || cam == null) return;
        EnsureTagStyles();
        string who = target != null ? SafeName(target) : "AI";
        Team team = target != null ? target.Team : unit.Team;
        Color color = team != null ? Color.Lerp(team.TeamColor, Color.white, .3f) : Color.white;
        float hp = Health(unit);
        float w = 300, h = 52, x = (Screen.width - w) / 2, y = Screen.height - h - 28;
        Fill(new Rect(x, y, w, h), new Color(0, 0, 0, .45f));
        tagStyle.fontSize = 16; tagSmallStyle.fontSize = 13;
        Shadowed(new Rect(x, y + 2, w, 22), who, tagStyle, color);
        Shadowed(new Rect(x, y + 24, w, 16), UnitName(unit) + (hp >= 0 ? "   " + Mathf.RoundToInt(hp * 100) + " %" : ""), tagSmallStyle, new Color(.92f, .92f, .9f, 1));
        if (hp >= 0)
        {
            Fill(new Rect(x + 20, y + h - 9, w - 40, 5), new Color(0, 0, 0, .7f));
            Fill(new Rect(x + 20, y + h - 9, (w - 40) * Mathf.Clamp01(hp), 5), Color.Lerp(new Color(.9f, .2f, .15f, 1), new Color(.3f, .9f, .35f, 1), hp));
        }
        if (mode == CamMode.Orbit) return;
        try
        {
            var aim = unit.UnitAimAt;
            if (aim == null || aim.AimAtPosition == Vector3.zero) return;
            Vector3 s = cam.WorldToScreenPoint(aim.AimAtPosition);
            if (s.z <= 0) return;
            float ax = s.x, ay = Screen.height - s.y;
            var c = new Color(1, 1, 1, .8f); var sh = new Color(0, 0, 0, .6f);
            Fill(new Rect(ax - 7, ay, 5, 1), c); Fill(new Rect(ax + 3, ay, 5, 1), c); Fill(new Rect(ax, ay - 7, 1, 5), c); Fill(new Rect(ax, ay + 3, 1, 5), c);
            Fill(new Rect(ax - 7, ay + 1, 5, 1), sh); Fill(new Rect(ax + 3, ay + 1, 5, 1), sh);
        }
        catch { }
    }
    private void DrawTag(Camera cam, Vector3 world, float dist, float range, string name, Color color, string info, float hp, bool selected, bool hovered)
    {
        Vector3 s = cam.WorldToScreenPoint(world);
        if (s.z <= 0) return;
        float x = s.x, y = Screen.height - s.y;
        if (x < -100 || x > Screen.width + 100 || y < -60 || y > Screen.height + 60) return;
        float scale = Math.Clamp(1.15f - dist / Math.Max(range, 1f) * .55f, .6f, 1.15f);
        if (selected) scale = Math.Max(scale, 1f);
        if (hovered) { scale = Math.Max(scale, 1f) * 1.2f; color = Color.Lerp(color, Color.white, .45f); info = (info.Length > 0 ? info + "  \u00b7  " : "") + "click: follow"; }
        tagStyle.fontSize = Math.Max(10, (int)(15 * scale));
        tagSmallStyle.fontSize = Math.Max(9, (int)(12 * scale));
        float w = 260;
        var nameRect = new Rect(x - w / 2, y - 22 * scale - 8 * scale, w, 22 * scale);
        if (hovered) Outline(nameRect, selected ? "> " + name + " <" : name, tagStyle);
        Shadowed(nameRect, selected ? "> " + name + " <" : name, tagStyle, Color.Lerp(color, Color.white, .25f));
        float barW = 56 * scale, barH = Math.Max(3, 5 * scale);
        var bar = new Rect(x - barW / 2, y - 6 * scale, barW, barH);
        if (hp >= 0)
        {
            Fill(new Rect(bar.x - 1, bar.y - 1, bar.width + 2, bar.height + 2), new Color(0, 0, 0, .7f));
            Fill(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(hp), bar.height), Color.Lerp(new Color(.9f, .2f, .15f, 1), new Color(.3f, .9f, .35f, 1), hp));
        }
        if (info.Length > 0) Shadowed(new Rect(x - w / 2, bar.yMax + 1, w, 18 * scale), info, tagSmallStyle, new Color(.9f, .92f, .9f, 1));
    }
    private void Shadowed(Rect rect, string text, GUIStyle style, Color color)
    {
        style.normal.textColor = new Color(0, 0, 0, .85f);
        GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, style);
        style.normal.textColor = color;
        GUI.Label(rect, text, style);
    }
    private void Fill(Rect rect, Color color)
    {
        var old = GUI.color;
        try { GUI.color = color; GUI.Box(rect, "", fillStyle); } finally { GUI.color = old; }
    }
    private static string UnitName(BaseGameObject obj)
    {
        try { var info = obj.ObjectInfo; return info == null ? "" : Plain(info.DisplayName ?? ""); } catch { return ""; }
    }
    // DamageManager.Health01 = Health / MaxHealth (DamageManager.cs:290 / 106:292); -1 = unknown.
    private static float Health(BaseGameObject obj)
    {
        try { var d = obj.DamageManager; return d == null ? -1 : Mathf.Clamp01(d.Health01); } catch { return -1; }
    }
}
