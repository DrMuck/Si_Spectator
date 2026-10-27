using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace SiSpectator;

// Kill feed while spectating (pure listener, nothing sent).
//
// On a lethal hit the server sends ObjectSetHealth with the instigator's NetID (DamageManager.cs 0947:1253-1266 /
// 106:1265-1277); the client runs DamageManager.OnDamageReceived, which calls - on every machine - the static
// Player.OnTargetDestroyed(Target, GameObject) (0947:1275 / 106:1287; the method itself returns at once on clients,
// Player.cs 0947:823-828 / 106:827-832) and then GameEvents.OnUnitDestroyed / OnStructureDestroyed. We hook a Harmony
// postfix on Player.OnTargetDestroyed: one hook for units and structures, no Il2Cpp delegate juggling, and it survives
// GameEvents.Clear() on scene changes (GameEvents.cs:135). The victim's ControlledBy is still set there (the vanilla
// server reads it in the same method for its death count).
// Killer = GameFuncs.GetBaseGameObject(instigator) (GameFuncs.cs 0947:924 / 106:905), as Unit -> ControlledBy (human)
// or AI; as Structure -> its name; null -> "<victim> died".
public sealed partial class ClientMod
{
    private sealed class FeedPart { public string Text; public Color Color; }
    private sealed class FeedEntry
    {
        public readonly List<FeedPart> Parts = new();
        public double At;
        public IntPtr KillerUnit, VictimUnit, KillerPlayer, VictimPlayer;
    }
    private readonly List<FeedEntry> feed = new();
    private static readonly Color FeedNeutral = new(.9f, .9f, .88f, 1);

    [HarmonyPatch(typeof(Player), nameof(Player.OnTargetDestroyed))]
    private static class KillHook
    {
        private static void Postfix(Target __0, GameObject __1)
        {
            if (!Active || instance == null || instance.killFeed == null || !instance.killFeed.Value || __0 == null) return;
            try { instance.OnKill(__0, __1); } catch (Exception error) { instance.OverlayError("kill feed (event)", error); }
        }
    }
    // Called on every spectate entry (also after a scene-load re-entry).
    private void KillFeedEntered()
    {
        feed.Clear();
        Write("Kill feed: subscribed (Harmony postfix on Player.OnTargetDestroyed; " + (killFeed.Value ? "on" : "off") + ", filter " + KillFeedFilter + ").");
    }
    private string KillFeedFilter => string.Equals((killFeedFilter?.Value ?? "all").Trim(), "players", StringComparison.OrdinalIgnoreCase) ? "players" : "all";

