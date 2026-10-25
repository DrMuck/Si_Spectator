using System.Collections.Generic;
using System.Linq;
using SiSpectator.Logic;
using Xunit;

namespace SiSpectator.Tests
{
    public class RulesTests
    {
        private static RulesSettings Defaults() => new RulesSettings();

        [Fact]
        public void Defaults_MatchThePlan()
        {
            var s = Defaults();
            Assert.True(s.Enabled); Assert.False(s.AllowAll); Assert.True(s.AllowAdmins); Assert.Equal(4, s.MaxSpectators);
        }

        [Fact]
        public void Admin_IsAllowedWithoutListEntry()
        {
            var r = SpectatorRules.CanEnter(Defaults(), isAdmin: true, onAllowedList: false, spectatorCount: 0, alreadySpectating: false, forced: false);
            Assert.True(r.Ok);
            Assert.True(SpectatorRules.IsPermitted(Defaults(), true, false));
        }

        [Fact]
        public void Admin_NotAllowedWhenAllowAdminsOff()
        {
            var s = Defaults(); s.AllowAdmins = false;
            Assert.Equal(EnterVerdict.NotAllowed, SpectatorRules.CanEnter(s, true, false, 0, false, false).Verdict);
        }

        [Fact]
        public void Player_NeedsListEntry()
        {
            var denied = SpectatorRules.CanEnter(Defaults(), false, false, 0, false, false);
            Assert.Equal(EnterVerdict.NotAllowed, denied.Verdict);
            Assert.False(string.IsNullOrEmpty(denied.Reason));
            Assert.True(SpectatorRules.CanEnter(Defaults(), false, true, 0, false, false).Ok);
        }

        [Fact]
        public void AllowAll_LetsEveryoneIn()
        {
            var s = Defaults(); s.AllowAll = true;
            Assert.True(SpectatorRules.CanEnter(s, false, false, 0, false, false).Ok);
            Assert.True(SpectatorRules.IsPermitted(s, false, false));
        }

        [Fact]
        public void Cap_BlocksEvenAdmins_ButNotForce()
        {
            var s = Defaults(); s.MaxSpectators = 2;
            Assert.True(SpectatorRules.CanEnter(s, true, false, 1, false, false).Ok);
            var full = SpectatorRules.CanEnter(s, true, false, 2, false, false);
            Assert.Equal(EnterVerdict.Full, full.Verdict);
            Assert.Contains("2/2", full.Reason);
            Assert.True(SpectatorRules.CanEnter(s, false, false, 2, false, forced: true).Ok);
            Assert.True(SpectatorRules.ForceExceedsCap(s, 2));
            Assert.False(SpectatorRules.ForceExceedsCap(s, 1));
        }

        [Fact]
        public void Cap_ZeroMeansUnlimited()
        {
            var s = Defaults(); s.MaxSpectators = 0;
            Assert.True(SpectatorRules.CanEnter(s, true, false, 500, false, false).Ok);
            Assert.False(SpectatorRules.ForceExceedsCap(s, 500));
        }

        [Fact]
        public void Disabled_BlocksEveryone_IncludingForce()
        {
            var s = Defaults(); s.Enabled = false;
            Assert.Equal(EnterVerdict.Disabled, SpectatorRules.CanEnter(s, true, true, 0, false, false).Verdict);
            Assert.Equal(EnterVerdict.Disabled, SpectatorRules.CanEnter(s, true, true, 0, false, true).Verdict);
            Assert.False(SpectatorRules.IsPermitted(s, true, true));
        }

        [Fact]
        public void AlreadySpectating_IsReported()
        {
            Assert.Equal(EnterVerdict.Already, SpectatorRules.CanEnter(Defaults(), true, false, 1, true, false).Verdict);
            Assert.Equal(EnterVerdict.Already, SpectatorRules.CanEnter(Defaults(), true, false, 1, true, true).Verdict);
        }

