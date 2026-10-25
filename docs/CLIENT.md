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
| `V\|<networkId>\|<channel>` | Selects that player and follows in the last follow mode (Auto at first). |
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

Press `C` to cycle the modes: Free → Auto → FollowFps → Shoulder → Orbit → Overview → Fixed. Following someone (click, `G`, `[` / `]`, panel, `/spectate view`) starts in the last follow mode, **Auto** at first.

| Mode | What it does |
|---|---|
| **Free** | The vanilla `FreeFlyCameraController`, unchanged: WASD, Space / Ctrl, Shift, wheel = speed, MMB = zoom, RMB = stick to object, Q / E = roll. A small centre dot marks the point a click picks from. |
| **Auto** | The followed player's own view, as far as the game replicates it. **Aliens** (`CreatureDecapod`): the owner sends its first / third person state, third-person angles and camera offset in every ObjectUpdate (CreatureDecapod.cs 0947:940-950 / 106:985-995; stored by other clients at 0947:1017-1026 / 106:1062-1071), so Auto shows exactly the player's view: `CameraReference + m_CameraLocalPosition`, rotation from `m_Camera3rdAngles` + free look; first person (offset zero) = the FollowFps anchor. **Soldiers**: the choice (`m_bTarget3rdPerson`, toggled by "Camera Cycle", Soldier.cs 0947:1722-1726 / 106:1770-1774) is **not networked** (absent from `OnSendNetUpdate` / `OnSendNetInit`, 0947:773-863), so Auto shows what a fresh player sees, first person; the HUD line says "view: default first person, player's choice not networked". **Vehicles / seats**: the vehicle orbit. |
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
- **Release.** A left click (or Escape) in any follow mode returns to Free; the camera stays where it is and free fly continues from there.
- **Large map.** While the large map is open the camera keeps still.
- **Target is the player.** A follow mode follows the player, not the unit. If the unit dies, the camera holds and the status shows "waiting for X to (re)spawn". If the player leaves or loses their team, the camera returns to Free.

### Picking a player

Several inputs pick whom to follow:
- **Hover + left click** (Free, Overview, Fixed; cursor locked): a white centre dot marks the pick point. About 15 times a second the client runs the pick: a ray from the camera through the screen centre takes the hit collider's parent `Unit` (its player, or the AI unit, see below); if the ray misses, the unit closest to the dot within 6 % of the screen height. The hovered unit's tag is highlighted (brighter, larger, a thin box) with "click: follow", and the dot turns yellow. **One left click follows it at once** in the last follow mode (Auto at first). `G` does the same for the hovered unit, or follows the current selection when nothing is hovered.
- **Left click while following** releases back to Free (camera stays where it is).
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

### Large map (M) and minimap (Tab)

Findings (identical code in 0.9.47 and 1.0.6):
- `LargeMapOpener` (Silica.UI.Menus/LargeMapOpener.cs:22-53, handler at :39) subscribes to the Global "Map" action in `OnEnable` and toggles the `LargeMap` menu. It has no team check, but which HUD object carries it is not visible in code; if it is disabled for a team-less, unit-less player (UIMode None), M does nothing.
- The FPS minimap lives under `UIManager.MinimapCanvas`, which `UIManager.SwitchUIMode` turns **off** in UIMode None (UIManager.cs 0947:310-323), the mode a free-flying team-less player is in. `MinimapToggle.MapActionPerfomed` (MinimapToggle.cs:41-54) also requires UIMode FPS. So Tab does nothing for a vanilla spectator.