    private static Color TeamTint(Team team) => team != null ? Color.Lerp(team.TeamColor, Color.white, .25f) : FeedNeutral;
    // "Name [Unit]" for a player, "AI · Unit" for an AI unit.
    private static void AddActor(FeedEntry e, Unit unit, Player player)
    {
        Team team = player != null && player.Team != null ? player.Team : unit != null ? unit.Team : null;
        if (player != null) e.Parts.Add(new FeedPart { Text = SafeName(player) + (unit != null ? " [" + UnitName(unit) + "]" : ""), Color = TeamTint(team) });
        else e.Parts.Add(new FeedPart { Text = "AI · " + (unit != null ? UnitName(unit) : "unit"), Color = TeamTint(team) });
    }
    private void OnKill(Target target, GameObject instigator)
    {
        var vUnit = target.OwnerUnit; var vStructure = target.OwnerStructure;
        BaseGameObject source = null;
        try { if (instigator != null) source = GameFuncs.GetBaseGameObject(instigator, true); } catch { }
        var kUnit = source == null ? null : source.TryCast<Unit>();
        var kStructure = kUnit != null || source == null ? null : source.TryCast<Structure>();
        var kPlayer = kUnit != null ? kUnit.ControlledBy : null;
        var vPlayer = vUnit != null ? vUnit.ControlledBy : null;
        var e = new FeedEntry { At = Now };
        if (vStructure != null)
        {
            if (kPlayer == null) return;   // structures only when a player destroyed them (harvesting / AI spam otherwise)
            AddActor(e, kUnit, kPlayer);
            e.Parts.Add(new FeedPart { Text = " destroyed ", Color = FeedNeutral });
            e.Parts.Add(new FeedPart { Text = UnitName(vStructure) + " (" + TeamLabel(vStructure.Team) + ")", Color = TeamTint(vStructure.Team) });
        }
        else if (vUnit != null)
        {
            if (KillFeedFilter == "players" && kPlayer == null && vPlayer == null) return;
            bool self = kUnit != null && kUnit.Pointer == vUnit.Pointer;
            if (source == null || self)
            {
                AddActor(e, vUnit, vPlayer);
                e.Parts.Add(new FeedPart { Text = self ? " died (own fault)" : " died", Color = FeedNeutral });
            }
            else
            {
                if (kUnit != null) AddActor(e, kUnit, kPlayer);
                else if (kStructure != null) e.Parts.Add(new FeedPart { Text = UnitName(kStructure) + " (" + TeamLabel(kStructure.Team) + ")", Color = TeamTint(kStructure.Team) });
                else e.Parts.Add(new FeedPart { Text = "unknown", Color = FeedNeutral });
                e.Parts.Add(new FeedPart { Text = "  ->  ", Color = FeedNeutral });
                AddActor(e, vUnit, vPlayer);
                Team kTeam = kUnit != null ? kUnit.Team : kStructure != null ? kStructure.Team : null;
                if (kTeam != null && vUnit.Team != null && kTeam.Pointer == vUnit.Team.Pointer)
                    e.Parts.Add(new FeedPart { Text = "  (TK)", Color = new Color(1f, .45f, .35f, 1) });
            }
        }
        else return;
        e.KillerUnit = kUnit != null ? kUnit.Pointer : IntPtr.Zero; e.VictimUnit = vUnit != null ? vUnit.Pointer : IntPtr.Zero;
        e.KillerPlayer = kPlayer != null ? kPlayer.Pointer : IntPtr.Zero; e.VictimPlayer = vPlayer != null ? vPlayer.Pointer : IntPtr.Zero;
        feed.Add(e);
        int max = Math.Clamp(killFeedLines?.Value ?? 8, 1, 30);
        while (feed.Count > max) feed.RemoveAt(0);
        Write("Kill: " + string.Concat(e.Parts.Select(p => p.Text)));
    }
    private bool InvolvesFollowed(FeedEntry e)
    {
        var unit = TargetUnit();
        if (unit != null && (unit.Pointer == e.KillerUnit || unit.Pointer == e.VictimUnit)) return true;
        return target != null && (target.Pointer == e.KillerPlayer || target.Pointer == e.VictimPlayer);
    }
    // Top right, below the map-toolbar height; newest at the bottom; lives KillFeedSeconds, then fades over 1.5 s.
    private void DrawKillFeed()
    {
        if (!killFeed.Value || feed.Count == 0) return;
        EnsureTagStyles();
        float life = Math.Clamp(killFeedSeconds?.Value ?? 10f, 2f, 120f);
        feed.RemoveAll(f => Now - f.At > life + 1.5);
        if (feed.Count == 0) return;
        tagSmallStyle.fontSize = 14;
        float lineH = 22, y = 104, right = Screen.width - 12;
        foreach (var e in feed)
        {
            double age = Now - e.At;
            float alpha = age <= life ? 1f : Mathf.Clamp01(1f - (float)(age - life) / 1.5f);
            float w = 12;
            foreach (var p in e.Parts) w += tagSmallStyle.CalcSize(new GUIContent(p.Text)).x;
            float x = right - w;
            bool mine = InvolvesFollowed(e);
            Fill(new Rect(x, y, w, lineH), mine ? new Color(.35f, .55f, .75f, .6f * alpha) : new Color(0, 0, 0, .45f * alpha));
            float cx = x + 6;
            foreach (var p in e.Parts)
            {
                float pw = tagSmallStyle.CalcSize(new GUIContent(p.Text)).x;
                var c = p.Color; c.a = alpha;
                var oldAlign = tagSmallStyle.alignment;
                tagSmallStyle.alignment = TextAnchor.MiddleLeft;
                ShadowedAlpha(new Rect(cx, y, pw + 2, lineH), p.Text, tagSmallStyle, c, alpha);
                tagSmallStyle.alignment = oldAlign;
                cx += pw;
            }
            y += lineH + 2;
        }
    }
    private void ShadowedAlpha(Rect rect, string text, GUIStyle style, Color color, float alpha)
    {
        style.normal.textColor = new Color(0, 0, 0, .85f * alpha);
        GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, style);
        style.normal.textColor = color;
        GUI.Label(rect, text, style);
    }
}