        [Fact]
        public void NullSettings_AreDisabled()
        {
            Assert.Equal(EnterVerdict.Disabled, SpectatorRules.CanEnter(null, true, true, 0, false, true).Verdict);
            Assert.False(SpectatorRules.IsPermitted(null, true, true));
        }
    }

    public class AllowedListTests
    {
        [Fact]
        public void AddRemoveContains()
        {
            var l = new AllowedList();
            Assert.True(l.Add(11, "Alpha"));
            Assert.False(l.Add(11, "Alpha2"));     // already listed: name refreshed
            Assert.Equal("Alpha2", l.NameOf(11));
            Assert.True(l.Contains(11));
            Assert.Equal(1, l.Count);
            Assert.True(l.Remove(11));
            Assert.False(l.Remove(11));
            Assert.False(l.Contains(11));
        }

        [Fact]
        public void ZeroSteamId_IsIgnored()
        {
            var l = new AllowedList();
            Assert.False(l.Add(0, "x"));
            Assert.Equal(0, l.Count);
        }

        [Fact]
        public void Constructor_Dedupes()
        {
            var l = new AllowedList(new[] { new KeyValuePair<ulong, string>(1, "a"), new KeyValuePair<ulong, string>(1, "b"), new KeyValuePair<ulong, string>(2, "c") });
            Assert.Equal(2, l.Count);
            Assert.Equal("b", l.NameOf(1));
        }

        [Fact]
        public void Rename_OnlyWhenChanged()
        {
            var l = new AllowedList(); l.Add(5, "old");
            Assert.True(l.Rename(5, "new"));
            Assert.False(l.Rename(5, "new"));
            Assert.False(l.Rename(6, "x"));
            Assert.False(l.Rename(5, ""));
            Assert.Equal("new", l.NameOf(5));
        }

        [Fact]
        public void CleanName_StripsControlCharsAndTrims()
        {
            Assert.Equal("ab", AllowedList.CleanName(" a\nb "));
            Assert.Equal("", AllowedList.CleanName(null));
            Assert.Equal(64, AllowedList.CleanName(new string('x', 100)).Length);
        }
    }

    public class NameMatchTests
    {
        private sealed class P { public string N; public P(string n) { N = n; } }
        private static readonly List<P> Players = new List<P> { new P("DrMuck"), new P("Muckel"), new P("Bob"), new P("bobby") };

        [Fact]
        public void ExactMatchWins_CaseInsensitive()
        {
            Assert.Equal("Bob", NameMatch.Find(Players, p => p.N, "BOB", out _).N);
            Assert.Equal("DrMuck", NameMatch.Find(Players, p => p.N, "drmuck", out _).N);
        }

        [Fact]
        public void UniqueSubstring_Matches()
        {
            Assert.Equal("Muckel", NameMatch.Find(Players, p => p.N, "kel", out _).N);
        }

        [Fact]
        public void Ambiguous_And_Missing_Explain()
        {
            Assert.Null(NameMatch.Find(Players, p => p.N, "muck", out string why));
            Assert.Contains("matches 2 players", why);
            Assert.Null(NameMatch.Find(Players, p => p.N, "zed", out why));
            Assert.Contains("no player matches", why);
            Assert.Null(NameMatch.Find(Players, p => p.N, "  ", out why));
        }
    }

    public class StateTests
    {
        [Fact]
        public void Enter_Twice_IsIdempotent()
        {
            var s = new SpectatorState();
            Assert.True(s.Enter(7));
            Assert.False(s.Enter(7));
            Assert.Equal(1, s.Count);
            Assert.True(s.IsSpectating(7));
            Assert.Equal("all", s.FogOf(7));
        }

        [Fact]
        public void Enter_ZeroId_Rejected()
        {
            var s = new SpectatorState();
            Assert.False(s.Enter(0));
            Assert.Equal(0, s.Count);
        }

        [Fact]
        public void Leave_WhenNotIn_IsFalse()
        {
            var s = new SpectatorState();
            Assert.False(s.Leave(7));
            s.Enter(7);
            Assert.True(s.Leave(7));
            Assert.False(s.Leave(7));
            Assert.False(s.IsSpectating(7));
        }

