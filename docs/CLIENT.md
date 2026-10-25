# Si_Spectator client 0.1.0

`Si_Spectator.dll` is the client half of the Si_Spectator pair (MelonLoader name **Spectator**, author DrMuck). The server
mod decides who spectates. This mod makes it watchable, with:

- the vanilla free camera;
- follow views of any player: first person, over the shoulder and orbit;
- an RTS-style overview;
- name tags;
- a switchable fog-of-war perspective;
- a panel.

Everything is local. The client never sends a gameplay packet. It does not:

- assign a unit;
- touch `Unit.CurrentUnit`;
- enter StrategyMode;
- press a UsePoint.

Only two things leave the client:

- our own packet 125 hello (`H|0.1.0`);
- the chat line `/spectate off` from the panel's "Return to game" button.

Source: `Client\*.cs` plus `Shared\Protocol.cs`. Game members used, checked in both branches: `docs\CLIENT_MEMBERS.md`. In-game test: `docs\TEST_CHECKLIST_CLIENT.md`.

## How the client decides it is spectating

| State | Meaning | Enter | Leave |
|---|---|---|---|
| Idle | Every patch is inert and every camera is vanilla. | Start, disconnect, main menu, `S\|off`, a team assignment | — |
| Spectating | Everything below is live. | `S\|on` from the server while the local player has no team. If `S\|on` arrives while the player still has a team, the client waits and enters as soon as the team is null. | `S\|off`, `Player.CurrentPlayer.Team` becomes non-null, disconnect or main menu → Idle. A scene load → Resuming. |
| Resuming | After a scene load while spectating. Nothing is active, so the vanilla faction screen may show for a moment. | Scene load while Spectating | The server re-sends `S` after the new level loads: `S\|on` → Spectating, `S\|off` → Idle. |

The client never decides on its own that someone is a spectator. A null team alone is not enough, because every lobby player has no team too.

**Hello**

The client sends `H|0.1.0` at these points:
- 2 s after joining a server (server ID or local player changed);
- 2 s after every scene load.

It repeats the hello every 2 s until an `S` arrives, at most 5 times. After 5 tries it logs "server mod probably not installed" and stays Idle.

**Receiving**

The receive hook is a prefix on `NetworkLayer.ProcessMessage`, the same mechanics as the Si_ModUI client:
- first byte 125, then the raw `SiSpec/1` marker;
- the packet is accepted only from the server's Steam ID;
- size ≤ `Protocol.MaxPacket`;
- the stream position is restored in `finally`.

At start the mod checks that 125 is not a value of `ENetworkPacketType`. If it is, the transport is disabled and the READY line says so.

**Server messages**

| Message | Action |
|---|---|
| `S` | Updates the state as above. |
| `V\|<networkId>\|<channel>` | Selects that player and switches to Shoulder. |
| `F\|all\|<teamShortName>` | Sets the fog perspective. Matching order: exact short name, then a short-name prefix, then the long name. |
| `C` and unknown kinds | Logged and ignored (phase 2). |

## What happens while spectating

### Faction screen

A prefix on `MP_Strategy.HandleUI` sets `ShowTeamSelectMenu = NONE` every frame. It runs after `UpdateGameLoop`, which forces `SELECT_TEAM` for team-less players.

When the current strategy screen is `Faction`, `Loadout` or `Respawn`, the client calls `StrategyHandler.SwitchTo(None)`. This also covers the pre-round countdown.

These screens stay:
- the battle-layout vote (`MissionState INIT` and `TeamsVersus NONE`), so a spectator can vote;
- the end screen and the map vote.

With the menu gone the cursor locks and the vanilla free-fly camera gets input.

`TeamSelectActionPerformed` and `RoleSelectActionPerformed` are swallowed. Chat is untouched.

### Camera modes

Press `C` to cycle the modes:

| Mode | What it does |
|---|---|
| **Free** | The vanilla `FreeFlyCameraController`, unchanged: WASD, Space / Ctrl, Shift, wheel = speed, MMB = zoom, RMB = stick to object, Q / E = roll. |
| **FollowFps** | Soldier: `CameraReference.TransformPoint(HumanHandsAnimator.WeaponViewOffset)`. Alien: `CameraReference`. The view looks at the followed player's networked crosshair point `UnitAimAt.AimAtPosition`, falling back to `GetViewDirection()`. Rotation is smoothed. The near clip is raised to `FpsNearClip` (0.3 m) so the remote third-person head does not fill the screen, and restored on mode change. |
| **Shoulder** | The game's own third person: `CameraReference.position + rot * (0.5, -0.1, -1.5)`, with a sphere cast against terrain and structures. |
| **Orbit** | Chase camera around the unit's `WorldPhysicalCenter`. Mouse = orbit, wheel = distance (2..120 m), with a sphere-cast collision. |
| **Overview** | Top-down view with the game's StrategyMode formula: pitch = `Lerp(10, 75, pct^0.4)`, distance = `Lerp(300, Lerp(4, 300, pct), terrainPct) / sin(pitch)`. Controls: wheel or Space / Ctrl = height, WASD = pan (Shift ×3), Q / E = rotate. It is centred on the followed unit. A commander target is centred on their team's HQ. With no target it uses the ground under the free camera. Panning detaches from the target; `[` / `]` or a new selection re-attaches. StrategyMode is never entered. |
| **Fixed** | Freezes the current pose. Tags keep working. |

