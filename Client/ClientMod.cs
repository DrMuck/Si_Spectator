using System.Diagnostics;
using System.Globalization;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

[assembly: MelonInfo(typeof(SiSpectator.ClientMod), "Spectator", SiSpectator.ClientMod.Version, "DrMuck")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]
[assembly: MelonPlatform(MelonPlatformAttribute.CompatiblePlatforms.WINDOWS_X64)]
[assembly: MelonPlatformDomain(MelonPlatformDomainAttribute.CompatibleDomains.IL2CPP)]

namespace SiSpectator;

// Client of the Si_Spectator pair: the server decides WHO spectates (packet S), this side makes it watchable. Free camera,
// follow views, an RTS-like overview, name tags and a fog perspective, all purely local. Nothing here ever sends a gameplay
// packet: no unit ownership, no Unit.CurrentUnit, no StrategyMode, no UsePoint. The only things that leave this client are
// our own packet 125 (hello) and one chat line ("/spectate off") from the panel's "Return to game" button.
//
// State machine
//   Idle        -> Spectating  on S|on (once the local player is team-less)
//   Spectating  -> Idle        on S|off, a team assignment, disconnect, main menu      (full restore)
//   Spectating  -> Resuming    on a scene load (restore; the server re-sends S|on after the new level is up)
//   Resuming    -> Spectating  on S|on;   Resuming -> Idle on S|off / disconnect
public sealed partial class ClientMod : MelonMod
{
    public const string Version = "0.1.0";
    private enum SpecState { Idle, Spectating, Resuming }

    private static ClientMod instance;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private StreamWriter log;
    private bool networkEnabled, keyboardEnabled = true, wasConnected;
    private double nextPoll, nextError, helloAt;
    private int helloTries;
    private string context = "";
    private SpecState state = SpecState.Idle;
    private bool stateKnown, serverOn, serverAllowed;   // stateKnown: an S arrived since the last connect / scene load
    private string serverVersion = "";

    // Active = every patch, camera and overlay of ours is live. Everything else in the mod is inert while this is false.
    internal static bool Active => instance != null && instance.state == SpecState.Spectating;