What the client does:
- **M**: in LateUpdate, when the "Map" action fired this frame and the large map's open state did not change since the previous LateUpdate (the vanilla opener did not react), the client opens / closes the `LargeMap` registered in `MenuManager._menus` via the non-generic `MenuManager.OpenMenu(Menu)` / `CloseMenu(Menu)` and centres it on the followed unit or the camera. Same guards as vanilla (no text input / console / pause / main menu / ModUI, not on the spawn-pick screen), and not while our panel is open. The session log says once which path acted: "Map key handled by Spectator ..." or "... by the vanilla LargeMapOpener".
- **Why a faction UI is instantiated.** The large map, the minimap canvas, the `UIManager` (and the cursor handling) and the `LargeMapOpener` all live in the per-faction UI prefab `Team.UIPrefab` (Team.cs 0947:292 / 106:270): `UIManagerSol`, `UIManagerCentauri`, `UIManagerAlien`, `UIManagerGM`. The scene component `TeamUI` (TeamUI.cs, identical in both branches; the `.../GameplayUIBase/UI` object) destroys its instance and instantiates `team.UIPrefab` under itself on every local team change, and only destroys for a null team. A team-less player therefore has no UIManager, no minimap, no large map and no vanilla M handler (live test 3: the LargeMap objects existed only inside those prefabs, e.g. `UIManagerSol/UIs/Shared/Canvas, BIG map/Sol_LargeMap`).
- **What the client does.** About 0.5 s after `S|on` (and again after a scene-load re-entry), when `TeamUI._currentUIInstance` is null, it instantiates the UIPrefab of the team named by `SpectatorUiFaction` (default `Sol`; fallback the first team with a prefab) under the `TeamUI` transform, exactly like `TeamUI` does (fallback parent: the parent of the registered `InGameMenu`). Log: `Spectator UI: instantiated <prefab> (team <name>) under <path>; UIManager.Instance set|null.` The prefab's own `LargeMap` registers itself in `MenuManager` (`Menu.Awake`), its `LargeMapOpener` handles M, its minimap canvas serves Tab. Purely local UI.
- **Lifetime.** Destroyed (deactivated first, so `UIManager.OnDisable` clears `UIManager.Instance` and unsubscribes immediately, then `Destroy`) on leave / `S|off`, on any non-null team of the local player (in a prefix on `TeamUI.PlayerTeamChanged`, i.e. before `TeamUI` creates the real one), on scene load and on disconnect. Never two UI managers. Log: `Spectator UI: destroyed (<why>).`
- **Opening the map.** The registered LargeMap is looked up in `MenuManager._menus`; with the prefab alive the vanilla opener usually reacts itself. Only when the open state did not change during the frame does the client toggle it via `OpenMenu(Menu)` (last resort `OpenMenu<LargeMap>(out _)`) / `CloseMenu(Menu)` (fallback `CloseMenu<LargeMap>()`). The respawn part (`LargeMap.RespawnMenu`) is hidden on maps we open. The earlier clone approach is removed (live test 3: a bare LargeMap clone rendered nothing without its canvas and UI manager).
- **Map concealment.** `ScoreBoard.LateUpdate` (on the LargeMap's "Match" object) shows the "NO TEAM / MAP CONCEALED" overlay (`mapHider`) whenever `GameMode.GetConcealMap()` is true, and that is `return !Team.CurrentTeam;` (GameMode.cs 0947:154 / 106:176, no override). A postfix returns false while spectating (logged once: `Map concealment off for the spectator (GameMode.GetConcealMap -> false).`). The map's fog layer then follows our fog perspective: Fog = All -> `GetTeamFogTexture(null)` = null -> no fog layer; Fog = X -> X's fog texture.
- **Minimap.** The minimap canvas sits under the FPS `UICollection` (`UIs/FPS/Canvas, Minimap`), which `UIManager.SwitchUIMode` only activates in UIMode FPS; in UIMode None it also switches the canvas off on every mode switch (UIManager.cs 0947:306-410 / 106:356-460), and a spectator never leaves None. So while spectating the client moves the canvas one level up (`UIs/FPS` -> `UIs`, still inside our UI instance, destroyed with it) and keeps it on (ShowMinimap / hold Tab as before); a postfix on `SwitchUIMode` re-runs this so the None branch never leaves it off. The FPS HUD itself stays off. Logged once per entry: `Minimap canvas: <path>, activeInHierarchy True (moved out of the FPS collection).`
- **Unit cap rows.** `RTSUnitCapPanel` (Silica.UI/RTSUnitCapPanel.cs) returns early without `Team.CurrentTeam`, so the prefab's placeholder `999` values stay; every `RTSUnitCapPanel` inside our UI instance is hidden (logged: `Unit cap rows hidden in the spectator UI: <n> RTSUnitCapPanel.`).
- **Risks to watch in the log** (from the decompile): the HUD scripts that dereference `Team.CurrentTeam` without a check (`Constructable`, `IdleHarvesters`, `RTSResourceStorageNotification`, `RTSRadialStructureSelectionPanel`, `RTSStructureSelectionShortcutButton`, `RTSTechLevelLabel`, `RTSUnlinkedStructuresNotification`, `RTSWorkersIndicator`) are RTS widgets in the RTS `UICollection`, which stays off in UIMode None; `RTSMap`'s resource layer checks the team; the map icon colouring handles a null team (`GameMode.GetTeamsAreEnemy` returns false). Anything on the always-on Shared canvas that assumes a team or a unit would show up as repeated Unity NullReferenceExceptions in `Latest.log`.
- Diagnostics: a `LargeMap diagnostics` line (MenuManager, registered menus) and a `LargeMap candidates (n): ...` line listing EACH LargeMap object: name, scene name, scene valid / invalid / error, hideFlags, SCENE / asset, registered, path with activeSelf / activeInHierarchy per level. Every M press we handle logs `Map key handled by Spectator: <path that worked>` or `Map key handled by Spectator: FAILED to open (<every attempt>)`; when the vanilla opener reacts, `Map key handled by the vanilla LargeMapOpener.` is logged once.
- While the large map is open, none of our keys fire except M (above) and Escape (vanilla closes the map); the camera keeps still; tags, crosshair dot, follow HUD and HUD line are hidden.
- **Minimap**: while spectating in UIMode None the client switches `MinimapCanvas` on itself and drives `MinimapToggle._minimap` / `_largeMinimap` from the Global "Large Minimap" action: **hold Tab = large minimap** (the 1.0.6 tap-to-toggle is not replicated). `ShowMinimap` (default on) also shows the small minimap. Hidden in the pause menu, while the large map is open, with H, and restored (canvas off) on leave.
- The map fog texture follows the fog perspective (our `GetCurrentFogTeam` postfix; `LargeMap.Update` reads `FogOfWar.CurrentTeamFogTexture` every frame, the minimap gets it from `FogOfWar.ShowFog`).

### Map faction filter

Both maps build their unit / structure icons from `TargetMapIconDisplay.GetData()` = `Target.ProcessTargetsForTeam(Player.CurrentPlayer.Team, ...)` (TargetMapIconDisplay.cs:35-39). For a team-less player that returns **every** active target with no visibility check (Target.cs 0947:215-252 / 106:227-264). `MapIconDisplay.UpdateMarkers` (MapIconDisplay.cs:29-75) rebuilds the icons from that list every frame: icons missing from the list are released to the pool on the next frame, and come back as soon as they are in the list again. Icon colour is the team colour or ally / hostile (`HandleColor`).

So one postfix on `Target.ProcessTargetsForTeam` (only when its team argument is null and we are spectating) filters the large map AND the minimap, because the code path is shared:
- **Show** (faction filter) `All | <team>`: keeps only targets of that team.
- **Fog** perspective X: keeps only targets visible or static-visible under X (`IsVisible || IsStaticVisible`, set by our `UpdateVisibility` prefix). So map icons follow the fog perspective too, which vanilla would not do for a team-less player.
- No forced refresh is needed: the next frame's list rebuild brings filtered icons back.
- The other caller of `ProcessTargetsForTeam`, `Crosshair` (Crosshair.cs:732), returns early without a `Unit.CurrentUnit` (line 716), which a spectator never has.

UI: while the large map is open, a small toolbar at the top of the screen shows `Show: [All] [Sol] [Centauri] [Alien]` and `Fog: [All] [...]` (match teams, current one highlighted). Only our buttons consume their clicks; the map keeps its own input. The same two rows are in the panel. No new keys. The faction filter resets to All on every spectate entry (not remembered).

### Fog perspective

Press `F` to cycle: All, then each team of the match. The match teams are the game mode's active team setups plus any team a player is on.

On first use the client installs two Harmony patches:
- a prefix on `BaseGameObject.UpdateVisibility(Team)` that computes the object's visibility for X itself, exactly like the original method (identical bodies in 0.9.47 and 1.0.6: `Target` active, `GetIsObjectAlwaysVisible`, X's `DetectedTargets` / `GetIsTargetAtIndexActivelyVisible`, then `SetVisibility(..., GetIsRendered(...))`), and skips the original. It does not depend on Il2CppInterop writing a rewritten argument back. If that computation ever throws, the prefix logs it once and falls back to replacing the team argument with X and running the original;
- a postfix on `FogOfWar.GetCurrentFogTeam` that returns X, for the minimap and large-map fog.

