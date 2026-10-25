using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using SiSpectator;
using Xunit;

namespace SiSpectator.Tests
{
    public class ProtocolTests
    {
        // ---- framing ----
        [Fact]
        public void Constants_AreTheContract()
        {
            Assert.Equal(125, Protocol.PacketType);
            Assert.Equal("SiSpec/1", Protocol.Marker);
            Assert.True(Protocol.MaxBody < Protocol.MaxPacket);
        }

        [Fact]
        public void TrySplit_AcceptsBoundaryLengths()
        {
            string atLimit = "X|" + new string('a', Protocol.MaxBody - 2);
            Assert.Equal(Protocol.MaxBody, atLimit.Length);
            Assert.True(Protocol.TrySplit(atLimit, out char k, out var f));
            Assert.Equal('X', k);
            Assert.Single(f);
            Assert.False(Protocol.TrySplit(atLimit + "a", out _, out _));   // oversize
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("h|1")]          // kind must be an upper-case letter
        [InlineData("HH|1")]         // kind must be one char
        [InlineData("1|x")]
        [InlineData("|x")]
        [InlineData("H|1\t")]        // control char
        [InlineData("H|é1")]    // non-ASCII
        [InlineData("H|1\u007f")]    // DEL
        public void TrySplit_RejectsMalformed(string body)
        {
            Assert.False(Protocol.TrySplit(body, out _, out var fields));
            Assert.Null(fields);
        }

        // ---- marker ----
        private static Func<int> Reader(IEnumerable<int> bytes)
        {
            var q = new Queue<int>(bytes);
            return () => q.Count > 0 ? q.Dequeue() : -1;
        }

        [Fact]
        public void ReadMarker_AcceptsTagLengthAscii()
        {
            const byte tag = 23;
            var bytes = new List<int> { tag, Protocol.Marker.Length };
            bytes.AddRange(Protocol.Marker.Select(c => (int)c));
            Assert.True(Protocol.ReadMarker(Reader(bytes), tag));
        }

        [Fact]
        public void ReadMarker_RejectsWrongTagLengthOrText()
        {
            const byte tag = 23;
            var good = new List<int> { tag, Protocol.Marker.Length };
            good.AddRange(Protocol.Marker.Select(c => (int)c));

            var wrongTag = new List<int>(good); wrongTag[0] = 22;
            var wrongLen = new List<int>(good); wrongLen[1] = Protocol.Marker.Length + 1;
            var wrongText = new List<int>(good); wrongText[wrongText.Count - 1] = '2';   // "SiSpec/2"
            var modui = new List<int> { tag, 9 }; modui.AddRange("SiModUI/2".Select(c => (int)c));
            var truncated = good.Take(5).ToList();

            Assert.False(Protocol.ReadMarker(Reader(wrongTag), tag));
            Assert.False(Protocol.ReadMarker(Reader(wrongLen), tag));
            Assert.False(Protocol.ReadMarker(Reader(wrongText), tag));
            Assert.False(Protocol.ReadMarker(Reader(modui), tag));
            Assert.False(Protocol.ReadMarker(Reader(truncated), tag));
        }

        // ---- H ----
        [Theory]
        [InlineData("0.1.0")]
        [InlineData("1.2.3-beta")]
        [InlineData("x")]
        public void Hello_RoundTrips(string version)
        {
            string body = Protocol.Hello(version);
            Assert.Equal("H|" + version, body);
            Assert.True(Protocol.TryHello(body, out string v));
            Assert.Equal(version, v);
        }

        [Theory]
        [InlineData("H")]
        [InlineData("H|")]
        [InlineData("H|1|2")]
        [InlineData("H|0.1 0")]
        [InlineData("H|0.1_0")]
        [InlineData("H|1234567890123456789012345")]   // 25 > MaxVersionChars
        [InlineData("S|on|1|0.1.0")]
        public void TryHello_RejectsMalformed(string body)
        {
            Assert.False(Protocol.TryHello(body, out string v));
            Assert.Equal("", v);
        }

        [Fact]
        public void Hello_ThrowsOnInvalidVersion()
        {
            Assert.Throws<ArgumentException>(() => Protocol.Hello("bad version"));
            Assert.Throws<ArgumentException>(() => Protocol.Hello(""));
            Assert.Throws<ArgumentException>(() => Protocol.Hello(null));
        }

        // ---- S ----
        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void State_RoundTrips(bool on, bool allowed)
        {
            string body = Protocol.State(on, allowed, "0.1.0");
            Assert.True(Protocol.TryState(body, out bool o, out bool a, out string v));
            Assert.Equal(on, o);
            Assert.Equal(allowed, a);
            Assert.Equal("0.1.0", v);
        }

        [Theory]
        [InlineData("S|on|1")]
        [InlineData("S|on|1|0.1.0|x")]
        [InlineData("S|yes|1|0.1.0")]
        [InlineData("S|ON|1|0.1.0")]
        [InlineData("S|on|2|0.1.0")]
        [InlineData("S|on|true|0.1.0")]
        [InlineData("S|on|1|0 1")]
        [InlineData("V|on|1|0.1.0")]
        public void TryState_RejectsMalformed(string body)
        {
            Assert.False(Protocol.TryState(body, out bool on, out bool allowed, out string v));
            Assert.False(on); Assert.False(allowed); Assert.Equal("", v);
        }

        [Fact]
        public void State_ThrowsOnInvalidVersion() => Assert.Throws<ArgumentException>(() => Protocol.State(true, true, "a|b"));