    public override void OnInitializeMelon()
    {
        instance = this;
        networkEnabled = !Enum.GetValues(typeof(ENetworkPacketType)).Cast<object>().Any(v => Convert.ToInt32(v) == Protocol.PacketType);
        try
        {
            var folder = Path.Combine(MelonEnvironment.UserDataDirectory, "Si_Spectator");
            Directory.CreateDirectory(folder);
            log = new StreamWriter(new FileStream(Path.Combine(folder, $"session-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception error) { LoggerInstance.Warning("Session log unavailable: " + error.Message); }
        CreatePrefs();
        Write("READY " + Version + " - spectator client. Marker " + Protocol.Marker + ", packet " + Protocol.PacketType
            + (networkEnabled ? "" : " (transport disabled: packet " + Protocol.PacketType + " is defined by this game build)")
            + ". Keys: " + KeySummary());
    }
    public override void OnDeinitializeMelon()
    {
        LeaveSpectating("mod unloaded", SpecState.Idle);
        ResetAppearance();
        networkEnabled = false; log?.Dispose(); log = null; instance = null;
    }
    public override void OnSceneWasLoaded(int buildIndex, string name)
    {
        // The level objects (camera, free-fly controller, game mode) are new: drop every reference into the old scene.
        if (state == SpecState.Spectating) LeaveSpectating("scene " + name, SpecState.Resuming);
        ResetAppearance();
        ResetMapRefs();
        stateKnown = false; helloTries = 0; helloAt = clock.Elapsed.TotalSeconds;   // first hello 2 s from now
        Write("Scene loaded: " + name + (state == SpecState.Resuming ? " (spectating resumes when the server confirms)" : ""));
    }

    // ---- transport (same mechanics as the Si_ModUI client, packet 125 / marker SiSpec/1) ----
    [HarmonyPatch(typeof(NetworkLayer), "ProcessMessage")]
    private static class Receive
    {
        private static bool Prefix(GameByteStreamReader __0, uint __1, NetworkID __2, bool __3)
        {
            if (instance == null || !instance.networkEnabled || __3 || !Game.GetIsClient() || __0 == null || __1 < 2) return true;
            var stream = __0.m_MemoryStream;
            long position = stream.Position;
            bool ours = false;
            try
            {
                if (__0.ReadByte() != Protocol.PacketType || __1 > Protocol.MaxPacket) return true;
                if (__1 - stream.Position < Protocol.Marker.Length + 2 || !Protocol.ReadMarker(stream.ReadByte, (byte)EGameByteStreamType.String)) return true;
                ours = true;
                if (!NetworkGameClient.GetInServer() || __2.SteamID.m_SteamID != NetworkGameClient.GetServerID().SteamID.m_SteamID) return false;
                string body = __0.ReadString();
                if (stream.Position > __1) return false;
                instance.HandleBody(body);
                return false;
            }
            catch (Exception error) { if (ours) instance.Error(error); return !ours; }
            finally { stream.Position = position; }
        }
    }
    private void HandleBody(string body)
    {
        if (!Protocol.TrySplit(body, out char kind, out _)) { Write("Ignored malformed packet."); return; }
        switch (kind)
        {
            case 'S':
                if (!Protocol.TryState(body, out bool on, out bool allowed, out string version)) { Write("Ignored malformed S."); return; }
                bool changed = !stateKnown || on != serverOn || allowed != serverAllowed;
                stateKnown = true; serverOn = on; serverAllowed = allowed; serverVersion = version;
                if (changed) Write("STATE from server " + version + ": " + (on ? "on" : "off") + ", allowed " + (allowed ? "yes" : "no"));
                if (on) TryEnter("server S|on");
                else if (state != SpecState.Idle) LeaveSpectating("server S|off", SpecState.Idle);
                break;
            case 'V':
                if (!Protocol.TryView(body, out ulong networkId, out int channel)) { Write("Ignored malformed V."); return; }
                if (!Active) { Write("V ignored: not spectating."); return; }
                ViewRequest(networkId, channel);
                break;
            case 'F':
                if (!Protocol.TryFog(body, out string team)) { Write("Ignored malformed F."); return; }
                if (!Active) { Write("F ignored: not spectating."); return; }
                FogRequest(team);
                break;
            default: Write("Ignored packet kind " + kind + "."); break;   // C (phase 2) and anything newer
        }
    }
    private void SendBody(string body)
    {
        var writer = GameByteStreamWriter.GetGameByteStreamWriter(0, "Si_Spectator::Send", true);
        writer.WriteByte(Protocol.PacketType); writer.WriteString(Protocol.Marker); writer.WriteString(body);
        NetworkLayer.SendPacket(writer, ENetworkPacketSend.Reliable);
    }
    private void SendHello()
    {
        helloAt = clock.Elapsed.TotalSeconds; helloTries++;
        SendBody(Protocol.Hello(Version));
        Write("HELLO " + helloTries + " (client " + Version + ")");
    }

    // ---- state machine ----
    private void TryEnter(string why)
    {
        if (state == SpecState.Spectating) return;
        var p = Player.CurrentPlayer;
        if (p == null || Game.InMainMenu) return;
        if (p.Team != null) { Write("S|on received while still on team " + p.Team.TeamShortName + ": waiting until the server makes this player team-less."); return; }
        var previous = state;
        state = SpecState.Spectating;
        EnterCameras();
        mapFilter = null;   // the map faction filter starts at All on every entry (not remembered)
        Write("SPECTATING (" + why + (previous == SpecState.Resuming ? ", resumed after scene load" : "") + "). " + CameraDiagnostics());
    }
    // Every restore is idempotent and guarded: on a scene load the old objects may already be gone.
    private void LeaveSpectating(string why, SpecState next)
    {
        if (state == SpecState.Idle && next == SpecState.Idle) return;
        bool wasActive = state == SpecState.Spectating;
        state = next;
        if (!wasActive) { Write("State " + next + " (" + why + ")."); return; }
        try { ClosePanel(); } catch (Exception error) { Error(error); }
        try { LeaveCameras(); } catch (Exception error) { Error(error); }
        try { ClearFog(); } catch (Exception error) { Error(error); }
        try { RestoreFactionMenu(); } catch (Exception error) { Error(error); }
        try { RestoreMinimap(); } catch (Exception error) { Error(error); }
        mapFilter = null; hoverPlayer = null; hoverUnit = null;
        ClearTarget();
        Write("LEFT spectating (" + why + "), now " + next + ".");
    }

    public override void OnUpdate()
    {
        if (keyboardEnabled && Active)
        {
            try { HandleKeys(); }
            catch (Exception error) { keyboardEnabled = false; Error(error); Write("Keyboard handling disabled after an error."); }
        }
        double now = clock.Elapsed.TotalSeconds;
        if (now < nextPoll) return;
        nextPoll = now + 0.25;
        try
        {
            var player = Player.CurrentPlayer;
            bool connected = Game.GetIsClient() && !Game.GetIsServer() && NetworkGameClient.GetInServer() && player != null && !Game.InMainMenu;
            if (!connected)
            {
                if (wasConnected) { LeaveSpectating("disconnected", SpecState.Idle); stateKnown = serverOn = false; context = ""; Write("Disconnected."); }
                wasConnected = false; return;
            }
            wasConnected = true;
            string next = NetworkGameClient.GetServerID().SteamID.m_SteamID.ToString(CultureInfo.InvariantCulture) + ":" + player.GetInstanceID();
            if (next != context)
            {
                if (state != SpecState.Idle) LeaveSpectating("server/player changed", SpecState.Idle);
                context = next; stateKnown = serverOn = false; helloTries = 0; helloAt = now;
                Write("Connected (" + next + ").");
            }
            if (networkEnabled && !stateKnown && helloTries < 5 && now - helloAt >= 2)
            {
                SendHello();
                if (helloTries == 5) Write("No S after 5 hellos: the Si_Spectator server mod is probably not installed there. Staying idle.");
            }
            // A team assignment ends spectating locally at once (the server clears its flag on the same team change).
            if (state == SpecState.Spectating && player.Team != null)
            {
                serverOn = false;
                LeaveSpectating("joined team " + player.Team.TeamShortName, SpecState.Idle);
            }
            else if (state != SpecState.Spectating && stateKnown && serverOn && player.Team == null) TryEnter("team-less and the last S said on");
            if (Active) PollTargets();
        }
        catch (Exception error) { Error(error); }
    }

    internal void Error(Exception error)
    {
        if (clock.Elapsed.TotalSeconds < nextError) return;
        nextError = clock.Elapsed.TotalSeconds + 10;
        Write("ERROR " + error);
    }
    internal void Write(string text)
    {
        var line = DateTime.UtcNow.ToString("o") + " " + text.Replace('\r', ' ').Replace('\n', ' ');
        LoggerInstance.Msg(line);
        if (log == null) return;
        try { log.WriteLine(line); } catch { try { log.Dispose(); } catch { } log = null; }
    }
    internal double Now => clock.Elapsed.TotalSeconds;
}