It then calls `BaseGameObject.UpdateVisibilityAll()`. Both patches return at once when the perspective is All or when not spectating. A client that never picks a team perspective never pays for the per-object hook.

All is the vanilla state: outside RTS mode everything is visible.

### Player HUD and crosshair while following: decision NO-GO for `ShowPlayerHud`

The vanilla HUD (health, ammo, crosshair, spread, unit info, special abilities) binds to the static `Unit.CurrentUnit`. Setting only `Unit.CurrentUnit` (never `Player.ControlledUnit` / `OwnerPlayer`) to the followed unit was researched across every reader: 202 references in 69 files (0.9.47), 204 (1.0.6, plus `MP_Campaign`, `SandboxStrategyController`, `AchievementsManager`, `CommanderGamepadHints`). It also switches the input map to `Unit | Voting` (Silica/GameInput.cs 0947:291-295), so every Unit-map key acts "for" that unit. Readers that would **send a packet or change server-visible state**:

| Reader | File:line (0947) | What it would do |
|---|---|---|
| `UsePoint.UpdateUse` | UsePoint.cs:205-247 | E = `CurrentUsePoint.Use(currentUnit)` (board, claim, dispense, take-over requests) or `ExitCompartment` for the followed unit |
| `PingManager.HandlePingAction` | Silica.UI/PingManager.cs:152-160 | middle mouse = a team ping packet |
| `FPSCommanding.CanLocalFPSCommand` + its RPCs | FPSCommanding.cs:63-72, 268 | F (our fog key) = squad orders to the followed player's AI group |
| `PurchasePointUI.CanInteract` | PurchasePointUI.cs:128-141, 229, 253 | world purchase panels become usable: `RPC_RequestPurchase`, `SendPlayerSelectUnit` |
| `StructureTaskUI` / `TeleportUI` | StructureTaskUI.cs:162-249, TeleportUI.cs:289-346 | structure task / teleport UIs shown and usable for that unit |
| `V|<networkId>|<channel>` | Selects that player and follows in the last follow mode (Auto at first). |isionModeManager.Update` | VisionModeManager.cs:146-158 | Q cycles vision modes of the followed unit |
| `DebugConsole` "Suicide" | DebugTools/DebugConsole.cs:2028-2042 | `Unit.CurrentUnit.Suicide()` for the followed unit |
| `GameManager.ToggleUnit` / "Delete Units" (cheats) | GameManager.cs:177-212, 394-397 | `SendPlayerSelectUnit` / destroy the unit when cheats are on |
| `MP_Strategy.OnUnitDestroyed` | MP_Strategy.cs:1948-1951 | starts the local respawn timer when the followed unit dies |
| Voting input map | Silica/GameInput.cs:294 | vote keys (P = Vote No) active |

Local-only readers (HUD widgets, `Game.CurrentGamePosition`, FOV, zoom, camera lerp, `CharacterIK`, `ConstructionSite` warnings, `DeathCam`, `Map` centre, `MissionTask`) would be fine. But neutralising the risky list needs at least eight prefixes on input / UI code that changes between builds (1.0.7 has no decompile here), and any missed reader breaks the hard rule (no gameplay packet, nothing the server could see). **Decision: no-go.** `Unit.CurrentUnit` is never set.

Replacement (own IMGUI, purely local): while following, a small panel at the bottom centre shows the player name in team colour, the unit name and an HP bar (`DamageManager.Health01`), and a small cross marks the followed player's networked aim point (`UnitAimAt.AimAtPosition`) in Auto / FollowFps / Shoulder. Ammo and spread are not replicated in a form we can read safely and are not shown.

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
| (fixed) | Left click | Free / Overview / Fixed: follow the hovered unit; while following: back to Free |
| (vanilla action) | M | large map (ours if the vanilla opener does not react) |
| (vanilla action) | Tab (hold) | large minimap |
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
| `ClickPicking` | true | Centre dot + hover highlight + left click follows; left click while following = back to Free |
| `FontSize` | 17 | Panel font size (14..26) |
| `FollowAiUnits` | true | Clicks may also select AI units |
| `SpectatorUiFaction` | Sol | Faction UI prefab loaded while spectating (minimap, large map, HUD canvas): Sol, Centauri or Alien |
| `ShowMinimap` | true | Small vanilla minimap while spectating (hold Tab = large minimap either way) |
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

## Free cam self-heal

Live test 2: spectating straight from the commander seat left the free cam dead (entry log "FreeFly active False, cursor visible"). The free-fly controller refuses while `StrategyMode.InStrategyMode`, and the client only leaves RTS mode in `MP_Strategy.SetCommander` when `CurrentPlayer.Team` still equals the seat's team (106:1589-1596); when the team is set to null first, RTS mode stays on, and `CursorManager` keeps the cursor visible for it. A spectator left in RTS mode is also a risk (RTS clicks are orders), so every LateUpdate while spectating:
- `InStrategyMode` → `StrategyMode.ToggleStrategyMode(false)`. It sends nothing in that direction (it only sends `SendPlayerSelectUnit` when enabling with a unit, StrategyMode.cs 0947:562-572). If a stale direct-control unit is set (`InDirectControl`), leaving would run `DirectControl(null)`, which sends a unit request, so the client does not call it and only logs. Log once per entry: `Self-heal: left the RTS (strategy) mode ...`.
- Cursor visible while no menu is open (`MenuManager.GetCurrent()` null), no text input, no pause / main menu, our panel and the large map closed, cameras enabled (ModUI closed), not in RTS mode and the game mode not asking for a cursor → `Cursor.lockState = Locked; visible = false`. Log once per entry: `Self-heal: cursor was visible with no menu open; locked and hidden.`
- When the ModUI panel closes (`GetCameraEnabled` flips back to true) the log says `ModUI panel closed: free cam self-heal check.`
- Diagnostics: if Free mode has had no active free-fly controller for more than 1 s while nothing of ours or the game's is open, one line per entry: `Free cam blocked: InStrategyMode=..., InDirectControl=..., CurrentUnit=..., Cinematic=..., CameraEnabled=..., ControlsEnabled=..., GameModeShowCursor=..., Cursor=visible/None, Menu=..., TextInput=..., UIMode=..., EnableMovement=...`.

## IMGUI notes

`GUI.DrawTexture` is stripped in the Silica Il2Cpp build (`NotSupportedException: Method unstripping failed`, found in the first live test). All filled rectangles (HP bars, dot, outlines, follow HUD) use a cached `GUIStyle` with a white `normal.background` drawn by `GUI.Box(rect, "", style)` under `GUI.color`, as Si_ModUI does. The client only uses IMGUI calls Si_ModUI also uses (`Label`, `Button`, `Box`, `BeginScrollView`/`EndScrollView`, `CalcSize`). Each overlay (tags, follow HUD, crosshair dot, map toolbar, panel, HUD line) is drawn in its own try/catch, and each distinct error is logged once as `ERROR overlay '<name>' (logged once)`.

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

## Phase 2: syncing the soldier's first / third person choice

The soldier view choice is local to the owning client (see Auto). Proposal, not implemented:
- **Who reports.** A player whose client runs Si_Spectator (spectating or not) reports its own view state to the server whenever it changes and once after every hello: `K|view|<netId>|<1st|3rd>` on the existing client→server channel. `netId` is the NetworkComponent ID of the unit the report is about (the reporter's `ControlledUnit`); the server accepts it only if that unit's `ControlledBy` is the sender (`__2`), and rate-limits it like `K` (≤ 5/s).
- **Relay.** The server keeps the last value per unit and sends `W|<netId>|<1st|3rd>` to every spectator client: on change, and in one batch after a spectator's hello (≤ 1800 chars, so ~90 units per packet; chunk otherwise).
- **Use.** The spectator client uses the relayed value in Auto for soldiers: 3rd → the game's own offset (`CameraReference.position + rot * (0.5, -0.1, -1.5)`, Soldier.cs 0947:2033); 1st or unknown → the FollowFps anchor. The HUD note then says "view: player's".
- **Protocol.** `Shared\Protocol.cs` would get `ViewReport(netId, third)` / `TryViewReport` and `ViewRelay` / `TryViewRelay`; `K|view|...` needs a sub-kind because `K` is the commander pose today, or a new kind letter (`P`) to keep `K` unchanged. Recommended: the new letter `P` (client→server) and `W` (server→client).
- **Fallback.** Without the mod on the followed player's client nothing is reported and Auto stays on the default.

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
