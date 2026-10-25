# Si_Spectator — plan (2026-10-25)

Spectator mode for Silica multiplayer (MP_Strategy). Admins, or players an admin designates, leave their team and
watch the match: free camera, the view of any FPS player, an RTS-style overview, name tags, and a switchable
fog-of-war perspective. Think "classic RTS observer" plus "BF6 spectator".

Two assemblies plus a shared protocol file, following the Si_ModUI / Si_Spawn conventions:

| Part | Assembly | Runtime | Role |
|---|---|---|---|
| `Server/` | `Si_Spectator_Server.dll` | netstandard2.1, refs from `include\<branch>` like Si_Spawn, dual branch (test 1.0.x + default 0.9.47) | Authority: who may spectate, the team-less state, guards, relay, ModUI export, chat fallback |
| `Client/` | `Si_Spectator.dll` | net6 Il2Cpp, dual branch: `SilicaBranch` switch selects `include\client\<branch>\` interop assemblies | The experience: cameras, name tags, FoW switch, player picker, panel |

**Branch pairing (user decision 2026-10-25):** one server DLL and one client DLL per Steam branch, never mixed.
`include\client\default\` = copy of the installed game's `MelonLoader\Il2CppAssemblies` (the client is on the default
branch, 0.9.47, build 25179420, interop regenerated 2026-10-24) plus the `net6` loader DLLs; also mirrored to
`Projects\_silica_refs\client_default\`. `include\client\test\` does not exist yet: capture it the next time the game
runs on the test branch. Until then the test client build is parked; every game member the client uses is checked
against BOTH decompiles (`C:\tmp\silica_decompile_0947`, `C:\tmp\silica_decompile_106`) so the same source builds for
either branch. Output paths: test → `bin\Release\net6.0\`, default → `bin\Release\default\` (same rule as the server).
| `Shared/Protocol.cs` | compiled into both | | Packet 125 wire format |
| `Tests/` | xunit | | Pure logic: protocol, permission rules, state machine |

The server part works alone: a vanilla client can `/spectate`, ends up team-less and watches through whatever the
vanilla client renders for a team-less player (documented in API_NOTES §3). That path is the fallback, not the
product.

## 1. Server

### State
- `Spectators`: set of Steam IDs currently spectating (runtime only, cleared per player on leave).
- `Allowed`: set of Steam IDs an admin designated (persisted). Admins (Admin Mod `IsAdmin`) are always allowed.
- Config `UserData/cfg/Spectator/Si_Spectator_Config.json`: `Enabled`, `AllowAll` (false), `AllowAdmins` (true),
  `Allowed` (list of `{steamId, name}`), `MaxSpectators` (4), `Announce` (true, chat line when someone enters/leaves),
  `Debug`. Resolver pattern = Si_Spawn `Config.cs` (move old folder once, `MelonEnvironment.UserDataDirectory`).

### Commands (Admin Mod registry; never share a command NAME with another mod)
Player (prefix `/`):
- `/spectate` — toggle. `/spectate on|off|status`. Alias `/spec`. Denied with a one-line reason when not allowed.
- `/play` — same as `/spectate off`.
- `/spectate view <player>` — only while spectating; relays a view request to the caller's client (packet `V`).
  Exists so the ModUI player picker can drive the client. No effect without the client mod (reply says so).
- `/spectate fow <all|sol|cent|alien>` — same idea, relays `F`.

Admin (prefix `!`, power: use `Power.Generic` unless API_NOTES names a better fit):
- `!spectator allow <player>` / `!spectator deny <player>` / `!spectator list`
- `!spectator force <player>` (put a player into spectator mode) / `!spectator kick <player>` (back to faction select)
- `!spectator all on|off`, `!spectator max <n>`, `!spectator reload`

### Entering / leaving (corrected from API_NOTES §2/§4)
Enter, in this order:
1. If `player.IsCommander`: release the seat with `SetCommander(team, null)` + `RPC_SynchCommander(team)` via
   reflection (protected in 1.0.6, private in 0.9.47; `kicked` stays false so no mutiny popup). A team change does NOT
   free the seat by itself and a spectating ex-commander who disconnects would leave a ghost commander (API_NOTES §2
   risk 1).
2. Remove the player from Si_CommManagement's public static `commanderApplicants[]` lists and from Si_EloMatchmaking's
   applicant/reserve pools by reflection (both optional, type lookup by name, no hard reference) — API_NOTES §4c.
3. The vanilla eliminated-team sequence: `NetworkLayer.SendPlayerSelectUnit(player, null); player.ControlledUnit = null;
   player.Team = null; NetworkLayer.SendPlayerSelectTeam(player, null);` (null round-trips on both branches).
4. Add the Steam ID to `Spectators`, send `S|on`, announce if configured.

Leave (`/spectate off`, `/play`, `!spectator kick`): remove from `Spectators`, send `S|off`. The player is team-less
and picks a faction on the vanilla screen. **Picking a faction ends spectating**: a server-side
`GameEvents.OnPlayerChangedTeam` subscriber clears the flag whenever a spectator gets a non-null team (Elo's
`!reserve`, BTB's `!team` and the faction screen all go through it). No `GetCanJoinTeam` prefix: 0.9.47 has no such
method and Elo/BTB take over the join RPC anyway (API_NOTES §4a).

Spectators are remembered **by Steam ID**, never by `Team == null` (every lobby player is team-less too). They stay
spectators across round restarts; `S|on` is re-sent on every hello and after every scene load / `Restart`.

### Guards while spectating
- **Si_AFKManager** kicks team-less players after 7 min when the server is nearly full: postfix on
  `Si_AFKManager.AwayFromKeyboard.OnUpdate` (type by name, skipped when absent) that removes spectators from its
  private static `AFKTracker` by reflection.
- **Hardening** (API_NOTES §2 risk 6, §7): a `[HarmonyPriority(Priority.First)]` prefix on `NetworkLayer.ProcessMessage`
  (server) drops `PlayerSelectTeam`, `PlayerSelectUnit`, `ObjectUpdate`, `ObjectRPC`, `ObjectClientDamageHit`,
  `ObjectSetHealth`, `Suicide`, `UnitGroup`, `AITask` packets whose sender `__2` is a spectator (type byte peeked, stream
  position restored). Chat, GameModeRPC (needed to rejoin), Ping, Heartbeat, Voice, 125/126 pass. Logged once per
  player per type.
- Spectators never receive a unit (`GetUnitPrefabForPlayer` returns null for team null).
- `OnPlayerLeft`: clear runtime state for that player (Allowed persists).
- Known limits, documented in README: with only spectators online the round never starts; spectators keep the
  "no players for 300 s" check alive; mid-round joiners on 0.9.47 servers with Si_PacketChunker may hold incomplete
  detection lists (fog view of a team may show too little).

### Transport (packet 125, marker `SiSpec/1`) — implemented in `Shared/Protocol.cs` (fixed contract)
Same mechanics as Si_ModUI: first byte 125, string marker, one string body ≤ 1800 chars, reliable. Both ModUI hooks
pass foreign first bytes through, the game ignores unknown types, so the two mods coexist. Each side checks at start
that 125 is not in `ENetworkPacketType` and disables the transport with a log line otherwise. Send/receive code paths
to copy: API_NOTES §8 (writer `GameByteStreamWriter.GetGameByteStreamWriter(0, "Si_Spectator::Send", true)`,
`NetworkLayer.SendPacketToPlayer` / `SendPacket`, prefix `Prefix(GameByteStreamReader __0, uint __1, NetworkID __2,
bool __3)` with `m_MemoryStream` position restored in `finally`).

Messages (see the header of `Shared/Protocol.cs`): client → server `H|<clientVersion>`, `K|pose` (phase 2);
server → client `S|on|off|<allowed>|<serverVersion>`, `V|<networkId>|<channel>`, `F|<all|team>`, `C|team|pose`
(phase 2). The detection relay (`T`/`W`) is dropped: every client already holds every team's detected lists
(API_NOTES §1). Limits: hello ≤ 1/s per client, `K` ≤ 5/s, body validated before parsing, malformed = ignored.

### ModUI export (`ModUIData()` + `ModUIPlayerData(steamId)`, builder copy `ModUI_Export.cs`)
Player tab: info lines (spectating / allowed, count), `/spectate` OnOff switch with per-player value, `/spectate view`
picker `player`, `/spectate fow` switch all|sol|cent|alien (per-player value = last choice), `/spectate status` report.
Admin tab: `!spectator allow` picker `player`, `!spectator deny` choice (names of allowed players, no Steam IDs in
labels/values — identity rule), `!spectator force` picker `player`, `!spectator kick` choice (current spectators by
name), `!spectator all` OnOff switch, `!spectator max` value, `!spectator reload`. `enabled` follows the config.

## 2. Client

### Session state
`Idle` → `Spectating` on `S|on` (or when `Player.CurrentPlayer.Team == null` and the last `S` said on). `S|off`,
a team assignment, scene load or disconnect → `Idle` with every patch/camera restored. Hello on join and after
every scene load (same trigger points as the ModUI client).

### While spectating (corrected from API_NOTES §1, §3, §5, §6, §9)
- **Faction screen suppressed**: Harmony prefix on `MP_Strategy.HandleUI()` sets `__instance.ShowTeamSelectMenu =
  ETeamSelect.NONE` while spectating (the vanilla matchmaking mode does the same); during the pre-round countdown call
  `StrategyHandler.SwitchTo(StrategyScreenType.None)` ourselves, except during the battle-layout vote (`MissionState
  INIT && TeamsVersus NONE`). With the menu gone the cursor locks and `Game.GetControlsEnabled()` is true. Chat
  untouched. `TeamSelectActionPerformed` / `RoleSelectActionPerformed` are swallowed while spectating.
- **Free camera = the vanilla `FreeFlyCameraController`.** It activates by itself when there is no unit, no RTS mode
  and the cursor is locked (API_NOTES §5); vanilla keys: WASD, Space/Ctrl up/down, Shift boost, wheel speed, MMB
  zoom, RMB stick-to-object, Q/E roll. We only set `EnableMovement = false` while one of our cameras drives
  `Game.CurrentCamera` and `true` again for Free. Verify at runtime that `Instance.transform` is the current camera.
- **Our cameras** run from a postfix on `FreeFlyCameraController.OrderedLateUpdate` (after soldier animation, before
  culling/crosshair) and pause while `Game.GetCameraEnabled()` is false (ModUI panel open):
  1. `Free` — vanilla free fly (above).
  2. `FollowFps` — soldier: `CameraReference.transform.TransformPoint(HumanHandsAnimator.WeaponViewOffset)`, looking at
     `unit.UnitAimAt.AimAtPosition` (smoothed) with `GetViewDirection()` as fallback; near clip raised so the own
     third-person body does not block. Vehicle (the player's `ControlledUnit` is a `Vehicle`): reproduce the
     `VehicleCameraController` orbit from its public fields, rotation from the vehicle's `UnitAimAt`; soldier in a
     compartment: `InCompartment.OwnerUnit.Camera` anchor.
  3. `Shoulder` — `CameraReference.position + rot * (0.5, -0.1, -1.5)`, like the game's own third person.
  4. `Orbit` — chase cam around the unit's `WorldPhysicalCenter`, mouse orbits, wheel zooms, sphere-cast collision.
  5. `Overview` — RTS-like top-down with the game's own formula (`CameraPitch = Lerp(10, 75, pct^0.4)`, height between
     4 and 300 m, API_NOTES §7), WASD/edge pans, wheel zooms, centred on the followed unit, the team HQ or the free
     position. Never enters `StrategyMode` (a spectator's clicks would become real orders, §7).
  6. `Fixed` — freeze the current pose; tags keep working.
  **Nothing is ever sent to the server and no unit ownership changes** (hard attach is forbidden, API_NOTES §6).
- **Target picking**: click in the world (ray from the camera through the mouse / crosshair, `Unit` on the hit
  collider's parents, `ControlledBy != null`) selects; a second click or the follow key enters the view. `[` / `]`
  previous / next player (team order, then across teams). Panel list click does the same.
- **Fog perspective** `All | <teams of the match>`: outside RTS mode the game already shows everything (CurrentStrategyTeam
  is null, API_NOTES §0.2). Team X view = Harmony prefix on `BaseGameObject.UpdateVisibility(Team)` replacing `__0`
  with X, plus postfix on `FogOfWar.GetCurrentFogTeam()` returning X for the minimap / large-map fog texture, then
  `BaseGameObject.UpdateVisibilityAll()` once. Every client holds every team's detected lists, so this is purely local.
  Restore (patches inert, `UpdateVisibilityAll`) on leave.
- **Name tags**: IMGUI label above every player-controlled unit (bounds top + 0.4 m): name in team colour, unit
  short name, HP bar; distance scaling, hidden beyond a configurable range in Free/Overview, hidden when the object is
  not visible under the current fog perspective. Commander: name tag on the team HQ with a "CMD" marker. Other
  spectators are never shown (they have no unit anyway). Toggle N.
- **Panel** (IMGUI, Si_ModUI look: team-tinted surfaces, same font sizes): header with mode / fog / target, player
  list grouped by team (name, unit, HP, CMD), buttons for camera modes, fog perspective, tags on/off, "Return to
  game" (sends `/spectate off` through the normal chat path, `Player.CurrentPlayer.SendChatMessage`). While the panel
  is open the cursor is free and the game gets no clicks (reuse ModUI's `BlockNativeClicks` / `GetControlsEnabled`
  patches pattern). Hidden when H (hide all overlays) is active.
- **Keys** (MelonPreferences `[Si_Spectator]`, defaults): `I` panel, `C` cycle camera mode, `F` cycle fog
  perspective, `K` name tags, `H` hide overlays, `[` / `]` previous / next player, `G` follow the selection, left click
  select / follow (P and N are avoided: P = vanilla "Vote No" in the Voting map, N = Global "Voice Channel Switch"),
  `Escape` back to Free (only while following; in Free mode Escape keeps opening the pause menu). Vanilla keys stay:
  Tab = large minimap, M = map, Enter = chat, F6 = ModUI, Space/Ctrl/Shift/Q/E/RMB/MMB = free fly. Every key of ours is
  ignored while chat input, the console, the pause menu, the main menu or the ModUI panel is open.
- **Server messages**: `S|off` → leave; `V` → select that player and enter `Shoulder`; `F` → set the fog perspective.
- **Phase 2**: `V`/`F` from the ModUI picker, kill feed from `GameEvents` (client-side), "Action cam" (jump to the
  latest kill / densest recent combat), commander camera relay (`K`/`C`) when commanders run the client mod.

### Not in scope
Replays, recording, control of any unit, anything that sends orders. The spectator client never sends gameplay RPCs;
if a chosen camera technique would (API_NOTES §6/§7 hard-attach / StrategyMode), it is not used.

## 3. Build, test, deploy
- `include/{default,test}` copied from `Si_Spawn/include`; server csproj = Si_Spawn's with the names changed;
  client csproj = Si_ModUI client's. `dotnet build -c Release` (test) and `-p:SilicaBranch=default` (default).
- refcheck both server builds against both SilicaCore versions (`C:\tmp\refcheck`, see API_NOTES §11).
- Tests: xunit project over `Shared/Protocol.cs` + a `Server/Logic/` folder that has no MelonLoader/Unity types
  (permission rules, state transitions, command parsing, chunking).
- Deploy (only when `Get-Process Silica` is empty): test server build → `E:\Steam\steamapps\common\Silica Dedicated
  Server\Mods`, client → `C:\SteamLibrary\steamapps\common\Silica\Mods`; start the server with `launch.bat` /
  `Silica.exe --melonloader.disablestartscreen` and read `MelonLoader\Latest.log` for the mod's load lines and the
  ModUI frame line (the export must appear). Dist copy of the default build → `Projects\_dist\Si_Spectator_<ver>_<date>`.
- In-game verification is the user's (we cannot play). Hand over a checklist.

## 4. Phasing
- Phase 0: `docs/API_NOTES.md` (research agent).
- Phase 1 (MVP): everything in §1 and §2 except the items marked phase 2. Server and client built by two agents in
  parallel against this protocol; integration = build + refcheck + tests + server start.
- Phase 2: ModUI-driven view/fog, kill feed, action cam, commander camera relay, polish from the user's first test.
