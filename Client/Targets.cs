using Il2Cpp;
using UnityEngine;

namespace SiSpectator;

// Who is watched. A PLAYER target is followed through player.ControlledUnit every frame, so a death or a vehicle change
// keeps the follow (the camera holds while the player respawns). An AI target (FollowAiUnits) is the Unit itself: no
// Player behind it, and when it dies the camera goes back to Free. Selection never touches the game state.
public sealed partial class ClientMod
{
    private Player target;           // selected player (follow modes view them)
    private Unit aiTarget;           // or: a selected AI-controlled unit (target is then null)
    private string targetName = "";
    // Unit under the crosshair dot in Free / Overview / Fixed (refreshed ~15 times a second); its tag is highlighted.
    private Player hoverPlayer; private Unit hoverUnit; private double hoverAt;
    private bool HasTarget => target != null || aiTarget != null;

    private static string SafeName(Player p)
    {
        try { return p == null ? "-" : Plain(p.PlayerName ?? "?"); } catch { return "?"; }
    }
    internal static string Plain(string text) => System.Text.RegularExpressions.Regex.Replace(text ?? "", "<[^>]*>", "");
    private static bool Alive(Unit u)
    {
        try { return u != null && !u.IsDestroyed; } catch { return false; }
    }
    private Unit TargetUnit()
    {
        try
        {
            if (target == null) return Alive(aiTarget) ? aiTarget : null;
            var unit = target.ControlledUnit;
            return Alive(unit) ? unit : null;
        }
        catch { return null; }
    }
    private static string AiLabel(Unit u)
    {
        try
        {
            if (u == null) return "AI";
            var team = u.Team;
            string name = UnitName(u);
            return "AI \u00b7 " + (name.Length > 0 ? name : "unit") + (team != null ? " (" + TeamLabel(team) + ")" : "");
        }
        catch { return "AI"; }
    }
    // For log lines and the camera notes.
    private string TargetDesc() => target != null ? SafeName(target) : aiTarget != null ? AiLabel(aiTarget) : "-";
    private string WaitingNote() => target != null ? "waiting for " + SafeName(target) + " to (re)spawn" : aiTarget != null ? "AI unit destroyed" : "no target";

