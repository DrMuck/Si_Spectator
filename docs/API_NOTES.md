# Si_Spectator – game API notes

Research notes for the Si_Spectator server + client mod pair. Written 2026-10-25 from the decompiled game code.

**Line references**
- `106:File.cs:123` means `C:\tmp\silica_decompile_106\File.cs` line 123. That decompile is the dedicated server Managed DLLs, Release 1.0.6, Steam test branch.
- `0947:File.cs:123` is the same for `C:\tmp\silica_decompile_0947`, Beta 0.9.47, Steam default branch.
- A path with no prefix is relative to `C:\Users\schwe\Projects`.
- The client runs the same game code as Il2Cpp.

**Branch state on this machine, as of 2026-10-24**
- The local dedicated server runs **Release 1.0.6**. `E:\...\Silica Dedicated Server\MelonLoader\Latest.log` says `Game Version: Release:1.0.6`.
- The local game client runs **Beta 0.9.47**. `C:\SteamLibrary\...\Silica\MelonLoader\Latest.log` says `Game Version: Beta:0.9.47`.
- The client interop `C:\SteamLibrary\steamapps\common\Silica\MelonLoader\Il2CppAssemblies\Il2CppSilicaCore.dll` (dated 2026-10-24) is a **0.9.47** build:
  - it contains `SelectBestVisibleCandidate`, a Team method that only 0.9.47 has;
  - it does not contain `TeamInitTargets`, which only 1.0.x has.

  Interop surface claims below were checked with `ilspycmd -t Il2Cpp.<Type>` against that DLL.

**Il2Cpp interop assumption.** Il2CppInterop exposes private and protected fields and methods as public members. Verified in this DLL for:
- `FreeFlyCameraController`: `TargetPos`, `TargetRot`, `ReferenceUnit`, `Activate()`, `Deactivate()`, and a setter on `IsActive`;
- `MP_Strategy.ShowTeamSelectMenu`, typed `Il2Cpp.GameModeExt.ETeamSelect`.

Interop namespaces seen:
- `Il2Cpp.*`: `FogOfWar`, `GameEvents`, `OrderedUpdateManager`, `MP_Strategy`, `StrategyMode`, `FreeFlyCameraController`;
- `Il2CppSilica.UI.*`: `StrategyHandler`, `StrategyScreenType`, `RTSPlayerName`;
- `Il2CppSilica.UI.Menus.MenuManager`;
- `Il2CppDebugTools.DebugConsole`.

**Where the plan in `docs/PLAN.md` is wrong:**
- Vanilla does **not** free the commander seat on a team change (§2, risk 1).
- A `GetCanJoinTeam` prefix alone does not block re-joining. Si_EloMatchmaking and Si_BasicTeamBalance replace the `REQUEST_JOIN_TEAM` handler, and 0.9.47 has no `GetCanJoinTeam` at all (§4).
- The `T` packet (detections relay) is not needed (§1).

---

## 0. Corrections to the "already established" list

1. **ENetworkPacketType values.**
   - 1.0.6 has 44 values, **0..43**. The last one is `TeamInitTargets = 43` (106:ENetworkPacketType.cs).
   - 0.9.47 has 43 values, **0..42**. The last one is `Overrides = 42`.
   - Neither enum goes up to 44. Byte 125 is free in both.
2. **Fog only filters the RTS view.** `StrategyMode.CurrentStrategyTeam` returns `Instance.Team`, and `StrategyMode.Team` returns `null` unless `InStrategyMode` is true (106:StrategyMode.cs:386-408):

   ```csharp
   public Team Team { get { if (!InStrategyMode || !Player.CurrentPlayer) return null; return Player.CurrentPlayer.Team; } }
   ```

   So outside RTS mode `BaseGameObject.UpdateVisibilityAll()` (106:BaseGameObject.cs:560-567) always passes `null` to `UpdateVisibility`. In first-person, third-person and free-fly views every object is visible to every client. Fog only filters the RTS view and the map fog texture.
3. **The free-fly camera turns itself on.** It is not tied to cheats. Details in §5.

---

## 1. Detected-target replication

### Evidence

**Sensors run on the server only.**
- `Sensor.TickSensors()` returns early on clients (106:Sensor.cs:117-163, same line in 0947):

  ```csharp
  if (Game.GetIsClient()) { return; }   // line 134
  ```

- `TargetGrid.DetectInGrid` is only called from `Sensor.Tick()` (106:Sensor.cs:203-209).
- `GameManager` registers `SensorTickPart1..3` as ordered updates (106:GameManager.cs:516-518).
- Clients never compute detections.

**Every detection is broadcast to every client, for every team.**
- `Team.DetectTarget(Target, Sensor, bool revealToFriends)` (106:Team.cs:1574-1620) adds a new target to `DetectedTargets` / `DetectedTargetsTime` (time `8f`), fires `GameEvents.OnTargetDetected`, then calls `RPC_TargetDetected(target)` (Team.cs:766-777).
- `RPC_TargetDetected` → `CreateRPCPacket(2)` (Team.cs:580-593) → `NetworkLayer.CreateTeamPacketWriter(this, ENetworkPacketType.TeamRPC)` (106:NetworkLayer.cs:272-283; writes the type byte and the team index) → `SendRPCPacket` → `NetworkLayer.SendPacket(writer, Reliable)` (NetworkLayer.cs:1289-1321).
- On the server, `SendPacket` loops over **all** `Player.Players` except the server player. Nothing filters by team.
- `LostTarget` (Team.cs:1645-1665) → `RPC_TargetLost` (Team.cs:779-790) works the same way.
- When the target is already known, `DetectTarget` only refreshes the time (`DetectedTargetsTime[value] = 8f`, 1576-1579) and sends **nothing**. An RPC goes out only when a target is newly added.

**Client side.**
- `ProcessMessage`, `case TeamRPC` (106:NetworkLayer.cs:857-865) runs `Team.GetTeamByIndex(index).OnNetRPC(rpc, reader)` for any team index.
- `Team.OnNetRPC`, `TARGET_DETECTED` (Team.cs:624-633) runs `DetectTarget(targetByNetID, null, revealToFriends: false)`.
- `TARGET_LOST` (634-642) runs `LostTarget`.
- Result: the client holds an exact copy of every team's list.

**Initial sync when a player joins.**
- 1.0.6: `NetworkLayer.SendTeamsInit(Player)` (106:NetworkLayer.cs:1906-1933) sends to that one player, for each team:
  - one `TeamInit` (27) packet;
  - `TeamInitTargets` (43) packets of at most 389 NetIDs each, from `Team.OnSendNetInitTargets(writer, start, max)` (Team.cs:707-722).
- 1.0.6 client:
  - `Team.OnNetInit` clears the lists (Team.cs:678-705);
  - `OnNetInitTargets` (Team.cs:724-735) calls `DetectTarget(target)` with the default `revealToFriends: true`, which also copies the target to allied teams on the client.
- 0.9.47: `NetworkLayer.SendTeamsInit()` (0947:NetworkLayer.cs:1707-1735) sends **one** `TeamInit` packet with all teams and all target lists to **every** player, on every join. The target list is in `Team.OnSendNetInit` (0947:Team.cs:695ff). The client reads it in `OnNetInit` (0947:Team.cs:720-745).

  Our `Si_PacketChunker\Si_PacketChunker\Si_PacketChunker.cs` (header comment) trims those lists to fit the 2400-byte packet, human teams first. Combined with "an RPC only for new targets":
  - a client that joins mid-round on 0.9.47 can miss entries;
  - for structures (`IsStatic`, never lost) the gap is permanent on that client.

**The detection timer is never decremented on clients.**
- `Team.TickTeams()` (106:Team.cs:871-910) skips the decrement on clients (`if (isClient) continue;`, 897-900).
- So every client entry keeps time 8, and `GetIsTargetAtIndexActivelyVisible(i)` (`DetectedTargetsTime[i] > 0f`, 1214-1217) is true for every listed target.
- On the server, enemy mobile targets are lost after 8 s without contact. Static targets (structures) stay listed with time <= 0 ("last seen") and are never sent as lost.
- A client cannot tell "last seen" from "visible now". That is also true for the vanilla commander client, so the spectator sees what that team's commander sees.

**Fog per team is computed locally for every team.**
- `FogOfWar.OnUpdate()` (106:FogOfWar.cs:503-640) adds explore jobs for every team in `TeamDataList`:
  - the team's own objects;
  - friendly teams' objects;
  - detected enemy objects that are actively visible (560-578).
- `GetTeamFogTexture(Team)` (275-287) returns the texture for any team.
- Only `ShowFog` (371-395) uses `GetCurrentFogTeam()` (209-224) to choose which texture to display.
- `FogOfWar.OnPlayerChangedTeam` (661-671) destroys the other teams' textures whenever any player changes team. They are recreated on demand by `GetTeamFogTexture`, and the explored data lives in `FogOfWarData`.

### Conclusion

**"Team X's fog view" can be done purely on the client.**
- Detected lists are replicated for all teams on both branches.
- Fog is computed locally for all teams.
- No server-side send is needed. The plan's `T`/`W` packets can be dropped.
- Caveat: 0.9.47 servers with Si_PacketChunker may give mid-round joiners incomplete lists.

What to patch on the client to render team X:
- **3D visibility.** Harmony prefix on `BaseGameObject.UpdateVisibility(Team strategyTeam)` (106:BaseGameObject.cs:569) that replaces `__0` with team X while "team X view" is on.
  - It is virtual (`SandboxObject` overrides it), so patch the base method.
  - Calls go through the vtable, so IL2CPP inlining is not a concern.
  - Call `BaseGameObject.UpdateVisibilityAll()` once after switching.
  - `FogOfWar.ShowFog` calls `UpdateVisibilityAll()` on every call, and `OnUpdate` calls `ShowFog` on every update, so visibility is refreshed continuously.
  - Because CurrentStrategyTeam is null outside RTS mode (§0.2), this patch is what hides team X's unseen enemies in free-fly or follow views.
- **Alternative.** Postfix the static getter `StrategyMode.get_CurrentStrategyTeam`. Risk: the C++ compiler may inline small getters into IL2CPP callers, which would bypass the patch. Verify at runtime before relying on it.
- **Minimap / large-map fog.** Postfix `FogOfWar.GetCurrentFogTeam()` (209) to return team X. `ShowFog` passes `GetTeamFogTexture(GetCurrentFogTeam())` to `Minimap.Instance.SetMapFogOfWarTexture` and to `CurrentTeamFogTexture` (379-388), whether or not `enable` is set. The world fog decal is only on in RTS mode (`ShowFog(!FogOfWarDisabled && StrategyMode.InStrategyMode)`, 505).
- **"All" (no fog).** Pass `null`. That is already the default outside RTS mode.
- **If the server ever had to send team X's detections:**
  - `Team.OnSendNetInitTargets(writer, start, max)` is public;
  - `NetworkLayer.SendPacketToPlayer(writer, ENetworkPacketSend.Reliable, player)` (106:NetworkLayer.cs:1323-1342, 0947:1124) targets one player;
  - on 1.0.6, a vanilla `TeamInitTargets` (43) packet sent to one player is handled natively (`OnNetInitTargets` only adds).

  Not needed.