        [Fact]
        public void TeamChange_ToRealTeam_Clears_ToNull_Keeps()
        {
            var s = new SpectatorState();
            s.Enter(7);
            Assert.False(s.OnTeamChanged(7, newTeamIsSet: false));   // our own move / restart ClearGame: stays
            Assert.True(s.IsSpectating(7));
            Assert.True(s.OnTeamChanged(7, newTeamIsSet: true));     // faction screen / !reserve / !team
            Assert.False(s.IsSpectating(7));
            Assert.False(s.OnTeamChanged(8, true));                   // not a spectator: nothing
        }

        [Fact]
        public void Hello_IsRateLimited_PerClient()
        {
            var s = new SpectatorState();
            Assert.True(s.AcceptHello(1, 10.0, "0.1.0"));
            Assert.False(s.AcceptHello(1, 10.5, "0.1.0"));
            Assert.True(s.AcceptHello(2, 10.5, "0.1.0"));             // another client is independent
            Assert.True(s.AcceptHello(1, 11.0, "0.1.1"));
            Assert.Equal("0.1.1", s.ClientVersion(1));
            Assert.True(s.HasClient(1));
            Assert.False(s.HasClient(3));
            Assert.True(s.AcceptHello(1, 5.0, "0.1.1"));              // clock went backwards: accept, do not lock out
        }

        [Fact]
        public void Fog_OnlyForSpectators_AndResetOnLeave()
        {
            var s = new SpectatorState();
            s.SetFog(7, "sol");
            Assert.Equal("all", s.FogOf(7));
            s.Enter(7);
            s.SetFog(7, "sol");
            Assert.Equal("sol", s.FogOf(7));
            s.Leave(7);
            s.Enter(7);
            Assert.Equal("all", s.FogOf(7));
        }

        [Fact]
        public void FirstDrop_LogsOncePerPlayerAndType()
        {
            var s = new SpectatorState();
            Assert.True(s.FirstDrop(7, 4));
            Assert.False(s.FirstDrop(7, 4));
            Assert.True(s.FirstDrop(7, 5));
            Assert.True(s.FirstDrop(8, 4));
        }

        [Fact]
        public void Forget_ClearsEverything()
        {
            var s = new SpectatorState();
            s.Enter(7); s.AcceptHello(7, 1, "0.1.0"); s.FirstDrop(7, 4); s.SetFog(7, "cent");
            Assert.True(s.Forget(7));
            Assert.False(s.IsSpectating(7));
            Assert.False(s.HasClient(7));
            Assert.True(s.FirstDrop(7, 4));
            Assert.True(s.AcceptHello(7, 1.1, "0.1.0"));             // rate limit memory gone too
            Assert.False(s.Forget(9));
        }

        [Fact]
        public void Version_ChangesOnStateChanges()
        {
            var s = new SpectatorState();
            int v0 = s.Version;
            s.Enter(7); int v1 = s.Version;
            s.Enter(7); int v2 = s.Version;
            s.Leave(7); int v3 = s.Version;
            Assert.True(v1 > v0);
            Assert.Equal(v1, v2);
            Assert.True(v3 > v2);
            Assert.Equal(new ulong[0], s.Spectators);
        }
    }

    public class CommandTests
    {
        [Theory]
        [InlineData("/spectate", SpectateAction.Toggle)]
        [InlineData("!spectate", SpectateAction.Toggle)]
        [InlineData("/spec", SpectateAction.Toggle)]
        [InlineData("/spectate on", SpectateAction.On)]
        [InlineData("/spectate ON", SpectateAction.On)]
        [InlineData("/spectate off", SpectateAction.Off)]
        [InlineData("/spectate status", SpectateAction.Status)]
        [InlineData("/spectate help", SpectateAction.Help)]
        [InlineData("/play", SpectateAction.Off)]
        [InlineData("/play whatever", SpectateAction.Off)]
        [InlineData("/spectate dance", SpectateAction.Invalid)]
        [InlineData("/spectate view", SpectateAction.Invalid)]
        [InlineData("/spectate fow", SpectateAction.Invalid)]
        [InlineData("/spectate fow wildlife", SpectateAction.Invalid)]
        [InlineData("", SpectateAction.Toggle)]
        [InlineData(null, SpectateAction.Toggle)]
        public void Spectate_Actions(string line, SpectateAction expected)
        {
            var c = SpectateCommand.Parse(line);
            Assert.Equal(expected, c.Action);
            if (expected == SpectateAction.Invalid) Assert.StartsWith("usage", c.Error);
        }