    // Players of the match in team order (team index, then name), spectators / lobby players (no team) left out.
    private List<Player> OrderedPlayers()
    {
        var list = new List<Player>();
        var all = Player.Players;
        if (all == null) return list;
        var me = Player.CurrentPlayer;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (p == null || p.Team == null || (me != null && p.Pointer == me.Pointer)) continue;
            list.Add(p);
        }
        list.Sort(ComparePlayers);
        return list;
    }
    private static int ComparePlayers(Player a, Player b)
    {
        int ta = a.Team == null ? int.MaxValue : a.Team.Index, tb = b.Team == null ? int.MaxValue : b.Team.Index;
        if (ta != tb) return ta.CompareTo(tb);
        return string.Compare(SafeName(a), SafeName(b), StringComparison.OrdinalIgnoreCase);
    }

    private void SelectTarget(Player p, string why)
    {
        target = p; aiTarget = null; targetName = SafeName(p);
        Write("Target " + targetName + (p != null && p.Team != null ? " (" + p.Team.TeamShortName + ")" : "") + " (" + why + ").");
    }
    private void SelectAi(Unit unit, string why)
    {
        target = null; aiTarget = unit; targetName = AiLabel(unit);
        Write("Target " + targetName + " (" + why + ").");
    }
    private void ClearTarget() { target = null; aiTarget = null; targetName = ""; }
    // Follow the selection in the last follow mode used (Auto at first).
    private void FollowSelected(string why)
    {
        if (!HasTarget && !PickFirstTarget()) { Write("Nobody to follow (" + why + ")."); return; }
        SetMode(IsFollowMode(mode) ? mode : lastFollow, why);
    }
    // G: the hovered unit if there is one, else the current selection.
    private void FollowHoveredOrSelected(string why)
    {
        if (!IsFollowMode(mode) && PickUnderCrosshair(out var player, out var unit)) { FollowPick(player, unit, why); return; }
        FollowSelected(why);
    }
    private void FollowPick(Player player, Unit unit, string why)
    {
        if (player != null) SelectTarget(player, why); else SelectAi(unit, why);
        SetMode(lastFollow, why);
    }
    private bool PickFirstTarget()
    {
        foreach (var p in OrderedPlayers()) if (Alive(p.ControlledUnit)) { SelectTarget(p, "first player"); return true; }
        return false;
    }
    // [ / ]: previous / next player with a living unit, in team order, wrapping across teams.
    private void StepTarget(int direction)
    {
        var players = OrderedPlayers().Where(p => Alive(p.ControlledUnit) || (target != null && p.Pointer == target.Pointer)).ToList();
        if (players.Count == 0) { Write("No player with a unit to step to."); return; }
        int index = target == null ? -1 : players.FindIndex(p => p.Pointer == target.Pointer);
        int next = index < 0 ? (direction > 0 ? 0 : players.Count - 1) : ((index + direction) % players.Count + players.Count) % players.Count;
        SelectTarget(players[next], direction > 0 ? "next" : "previous");
        if (!IsFollowMode(mode) && mode != CamMode.Overview) SetMode(lastFollow, direction > 0 ? "next player" : "previous player");
        else if (mode == CamMode.Overview) overviewTracks = true;
    }
    // Packet V from the server (/spectate view <player>): select and enter Shoulder.
    private void ViewRequest(ulong networkId, int channel)
    {
        var all = Player.Players;
        for (int i = 0; all != null && i < all.Count; i++)
        {
            var p = all[i];
            if (p == null || p.PlayerID.SteamID.m_SteamID != networkId || p.PlayerChannel != channel) continue;
            if (p.Team == null) { Write("V: " + SafeName(p) + " has no team, nothing to view."); return; }
            SelectTarget(p, "server V");
            SetMode(lastFollow, "server V");
            return;
        }
        Write("V: player " + networkId + "/" + channel + " not found.");
    }

    // Left click (cursor locked): a ray from the camera through the crosshair; the Unit on the hit collider's parents is
    // picked - its controlling player if it has one, else (FollowAiUnits) the AI unit itself. A miss falls back to the unit
    // closest to the crosshair on screen (within 6 % of the screen height) so that distant infantry can be clicked;
    // player units win over AI units there.
    // One click follows at once (no select-then-follow step). While following, a click releases back to Free with the
    // camera left where it is (handled in HandleKeys).
    private void ClickPick()
    {
        if (!PickUnderCrosshair(out var player, out var unit)) return;
        FollowPick(player, unit, "click");
    }
    private void UpdateHover()
    {
        bool want = Active && !IsFollowMode(mode) && !panelOpen && !hideOverlays && clickPicking.Value && !Cursor.visible && !LargeMapOpen();
        if (!want) { hoverPlayer = null; hoverUnit = null; return; }
        if (Now < hoverAt) return;
        hoverAt = Now + 1.0 / 15;
        if (PickUnderCrosshair(out var player, out var unit)) { hoverPlayer = player; hoverUnit = unit; }
        else { hoverPlayer = null; hoverUnit = null; }
    }
    private bool IsHovered(Unit u) => hoverUnit != null && u != null && hoverUnit.Pointer == u.Pointer;
    private bool AiPicking => followAiUnits?.Value ?? true;
    // A unit we may pick: alive, visible under the fog perspective, and either player-controlled (another player on a
    // team) or AI (when AI picking is on). Returns the player for a player unit.
    private bool Pickable(Unit unit, Player me, out Player player)
    {
        player = null;
        if (!Alive(unit) || (fogTeam != null && !unit.IsVisible)) return false;
        var by = unit.ControlledBy;
        if (by != null)
        {
            if (by.Team == null || (me != null && by.Pointer == me.Pointer)) return false;
            player = by; return true;
        }
        return AiPicking;
    }
    private bool PickUnderCrosshair(out Player player, out Unit unit)
    {
        player = null; unit = null;
        var cam = Game.CurrentCamera;
        if (cam == null) return false;
        var center = new Vector3(Screen.width * .5f, Screen.height * .5f, 0);
        var me = Player.CurrentPlayer;
        try
        {
            var ray = cam.ScreenPointToRay(center);
            if (Physics.Raycast(ray, out RaycastHit hit, 3000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) && hit.collider != null)
            {
                var hitUnit = hit.collider.GetComponentInParent<Unit>();
                if (hitUnit != null && Pickable(hitUnit, me, out player)) { unit = hitUnit; return true; }
                player = null;
            }
        }
        catch (Exception error) { Error(error); }
        float limit = Screen.height * .06f, bestDist = limit;
        foreach (var p in OrderedPlayers())
        {
            var u = p.ControlledUnit;
            if (!Alive(u) || (fogTeam != null && !u.IsVisible)) continue;
            float d = ScreenDistance(cam, u, center);
            if (d < bestDist) { bestDist = d; player = p; unit = u; }
        }
        if (player != null || !AiPicking) return player != null;
        bestDist = limit;
        foreach (var team in MatchTeams())
        {
            var units = team.Units;
            for (int i = 0; units != null && i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.ControlledBy != null || !Pickable(u, me, out _)) continue;
                float d = ScreenDistance(cam, u, center);
                if (d < bestDist) { bestDist = d; unit = u; }
            }
        }
        return unit != null;
    }
    private static float ScreenDistance(Camera cam, Unit u, Vector3 center)
    {
        Vector3 s = cam.WorldToScreenPoint(u.WorldPhysicalCenter);
        if (s.z <= 0) return float.MaxValue;
        return Vector2.Distance(new Vector2(s.x, s.y), new Vector2(center.x, center.y));
    }

    // 4 Hz: drop a target that left the server or joined no team any more, or an AI target that died.
    private void PollTargets()
    {
        if (target == null && aiTarget != null)
        {
            if (Alive(aiTarget)) return;
            Write("AI target " + targetName + " destroyed.");
            ClearTarget();
            if (IsFollowMode(mode)) SetMode(CamMode.Free, "AI target destroyed");
            else if (mode == CamMode.Overview) overviewTracks = false;
            return;
        }
        if (target == null) return;
        bool present = false;
        try
        {
            var all = Player.Players;
            for (int i = 0; all != null && i < all.Count; i++) if (all[i] != null && all[i].Pointer == target.Pointer) { present = true; break; }
            if (present && target.Team == null) present = false;
        }
        catch { present = false; }
        if (present) return;
        Write("Target " + targetName + " left the match.");
        ClearTarget();
        if (IsFollowMode(mode)) SetMode(CamMode.Free, "target gone");
        else if (mode == CamMode.Overview) overviewTracks = false;
    }

    // Commander marker: the team's HQ (Team.BaseStructure) when that team's commander is a player.
    private readonly Dictionary<int, Structure> hqCache = new();
    private double hqCacheAt = -100;
    private Structure HqOf(Team team)
    {
        if (team == null) return null;
        if (Now - hqCacheAt > 2) { hqCache.Clear(); hqCacheAt = Now; }
        int key = team.Index;
        if (hqCache.TryGetValue(key, out var cached)) return cached != null && !cached.IsDestroyed ? cached : null;
        Structure found = null;
        try
        {
            var info = team.BaseStructure;
            var list = team.Structures;
            for (int i = 0; info != null && list != null && i < list.Count; i++)
            {
                var s = list[i];
                if (s != null && !s.IsDestroyed && s.ObjectInfo != null && s.ObjectInfo.Pointer == info.Pointer) { found = s; break; }
            }
        }
        catch { }
        hqCache[key] = found;
        return found;
    }
    private Structure CommanderHq(Player p)
    {
        try { return p != null && p.Team != null && p.IsCommander ? HqOf(p.Team) : null; } catch { return null; }
    }
}