---

## 2. Null team over the wire

**Round trip.**
- `GameByteStreamWriter.WriteTeam(Team)` (106:GameByteStreamWriter.cs:729-735, 0947:724-730) writes the byte `team ? team.Index + 1 : 0`.
- `ReadTeam()` (106:GameByteStreamReader.cs:557-561, 0947 same) returns `Team.GetTeamByIndex(byte - 1)`. For 0 that is -1, and `GetTeamByIndex` (Team.cs:2292-2299) returns `null`.
- **Null round-trips cleanly on both branches.**

**What a client does with `PlayerSelectTeam(player, null)`.**
- NetworkLayer.cs:485-502 (0947:309-326) runs `player7.Team = null`.
- The `Player.Team` setter (106:Player.cs:71-97) fires `GameEvents.OnPlayerChangedTeam(player, old, null)` and `CleanupGroups()`.
- The client part of `MP_Strategy.OnPlayerChangedTeam` (106:MP_Strategy.cs:1854-1864) only clears `WaitingForServer` for the local player.
- Other client subscribers only rebuild lists or colours and handle null:
  - `UIManager` → `StaticUpdatePlayersList`;
  - `PlayerInfoManager.LoadList`;
  - `TeamBasedPlayerScoreSorting`;
  - `CurrentTeamUIColorSource`;
  - `BarksHandler`;
  - `FogOfWar`.

**A null team is a normal vanilla state.**
- `GameMode.ClearGame` → `Player.ClearAllPlayerTeams()` (106:GameMode.cs:508, Player.cs:340-346) runs at every restart.
- A mid-round joiner has no team while the faction screen is up.
- `CheckPlayersOnEliminatedTeam` (106:MP_Strategy.cs:1920-1962) already uses the exact sequence we need:

  ```csharp
  NetworkLayer.SendPlayerSelectUnit(player, null);
  player.ControlledUnit = null;
  player.Team = null;
  NetworkLayer.SendPlayerSelectTeam(player, null);
  ```

**Null-safe paths (checked).**
- `MP_Strategy.GetUnitPrefabForPlayer` returns null for a null team (106:1889-1893).
- `GameModeExt.GetTeamSetup(null)` returns null (106:GameModeExt.cs:96-106).
- Chat:
  - Server routing of team-only chat: `(!teamOnly || !(player2.Team != player.Team))` (106:NetworkLayer.cs:1762-1770).
  - A spectator's team chat reaches only other team-less players (lobby players and spectators). That gives a free "spectator channel".
  - Spectators never receive any team's team chat.
  - Client filter: `Player.CurrentPlayer.Team == player10.Team` (582).
  - `Player.LogChatMessage` prints `[NO TEAM]` (Player.cs:598-636).
  - `ChatMessageUI` colours a team-less sender white (ChatMessageUI.cs:69).
- Voice:
  - The server relays voice only where `GetCanReceiveVoice` passes (NetworkLayer.cs:1275-1287, Player.cs:383-424).
  - A different team means proximity-only, and proximity needs both players to have a `ControlledUnit`.
  - So spectators hear only other team-less players' team voice and no proximity voice.
- Scoreboard: `PlayerScore` colour (Silica.UI/PlayerScore.cs:158-164) and `VoiceChatEntry` are null-checked.
- `MP_Strategy.UpdateGameLoop`: the respawn branch requires `(bool)Player.CurrentPlayer.Team` (2405).
- `HelperMethods.GetTeamColor(Team|Player)` handles null (`Silica_data-bomb\Si_AdminMod\HelperMethods.cs:403-428`).
- `Si_Logging`: `ProcessOnPlayerChangedTeam` returns on `newTeam == null` (Si_Logging.cs:421-455).
- `Si_ReplayLogging`: `GetTeamIndex(null)` is safe (Si_ReplayLogging.cs:463, 883).

**Null-safety risks.**
1. **The commander seat is NOT freed by a team change.** Severity: high.
   - `MP_Strategy.OnPlayerChangedTeam` (server part, 106:1866-1882):
     - destroys the player's units (`DestroyAllUnitsForPlayer`);
     - removes the respawn tracker;
     - before `GameBegun`, removes the player from the old team's lottery.
   - It never calls `SetCommander(team, null)`.
   - `GameMode.DestroyAllUnitsForPlayer` (106:GameMode.cs:629-644) only sets `ControlledUnit = null` for a commander.
   - `MP_Strategy.OnPlayerLeft` (106:1812-1823) clears the seat only `if (player.Team != null && player.IsCommander)`. A commander moved to spectator who then disconnects leaves a **ghost commander**: the AI never takes over (AI takeover needs `!teamSetup.Commander`, 2546-2577).
   - `Player.IsCommander` → `MP_Strategy.GetPlayerIsCommander` only compares `teamSetup.Commander == player` (343-358), so a team-less player can still "be" a commander.
   - **Always demote first:** `SetCommander(team, null)` + `RPC_SynchCommander(team)`.
     - 1.0.6: both are `protected` (106:1553, 949).
     - 0.9.47: `SetCommander` is private (0947:1511).
     - Use reflection the way `Silica_data-bomb\Si_CommManagement\CommanderPrimitives.cs:69-100` does, or call Si_EloMatchmaking `GameApi.SeatCommander`. CommManagement's `DemoteTeamsCommander` (53-67) then also sends the role RPC and respawns the player, which a spectator must not get.
     - Public alternative with no reflection, in both branches: `MP_Strategy.CommanderMutinyForTeam(Team)` (106:1639-1648, 0947:1598). It runs `SetCommander(team, null)` + `RPC_SynchCommander(team, kicked: true)`. The side effect: the client gets the "removed from commander seat" popup and a 30 s mutiny lock (`ShowRoleSelectionIfOusted`, 1651-1680). Prefer reflection with `kicked: false`.
     - `SetCommander(null)` is also what starts the AI takeover countdown (`AICommanderBeginCommandDelay = AICommanderPlayerLeftWaitTime`, 1632-1635, counted down in `UpdateGameLoop` 2546-2577).
2. **The commander lottery uses Team as a Dictionary key.** `CommanderLottery.TryGetValue(null, …)` throws `ArgumentNullException`.
   - Client: `LeaveCommanderLotteryIfQueued` (106:1401-1406) uses `Team.CurrentTeam`. It is only reached from role selection, which needs a team.
   - Server: `REQUEST_COMMANDER_LOTTERY_ENTRY` (106:782-791) adds the player for **the team the client sent**, never checked against `player.Team`.
   - `ResolveCommanderLottery` (1511-1532) then calls `SetCommander(key, player)` without a team check.
   - A crafted packet could seat a spectator. Vanilla UI cannot send this without a team.
3. **The RTS UI assumes `Team.CurrentTeam` is set** without null checks. Examples:
   - `RTSUnitCountDisplay.cs:51`
   - `RTSWorkersIndicator.cs:95,173`
   - `RTSResourceStorageNotification.cs:46,97`
   - `Silica.UI/Constructable.cs:275,341`
   - `RTSRadialStructureSelectionPanel.cs:132`
   - StrategyMode selection, `StrategyMode.cs:3688`: `Team.DetectedTargets` on a null `Team`.

   This only matters if a team-less client enters StrategyMode (§7).
4. **Surrender vote.** `SurrenderVoteManager.StartVoteForTeam_Request` (106:Silica.Voting/SurrenderVoteManager.cs:47-51) reads `player.Team.TeamId`, which throws on the client if a spectator can open the vanilla surrender vote. Verify whether that UI is reachable without a team. The databomb `!surrender` checks null (`Si_SurrenderCommand.cs:101-106`).
5. **Name tags.** `RTSPlayerNameManager.cs:81` uses `player.Team.TeamColor` for other players who have a `ControlledUnit`. Units are only spawned for players with a team, so this is safe unless an admin spawns one for a team-less player.
6. **Vanilla server trust gaps.** A vanilla client never does these, but a modified or "hard-attached" client (§6) could. The server mod should drop them from spectators as cheap hardening, with a prefix on `NetworkLayer.ProcessMessage` on the server that checks the sender `__2`:
   - `PlayerSelectTeam` from a client is relayed and applied without validation (106:NetworkLayer.cs:485-501). The player is identified by the ID **in the packet**, not by the sender.
   - `PlayerSelectUnit` (445-483) only checks `GetCanSwitchToUnit` (Player.cs:644-655: the unit is not controlled). No team check, so any client can grab any uncontrolled unit of any team.
   - `ObjectUpdate` / `ObjectRPC` from clients are relayed and applied without an owner check (NetworkLayer.cs:652-683).
   - Strategy orders are effectively unvalidated in MP_Strategy (§7).
   - Spawn requests: `TrySpawnPlayerAtSpawnPoint` checks `SpawnPoint.GetAvailableUnits(player.Team, …)` (106:GameMode.cs:~768-790). `AvailableUnits_Override` (SpawnPoint.cs:56-59) is team-independent, and the spawned unit gets `component.Team = player.Team`, which would be null. Verify whether any map uses overrides.

---

## 3. What a vanilla client shows for a team-less player mid-round

**State.**
- `protected ETeamSelect ShowTeamSelectMenu = ETeamSelect.SELECT_TEAM;` (106:MP_Strategy.cs:127). In 0.9.47 it is `private` (0947:106).
- `GameModeExt.ETeamSelect { NONE, SELECT_TEAM, SELECT_ROLE, RESPAWN_MENU }` (106:GameModeExt.cs:32-38).

**After the server sets the team to null during a running round:**
1. The client `OnPlayerChangedTeam` (1854-1864) changes nothing visible.
2. `MP_Strategy.UpdateGameLoop` (106:2395) forces the menu back every frame (0947:2497-2499 is identical):

   ```csharp
   if (!base.WaitingForServer && base.GameOngoing && DefeatChangeSideTimer <= 0f && !Player.CurrentPlayer.Team)
       ShowTeamSelectMenu = ETeamSelect.SELECT_TEAM;   // 106:2534-2537
   ```

3. `GameMode.Update` calls `UpdateGameLoop(dt)` and then `HandleUI()` every frame (106:GameMode.cs:1150-1186).
4. `MP_Strategy.HandleUI` (1740-1793) calls `StrategyHandler.SwitchTo(StrategyScreenType.Faction, this)`. `SwitchTo` (106:Silica.UI/StrategyHandler.cs:34-112):
   - sets `GameManager.Instance.Blur01 = 1f`, so the background is **blurred**;
   - closes the other strategy screens;
   - opens `FactionMenu`, a `Silica.UI.StrategyFactionsMenu`.
5. `GetShowCursor()` returns `ShowTeamSelectMenu != NONE` (1703-1706), and `CursorManager` shows the cursor (Silica.UI/CursorManager.cs:244-251).
6. With the cursor visible, `Game.GetControlsEnabled()` is false (106:Game.cs:663-674), so the free-fly camera ignores input (§5).