How the follow modes work:
- **Vehicles.** If the followed player's `ControlledUnit` is a `Vehicle` (a driver), or their soldier sits in a compartment (gunner or passenger), the client rebuilds the `VehicleCameraController` orbit of the vehicle. It uses the controller's public `LocalTransform`, `LocalOffset`, `WorldOffset`, `DistanceMin`, `DistanceMax` and `CollisionRadius`. The rotation comes from the aimer's `UnitAimAt`, falling back to the vehicle's.
- **Timing.** Every mode except Free sets `FreeFlyCameraController.EnableMovement = false` in a prefix on its `OrderedLateUpdate`. The postfix then places `Game.CurrentCamera`, which runs after the soldier animation and before culling and the crosshair. The free-fly controller's `TargetPos` / `TargetRot` are kept in step, so returning to Free is seamless.
- **Pause.** The modes pause while `Game.GetCameraEnabled()` is false, which is the case while the Si_ModUI panel is open.
- **Fallback.** If no free-fly controller ticks, the camera is driven from MelonLoader's LateUpdate instead.
- **Target is the player.** A follow mode follows the player, not the unit. If the unit dies, the camera holds and the status shows "waiting for X to (re)spawn". If the player leaves or loses their team, the camera returns to Free.

### Picking a player

Several inputs select a player:
- **Left click** while the cursor is locked: a ray from the camera through the screen centre picks the hit collider's parent `Unit`, if it has a controlling player. If the ray misses, the client takes the player unit closest to the crosshair, within 6 % of the screen height. A second click on the same player, or `G`, follows them in the last follow mode (Shoulder at first).
- **`[` / `]`**: previous / next player with a living unit, in team order, wrapping across teams. Outside a follow mode this also switches to the last follow mode.
- **Panel list**: a click follows at once. A commander without a unit opens Overview on their HQ.

### AI units (solo testing)

With `FollowAiUnits` on (default; panel button "AI units"), a click also picks AI-controlled units. This lets you test the follow cameras alone against the AI commanders.

- **Picking.** The crosshair ray takes any alive `Unit`: its player if it has one, otherwise the AI unit itself. In the closest-to-crosshair fallback, player units win over AI units.
- **Target.** For an AI unit the target is the unit, with no player behind it. FollowFps, Shoulder, Orbit and Overview use the same anchor rules as for players (soldier eye, alien `CameraReference`, vehicle orbit). Aim comes from the unit's `UnitAimAt` when it is set, otherwise from its view direction or facing; AI aim may therefore look less precise than a player's.
- **Death.** When the AI unit dies or is destroyed, the camera goes back to Free (a player target would instead wait for the respawn).
- **Header.** The HUD line and the panel show `AI · <unit name> (<team>)`.
- **Not affected.** `[` / `]` and the panel's player list step over players only. Packet `V` (`/spectate view`) refers to players only.
- **Tags.** `TagsForAi` (default off; panel button "AI tags", active only while AI units is on) draws small tags over AI units: the unit name in team colour, no player name, no HP bar, hidden beyond `TagRange` in every mode. With it off, only the selected AI target gets its tag.
- **Turning it off.** Switching "AI units" off drops a selected AI target; a follow mode returns to Free.

### Fog perspective

Press `F` to cycle: All, then each team of the match. The match teams are the game mode's active team setups plus any team a player is on.

On first use the client installs two Harmony patches:
- a prefix on `BaseGameObject.UpdateVisibility(Team)` that computes the object's visibility for X itself, exactly like the original method (identical bodies in 0.9.47 and 1.0.6: `Target` active, `GetIsObjectAlwaysVisible`, X's `DetectedTargets` / `GetIsTargetAtIndexActivelyVisible`, then `SetVisibility(..., GetIsRendered(...))`), and skips the original. It does not depend on Il2CppInterop writing a rewritten argument back. If that computation ever throws, the prefix logs it once and falls back to replacing the team argument with X and running the original;
- a postfix on `FogOfWar.GetCurrentFogTeam` that returns X, for the minimap and large-map fog.

