using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace SiSpectator
{
    // Packet 125 transport (marker SiSpec/1, see Shared/Protocol.cs) and the spectator hardening, in ONE
    // [Priority.First] prefix on the server's NetworkLayer.ProcessMessage: the type byte is peeked once, the stream
    // position is always restored in finally, and only two things ever return false:
    //   - our own packets (byte 125 + marker), which the game would ignore anyway (no default case in the switch);
    //   - the listed gameplay packet types when their sender (__2, authoritative on the server) is a spectator.
    // Packets of non-spectators are never touched beyond the peek. Si_ModUI's prefix (126) passes 125 through and ours
    // passes 126 through, so the two coexist.
    internal static class Net
    {
        internal static bool Enabled;           // transport (125) usable: the game does not use 125 itself
        internal static bool HardeningOn;       // patch installed
        private static AccessTools.FieldRef<GameByteStreamReader, MemoryStream> _stream;
        private static readonly bool[] Blocked = new bool[256];
        private static float _nextError;

        internal static void Install(HarmonyLib.Harmony harmony)
        {
            Enabled = !Enum.GetValues(typeof(ENetworkPacketType)).Cast<object>().Any(v => Convert.ToInt32(v) == Protocol.PacketType);
            if (!Enabled) MelonLogger.Error("Transport disabled: packet type " + Protocol.PacketType + " is now used by the game (hardening stays on).");

            foreach (var t in new[] { ENetworkPacketType.PlayerSelectTeam, ENetworkPacketType.PlayerSelectUnit, ENetworkPacketType.ObjectUpdate,
                                      ENetworkPacketType.ObjectRPC, ENetworkPacketType.ObjectClientDamageHit, ENetworkPacketType.ObjectSetHealth,
                                      ENetworkPacketType.Suicide, ENetworkPacketType.UnitGroup, ENetworkPacketType.AITask })
                Blocked[(byte)t] = true;

            _stream = AccessTools.FieldRefAccess<GameByteStreamReader, MemoryStream>("m_MemoryStream");
            var target = AccessTools.Method(typeof(NetworkLayer), "ProcessMessage", new[] { typeof(GameByteStreamReader), typeof(uint), typeof(NetworkID), typeof(bool) });
            if (target == null) { MelonLogger.Error("NetworkLayer.ProcessMessage not found: no transport, no hardening."); Enabled = false; return; }
            var prefix = new HarmonyMethod(typeof(Net).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
            harmony.Patch(target, prefix: prefix);
            HardeningOn = true;
        }

        private static bool Prefix(GameByteStreamReader __0, uint __1, NetworkID __2, bool __3)
        {
            if (!HardeningOn || !__3 || __0 == null || __1 < 2) return true;
            MemoryStream stream;
            try { stream = _stream(__0); } catch (Exception ex) { Report(ex); return true; }
            if (stream == null) return true;
            long position = stream.Position;
            bool ours = false;
            try
            {
                if (!Game.GetIsServer() || Game.GetIsClient()) return true;
                byte type = __0.ReadByte();
                if (type == Protocol.PacketType)
                {
                    if (!Enabled || __1 > Protocol.MaxPacket) return true;
                    if (__1 - stream.Position < Protocol.Marker.Length + 2 || !Protocol.ReadMarker(stream.ReadByte, (byte)EGameByteStreamType.String)) return true;
                    ours = true;
                    string body = __0.ReadString();
                    if (stream.Position > __1) return false;
                    SpectatorMod.OnPacket(__2, body);
                    return false;
                }
                if (!Blocked[type] || SpectatorMod.State.Count == 0) return true;
                ulong id = __2.m_ID;
                if (!SpectatorMod.State.IsSpectating(id)) return true;
                if (SpectatorMod.State.FirstDrop(id, type))
                {
                    Player p = null;
                    try { p = Player.FindPlayer(__2); } catch { }
                    MelonLogger.Msg("dropped " + (ENetworkPacketType)type + " from spectator " + GameApi.NameOf(p) + " (logged once per player and type)");
                }
                return false;
            }
            catch (Exception ex) { Report(ex); return !ours; }
            finally { try { stream.Position = position; } catch { } }
        }

        /// <summary>Reliable 125 packet to one player. False when the transport is off or the send failed.</summary>
        internal static bool Send(Player player, string body)
        {
            if (!Enabled || player == null || !Protocol.ValidBody(body)) return false;
            try
            {
                var writer = GameByteStreamWriter.GetGameByteStreamWriter(0, "Si_Spectator::Send", true);
                writer.WriteByte(Protocol.PacketType);
                writer.WriteString(Protocol.Marker);
                writer.WriteString(body);
                bool ok = NetworkLayer.SendPacketToPlayer(writer, ENetworkPacketSend.Reliable, player);
                SpectatorMod.Dbg("-> " + GameApi.NameOf(player) + ": " + body + (ok ? "" : " (send failed)"));
                return ok;
            }
            catch (Exception ex) { Report(ex); return false; }
        }

        private static void Report(Exception error)
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextError) return;
            _nextError = now + 10;
            MelonLogger.Warning("network: " + error.GetBaseException().Message);
        }
    }
}