**Faction screen.**
- `StrategyFactionsMenu` marks factions as restricted via the client-side `GetCanJoinTeam` (StrategyFactionsMenu.cs:52-62).
- A click calls `SelectTeam(team)` (106:MP_Strategy.cs:1193-1235) → `RPC_RequestJoinTeam` (884-895).
- **A server-only spectator can therefore re-join at any time** unless the server denies it (§4).

**Escape.**
- `GameManager.OnEscape()` (106:GameManager.cs:163-176) asks, in order: `StrategyMode.OnEscape`, `GameMode.CurrentGameMode.OnEscape()`, `MenuManager.OnEscape()`. If all refuse, it toggles `InGameMenu`, the pause menu.
- `MP_Strategy.OnEscape()` (1795-1808) returns `true` only for `SELECT_TEAM && Team.CurrentTeam != null`, or `SELECT_ROLE` with a unit or as commander. **With no team it returns false.**
- `MenuManager.OnEscape()` (Silica.UI.Menus/MenuManager.cs:42-62):
  - `StrategyFactionsMenu.Back()` returns false (87-90);
  - the menu closes only if the prefab's serialized `CloseOnBack` is set (Menu.cs:45-48). The value is not in code.
- If it does not close, Escape opens the pause menu on top.
- If it does close, `StrategyHandler.SwitchTo` early-returns because `CurrentStrategyScreen` is still `Faction` (StrategyHandler.cs:46-49), so it may stay closed while the cursor stays visible (`GetShowCursor` is still true).
- **Verify at runtime.** Either way there is no usable camera.

**Which camera renders.**
- Always `Game.CurrentCamera`, which `Game.InitGameLevel` sets to `Camera.main` (106:Game.cs:457).
- `MoveCameraToNoTeamView()` (106:MP_Strategy.cs:1381-1390) uses `StrategySceneSetup.NoTeamCameraRef` (StrategySceneSetup.cs:17; legacy field `MP_Strategy.NoTeamCameraRef`, 113). It runs only from client-side `SelectTeam`, `SelectRole_None` and `StartGameLoop` (1234, 1281, 2850), **not** when the server sets a null team.
- So the view stays where it was. The server deletes the player's unit (`DestroyAllUnitsForPlayer` → `NetworkComponent.DeleteAllNetObjects`, NetworkComponent.cs:355-372), and the camera stays at the last unit position, held by the free-fly controller.
- No `DeathCam` is played on a team change. `DeathCam.Play` only runs for an eliminated team (1946) and at round end (2168).
- None of these exist: "OverviewCamera", "LevelCamera", "SpawnCamera".

**Server-only experience.** The player gets a blurred faction-select screen over a frozen view, cannot dismiss it, and cannot fly. Clicking a faction re-joins the game (if allowed). Usable as a "bench", not as a spectator camera.

**Vanilla precedent for "no faction menu".** `MP_StrategyMatchmaking` sets `ShowTeamSelectMenu = ETeamSelect.NONE` in `UpdateGameLoop` (106:MP_StrategyMatchmaking.cs:321) and again in `HandleUI` (435-448). That class does not exist in 0.9.47.

---

## 4. Server-side guards and interactions

### (a) `MP_Strategy.GetCanJoinTeam(Team team, Player player)` (106:MP_Strategy.cs:1151-1190)

```csharp
if (!MaximumPlayerDifferenceEnabled || IsOneTeamOnly) return true;   // MaxPlayerDifference > 0 (241)
if (!team) return false;
if (player && player.Team == team) return true;
// smallest player count among active, not-lost team setups (the player excluded from their current team),
// then: return countOfTarget - smallest < MaximumPlayerDifference;
```

- With the max-difference setting off, it returns `true` even for `team == null`.
- It is used by:
  - the vanilla `REQUEST_JOIN_TEAM` handler (581-609);
  - the client faction screen (restricted flags).
- **0.9.47 has no `GetCanJoinTeam`.** Its `REQUEST_JOIN_TEAM` (0947:589-610) accepts any team, including null.
- Si_EloMatchmaking and Si_BasicTeamBalance replace `REQUEST_JOIN_TEAM` handling with their own `ProcessNetRPC` prefixes (see c). **A `GetCanJoinTeam` prefix is not enough to keep a spectator team-less** on our servers.
- **Robust option:** a Harmony prefix on `MP_Strategy.ProcessNetRPC` with `[HarmonyPriority(Priority.First)]`. It runs before Elo (`Priority.High`) and BTB (default). For the join RPC from a spectator it reads the player from the packet without consuming it: save and restore `m_MemoryStream.Position` the way Si_ModUI does. It then sends `CLEAR_REQUEST` and returns false.
- Caveat: in Harmony 2.x a `false` from one prefix does **not** stop the other prefixes from running. They can only check `__runOriginal`, and Elo/BTB do not. So either:
  - also patch `Player.set_Team` (server): when the player is a spectator and the new value is not null and is not our own move, return false; plus a prefix on `NetworkLayer.SendPlayerSelectTeam` that rewrites `__1` to `__0.Team`, so the other mods' broadcast matches reality; or
  - accept "picking a faction = leaving spectator" and just clear the spectator flag in `OnPlayerChangedTeam` when the new team is not null.

  Simplest, and matches the plan's "Leave" path: the second option.
- RPC indices differ by branch:
  - 1.0.6 ERPCs (106:MP_Strategy.cs:15-37): `INIT_GAME=0, REQUEST_JOIN_TEAM=1, REQUEST_ROLE=2, CLEAR_REQUEST=3, … VOTE_TEAMVERSUS=11, REQUEST_COMMANDER_LOTTERY_ENTRY=12 …`.
  - 0.9.47 has `TEAM_LOCK=12` (0947:21-40), which shifts every later index by one.
  - Resolve by name (`Enum.Parse` on the nested `MP_Strategy.ERPCs`) the way Elo does.
  - Indices 0..11 are identical.

### (b) Commander lottery and role requests for a team-less player

- `REQUEST_ROLE` (106:611-668):
  - `NONE`: destroys units, removes the respawn tracker, clears the seat if the player is commander.
  - `COMMANDER`: needs `GetTeamSetup(player.Team)`, which is null for a null team, so it is answered with `RPC_ClearRequest`.
  - `UNIT`: only `RPC_SynchRole`.
- Lottery entry (782-791) trusts the client's team. `ResolveCommanderLottery` (1511-1532) seats without a team check. The server's `OnPlayerChangedTeam` removes the player from the **old** team's lottery only `if (!GameBegun && oldTeam != null)` (1877-1880). So a lottery entrant who becomes a spectator before round start is removed correctly by vanilla.
- Spawning: `GetUnitPrefabForPlayer(null team)` returns null (1889-1893). Spawn-point requests depend on `GetAvailableUnits(null, …)` (§2 risk 6).

### (c) Community mods and `Team == null`

