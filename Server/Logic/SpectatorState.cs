using System.Collections.Generic;
using System.Linq;

namespace SiSpectator.Logic
{
    /// <summary>
    /// Runtime state of the server mod, keyed by Steam ID (never by Team == null: every lobby player is team-less too).
    /// Main thread only (chat callbacks, ProcessMessage and GameEvents all run there).
    /// </summary>
    public sealed class SpectatorState
    {
        public const double HelloInterval = 1.0;   // seconds; hello <= 1/s per client (Protocol header)

        private readonly HashSet<ulong> spectators = new HashSet<ulong>();
        private readonly Dictionary<ulong, double> lastHello = new Dictionary<ulong, double>();
        private readonly Dictionary<ulong, string> clientVersion = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> fog = new Dictionary<ulong, string>();
        private readonly HashSet<KeyValuePair<ulong, byte>> dropLogged = new HashSet<KeyValuePair<ulong, byte>>();

        /// <summary>Bumped on every change that a UI export or a log could show; cache key material.</summary>
        public int Version { get; private set; }
        public int Count => spectators.Count;
        public bool IsSpectating(ulong steamId) => spectators.Contains(steamId);
        public ulong[] Spectators => spectators.ToArray();

        /// <summary>True when the player was not spectating before (the caller then runs the team-less sequence and sends S|on).</summary>
        public bool Enter(ulong steamId)
        {
            if (steamId == 0 || !spectators.Add(steamId)) return false;
            fog[steamId] = "all";
            Version++;
            return true;
        }

        /// <summary>True when the player was spectating (the caller then sends S|off).</summary>
        public bool Leave(ulong steamId)
        {
            if (!spectators.Remove(steamId)) return false;
            fog.Remove(steamId);
            Version++;
            return true;
        }

        /// <summary>GameEvents.OnPlayerChangedTeam: a spectator who gets a real team stops spectating. True when the flag was cleared.</summary>
        public bool OnTeamChanged(ulong steamId, bool newTeamIsSet) => newTeamIsSet && Leave(steamId);

        /// <summary>Rate limit for H: at most one accepted hello per HelloInterval per client. Remembers the client version.</summary>
        public bool AcceptHello(ulong steamId, double now, string version)
        {
            if (lastHello.TryGetValue(steamId, out double last) && now - last < HelloInterval && now >= last) return false;
            lastHello[steamId] = now;
            if (!clientVersion.TryGetValue(steamId, out string old) || old != version) { clientVersion[steamId] = version ?? ""; Version++; }
            return true;
        }

        public bool HasClient(ulong steamId) => clientVersion.ContainsKey(steamId);
        public string ClientVersion(ulong steamId) => clientVersion.TryGetValue(steamId, out string v) ? v : "";

        /// <summary>Last fog perspective a spectator chose through /spectate fow (for the ModUI switch value).</summary>
        public string FogOf(ulong steamId) => fog.TryGetValue(steamId, out string f) ? f : "all";
        public void SetFog(ulong steamId, string value)
        {
            if (!spectators.Contains(steamId)) return;
            if (!fog.TryGetValue(steamId, out string old) || old != value) { fog[steamId] = value; Version++; }
        }

        /// <summary>Hardening log gate: true the first time a packet type is dropped for a player.</summary>
        public bool FirstDrop(ulong steamId, byte packetType) => dropLogged.Add(new KeyValuePair<ulong, byte>(steamId, packetType));

        /// <summary>OnPlayerLeft: everything about the player is runtime state and goes (the allowed list lives in the config).</summary>
        public bool Forget(ulong steamId)
        {
            bool was = spectators.Remove(steamId);
            lastHello.Remove(steamId);
            clientVersion.Remove(steamId);
            fog.Remove(steamId);
            dropLogged.RemoveWhere(k => k.Key == steamId);
            Version++;
            return was;
        }
    }
}