It then calls `BaseGameObject.UpdateVisibilityAll()`. Both patches return at once when the perspective is All or when not spectating. A client that never picks a team perspective never pays for the per-object hook.

All is the vanilla state: outside RTS mode everything is visible.

### Name tags

Press `K` to toggle. A tag sits over every player-controlled unit and shows:
- the name in the team colour, with a 1-px shadow;
- the unit name (`ObjectInfo.DisplayName`);
- an HP bar from `DamageManager.Health01`.

The anchor is the vanilla RTSPlayerName formula + 0.4 m. Tags scale with distance.

A tag is hidden when:
- the unit is beyond `TagRange` (300 m) in Free and Overview (1500 m in the other modes);
- the unit is not visible under the current fog perspective (`IsVisible`);
- it is your own unit in FollowFps.

A team's HQ structure (`Team.BaseStructure`) gets a `CMD <name>` tag when its commander is a player. Other spectators have no unit and never get a tag.

### HUD line and panel

**HUD line.** A one-line status at the top of the screen shows the mode, the target, the fog perspective and key hints. Turn it off with the `ShowHudLine` setting. `H` hides every overlay.

**Panel.** `I` opens the panel, in the Si_ModUI look and font size. It shows:
- a header: camera mode, target, fog perspective;
- 6 camera-mode buttons;
- fog buttons: All plus the match teams;
- name tags on/off and "hide overlays";
- the player list grouped by team, with name, unit, HP and `[CMD]`;
- "Return to game", which sends `/spectate off` through the normal chat path.

While the panel is open:
- the cursor is free;
- the game gets no input. The client uses the ModUI patterns: `MenuManager.IsAnyMenuOpened` → true, `GameInput.GetPreferredInput` → UI, `Game.GetControlsEnabled` → false, and the EventSystem is disabled over native menus.
- The cameras keep running, because `GetCameraEnabled` is NOT patched.

Escape, `I` or a click outside the panel closes it. Opening the pause menu or chat also closes it.

### Keys and settings

Settings live in `UserData\MelonPreferences.cfg`, category `[Si_Spectator]`. Key settings take `UnityEngine.InputSystem.Key` names or a single character.

| Key | Default | Action |
|---|---|---|
| `KeyPanel` | I | panel |
| `KeyCameraMode` | C | cycle camera mode |
| `KeyFog` | F | cycle fog perspective |
| `KeyTags` | K | name tags on/off |
| `KeyHideOverlays` | H | hide every overlay |
| `KeyPrevPlayer` / `KeyNextPlayer` | `[` / `]` | previous / next player |
| `KeyFollow` | G | follow the selected player |
| (fixed) | Left click | select, then follow on a second click |
| (fixed) | Escape | in a follow mode, back to Free; in Free, the pause menu as usual |

Other settings:

| Setting | Default | Meaning |
|---|---|---|
| `TagRange` | 300 | Tag range in m (Free and Overview) |
| `FollowSmoothing` | 12 | Rotation smoothing rate in 1/s; 0 = off |
| `FpsNearClip` | 0.3 | Near clip in m while in FollowFps |
| `OrbitSensitivity` | 0.15 | Orbit speed in degrees per mouse count |
| `ShowTags` | true | Tags on at start |
| `ShowHudLine` | true | Show the HUD line |
| `ClickPicking` | true | Left-click selection on |
| `FontSize` | 17 | Panel font size (14..26) |
| `FollowAiUnits` | true | Clicks may also select AI units |
| `TagsForAi` | false | Small tags over AI units (needs `FollowAiUnits`) |

When our keys are ignored:
- while text input is active (chat, console);
- in the pause menu or the main menu;
- while the Si_ModUI panel is open.

ModUI is detected by `Game.GetCameraEnabled()` returning false while our panel is closed. ModUI's client patches exactly that method while its panel is up. Vanilla returns false only during a benchmark. `MenuManager.IsAnyMenuOpened` was not used for this, because it is also true for the large map and the end screen, where our keys should still work.

Vanilla keys keep working:
- Tab = large minimap;
- M = map;
- Enter = chat;
- F6 = ModUI;
- the free-fly keys in Free.

Key overlaps:
- `P` and `N` are avoided on purpose: `P` is the vanilla "Vote No" key in the Voting map (opening the panel during a map or surrender vote would cast a vote) and `N` is the Global "Voice Channel Switch". Free letters for rebinding: G H I J K U. Pre-release settings files that still hold `P` / `N` are moved to `I` / `K` once on start (`PrefsVersion` 2); any other value you set is kept.
- `F` is the Unit-map "CommandingTap". It does nothing without a unit.
- In Overview, Q / E rotate the view; the free-fly roll on Q / E is off there.

