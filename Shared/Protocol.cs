using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SiSpectator
{
    // Si_Spectator wire format v1. Compiled into the Mono server mod and the Il2Cpp client mod. No Unity or game types
    // here so the xunit tests can cover it.
    //
    // One native packet: byte PacketType (125), string Marker, string body (body <= MaxBody chars, ASCII, '|' fields).
    // Unknown packet types are ignored by the game (NetworkLayer.ProcessMessage has no default case), and Si_ModUI's
    // hooks on 126 pass every other first byte through, so the two mods coexist.
    //
    // client -> server
    //   H|<clientVersion>                         hello: after join and after every scene load; the server answers S
    //   K|x|y|z|qx|qy|qz|qw                       own commander camera pose, <= 5/s, only while commander (phase 2)
    //
    // server -> client
    //   S|<on|off>|<allowed 0|1>|<serverVersion>  spectator state of the receiving player; sent on hello, on change
    //   V|<networkId>|<channel>                   view this player (from /spectate view <player>)
    //   F|<all|teamShortName>                     fog perspective (from /spectate fow ...)
    //   C|<teamShortName>|x|y|z|qx|qy|qz|qw       commander camera relay (phase 2)
    //
    // Nothing in a body is executed, loaded or used as a path. Every parser rejects anything it does not expect.
    public static class Protocol
    {
        public const byte PacketType = 125;
        public const string Marker = "SiSpec/1";
        public const int MaxBody = 1800, MaxPacket = 2100;
        public const int MaxVersionChars = 24, MaxTeamChars = 24;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Marker is ASCII with a single-byte native string length. Inspect raw bytes so another mod's packet 125
        // never triggers native type-error logging on our side. stringTag = (byte)EGameByteStreamType.String.
        public static bool ReadMarker(Func<int> readByte, byte stringTag)
        {
            if (readByte() != stringTag || readByte() != Marker.Length) return false;
            foreach (char c in Marker) if (readByte() != c) return false;
            return true;
        }

        public static bool ValidBody(string body) => body != null && body.Length >= 1 && body.Length <= MaxBody && body.All(c => c >= 0x20 && c < 0x7f);
        public static bool ValidVersion(string v) => v != null && v.Length >= 1 && v.Length <= MaxVersionChars && v.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-');
        public static bool ValidTeam(string t) => t == "all" || (t != null && t.Length >= 1 && t.Length <= MaxTeamChars && t.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'));

        // Splits a body into kind + fields. Returns false for anything malformed; callers switch on kind.
        public static bool TrySplit(string body, out char kind, out string[] fields)
        {
            kind = '\0'; fields = null;
            if (!ValidBody(body)) return false;
            var p = body.Split('|');
            if (p[0].Length != 1 || !char.IsUpper(p[0][0])) return false;
            kind = p[0][0]; fields = p.Skip(1).ToArray();
            return true;
        }

        // ---- client -> server ----
        public static string Hello(string clientVersion)
        {
            if (!ValidVersion(clientVersion)) throw new ArgumentException("Invalid version.");
            return "H|" + clientVersion;
        }
        public static bool TryHello(string body, out string clientVersion)
        {
            clientVersion = "";
            if (!TrySplit(body, out var k, out var f) || k != 'H' || f.Length != 1 || !ValidVersion(f[0])) return false;
            clientVersion = f[0]; return true;
        }
        public static string CameraReport(float x, float y, float z, float qx, float qy, float qz, float qw) => "K|" + Pose(x, y, z, qx, qy, qz, qw);
        public static bool TryCameraReport(string body, out float[] pose)
        {
            pose = null;
            if (!TrySplit(body, out var k, out var f) || k != 'K' || f.Length != 7 || !TryPose(f, 0, out pose)) return false;
            return true;
        }

        // ---- server -> client ----
        public static string State(bool on, bool allowed, string serverVersion)
        {
            if (!ValidVersion(serverVersion)) throw new ArgumentException("Invalid version.");
            return "S|" + (on ? "on" : "off") + "|" + (allowed ? "1" : "0") + "|" + serverVersion;
        }
        public static bool TryState(string body, out bool on, out bool allowed, out string serverVersion)
        {
            on = allowed = false; serverVersion = "";
            if (!TrySplit(body, out var k, out var f) || k != 'S' || f.Length != 3) return false;
            if (f[0] != "on" && f[0] != "off") return false;
            if (f[1] != "0" && f[1] != "1") return false;
            if (!ValidVersion(f[2])) return false;
            on = f[0] == "on"; allowed = f[1] == "1"; serverVersion = f[2]; return true;
        }
        public static string View(ulong networkId, int channel)
        {
            if (channel < 0 || channel > 255) throw new ArgumentException("Invalid channel.");
            return "V|" + networkId.ToString(Inv) + "|" + channel.ToString(Inv);
        }
        public static bool TryView(string body, out ulong networkId, out int channel)
        {
            networkId = 0; channel = 0;
            if (!TrySplit(body, out var k, out var f) || k != 'V' || f.Length != 2) return false;
            if (!ulong.TryParse(f[0], NumberStyles.None, Inv, out networkId) || !Number(f[1], 0, 255, out channel)) return false;
            return true;
        }
        public static string Fog(string team)
        {
            if (!ValidTeam(team)) throw new ArgumentException("Invalid team.");
            return "F|" + team;
        }
        public static bool TryFog(string body, out string team)
        {
            team = "";
            if (!TrySplit(body, out var k, out var f) || k != 'F' || f.Length != 1 || !ValidTeam(f[0])) return false;
            team = f[0]; return true;
        }
        public static string CommanderCamera(string team, float x, float y, float z, float qx, float qy, float qz, float qw)
        {
            if (!ValidTeam(team) || team == "all") throw new ArgumentException("Invalid team.");
            return "C|" + team + "|" + Pose(x, y, z, qx, qy, qz, qw);
        }
        public static bool TryCommanderCamera(string body, out string team, out float[] pose)
        {
            team = ""; pose = null;
            if (!TrySplit(body, out var k, out var f) || k != 'C' || f.Length != 8 || !ValidTeam(f[0]) || f[0] == "all" || !TryPose(f, 1, out pose)) return false;
            team = f[0]; return true;
        }

        // ---- helpers ----
        private static string Pose(float x, float y, float z, float qx, float qy, float qz, float qw)
            => string.Join("|", new[] { x, y, z, qx, qy, qz, qw }.Select(v => v.ToString("0.###", Inv)));
        private static bool TryPose(string[] f, int start, out float[] pose)
        {
            pose = new float[7];
            for (int i = 0; i < 7; i++)
            {
                if (f[start + i].Length > 16 || !float.TryParse(f[start + i], NumberStyles.Float, Inv, out pose[i])) return false;
                if (float.IsNaN(pose[i]) || float.IsInfinity(pose[i]) || Math.Abs(pose[i]) > 1e6f) return false;
            }
            return true;
        }
        private static bool Number(string s, int min, int max, out int value)
            => int.TryParse(s, NumberStyles.None, Inv, out value) && value >= min && value <= max;
    }
}