| Mod | Behaviour toward team-less players | Action for Si_Spectator |
|---|---|---|
| **Si_AFKManager** 1.3.6 (`Si_AFKManager\Si_AFK.cs`) | `OnUpdate` (252-372): every 60 s while `GameOngoing`, each player with `Team == null` (except the server player) gets +1 minute in the private static `List<AFKCount> AFKTracker` (69); a team player is removed from it. At `AFK_MinutesBeforeKick` (default **7**) the player is kicked if `AFK_KickIfServerNotFull` (default false) is set, or else when `ServerAlmostFull()` (`Players.Count + 2 >= GetPlayersMax()`, 120-128). The admin `!afk` (173-250) kicks team-less AFK players at once. | **Spectators will be kicked when the server is nearly full.** Fix without touching that mod: a postfix on `AwayFromKeyboard.OnUpdate` (found by type name via `AccessTools.TypeByName("Si_AFKManager.AwayFromKeyboard")`) that removes spectators from `AFKTracker` through reflection. The count then never reaches the limit. |
| **Si_BasicTeamBalance** (user fork with 4-way, `Si_BasicTeamBalance\Si_BasicTeamBalance.cs`) | Prefix on `MP_Strategy.ProcessNetRPC` (688-704) → `ProcessJoinTeam` (474-686) handles `REQUEST_JOIN_TEAM` itself. For a joiner with `Team == null`: if the target is imbalanced and has no commander mid-game the join is denied, otherwise the player is forced to the lowest-population team (631-672). Counts use `Team.GetNumPlayers()` (team-less excluded). It never auto-assigns a player who did not ask. The `!team` admin command lives here (registered at 100): `Command_Team` (383-442) → `SwapTeam` (444-455). | Nothing needed, unless we want to block re-joins (see a). Note that Elo unpatches this prefix when its own join handling is on. |
| **Si_AutoTeamsSelect** (`Si_AutoTeamsSelect.cs:155-230`) | Only sets the versus mode after `MP_Strategy.Restart` (Restart postfix + `GameMode.Update` postfix). Never touches player teams. | Nothing. |
| **Si_CommManagement** (`Si_CommManagement\*.cs`) | `CommanderApplications.commanderApplicants` (public static `List<Player>[]`, indexed by `Team.Index`, 39-50). `!commander` requires a team (81). `HoldCommanderLottery` runs at `MissionState STARTED` (postfix 155-170, body 296-380) and dereferences `CommanderPlayer.Team.Index` (356), calls `SpawnUnitForPlayer(applicant, applicant.Team)` (344, 380) and `PromoteToCommander` (`commander.Team.TeamShortName`, CommanderPrimitives.cs:102-108). An applicant who became a spectator before round start causes an **NRE that aborts the lottery** for the remaining teams (the postfix catches and logs it), or a spawn with a null team. Departure patches null-check (CommanderDepartures.cs:42, 98). | **When entering spectator, remove the player from every `commanderApplicants[i]`** (public static field; Elo already reads and clears it by reflection, Si_EloMatchmaking `GameApi.cs` ~170-193). |
| **Si_EloMatchmaking** (`Si_EloMatchmaking\Si_EloMatchmaking\`) | `TeamBalance.Patch_JoinTeam` (TeamBalance.cs:302-406, `Priority.High`) takes over `REQUEST_JOIN_TEAM` when `BalanceTakeOverJoinHandling` is on, and unpatches BTB. A null target gets `ClearRequest` (334). Team-less players are skipped in `TeamStates` (~259-266), presence (EloMod.cs:473-478) and reserve swaps (472-476). Round-start seating (EloMod.cs ~240-300) moves the applicants it picks via `GameApi.MovePlayer` and seats them, so a spectator in the applicant pool (CommMgmt list or lottery) would be moved and seated. `!reserve <team>` (TeamBalance.cs:515-562) lets a team-less player queue; `MoveReserved` (447) then moves them. | Clear the applicant lists (as for CommMgmt). Decide whether `!reserve` may take a spectator out; if so, clear the spectator flag on any non-null team change. |

### (d) How team moves are done elsewhere

All use `Team = …; SendPlayerSelectTeam`:
- BTB `SwapTeam` (Si_BasicTeamBalance.cs:444-455): `DestroyAllUnitsForPlayer(player); player.Team = team; NetworkLayer.SendPlayerSelectTeam(player, team);`, then a respawn.
- Elo `GameApi.MovePlayer` (GameApi.cs:197-210): `DestroyAllUnitsForPlayer` + the same pair, no respawn.
- Elo `TeamBalance.Place` (390-393): the pair only.
- The Admin Mod has no `!team`; it is in BTB.

For spectator entry use the vanilla eliminated-team sequence (§2) after demoting a commander:

```csharp
if (player.IsCommander) { SetCommander(team, null); RPC_SynchCommander(team); }   // reflection, see §2 risk 1
NetworkLayer.SendPlayerSelectUnit(player, null);
player.ControlledUnit = null;
player.Team = null;                       // server OnPlayerChangedTeam destroys units, respawn tracker, lottery
NetworkLayer.SendPlayerSelectTeam(player, null);
```

Also remove the player from the CommMgmt and Elo applicant lists.

### (e) A whole round without a team

**Vanilla server paths are fine:**
- round-end and elimination code only iterates players of a given team (`CheckPlayersOnEliminatedTeam`);
- `Player.GetNumPlayersForTeam(team)` (Player.cs:724-735) simply compares;
- `Team.GetNumPlayers` and `GetAnyPlayersOnAnyTeam` (359-369) ignore spectators.

**Side effects.**
- With only spectators online, `MissionState INIT` keeps resetting the timer, so the round never starts (2469-2475).
- `GameMode`'s "no players for 300 s" check (106:GameMode.cs:1153-1166) counts every `Player.Players`, so spectators keep a round alive.
- At every restart `ClearGame` sets every team to null and `Restart()` sets `SELECT_TEAM` (2788-2799).
  - The server mod must remember spectators **by Steam ID**: Team == null is also every lobby player.
  - The client mod must re-suppress the faction menu each round.

**Logging and stats mods checked:**
- `Si_Logging` (team-change log skipped for null; `BuildLogs.cs:191` null check; `Damage.cs` null checks);
- `Si_ReplayLogging` (`GetTeamIndex(null)` safe);
- Elo / Leaderboard skip team-less players;
- Si_AntiCheat only inspects damage packets.

---

## 5. Free-fly camera (`FreeFlyCameraController`, 106:FreeFlyCameraController.cs, identical in 0947)

**Members.**
- `public Camera Camera` (12), `MinSpeed 0.05`, `MaxSpeed 3000`, `public bool EnableMovement = true` (21), `DynamicDOFVolume`.
- `public static Instance { get; private set; }` (50), set in `Awake()`.
- `public bool IsActive { get; private set; }` (54).
- `FOV`.
- `public static Unit GetReferenceUnit()`.
- `public void ResetCamera()` (67-88).
- `public void OrderedLateUpdate(float)` (90-105).
- Private: `Activate()` (107-111: `IsActive = true; UIManager.RequestSwitchUIMode(UIMode.None);`).
- Private: `Deactivate()` (113-119: `IsActive = false; TargetPos = transform.position; TargetRot = transform.rotation; ResetCamera();`).
- Private: `UpdateCamera()` (121-361).
- Private fields: `TargetPos`, `TargetRot` (44-46), `ReferenceTransform`, `ReferenceUnit`, `ReferenceLocalPos`, `ReferenceLocalRot`, `CurrentSpeed`, `CurrentRoll`, `CurrentFOV`, `TargetFOV`.

**Who sets `IsActive`.** Only `Activate` and `Deactivate`, both called from `OrderedLateUpdate`. That is registered in `OnEnable` with `OrderedUpdateManager.RegisterLateUpdate(OrderedLateUpdate, 0)` (365):

```csharp
if (!EnableMovement || CinematicCamera.IsPlaying || !Game.GetCameraEnabled() || StrategyMode.InStrategyMode || (bool)Unit.CurrentUnit)
{ Deactivate(); return; }
if (!IsActive) Activate();
if (IsActive) UpdateCamera();
```

`grep FreeFlyCamera` across the decompile finds only reads:
- `Game.FreeFlyCamera` (Game.cs:107), FOV (123, 706);
- `MP_StrategyMatchmaking.cs:314-318` (`EnableMovement = false`);
- `MP_TowerDefense.cs:2912` (reads `isActiveAndEnabled` next to `Game.CheatsEnabled`);
- `UnitRecorder.cs:116-118`, `CinematicStateExtractor.cs:37`;
- the AI debug helpers via `GetReferenceUnit()`.

**There is no "freecam" console command.** `CCopyCameraView` (key `Camera`, DebugTools/DebugConsole.cs:2201-2215) only copies the camera pose to the clipboard (`CameraOperations.CopyCameraToClipboard` / `PasteCameraFromClipboard`, CameraOperations.cs:6-35; the paste writes `Game.CurrentCamera.transform`).

**Cheats are not required.** Activation is automatic whenever there is no unit, no StrategyMode, no cinematic, `GetCameraEnabled()` is true (`GameMode.GetCameraEnabled()` defaults to true, GameMode.cs:191) and `EnableMovement` is true. Input is read only when `Game.GetControlsEnabled()` is true: cursor hidden and locked, no text input (Game.cs:663-674). That is why the vanilla team-less player cannot fly: the faction menu keeps the cursor visible.

**What a client mod must do:**
- **(a) Turn it on** for a player with no unit: nothing to call. Make sure:
  - `Unit.CurrentUnit == null` and not in StrategyMode;
  - `FreeFlyCameraController.Instance.EnableMovement == true`;
  - the cursor is locked: suppress the faction menu (`ShowTeamSelectMenu = NONE`, §9a) so `GetShowCursor()` is false and `CursorManager` hides the cursor.
- **(b) Turn it off:** set `Instance.EnableMovement = false`. Every frame it then runs `Deactivate()`, which copies the transform into `TargetPos/TargetRot` and `ResetCamera()`s (FOV, DOF, speed) without moving the camera. Our own code then drives the camera. Set it back to `true` to return to free flight. Leave `IsActive` alone: the next `OrderedLateUpdate` overwrites it.
- **(c) Set position and rotation.**
  - Write `transform.position/rotation` **and** `TargetPos/TargetRot` (interop properties).
  - Otherwise `UpdateCamera` pulls back to `TargetPos`. With the setting `m_FFCameraLerp != 0` it slerps toward it (123-132, 350-359).
  - Simpler alternative: set the transform, then call `Deactivate()` (public in interop). It copies the transform into `Target*`, and the controller re-activates on the next frame.
  - Built-in "stick to object": set `ReferenceTransform`, `ReferenceUnit`, `ReferenceLocalPos = ReferenceTransform.InverseTransformPoint(camPos)` and `ReferenceLocalRot = Inverse(ReferenceTransform.rotation) * camRot`, the way the right-mouse handler does (174-192). The camera then rides along with that transform at a fixed local offset, mouse-look stays relative, and the AI debug views key on `ReferenceUnit`. That is a cheap chase cam.
  - `Player.CurrentPlayer.LookAtPosition` (Player.cs:54) makes the free-fly turn toward a point and clears itself when reached (303-313). That suits an auto director.
- **(d) Input actions** (InputMap `Freecam` = 0x40, GameInput.cs:19-31), plus the `Unit`-map actions FreeFly reads. Defaults come from the input asset embedded as JSON in `Silica/SilicaInputActions.cs:2775` (`InputActionAsset.FromJson`). Users can rebind them.

| Map | Action | Default KBM | Default gamepad | Use in FreeFly |
|---|---|---|---|---|
| Unit | Movement | W A S D | left stick | move |
| Freecam | Ascend / Descend | Space / Left Ctrl | RT / LT | up / down |
| Freecam | Camera Roll | Q (−) / E (+) | – | roll |
| Unit | Zoom | mouse wheel | – | speed (`CurrentSpeed` 0..1 → 0.05..3000 m/s); with Enable Zoom held it changes FOV |
| Freecam | Enable Zoom | middle mouse (hold) | right stick press | wheel → FOV |
| Freecam | Reset Zoom | middle mouse (performed) | right stick press | FOV = 60 |
| Unit | Speed Boost | Left Shift | left stick press | ×5 |
| Freecam | Camera Stick To Object | right mouse | X / West | attach to the rigidbody under the crosshair, or detach |
| Freecam | Camera Focus | left mouse | Y / North | DOF focus (debug: damage collider highlight) |

  Map selection: `GameInput.GetPreferredInput()` (Silica/GameInput.cs:296-330) returns `Unit | Freecam` when there is no unit, no StrategyMode and the cursor is hidden. `UpdateInput` always adds `Global` (345). In the Global map: `Map` = M, `Large Minimap` = Tab, `Chat` = Enter, `Menu` = Escape, `Select Team` has no default key, `Open Console` = `` ` ``. `GameMode.OverrideInput(map)` (GameMode.cs:1058) is a virtual hook.
- **`Game.CurrentCamera`:** nothing to assign. `Game.InitGameLevel` sets it to `Camera.main` (Game.cs:457). `Game.UpdateCameraFOV` uses `FreeFlyCamera.FOV` while it is active (706).
- **Not proven by code:** that `FreeFlyCameraController.Instance.transform` **is** `Game.CurrentCamera.transform`. The controller moves `base.transform`; `UnitRecorder` uses its `Camera` field. It could be a parent rig. Verify at runtime: `Instance.transform == Game.CurrentCamera.transform` or `Instance.Camera == Camera.main`.
- **Interplay with Si_ModUI.** While its panel is open, the client patches `Game.GetCameraEnabled` to return false (Si_ModUI\Client\Ui.cs:200), so FreeFly deactivates and freezes. Our follow cameras should pause in the same way (check `Game.GetCameraEnabled()`).

---

## 6. Following another player's view (client only)

**Ownership and current unit.**
- `Unit.CurrentUnit` (106:Unit.cs:137-154) is static. Its setter only fires `Unit.OnCurrentUnitChanged` and sets `LastControlledUnit`. The only writer is the `Player.ControlledUnit` setter, when `this == CurrentPlayer` (Player.cs:160-163).
- `Player.ControlledUnit` setter (Player.cs:116-190):
  - old unit: `NetworkComponent.OwnerPlayer = null`, `OnUnselect(this)`, `ControlledBy = null`, vision mode, `OnPlayerUnselectUnit`;
  - new unit: **`NetworkComponent.OwnerPlayer = this`**, `ControlledBy = this`, `OnSelect(this)`, vision mode, `OnPlayerSelectUnit`;
  - then `Unit.CurrentUnit = value` for the local player, `OnPlayerChangedUnit`, AIGroup hooks, `CleanupGroups`.
- `Player.SetUnit(player, unit)` (Player.cs:658-668) = `SendPlayerSelectUnit` + `ControlledUnit` on the server.
- `Unit.OnSelect/OnUnselect(Player)` (Unit.cs:1464-1480) only call `Camera.OnSelect()/OnUnselect()` when `byPlayer == Player.CurrentPlayer` (`OnSelect` resets the pitch).

**Camera classes.**
- `UnitCameraController : MonoBehaviour` (106:UnitCameraController.cs, 77 lines):
  - sits on a child transform of the unit; `OwnerUnit` is set in `Awake`;
  - **`base.transform` is the camera anchor**;
  - `CameraUpdate(localVelocity)` (30-44) delegates to `InCompartment.OwnerUnit.Camera` when the unit is in a compartment;
  - `OnCameraUpdate` (46-71) writes `Game.CurrentCamera.transform.position = transform.position` and a rotation from `m_Camera_Yaw/Pitch`, which come from the local mouse (`Game.GetViewInput()`).
- Each unit's controller is `Unit.Camera` (public field, "[AUTOMATIC]", Unit.cs:79-81).
- `VehicleCameraController : UnitCameraController` (106:VehicleCameraController.cs):
  - public `DistanceMin = 2`, `DistanceMax = 10`, `SpeedToDistanceMult`, `LocalTransform`, `LocalOffset`, `WorldOffset`, `UpwardLocalOffset`, `CollisionRadius = 0.5`;
  - third-person orbit: pivot = `(LocalTransform ?? transform).TransformVector(LocalOffset) + WorldOffset + transform.position`; the camera sits `DistanceMax` back along the view rotation, shortened by a sphere-cast (60-155).
- There is no `FirstPersonCamera` class.
- **Soldier** drives its own camera in `Soldier.UpdateCamera(float)` (106:Soldier.cs:2035-2133):
  - anchor = **`Soldier.CameraReference`** (public `GameObject`, Soldier.cs:136);
  - first person: `CameraReference.transform.TransformPoint(HumanHandsAnimator.WeaponViewOffset)`;
  - third person: `CameraReference.position + camera.TransformVector(0.5, -0.1, -1.5)`;
  - fallback: `transform.position + up * m_CameraTargetHeight`;
  - dead: `DeathBone` (public, 132);
  - rotation: `Euler(viewAngles.y + bob, viewAngles.x + bob, roll)`;
  - in a compartment it hands over to `InCompartment.OwnerUnit.Camera.CameraUpdate(...)` (2061-2066).
- None of these names exist: CameraTransform, CameraAnchor, EyeTransform, HeadBone.

**Vehicles and compartments.**
- `Unit.InCompartment`, `DriverCompartment`, `Driver` (Unit.cs:342-344, 528-550); `UnitCompartment.DriverUnit`, `BoardedUnits`, `OwnerUnit`, `IsDriver` (UnitCompartment.cs:17-102).
- When a soldier boards a **driver** seat, the server switches that player's `ControlledUnit` to the vehicle and sends `PlayerSelectUnit` (UnitCompartment.cs:416-424). On exit it switches back (438-446).
- Gunners and passengers keep the soldier as `ControlledUnit` and use the compartment owner's camera.
- So for the followed player: if `ControlledUnit` is a Vehicle, use `vehicle.Camera`. If it is a Soldier with `InCompartment?.OwnerUnit?.Camera`, use that. Otherwise use the soldier's `CameraReference`.

**Networked aim and view.**
- Soldier: `m_ViewAngles` (private, Soldier.cs:273) and `m_FreeLookAngles` go out in every `ObjectUpdate` from the controlling client (`OnSendNetUpdate` ~787-804; read for non-owners in `OnNetUpdate` 807-842; init 845-866).
  - 8-bit quantized (`WriteFloatAsByte`): about 1.4° yaw, about 0.7° pitch.
  - `Soldier.GetViewDirection()` (1973-1976) = view + free-look; `GetHeldWeaponAimDirection()` (1978-1981).
  - The base `Unit.GetViewDirection()` = `transform.forward` (Unit.cs:1415-1418).
- `UnitAimAt` (106:UnitAimAt.cs):
  - `public Vector3 AimAtPosition`, `AimAtFreeLookPosition` (26-29);
  - sent by the controlling client (`GetShouldSendNetUpdate` 77-88, `OnSendNetUpdate` 90-102) as full-precision `Vector3`, applied on every other client (104-126);
  - the owner computes it from **its camera ray** (`OrderedUpdate` 282-312);
  - so on a spectator client, `unit.UnitAimAt.AimAtPosition` is the point under the followed player's crosshair. That is the best look target.
  - `Unit.UnitAimAt` is a property (Unit.cs:229).

**How the local unit camera is driven each frame.**
- `OrderedUpdateManager` (106:OrderedUpdateManager.cs) stores late updates at `priority*1000 - subPriority` (160-164) in an ascending list and **runs from the highest value to the lowest** (`Run` 80-120). After the list it calls `Game.UpdateCameraLerp` (223-233).
- Order of the relevant entries:
  1. `Soldier.OrderedLateUpdateAll` (100 → 100000; Soldier.cs:747-750) → `OnOrderedLateUpdate` (2990-3036) → `UpdateCamera(dt)`, only if `GetLocallyControlled()`.
  2. `FreeFlyCameraController.OrderedLateUpdate` (0).
  3. `CullingComponent.UpdatePositionLate` (-1000), `Crosshair` (-1000000), `Soldier.OrderedLateUpdateFinish` (-1000001).
- Vehicles: `Vehicle.UpdateInput(dt, v)` (106:Vehicle.cs:1267-1288, only if `GetLocallyControlled()`) → `HandleCamera` (1308-1314) → `Camera.CameraUpdate(localVelocity)`.
- `GetLocallyControlled()` = `ControlledBy == Player.CurrentPlayer` (Unit.cs:730-737).

### Recommendation: (A) soft follow

Place our own camera every frame:
- **Hook:** a Harmony **postfix on `FreeFlyCameraController.OrderedLateUpdate`**, with `EnableMovement = false` while following so FreeFly does not fight. Alternatively, register an Il2Cpp `Action<float>` with `OrderedUpdateManager.RegisterLateUpdate(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<float>>(f), -1)`. Either way it runs after soldier animation/camera (100) and before culling and crosshair. MelonLoader's `OnLateUpdate` has no defined order relative to `OrderedUpdateManager.LateUpdate`.
- **Soldier position:**
  - first person = `CameraReference.transform.TransformPoint(HumanHandsAnimator.WeaponViewOffset)`;
  - over-the-shoulder = `CameraReference.position + rot * (0.5, -0.1, -1.5)`, like the game's own 3rd person.
  - A remote soldier is rendered with its third-person body, so a first-person camera sits inside the head mesh. Raise the near clip or prefer the shoulder view.
  - Verify that a remote soldier's `CameraReference` follows the head animation; animation activation is distance-based (`CharacterAnimator.CheckActivationDistance`, Soldier.cs:2888).
- **Soldier rotation:** `Quaternion.LookRotation(AimAtPosition - pos)`, smoothed, or `Quaternion.LookRotation(soldier.GetViewDirection())`.
- **Vehicles:** reproduce the `VehicleCameraController` orbit from its public fields. Rotation:
  - drivers: from the vehicle's `UnitAimAt`; the vehicle is their `ControlledUnit`, so it is locally controlled on their client and gets sent;
  - gunners: from the soldier's `UnitAimAt`. Verify it keeps updating in a compartment.
- **Nothing is sent to the server** and no ownership changes.

### Why not (B) hard attach (reuse the unit's own camera)

- `Player.CurrentPlayer.ControlledUnit = unit` locally sets `unit.NetworkComponent.OwnerPlayer = spectator`. Then:
  - `NetworkComponent.GetLocallyOwned()` (NetworkComponent.cs:470-484) and `GetCanSendNetUpdates()` (500-514) become true;
  - the spectator client **starts sending ObjectUpdates for that unit** with its own input;
  - the server relays and applies them without an owner check (§2 risk 6);
  - result: the real player's unit is hijacked or jitters for everyone.
- It also overwrites `ControlledBy` locally (the real player loses their name tag), runs `Soldier.UpdateInputControls` (local fire input), overwrites and sends `UnitAimAt`, and switches the HUD.
- Si_AntiCheat's sender check would flag damage packets that name an instigator the sender does not own.
- Setting **only `Unit.CurrentUnit`** (static, no ownership) avoids the network writes, but has other problems:
  - FreeFly deactivates;
  - HUD widgets bind to that unit (`DamageUIHandler`, `UnitInfo`, `SpreadIndicator`, …);
  - the input map becomes `Unit`;
  - `UsePoint.UpdateUse` (UsePoint.cs:205-240) lets the spectator press Use on behalf of that unit, which calls `CurrentUsePoint.Use(currentUnit, …)`. Verify whether that sends a request.
  - The game's camera code would still not run, because it needs `GetLocallyControlled()`.

  Not recommended.

---

## 7. Commander (RTS) view

**Camera.**
- The rig is `StrategyMode.Instance.transform` (target pose). `StrategyMode.Update()` (106:StrategyMode.cs:2276-2368) moves the rig from input and smooth-damps `Game.CurrentCamera` toward it (2363-2369). Unity `Update`, not the ordered late update.
- Fields:
  - `CameraMinTerrainHeight = 4`, `CameraMaxTerrainHeight = 300`, `CameraHeightToPitchPower = 0.4` (127-133);
  - `CameraHeightPct` (0..1, default 0.5, 434), changed by Commander-map "Zoom Camera" (2340-2349);
  - `CameraPitch = Lerp(10, 75, CameraHeightPct ^ 0.4)` (410);
  - `TerrainHeightPct = 1 - Clamp01((CameraHeightPct - 0.5) / 0.5)` (412);
  - `CameraTerrainHeight`, `CameraBounds`.
- Placement formula, `MoveCameraToPosition` (779-784):

  ```csharp
  float d = Mathf.Lerp(CameraMaxTerrainHeight, Mathf.Lerp(CameraMinTerrainHeight, CameraMaxTerrainHeight, CameraHeightPct), TerrainHeightPct)
            / Mathf.Sin(CameraPitch * Mathf.Deg2Rad);
  transform.position = transform.rotation * Vector3.back * d + new Vector3(p.x, transform.position.y, p.z);
  ```

- `PlayerNameColorFriendly/Enemy` (150-153) are unused (§9d).

**`Player.LookAtPosition`.**
- A plain field (Player.cs:54). It is written only locally (MissionEvent.cs:1408, Sandbox Flow `LookAt.cs:41`, and reset to zero by its consumers).
- It is consumed by StrategyMode (2306-2309), UnitCameraController (50-59), VehicleCameraController and FreeFly.
- **Not networked.** StrategyMode and MP_Strategy have no camera RPCs, so the server and other clients never know where a commander is looking. The plan's phase-2 `K`/`C` relay would be the only way, and it needs our client mod on the commander's side.

**Can a non-commander enter StrategyMode as an observer?**
- `StrategyMode.ToggleStrategyMode(bool enable, bool calledFromDirectControl = false)` is public static (707-760). It:
  - instantiates the prefab (`IStrategyGameMode.GetStrategyModePrefab`);
  - if a unit is controlled, sends `SendPlayerSelectUnit(null)`;
  - calls `UpdateVisibilityAll`, `MoveCameraToPosition(camPos)` and `ShowStrategyGUI`.
- Vanilla opens it only:
  - from `SetCommander` for the local commander (MP_Strategy.cs:1590-1597);
  - via the cheat key (GameManager.cs:341-343: needs `Game.CheatsEnabled` and `GetCanFreelyOpenStrategyMode()`, which is false for a null team, MP_Strategy.cs:1682-1697).
- `StrategyMode.Team` is **always `Player.CurrentPlayer.Team`** (398-408). You cannot choose a team.
- With a null team:
  - `ShowStrategyGUI` shows no HUD (`if ((bool)Team)`, 1440-1453);
  - selection throws (3688);
  - no fog (CurrentStrategyTeam null).
- Faking `Player.CurrentPlayer.Team = X` locally (no packet) would make the whole UI act as team X, and **every click becomes an order for team X**:
  - `StrategyNetworkManager.RequestMoveAttackOrder / RequestSellStructure / RequestToggleRepair / RequestCommandOrder` (106:Silica/StrategyNetworkManager.cs:122-260);
  - structure, production and construction RPCs (for example ConstructionSite.cs:1127-1150);
  - `RequestDirectControl` → `SendPlayerSelectUnit` (StrategyMode.cs:788-830; the server only checks `ControlledBy == null`).
- Server validation:
  - 1.0.6 drops objects in `DropObjectsSenderMayNotCommand` / `GetSenderMayCommand` (StrategyNetworkManager.cs:91-114). But `RequestSender.GetMayCommandTeam` **returns true for every game mode except MP_Sandbox and MP_Scenario** (106:Baltarus.Scripts.Sandbox.Players/RequestSender.cs:17-23).
  - 0.9.47 has no check at all.
  - **The server would execute a spectator's orders.**

**Verdict.**
- **No-go** for "spectator uses vanilla StrategyMode for team X".
- **Go** for our own top-down preset: the same pitch/height formula on our camera, team-X visibility through the §1 patches, the minimap via `GetCurrentFogTeam`, and our own IMGUI overlays.
- Server-side insurance either way: drop `StrategyNetworkManager` RPCs, `ObjectRPC`, `PlayerSelectUnit` and `PlayerSelectTeam` from spectators (§2 risk 6).

---

## 8. Transport details for a second protocol (byte 125)

**Byte 125 is free in both branches.**
- 1.0.6 enum: 0..43; 0.9.47 enum: 0..42 (§0).
- `ProcessMessage`'s switch has **no `default` case** (106:NetworkLayer.cs:403-1098), so an unknown type is silently ignored. A client mod sending 125 to a server without the server mod is harmless, and the other way round too.
- Do the same self-check as Si_ModUI: `networkEnabled = !Enum.GetValues(typeof(ENetworkPacketType)).Cast<object>().Any(v => Convert.ToInt32(v) == 125)` (Si_ModUI\Client\ClientMod.cs:97).
- Other mods: Si_ModUI uses 126 with marker `"SiModUI/2"` (Si_ModUI\Shared\Protocol.cs:25-26). Si_PacketChunker writes only vanilla types 27/37/38. Si_AntiCheat only observes `ProcessMessage` (prefix and postfix, no consumption). **No collision.**

**Server → client.** Si_ModUI\Server\ServerMod.cs:283-285, inside a token-bucket `Flush`:

```csharp
var writer = GameByteStreamWriter.GetGameByteStreamWriter(0, "Si_ModUI::Send", true);   // (uint netID, string funcName, bool writeDebug = true), GameByteStreamWriter.cs:19, both branches
writer.WriteByte(Protocol.PacketType); writer.WriteString(Protocol.Marker); writer.WriteString(item.Value);
NetworkLayer.SendPacketToPlayer(writer, ENetworkPacketSend.Reliable, player);
```

- `SendPacketToPlayer` (106:NetworkLayer.cs:1323-1342, 0947:1124) uses `player.PlayerID` / `player.PlayerChannel` internally. There is no lane or channel parameter.

**Client → server.** Si_ModUI\Client\ClientMod.cs:227-232: the same writer, then `NetworkLayer.SendPacket(writer, ENetworkPacketSend.Reliable)`. On a client that goes to `SendClientPacket(NetworkGameClient.GetServerID(), …)` (NetworkLayer.cs:1314-1318).

**Receive.** `[HarmonyPatch(typeof(NetworkLayer), "ProcessMessage")]` on the private static `ProcessMessage(GameByteStreamReader packetReader, uint packetLength, NetworkID remoteID, bool isServer)` (106:NetworkLayer.cs:386, 0947:212):

```csharp
private static bool Prefix(GameByteStreamReader __0, uint __1, NetworkID __2, bool __3)
```

- `__0`: the reader, positioned at the type byte. `__1`: packet length. `__2`: the **sender**, the authoritative identity on the server. `__3`: isServer.
- Server mod (ServerMod.cs:153-202):
  - gets the `MemoryStream` with `AccessTools.FieldRefAccess<GameByteStreamReader, MemoryStream>("m_MemoryStream")` (33);
  - returns `true` unless the first byte matches **and** the marker matches;
  - the marker check reads raw bytes: `Protocol.ReadMarker(stream.ReadByte, (byte)EGameByteStreamType.String)`, i.e. string type tag + 1-byte length + ASCII;
  - checks `__1` against `MaxPacket = 2100` (the native buffer is 2400);
  - returns `false` only for its own packets;
  - restores `stream.Position` in `finally`.
- Client mod (ClientMod.cs:118-142): `__0.m_MemoryStream` (interop property), the same checks, and in addition requires `NetworkGameClient.GetInServer()` and `__2 == NetworkGameClient.GetServerID()`.
- The streams are **typed**: every `Write*` prefixes an `EGameByteStreamType` tag. Read with the matching `Read*` in the same order.
- Keep a body at or below about 1800 chars (Si_ModUI `MaxBody`), or chunk it.

**0.9.47 differences relevant to the transport and the mod.**
- `ProcessMessage` at 0947:212, same signature. `SendPacket` 1090, `SendPacketToPlayer` 1124. Writer factory identical.
- No `TeamInitTargets`. `SendTeamsInit()` broadcasts.
- `MP_Strategy.ERPCs` has `TEAM_LOCK=12` (§4a). `ShowTeamSelectMenu` and `SetCommander` are private.
- No `GetCanJoinTeam`; no `MP_StrategyMatchmaking`; `StrategyNetworkManager` does no sender check.
- `GameEvents` lacks `OnCleared`, `OnPlayerNameChanged`, `OnTargetRepaired`.
- Si_ModUI's server assembly builds against the MelonLoader **net35** `MelonLoader.dll` / `0Harmony.dll` (`_silica_refs\net35`; CS1705 with the net6 ones). Other server mods (Si_Spawn) use the net6 MelonLoader refs from `include\<branch>`.

---

## 9. Client UI hooks

### (a) Suppressing the faction menu

- `MP_Strategy.ShowTeamSelectMenu` is reachable: in the interop it is a public property of type `Il2Cpp.GameModeExt.ETeamSelect` (Il2CppSilicaCore.dll, `MP_Strategy`, about line 657 of the ilspy output).
- **Recommended:** a Harmony prefix on `MP_Strategy.HandleUI()` (106:1740). While spectating, set `__instance.ShowTeamSelectMenu = GameModeExt.ETeamSelect.NONE`. This mirrors `MP_StrategyMatchmaking.HandleUI` (MP_StrategyMatchmaking.cs:435-448).
  - It runs after `UpdateGameLoop` in the same frame (GameMode.cs:1177-1185), so the forced `SELECT_TEAM` at 2534 is overridden before any UI decision.
  - With `NONE`, `HandleUI` reaches `else if (GameBegun && Ceil(Timer) <= 0) StrategyHandler.SwitchTo(StrategyScreenType.None)` (1788-1791). That closes all strategy screens (`CloseAll`, StrategyHandler.cs:114-122) and sets `Blur01 = 0`.
  - During the pre-round countdown it does not switch to None. Call `StrategyHandler.SwitchTo(StrategyScreenType.None)` yourself, unless `MissionState == INIT && TeamsVersus == NONE`: that is the battle-layout vote, so leave it.
  - Result: `GetShowCursor()` is false, so the cursor is locked and free-fly controls work.
  - `TeamSelectActionPerformed` (2908) and `RoleSelectActionPerformed` set the menu again. Either swallow them or use them as the "leave spectator" gesture.
- **MenuManager** (106:Silica.UI.Menus/MenuManager.cs; interop `Il2CppSilica.UI.Menus.MenuManager`):
  - `static Instance`;
  - static events `OnMenuOpened` / `OnMenuClosed (Action<Menu>)`;
  - `OnEscape()`;
  - `OpenMenu<T>(out T)` / `OpenMenu(Menu)`, `CloseMenu<T>()` / `CloseMenu(Menu)`;
  - `GetCurrent()` (215);
  - `IsMenuOpened<T>(bool)`, `static IsMenuOpen<T>()`, `IsAnyMenuOpened(bool)` (237), `IsAnyActiveMenuCoveringWholeScreen()`.
  - Prefer the non-generic overloads from Il2Cpp; the generic ones exist through `MethodInfoStoreGeneric_*`.
- **StrategyHandler** (interop `Il2CppSilica.UI.StrategyHandler`): `SwitchTo(StrategyScreenType, GameModeExt = null)`, `Victory()`, `Defeat()`, `ShowMessage`. `StrategyScreenType { None, LayoutSelection, Faction, Loadout, Respawn, NextMap, End }`.
- **What Si_ModUI's client does** (Si_ModUI\Client\Ui.cs):
  - `NativeMenu() => Game.IsPauseMenuActive || Game.InMainMenu` (114). Only those block its panel.
  - `OverlayName()` = `MenuManager.Instance.GetCurrent().GetIl2CppType().Name` (115-119).
  - `BlockNativeClicks(bool)` (120-141) disables `EventSystem.current` and keeps the instance to re-enable it later. A disabled EventSystem is no longer `current`.
  - Patches (192-201):
    - `MenuManager.IsAnyMenuOpened` postfix → true while the panel blocks;
    - `GameInput.GetPreferredInput` prefix → `InputMap.UI`;
    - `Game.GetControlsEnabled` → false;
    - `Game.GetCameraEnabled` → false;
    - `GameManager.OnEscape` → closes the panel.
  - Cursor handling: `OnLateUpdate` forces `Cursor.lockState = None; visible = true` while open. `ClosePanel` restores `GameMode.CurrentGameMode.GetShowCursor()` (158-175).

### (b) Chat input open

- `UIManager.TextInputActive || DebugConsole.IsOpen` (Si_ModUI\Client\Ui.cs:109; `using Il2CppDebugTools`).
- `Game.GetControlsEnabled()` is already false while it is true.

### (c) HUD with no unit and no team

- `StrategyUI` appears only in StrategyMode with a team.
- `UIManager.RequestSwitchUIMode(UIMode.None)` runs on FreeFly `Activate()`.
- **Large map (M):** `LargeMapOpener.MapActionPerfomed` (Silica.UI.Menus/LargeMapOpener.cs) toggles `LargeMap` when there is no text input or console, and when `!GameMode.GetPickSpawn()` (not on the respawn screen). **There is no team check.** Whether the `LargeMapOpener` component is active in the no-unit HUD: verify.
- **Minimap:** Tab = Large Minimap (MinimapToggle.cs:22).
- **Map fog:**
  - `RTSMap` uses `FogOfWar.Instance.CurrentTeamFogTexture` (Silica.UI/RTSMap.cs:126);
  - `Minimap` gets `SetMapFogOfWarTexture(...)` from `ShowFog` (FogOfWar.cs:384-388);
  - both come from `GetTeamFogTexture(GetCurrentFogTeam())`. With a null team that is `null`, i.e. **no fog**, everything shown;
  - the resource overlay only draws `if ((bool)Team.CurrentTeam)` (RTSMap.cs:371-376).
  - Postfix `GetCurrentFogTeam` for team X's map fog (§1).
- **Console `Fog`** (`CDisableFogOfWar`) works for anyone: `Game.CheatsEnabledLocal => true` (Game.cs:141).

### (d) Player names over units (to match the look)

- `StrategyMode.PlayerNameColorFriendly/Enemy` (StrategyMode.cs:150-153) have **no consumers** in the decompile.
- Vanilla uses `Silica.UI.RTSPlayerNameManager` (RTSPlayerNameManager.cs:56-87):
  - in `LateUpdate`, for every other player whose `ControlledUnit` is alive, `IsVisible` and on screen (`GameMath.IsWorldPositionOnScreen`), it takes a pooled `RTSPlayerName` and calls `Init(player, player.Team.TeamColor)`;
  - skipped while in DirectControl.
- `Silica.UI.RTSPlayerName` (RTSPlayerName.cs):
  - `TextMeshProUGUI NameText`, `color = team colour`, `text = PlayerName`;
  - screen position = `Game.CurrentCamera.WorldToScreenPoint(unit.WorldPhysicalCenter + (0, unit.PhysicalBounds.extents.y, 0))` (30-39).
  - Font asset and size come from the prefab, not from code. To match them, read `NameText.font` / `fontSize` from a live `RTSPlayerName` at runtime.
- In IMGUI: `GUI.Label` at that anchor (remember IMGUI's y is flipped: `Screen.height - p.y`), colour `Team.TeamColor`, with a 1-px dark shadow.
- Where the manager lives (probably only in the strategy HUD): verify.

### (e) IMGUI / client build gotchas (copied)

From `memory\project_si_modui.md` ("Client build gotcha", status 2026-10-20 and later):
- Avoid lambdas returning `string[]` or `ToDictionary` (CS0656 NullableAttribute vs Il2Cppmscorlib). `MatchEvaluator` lambdas also hit CS0656.
- Do not use the `GUIStyle` copy constructor.
- Qualify `UnityEngine.HideFlags`.
- `GameInput` needs `using Il2CppSilica`.
- Server side (Newtonsoft): `JToken.ToString(Formatting)` is missing; use `JsonConvert.SerializeObject`.

From Si_ModUI README / Ui.cs practice:
- Render with rich text off.
- Disable the EventSystem (and keep its instance) when the panel sits over a native menu.
- Restore cursor state from `GameMode.CurrentGameMode.GetShowCursor()` on close.
- Repaint styles when the team changes (the `appearanceTeam` cache).

---

## 10. GameEvents (106:GameEvents.cs; static `Action` fields, not C# events)

**Subscribing.**
- Mono server: `GameEvents.OnGameStarted += Handler;` (Si_Spawn\Si_Spawn\Si_Spawn.cs:70-71).
- Il2Cpp client: `GameEvents.OnPlayerLeft += DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Player>>(Handler);` (pattern from `Silica\Si_SurrenderCommand\Si_SurrenderCommand.cs:89`).
- Or Harmony-postfix the method that fires the event.

The "fires on client" column comes from the invoking code paths:

| Event | Signature | Fired from | On clients? |
|---|---|---|---|
| `OnPlayerJoined` / `OnPlayerLeft` | `Action<Player>` | NetworkGameClient.cs:397 / 412; server NetworkGameServer.cs:905 / 798, 841, 1003 | yes |
| `OnPlayerCreated` / `OnPlayerDeleted` / `OnPlayerNameChanged`* | `Action<Player>` | Player lifecycle | yes |
| `OnPlayerChangedTeam` | `Action<Player, Team, Team>` (player, old, new) | `Player.Team` setter (Player.cs:83) | yes (PlayerSelectTeam packet) |
| `OnPlayerSelectUnit` / `OnPlayerChangedUnit` | `Action<Player, Unit, Unit>` (player, old, new) | `Player.ControlledUnit` setter (151, 166) | yes (PlayerSelectUnit packet) |
| `OnPlayerUnselectUnit` | `Action<Player, Unit>` | same setter (133) | yes |
| `OnUnitDestroyed` | `Action<Unit, GameObject>` (unit, instigator) | `DamageManager.OnDamageReceived` when health ≤ 0 (DamageManager.cs:1255-1296) | **yes**: clients run it from `ObjectSetHealth` (NetworkLayer.cs:719-737 → `GetSetHealthPacket` 998-1014 → `OnSetHealth` 1340-1348). The instigator is resolved from a NetID and may be null. |
| `OnStructureDestroyed` | `Action<Structure, GameObject>` | same place (1296) | yes |
| `OnTargetDestroyed` | `Action<Target, GameObject>` | same (1286) | yes |
| `OnTargetHit` | `Action<Target, float, GameObject>` (target, damage, instigator) | 1285, 1328 | yes on the death path; for ordinary hits via `SynchHealth`: verify |
| `OnTargetRepaired`* | `Action<Target, float, GameObject>` | | verify |
| `OnBaseGameObjectDestroyed` / `…Deleted` / `…PostInit` | `Action<BaseGameObject>` | | yes (destroyed: same path) |
| `OnUnitSpawned` / `OnUnitPostInit` / `OnUnitDeleted` / `OnUnitIdle` / `OnUnitReload` | `Action<Unit>` | Unit init (Unit.cs:1196) | yes (spawned) |
| `OnUnitChangedTeam` | `Action<Unit, Team, Team>` | | |
| `OnUnitEnterCompartment` / `OnUnitExitCompartment` | `Action<Unit, UnitCompartment>` | Unit.cs:1047 | probably; verify |
| `OnStructureSpawned` / `OnStructureDeleted` | `Action<Structure>` | | |
| `OnStructureChangedTeam` | `Action<Structure, Team, Team>` | | |
| `OnCommanderChanged` | `Action<Team, Player, Player>` (team, old, new) | `MP_Strategy.SetCommander` (1588) | **yes**: the client runs `SetCommander` on `SYNCH_COMMANDER` (700-709) |
| `OnGameStarted` | `Action<GameMode>` | `OnMissionStateChanged(STARTED)` (2159) | yes, via the `MISSION_STATE_CHANGED` RPC (711-716) |
| `OnGameEnded` | `Action<GameMode, Team>` (mode, winner) | `OnMissionStateChanged(ENDED)` (2170) | yes |
| `OnGameInit` / `OnGameRestart` | `Action<GameMode>` | Restart (2796) | yes |
| `OnTargetDetected` | `Action<Team, Target, Sensor>` | `Team.DetectTarget` (1599) | yes, for every team (sensor null on clients) |
| `OnTargetLost` | `Action<Team, Target>` | `Team.LostTarget` | yes |
| `OnChatMessage` | `Action<Player, string, bool>` (sender or null, text, teamOnly) | NetworkLayer.cs:594 | yes, only for chat the client may see |
| `OnTeamTechnologyTierChanged` | `Action<Team, int, int>` | Team.cs:2000 | yes (`SYNCH_TECHNOLOGYTIER`) |
| `OnConstructionStarted` / `OnConstructionCompleted` | `Action<ConstructionSite>` / `Action<ConstructionSite, bool>` | ConstructionSite.cs:559 | verify |
| `OnToggledStrategyMode` | `Action<bool>` | StrategyMode (local) | local only |
| `OnLocalPlayerGroupChanged` / `…UnitAdded` / `…UnitRemoved` | `Action<Unit, AIGroup, AIGroup>` / `Action<AIGroup, Unit>` | | local |
| `OnObjectReceivedAttackOrder` / `DefendOrder` / `RepairOrder` | `Action<BaseGameObject, Target>` | | probably server only (orders are executed there) |
| `OnObjectReceivedMoveOrder` | `Action<BaseGameObject, Vector3>` | | probably server only |
| `OnObjectReceivedStopOrder` | `Action<BaseGameObject>` | | probably server only |
| `OnResourcesStored` / `OnResourcesHarvested` | `Action<Team, Resource, int>` / `Action<Team, Resource, int, int>` | harvested: Team RPC (Team.cs:657-666) | harvested yes |
| others | `OnLevelBeginLoad/EndLoad/Init (string, GameModeInfo)`, `OnZoneEvent`, `OnOpenableEvent`, `OnDialogueCompleted`, `OnReentryComplete`, `OnSpecialActionPerformed`, `OnSpawnPointChanged`, `OnUsePointUsed`, `OnObjectiveCompleted`, `OnTutorialTaskEvent`, `OnAITaskChangeState`, `OnResourceHoldersFilled/Emptied`, `OnCleared`* (`Action`) | | |

\* not in 0.9.47.

Instance event worth knowing: `MP_Strategy.OnLocalRoleChanged (Action<ETeamRole, ETeamRole>)` (106:MP_Strategy.cs:265). The game has no kill feed of its own (no "killfeed" class); `Player.OnTargetDestroyed` (Player.cs:~836-930) only updates scores.

---

## 11. Project templates and tooling

### Server csproj: `Si_Spawn\Si_Spawn\Si_Spawn.csproj` (dual branch, verbatim)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <!-- Silica branch to build against: "test" (Steam test branch, the default here) or "default" (Steam default
         branch, what the public server runs). Reference DLLs live in include\<branch>\ (copied from that branch's
         server install, see Projects\_silica_refs). The default build goes to bin\Release\default\ so it can never
         be mistaken for the test build:  dotnet build -c Release -p:SilicaBranch=default -->
    <SilicaBranch Condition="'$(SilicaBranch)' == ''">test</SilicaBranch>
    <OutputPath Condition="'$(SilicaBranch)' == 'default'">bin\$(Configuration)\default\</OutputPath>
    <IntermediateOutputPath Condition="'$(SilicaBranch)' == 'default'">obj\$(Configuration)\default\</IntermediateOutputPath>
    <AppendTargetFrameworkToOutputPath Condition="'$(SilicaBranch)' == 'default'">false</AppendTargetFrameworkToOutputPath>
    <Nullable>enable</Nullable>
    <MSBuildWarningsAsMessages>MSB3270</MSBuildWarningsAsMessages>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="..\include\$(SilicaBranch)\*.dll">
      <Private>False</Private>
    </Reference>
  </ItemGroup>
</Project>
```

- Repo layout: `include\default\` and `include\test\` hold copies of the `_silica_refs` sets. They are git-ignored (`.gitignore`: `bin/ obj/ include/ *.pdb *.cache`).
- Each set contains: `0Harmony, AstarPathfindingProject, Drawing, MelonLoader, Newtonsoft.Json, PackageTools, Si_AdminMod, SilicaCore, UnityEngine{,.AIModule,.AudioModule,.CoreModule,.InputLegacyModule,.PhysicsModule,.TerrainModule,.TextRenderingModule,.UI,.UIModule}, com.rlabrecque.steamworks.net`.

### Client csproj: `Si_ModUI\Client\Si_ModUI.csproj` (verbatim)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <AssemblyName>Si_ModUI</AssemblyName>
    <Version>1.5.2</Version>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>
    <NuGetAudit>false</NuGetAudit>
    <ClientLoaderRoot Condition="'$(ClientLoaderRoot)' == ''">C:\SteamLibrary\steamapps\common\Silica\MelonLoader</ClientLoaderRoot>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="*.cs" />
    <Compile Include="../Shared/*.cs" />
    <Reference Include="$(ClientLoaderRoot)/net6/MelonLoader.dll" Private="false" />
    <Reference Include="$(ClientLoaderRoot)/net6/0Harmony.dll" Private="false" />
    <Reference Include="$(ClientLoaderRoot)/net6/Il2CppInterop.Runtime.dll" Private="false" />
    <Reference Include="$(ClientLoaderRoot)/net6/Il2CppInterop.Common.dll" Private="false" />
    <Reference Include="$(ClientLoaderRoot)/Il2CppAssemblies/Il2CppSilicaCore.dll" Private="false" />
    <Reference Include="$(ClientLoaderRoot)/Il2CppAssemblies/Il2Cppmscorlib.dll" Private="false" />
    <Reference Include="$(ClientLoaderRoot)/Il2CppAssemblies/Il2Cppcom.rlabrecque.steamworks.net.dll" Private="false" />
    <Reference Include="$(ClientLoaderRoot)/Il2CppAssemblies/UnityEngine.CoreModule.dll" Private="false" />
    <Reference Include="$(ClientLoaderRoot)/Il2CppAssemblies/UnityEngine.IMGUIModule.dll" Private="false" />
    <Reference Include="$(ClientLoaderRoot)/Il2CppAssemblies/UnityEngine.UI.dll" Private="false" />
    <Reference Include="$(ClientLoaderRoot)/Il2CppAssemblies/UnityEngine.TextRenderingModule.dll" Private="false" />
    <Reference Include="$(ClientLoaderRoot)/Il2CppAssemblies/Unity.InputSystem.dll" Private="false" />
  </ItemGroup>
</Project>
```

- The client compiles against whatever interop MelonLoader last generated for the **installed** client branch. Today that is 0.9.47 (see the header).
- A member that exists only in 1.0.x (for example `ENetworkPacketType.TeamInitTargets`) will not compile, and a member that was removed in 1.0.x will fail at runtime on 1.0.x clients.
- Si_ModUI 1.4.x was built against 1.0.4 interop and shipped for both branches after checking that every member it uses exists in both (memory `project_silica_dual_branch_builds.md`).
- Si_Spectator will need: `MenuManager`, `StrategyHandler`, `GameModeExt.ETeamSelect`, `MP_Strategy.HandleUI`, `FogOfWar.GetCurrentFogTeam`, `BaseGameObject.UpdateVisibility`, `FreeFlyCameraController`, `UnitAimAt`, `Soldier.CameraReference`, `VehicleCameraController`, `OrderedUpdateManager`, `GameEvents`. All exist in both decompiles.
- Usings in the ModUI client: `Il2Cpp`, `Il2CppSilica`, `Il2CppSilica.UI`, `Il2CppSilica.UI.Menus`, `Il2CppDebugTools`, `HarmonyLib`, `MelonLoader`, `UnityEngine` (Ui.cs:1-12). MelonInfo / MelonGame / MelonPlatform / MelonPlatformDomain(IL2CPP) attributes are at ClientMod.cs:13-16.

### Config path resolver: `Si_Spawn\Si_Spawn\Config.cs`

```csharp
public static string Path => System.IO.Path.Combine(MelonEnvironment.UserDataDirectory, "cfg", "Spawn", "Si_Spawn_Config.json");
public static SpawnConfig Load()   // File.Exists ? JsonConvert.DeserializeObject<SpawnConfig>(...) ?? new : cfg.Save(); then Sanitize(); errors -> defaults + warning
public void Save()                 // Directory.CreateDirectory(GetDirectoryName(Path)); File.WriteAllText(Path, JsonConvert.SerializeObject(this, Formatting.Indented))
```

- For Si_Spectator: `UserData/cfg/Spectator/Si_Spectator_Config.json`.
- Si_Spawn has no "move old folder once" step (it was new). Mods that predate `cfg/` do that migration on first start (memory `project_silica_cfg_layout.md`). Si_Spectator is new, so it does not need it.

### ModUI export

- Si_ModUI finds `public static string ModUIData()` (and optionally `public static string ModUIPlayerData(ulong steamId)`) on any type of the mod's assembly, by assembly name = mod id.
- It calls `ModUIData` on every frame build, every few seconds, so cache the result. It merges `commands` and embeds the JSON as `data` (Si_ModUI README "Mod data", lines ~59-90).
- Example: `Si_Spawn\Si_Spawn\ModUI.cs`:
  - `_cache` keyed on config + catalog count;
  - `ModUI.ModUIExport.New().Enabled(..).Info(..).Switch(..).Value(..).Picker(..).Action(..).OnOff(..)`, then `.Catalog(...)…Done().Build()`;
  - `ModUIPlayerData` returns `ModUIExport.PlayerValues(("!spawn team", s.TeamMode), …)`.
- Builder file: copy **`Si_ModUI\Export\ModUI_Export.cs`**. It is identical to `Si_Spawn\Si_Spawn\ModUI_Export.cs`, namespace `ModUI`, Newtonsoft only. API:
  - `Info`, `Enabled`, `Action`, `Report`, `Picker(text,label,desc,admin,arg,choices…)`, `Switch`, `OnOff`, `OnOffAuto`, `Multi`, `Value`, `Table`, `Catalog`, `Browse`, `Extra`, `Build`;
  - `ForPlayer().Value().Table().Build()` and `PlayerValues(...)`.
  - Picker `arg: "player"` gives the player picker the plan needs.
- Rules from the file header:
  - An entry whose text equals a registered bare command only decorates it and cannot change its admin flag, so switch and admin rows need their own sub-command text.
  - Admin entries never reach players without the power.
  - Rich-text tags in descriptions are stripped.

### Command registration

- Admin Mod API (`Silica_data-bomb\Si_AdminMod\HelperMethods.cs:45-56`):
  - `RegisterAdminCommand(string cmd, CommandCallback cb, Power power, string? desc = null)`;
  - `RegisterPlayerCommand(string cmd, CommandCallback cb, bool hideFromChat)`;
  - `RegisterPlayerPhrase(...)`;
  - `CommandCallback = void (Player? caller, string args)`.
- **Si_Spawn does not use `RegisterAdminCommand`.** In `OnLateInitializeMelon` (Si_Spawn.cs:42-66) it:
  - checks `RegisteredMelons.Any(m => m.Info.Name == "Admin Mod")`;
  - removes a foreign `spawn` entry (`TakeOverCommand`, 84-105, repeated on scene load);
  - adds `new AdminCommand { AdminCommandText = "spawn", AdminCallback = Command_Spawn, AdminPower = Power.Cheat, AdminCommandDescription = "…" }` straight to `AdminMethods.AdminCommands`.
- Reason (memory `project_si_spawn.md`, 1.0.1): `RegisterAdminCommand` also registers a console alias `sam_<cmd>` in DebugConsole's private `s_Commands` dictionary. It **throws "An item with the same key has already been added"** if another mod took that name first, and that aborts our init.
- **Rule: never share a command NAME with another mod via `RegisterAdminCommand`.** Pick unique names (`spectate`, `spectator`; check that no installed mod uses `spec` or `play` before adding aliases), or add the `AdminCommand` object directly as Si_Spawn does.
- Elo registers player commands with `HelperMethods.RegisterPlayerCommand("reserve", OnReserveCommand, true)` (TeamBalance.cs:192).
- Power: Si_Spawn uses `Power.Cheat`, BTB `!team` uses `Power.Teams`, AFK `!kick` uses `Power.Kick`. `Power.Teams` fits "move a player out of or into teams" best.

### refcheck (`C:\tmp\refcheck`)

- A Mono.Cecil console app (net8.0; `Program.cs`, `refcheck.csproj` with Mono.Cecil 0.11.6; built at `C:\tmp\refcheck\bin\Release\net8.0\refcheck.dll`).
- It resolves every type, method and field reference that a mod DLL makes into the game assemblies against a given Managed folder. It reports `TYPE/METHOD/FIELD missing`, with a "now:" hint listing same-named methods.
- Usage (Program.cs:2):

  ```
  dotnet C:\tmp\refcheck\bin\Release\net8.0\refcheck.dll <gameManagedDir> "<extraDir;extraDir>" <modDll|modDir> [gameAsmPrefix...]
  ```

  - Default prefixes: `SilicaCore Assembly-CSharp AstarPathfindingProject`.
  - Example: `refcheck.dll "C:\Users\schwe\Projects\_silica_refs\default" "C:\Users\schwe\Projects\_silica_refs\net35" bin\Release\default\Si_Spectator_Server.dll`.
  - Run it for both branches after every build or game update. It finds MissingMethodExceptions that only fire at runtime.
  - Untested idea: for the client, point it at `...\MelonLoader\Il2CppAssemblies` with prefix `Il2CppSilicaCore`.

### `_silica_refs` (`C:\Users\schwe\Projects\_silica_refs`, README.txt)

- `default\`: Steam default branch (Beta 0.9.47, server build 25179400). SilicaCore and five UnityEngine modules were copied from the dedicated server on 2026-10-20. Steamworks, Drawing, PackageTools, Astar and the extra Unity modules are the 1.0.4 copies.
- `test\`: Steam test branch, Release 1.0.4, copied 2026-10-22. The decompile and the local server are already 1.0.6, so refresh `test\` from the server's `Silica_Data\Managed` before relying on it.
- `net35\`: MelonLoader + 0Harmony net35, only for Si_ModUI_Server.
- Both branch sets also contain MelonLoader.dll + 0Harmony.dll (net6), Newtonsoft.Json.dll and Si_AdminMod.dll.
- Each repo keeps its own git-ignored copy in `include\default` / `include\test`.
- Build the default branch with `dotnet build -c Release -p:SilicaBranch=default`.
- Dist for the public server goes to `Projects\_dist\<dated>\` (memory `project_silica_dual_branch_builds.md`).

---

## Runtime checks still open

1. `FreeFlyCameraController.Instance.transform == Game.CurrentCamera.transform` (§5).
2. Faction menu `CloseOnBack`, i.e. what Escape does when team-less (§3).
3. Whether `LargeMapOpener` is active and M works with no unit and no team. Whether `RTSPlayerNameManager` exists outside StrategyUI (§9c/d).
4. Whether a remote soldier's `CameraReference` follows head animation at follow distance, and whether a passenger's `UnitAimAt` keeps updating (§6).
5. Whether IL2CPP inlined `StrategyMode.get_CurrentStrategyTeam`. Prefer the `UpdateVisibility` prefix (§1).
6. That `OnTargetHit` fires on clients for ordinary hits (SynchHealth path), and the compartment and construction events (§10).
7. Whether the vanilla surrender-vote UI is reachable without a team (§2 risk 4).
8. Whether Harmony 2 in this MelonLoader really runs later prefixes after one returns false; it decides the join-block design (§4a).