        [Fact]
        public void Spectate_View_KeepsMultiWordName()
        {
            var c = SpectateCommand.Parse("/spectate view  Dr  Muck ");
            Assert.Equal(SpectateAction.View, c.Action);
            Assert.Equal("Dr Muck", c.Arg);
        }

        [Theory]
        [InlineData("all", "all")]
        [InlineData("ALL", "all")]
        [InlineData("none", "all")]
        [InlineData("sol", "sol")]
        [InlineData("cent", "cent")]
        [InlineData("Centauri", "cent")]
        [InlineData("alien", "alien")]
        [InlineData("aliens", "alien")]
        public void Spectate_Fow_Canonical(string arg, string expected)
        {
            var c = SpectateCommand.Parse("/spectate fow " + arg);
            Assert.Equal(SpectateAction.Fow, c.Action);
            Assert.Equal(expected, c.Arg);
        }

        [Theory]
        [InlineData("!spectator", AdminAction.Help)]
        [InlineData("!spectator help", AdminAction.Help)]
        [InlineData("!spectator list", AdminAction.List)]
        [InlineData("!spectator status", AdminAction.Status)]
        [InlineData("!spectator reload", AdminAction.Reload)]
        [InlineData("!spectator allow", AdminAction.Invalid)]
        [InlineData("!spectator deny", AdminAction.Invalid)]
        [InlineData("!spectator force", AdminAction.Invalid)]
        [InlineData("!spectator kick", AdminAction.Invalid)]
        [InlineData("!spectator all", AdminAction.Invalid)]
        [InlineData("!spectator all maybe", AdminAction.Invalid)]
        [InlineData("!spectator max", AdminAction.Invalid)]
        [InlineData("!spectator max -1", AdminAction.Invalid)]
        [InlineData("!spectator max 65", AdminAction.Invalid)]
        [InlineData("!spectator max four", AdminAction.Invalid)]
        [InlineData("!spectator frobnicate", AdminAction.Invalid)]
        public void Admin_Actions(string line, AdminAction expected)
        {
            var c = SpectatorAdminCommand.Parse(line);
            Assert.Equal(expected, c.Action);
            if (expected == AdminAction.Invalid) Assert.StartsWith("usage", c.Error);
        }

        [Theory]
        [InlineData("!spectator allow Bob", AdminAction.Allow, "Bob")]
        [InlineData("!spectator deny Dr Muck", AdminAction.Deny, "Dr Muck")]
        [InlineData("!spectator force  bob ", AdminAction.Force, "bob")]
        [InlineData("/spectator kick Bob", AdminAction.Kick, "Bob")]
        public void Admin_TargetCommands(string line, AdminAction expected, string arg)
        {
            var c = SpectatorAdminCommand.Parse(line);
            Assert.Equal(expected, c.Action);
            Assert.Equal(arg, c.Arg);
        }

        [Theory]
        [InlineData("on", true)]
        [InlineData("off", false)]
        [InlineData("TRUE", true)]
        [InlineData("0", false)]
        public void Admin_All(string v, bool on)
        {
            var c = SpectatorAdminCommand.Parse("!spectator all " + v);
            Assert.Equal(AdminAction.All, c.Action);
            Assert.Equal(on, c.On);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(4)]
        [InlineData(64)]
        public void Admin_Max(int n)
        {
            var c = SpectatorAdminCommand.Parse("!spectator max " + n);
            Assert.Equal(AdminAction.Max, c.Action);
            Assert.Equal(n, c.Number);
        }
    }
}
