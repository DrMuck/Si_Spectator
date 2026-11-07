using System;

namespace SiSpectator
{
    // Stable public surface for other server mods (Si_ServerControl's AFK slot manager), resolved by reflection there:
    // assembly "Si_Spectator_Server", type "SiSpectator.SpectatorApi". No hard reference needed; keep these signatures.
    public static class SpectatorApi
    {
        /// <summary>Version of this accessor surface (bump on a breaking change).</summary>
        public const int ApiVersion = 1;

        /// <summary>True while the player with this Steam ID is in spectator mode (team-less by choice, not AFK).</summary>
        public static bool IsSpectating(ulong steamId) => SpectatorMod.State.IsSpectating(steamId);

        /// <summary>Number of players spectating right now.</summary>
        public static int Count => SpectatorMod.State.Count;

        /// <summary>Steam IDs of everyone spectating (a copy).</summary>
        public static ulong[] Spectators() => SpectatorMod.State.Spectators;
    }
}
