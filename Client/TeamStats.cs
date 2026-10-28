using Il2Cpp;
using UnityEngine;

namespace SiSpectator;

// Team stats strip under the large-map toolbar (spectating, map open, MapTeamStats, not hidden by H). Read only.
//   cash   = Team.TotalResources (m_StoredResources + m_StartingResources, Team.cs 0947:386 / 106:356) of
//            Team.ResourceCapacity (0947:389 / 106:359), like the vanilla RTS resource panel. The starting part arrives
//            by the team RPC SYNCH_STARTINGRESOURCES, a broadcast (Team.SendRPCPacket -> NetworkLayer.SendPacket); the
//            stored part is recomputed on each client from the structures' ResourceHolder.AmountStored, whose
//            RPC_SynchAmount also goes to every client (NetworkComponent.SendRPCPacket -> NetworkLayer.SendPacket).
//   earned = Team.ExtractedResources (0947:362 / 106:332, SYNCH_EXTRACTEDRESOURCES broadcast).
//   units  = Team.Units by ObjectInfo.UnitType / UnitWeight: Inf = Infantry; Eco = Harvester / Transport / Repair;
//            everything else by weight (Light / Medium / Heavy; Any counts as Med). Destroyed units are skipped.
public sealed partial class ClientMod
{
    private sealed class TeamCounts { public int Inf, Light, Med, Heavy, Eco; }
    private readonly Dictionary<IntPtr, TeamCounts> teamCounts = new();
    private double teamCountsAt = -100;
    private bool teamStatsLogged;

    private TeamCounts CountsFor(Team team)
    {
        if (!teamCounts.TryGetValue(team.Pointer, out var c)) teamCounts[team.Pointer] = c = new TeamCounts();
        return c;
    }
    private void RefreshTeamCounts(List<Team> teams)
    {
        if (Now - teamCountsAt < .5) return;
        teamCountsAt = Now;
        teamCounts.Clear();
        foreach (var team in teams)
        {
            var c = CountsFor(team);
            var units = team.Units;
            for (int i = 0; units != null && i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.IsDestroyed) continue;
                var info = u.ObjectInfo;
                if (info == null) continue;
                var type = info.UnitType;
                if (type == UnitType.Infantry) c.Inf++;
                else if (type == UnitType.Harvester || type == UnitType.Transport || type == UnitType.Repair) c.Eco++;
                else if (info.UnitWeight == UnitWeight.Light) c.Light++;
                else if (info.UnitWeight == UnitWeight.Heavy) c.Heavy++;
                else c.Med++;
            }
        }
    }
    // Draws the strip at y, returns its bottom.
    private float RenderTeamStats(float centerX, float y, float minWidth)
    {
        if (mapTeamStats == null || !mapTeamStats.Value) return y;
        var teams = MatchTeams();
        if (teams.Count == 0) return y;
        if (!teamStatsLogged) { teamStatsLogged = true; Write("Team stats: " + teams.Count + " teams."); }
        RefreshTeamCounts(teams);
        var lines = new List<string>();
        float w = minWidth;
        foreach (var team in teams)
        {
            var c = CountsFor(team);
            string line = TeamLabel(team) + "   cash " + team.TotalResources + "/" + team.ResourceCapacity + "   earned " + team.ExtractedResources
                + "   Inf " + c.Inf + "  Light " + c.Light + "  Med " + c.Med + "  Heavy " + c.Heavy + "  Eco " + c.Eco;
            lines.Add(line);
            w = Math.Max(w, smallStyle.CalcSize(new GUIContent(line)).x + 28);
        }
        w = Math.Min(w, Screen.width - 24);
        float rowH = 24, h = lines.Count * rowH + 10;
        var rect = new Rect(centerX - w / 2, y, w, h);
        Surface(rect, new Color(surface.r, surface.g, surface.b, .88f));
        var old = smallStyle.normal.textColor;
        try
        {
            for (int i = 0; i < lines.Count; i++)
            {
                smallStyle.normal.textColor = Color.Lerp(teams[i].TeamColor, Color.white, .3f);
                GUI.Label(new Rect(rect.x + 14, rect.y + 5 + i * rowH, rect.width - 28, rowH), lines[i], smallStyle);
            }
        }
        finally { smallStyle.normal.textColor = old; }
        mapToolbarRect = Rect.MinMaxRect(Math.Min(mapToolbarRect.xMin, rect.xMin), mapToolbarRect.yMin, Math.Max(mapToolbarRect.xMax, rect.xMax), rect.yMax);
        return rect.yMax;
    }
}
