using HarmonyLib;
using Il2Cpp;

namespace SiSpectator;

// Fog perspective All | <team of the match>. Outside RTS mode every client already sees everything (StrategyMode's team
// is null, API_NOTES §0.2), so "All" is the vanilla state. Team X = every client holds every team's detected lists
// (API_NOTES §1), so X's view is purely local:
//   - prefix on BaseGameObject.UpdateVisibility(Team) computes the object's visibility for X itself, exactly like the
//     original (106:BaseGameObject.cs:569-598, 0947:588-617, identical bodies), calls SetVisibility and skips the
//     original. That does not depend on Il2CppInterop writing a modified argument back to the native call. Only if that
//     path throws does it fall back to rewriting the team argument (ref __0) and running the original (logged once).
//     FogOfWar.ShowFog runs UpdateVisibilityAll every update, so the view stays current,
//   - postfix on FogOfWar.GetCurrentFogTeam returns X (minimap / large-map fog texture).
// Both patches are installed the first time a team perspective is chosen and are inert (return at once) otherwise, so a
// client that never uses it pays nothing for the per-object hook.
public sealed partial class ClientMod
{
    private Team fogTeam;            // null = All
    private bool fogPatched, fogPatchFailed;

    private static class FogPatches
    {
        private static bool computeFailed;
        public static bool VisibilityPrefix(BaseGameObject __instance, ref Team __0)
        {
            var t = instance?.fogTeam;
            if (t == null || !Active || __instance == null) return true;
            if (!computeFailed)
            {
                try { ApplyVisibility(__instance, t); return false; }
                catch (Exception error)
                {
                    computeFailed = true;
                    instance.Write("Fog perspective: own visibility computation failed, falling back to the argument rewrite. " + error.GetType().Name + ": " + error.Message);
                }
            }
            __0 = t;
            return true;
        }
        // Mirror of BaseGameObject.UpdateVisibility(Team strategyTeam) with strategyTeam = the chosen perspective.
        private static void ApplyVisibility(BaseGameObject obj, Team team)
        {
            bool isVisible = true, staticVisible = true;
            var target = obj.Target;
            if (target == null || !target.isActiveAndEnabled) { isVisible = false; staticVisible = false; }
            else if (GameMode.CurrentGameMode != null && GameMode.CurrentGameMode.GetIsObjectAlwaysVisible(obj, team)) { isVisible = true; staticVisible = true; }
            else if (!obj.IsDestroyed && !SameTeam(team, obj.Team))
            {
                int index = team.DetectedTargets.IndexOf(target);
                if (index <= -1) { isVisible = false; staticVisible = false; }
                else { isVisible = team.GetIsTargetAtIndexActivelyVisible(index); staticVisible = true; }
            }
            obj.SetVisibility(isVisible, staticVisible, obj.GetIsRendered(isVisible, staticVisible));
        }
        private static bool SameTeam(Team a, Team b) => b != null && a.Pointer == b.Pointer;
        public static void FogTeamPostfix(ref Team __result)
        {
            var t = instance?.fogTeam;
            if (t != null && Active) __result = t;
        }
    }
    private bool EnsureFogPatches()
    {
        if (fogPatched) return true;
        if (fogPatchFailed) return false;
        try
        {
            var visibility = AccessTools.Method(typeof(BaseGameObject), nameof(BaseGameObject.UpdateVisibility), new[] { typeof(Team) });
            var fogTeamGetter = AccessTools.Method(typeof(FogOfWar), nameof(FogOfWar.GetCurrentFogTeam));
            if (visibility == null || fogTeamGetter == null) throw new MissingMethodException("BaseGameObject.UpdateVisibility(Team) / FogOfWar.GetCurrentFogTeam not found");
            HarmonyInstance.Patch(visibility, prefix: new HarmonyMethod(AccessTools.Method(typeof(FogPatches), nameof(FogPatches.VisibilityPrefix))));
            HarmonyInstance.Patch(fogTeamGetter, postfix: new HarmonyMethod(AccessTools.Method(typeof(FogPatches), nameof(FogPatches.FogTeamPostfix))));
            fogPatched = true;
            Write("Fog perspective patches installed (UpdateVisibility prefix, GetCurrentFogTeam postfix).");
            return true;
        }
        catch (Exception error) { fogPatchFailed = true; Error(error); Write("Fog perspective unavailable: patching failed."); return false; }
    }

    // Teams of the match: active team setups of the game mode plus any team a player is on, in team order.
    private List<Team> MatchTeams()
    {
        var teams = new List<Team>();
        try
        {
            var mode = GameMode.CurrentGameMode == null ? null : GameMode.CurrentGameMode.TryCast<GameModeExt>();
            var setups = mode == null ? null : mode.BaseTeamSetups;
            for (int i = 0; setups != null && i < setups.Count; i++)
            {
                var setup = setups[i];
                if (setup == null || setup.Team == null || mode.GetTeamSetup(setup.Team) == null) continue;
                AddTeam(teams, setup.Team);
            }
            var players = Player.Players;
            for (int i = 0; players != null && i < players.Count; i++) if (players[i] != null && players[i].Team != null) AddTeam(teams, players[i].Team);
        }
        catch (Exception error) { Error(error); }
        teams.Sort((a, b) => a.Index.CompareTo(b.Index));
        return teams;
    }
    private static void AddTeam(List<Team> teams, Team team)
    {
        foreach (var t in teams) if (t.Pointer == team.Pointer) return;
        teams.Add(team);
    }
    private static string TeamLabel(Team t) => t == null ? "All" : Plain(string.IsNullOrEmpty(t.TeamShortName) ? t.TeamName : t.TeamShortName);

    private void CycleFog()
    {
        var teams = MatchTeams();
        if (teams.Count == 0) { SetFog(null, "cycle (no teams)"); return; }
        int index = fogTeam == null ? -1 : teams.FindIndex(t => t.Pointer == fogTeam.Pointer);
        SetFog(index + 1 >= teams.Count ? null : teams[index + 1], "key " + kFog);
    }
    private void SetFog(Team team, string why)
    {
        if (team != null && !EnsureFogPatches()) team = null;
        bool same = (team == null && fogTeam == null) || (team != null && fogTeam != null && team.Pointer == fogTeam.Pointer);
        fogTeam = team;
        if (!same) RefreshVisibility();
        Write("Fog perspective " + TeamLabel(team) + " (" + why + ").");
    }
    private static void RefreshVisibility()
    {
        try { BaseGameObject.UpdateVisibilityAll(); } catch { }
    }
    private void ClearFog()
    {
        if (fogTeam == null) return;
        fogTeam = null;
        RefreshVisibility();
    }
    // Packet F from the server (/spectate fow all|sol|cent|alien): match the team short name, then a prefix of it, then
    // the long name, case-insensitive.
    private void FogRequest(string name)
    {
        if (name == "all") { SetFog(null, "server F"); return; }
        var teams = MatchTeams();
        Team found = teams.FirstOrDefault(t => string.Equals(t.TeamShortName, name, StringComparison.OrdinalIgnoreCase))
            ?? teams.FirstOrDefault(t => (t.TeamShortName ?? "").StartsWith(name, StringComparison.OrdinalIgnoreCase))
            ?? teams.FirstOrDefault(t => (t.TeamName ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        if (found == null) { Write("F: no team of this match matches '" + name + "'."); return; }
        SetFog(found, "server F");
    }
}