## What is restored on leave

Leaving means Idle, or Resuming after a scene load. Each step is guarded, because after a scene load the old objects may already be gone:

1. The panel closes. The cursor goes back to `GameMode.GetShowCursor()`, the EventSystem is re-enabled, and the input / menu patches go inert.
2. `FreeFlyCameraController.EnableMovement = true`, if we changed it. The near clip plane is restored. The mode is set to Free.
3. The fog perspective goes back to All, and `BaseGameObject.UpdateVisibilityAll()` runs. The fog patches stay installed but are inert.
4. Before the round the faction screen would otherwise stay closed, so if the player has no team, `ShowTeamSelectMenu` is set back to `SELECT_TEAM`. Once the round runs, the vanilla loop forces it anyway.
5. The target and the HQ cache are cleared. Every Harmony prefix checks `Active` first and does nothing.

## Limits and open runtime checks

These are the items from API_NOTES "Runtime checks still open" that concern the client. None could be verified here, because there was no live server round with this client.

1. **Camera identity.** It is not proven that `FreeFlyCameraController.Instance.transform` is `Game.CurrentCamera.transform`. The client logs the answer when spectating starts ("Runtime check 1: ..."). If it is NOT the same, our modes still drive `Game.CurrentCamera`, but the hand-back to Free may jump once.
2. **Escape on the faction screen** (`CloseOnBack`). This no longer matters while spectating, because the screen is suppressed.
3. **Large map and name manager.** Unknown whether `LargeMapOpener` (M) works with no unit and no team, and whether `RTSPlayerNameManager` exists outside the strategy UI. Our own tags do not depend on it.
4. **Remote head and passenger aim.** Unknown whether a remote soldier's `CameraReference` follows the head animation at follow distance. Animation activation depends on distance, so FollowFps may look stiff at long range. Also unknown whether a passenger's `UnitAimAt` keeps updating in a compartment. The client falls back to the vehicle's aim.
5. **Fog patch.** The fog perspective relies on the `UpdateVisibility` prefix, not on `StrategyMode.get_CurrentStrategyTeam` (which IL2CPP may inline). The prefix computes and applies the visibility itself, so it no longer depends on a `ref` argument rewrite. Still untested in game: whether every visibility update goes through the patched method (the per-frame `UpdateVisibilityAll` loop calls it virtually, so it should). The minimap fog via `GetCurrentFogTeam` switches independently.
6. **Mid-round joiners on 0.9.47.** On 0.9.47 servers with Si_PacketChunker, a mid-round joiner may hold incomplete detection lists, so a team perspective shows too little (API_NOTES §1).

Other limits:
- Edge panning in Overview is not implemented: the cursor is locked in that mode. Pan with WASD.
- On the 1.0.x `MP_StrategyMatchmaking` mode, the game itself sets `EnableMovement = false`. The client sets it back while spectating in Free. Untested.
- The client cannot see where a commander looks. That needs the phase-2 `K` / `C` relay; a commander is shown via their HQ.
- Kill feed, action cam and ModUI-driven view / fog are phase 2. The server already relays `V` / `F` from `/spectate view|fow`.

## Install

1. MelonLoader **0.7.x** (tested with v0.7.3 Open-Beta) in the Silica client.
2. Copy the `Si_Spectator.dll` that matches the client's Steam branch into `<Silica>\Mods\`:
   - default branch (Beta 0.9.47): `Projects\_dist\Si_Spectator_0.1.0_2026-10-25\default\Client_Mods\Si_Spectator.dll`, built with `dotnet build -c Release`;
   - test branch (Release 1.0.x): `...\test\Client_Mods\Si_Spectator.dll`, built with `dotnet build -c Release -p:SilicaBranch=test` against the 1.0.7 interop.
3. Branch pairing: one client DLL per Steam branch, never mixed. The server needs `Si_Spectator_Server.dll` of ITS branch; the protocol is the same on both.
4. Without the server mod the client stays Idle (5 hellos, then silence).

Si_ModUI can be installed at the same time:
- it uses packet 126 and its own patches, which coexist with ours;
- F6 opens it over a spectator session, and our cameras freeze while it is open.

Session logs are written to `UserData\Si_Spectator\session-<UTC>.log`, one per game start. Every state change, mode, target and fog change, and every ignored packet is logged there.

Build:

| Branch | Command | Output |
|---|---|---|
| default | `dotnet build -c Release` in `Client\` | `bin\Release\default\` |
| test | `dotnet build -c Release -p:SilicaBranch=test` | `bin\Release\net6.0\` |

The reference sets live in `include\client\<branch>\` (see `include\client\README.txt`).