        // ---- V ----
        [Theory]
        [InlineData(76561198000000001UL, 0)]
        [InlineData(0UL, 255)]
        [InlineData(ulong.MaxValue, 7)]
        public void View_RoundTrips(ulong id, int channel)
        {
            string body = Protocol.View(id, channel);
            Assert.True(Protocol.TryView(body, out ulong i, out int c));
            Assert.Equal(id, i);
            Assert.Equal(channel, c);
        }

        [Theory]
        [InlineData("V|1")]
        [InlineData("V|1|0|0")]
        [InlineData("V|abc|0")]
        [InlineData("V|-1|0")]
        [InlineData("V|+1|0")]
        [InlineData("V| 1|0")]
        [InlineData("V|1|256")]
        [InlineData("V|1|-1")]
        [InlineData("V|1.5|0")]
        [InlineData("V|18446744073709551616|0")]   // ulong overflow
        public void TryView_RejectsMalformed(string body)
        {
            Assert.False(Protocol.TryView(body, out _, out _));
        }

        [Fact]
        public void View_ThrowsOnInvalidChannel()
        {
            Assert.Throws<ArgumentException>(() => Protocol.View(1, -1));
            Assert.Throws<ArgumentException>(() => Protocol.View(1, 256));
        }

        // ---- F ----
        [Theory]
        [InlineData("all")]
        [InlineData("Sol")]
        [InlineData("Centauri")]
        [InlineData("Alien")]
        [InlineData("Team_2-b")]
        public void Fog_RoundTrips(string team)
        {
            Assert.True(Protocol.TryFog(Protocol.Fog(team), out string t));
            Assert.Equal(team, t);
        }

        [Theory]
        [InlineData("F")]
        [InlineData("F|")]
        [InlineData("F|Sol|Alien")]
        [InlineData("F|Sol Team")]
        [InlineData("F|Sol.")]
        [InlineData("F|1234567890123456789012345")]   // 25 > MaxTeamChars
        public void TryFog_RejectsMalformed(string body)
        {
            Assert.False(Protocol.TryFog(body, out string t));
            Assert.Equal("", t);
        }

        [Fact]
        public void Fog_ThrowsOnInvalidTeam() => Assert.Throws<ArgumentException>(() => Protocol.Fog("two words"));

        // ---- K / C (phase 2, still part of the contract) ----
        [Fact]
        public void CameraReport_RoundTrips()
        {
            string body = Protocol.CameraReport(1.5f, -2.25f, 300f, 0f, 0.7071f, 0f, 0.7071f);
            Assert.True(Protocol.TryCameraReport(body, out var pose));
            Assert.Equal(7, pose.Length);
            Assert.Equal(1.5f, pose[0]);
            Assert.Equal(-2.25f, pose[1]);
            Assert.Equal(300f, pose[2]);
            Assert.Equal(0.707f, pose[4], 3);
        }

        [Fact]
        public void Pose_IsCultureInvariant()
        {
            var old = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string body = Protocol.CameraReport(1.5f, 0, 0, 0, 0, 0, 1);
                Assert.StartsWith("K|1.5|", body);
                Assert.True(Protocol.TryCameraReport(body, out var pose));
                Assert.Equal(1.5f, pose[0]);
            }
            finally { Thread.CurrentThread.CurrentCulture = old; }
        }

        [Theory]
        [InlineData("K|1|2|3|0|0|0")]               // 6 fields
        [InlineData("K|1|2|3|0|0|0|1|9")]           // 8 fields
        [InlineData("K|NaN|2|3|0|0|0|1")]
        [InlineData("K|1|Infinity|3|0|0|0|1")]
        [InlineData("K|1|2|-Infinity|0|0|0|1")]
        [InlineData("K|1|2|3|0|0|0|2000000")]       // > 1e6
        [InlineData("K|1|2|3|0|0|0|1.00000000000000001")]   // field > 16 chars
        [InlineData("K|1|2|3|0|0|0|x")]
        [InlineData("K|1,5|2|3|0|0|0|1")]
        public void TryCameraReport_RejectsMalformed(string body)
        {
            Assert.False(Protocol.TryCameraReport(body, out _));
        }

        [Fact]
        public void CommanderCamera_RoundTrips()
        {
            string body = Protocol.CommanderCamera("Sol", 10, 20, 30, 0, 0, 0, 1);
            Assert.True(Protocol.TryCommanderCamera(body, out string team, out var pose));
            Assert.Equal("Sol", team);
            Assert.Equal(10f, pose[0]);
            Assert.Equal(1f, pose[6]);
        }

        [Theory]
        [InlineData("C|all|1|2|3|0|0|0|1")]
        [InlineData("C|Sol|NaN|2|3|0|0|0|1")]
        [InlineData("C|Sol|1|2|3|0|0|0")]
        [InlineData("C|Sol Team|1|2|3|0|0|0|1")]
        public void TryCommanderCamera_RejectsMalformed(string body)
        {
            Assert.False(Protocol.TryCommanderCamera(body, out _, out _));
        }

        [Fact]
        public void CommanderCamera_ThrowsForAll() => Assert.Throws<ArgumentException>(() => Protocol.CommanderCamera("all", 0, 0, 0, 0, 0, 0, 1));

        // ---- cross-kind: every parser only accepts its own kind ----
        [Fact]
        public void Parsers_RejectOtherKinds()
        {
            string hello = Protocol.Hello("1.0"), state = Protocol.State(true, true, "1.0"), view = Protocol.View(5, 0), fog = Protocol.Fog("Sol");
            Assert.False(Protocol.TryHello(state, out _));
            Assert.False(Protocol.TryState(hello, out _, out _, out _));
            Assert.False(Protocol.TryView(fog, out _, out _));
            Assert.False(Protocol.TryFog(view, out _));
        }
    }
}
