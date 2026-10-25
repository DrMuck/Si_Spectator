# Si_Spectator client – game members used, checked against both branches

Generated 2026-10-25 for client 0.1.0. Every game type / member the client source touches, located in both decompiles:

- `0947` = `C:\tmp\silica_decompile_0947` (Steam default branch, Beta 0.9.47, what the local client runs and what the
  default build compiles against: `include\client\default\Il2CppAssemblies`).
- `106` = `C:\tmp\silica_decompile_106` (Steam test branch, Release 1.0.6).

Method: a script matched each member's declaration (full signature for methods) in both trees
(`verify.py` in the agent scratchpad; regexes are the declarations quoted below in spirit). **106 members, 0 missing,
no reflection or `AccessTools` fallback needed.** The bodies of the three methods we patch with behaviour that depends on
their logic were diffed as well: `BaseGameObject.UpdateVisibility`, `MP_Strategy.HandleUI` and
`FreeFlyCameraController` (whole file) are identical between the branches; `VehicleCameraController.cs` and
`UnitCameraController.cs` are identical files.

Compile-level confirmation on top of the decompile match: the same source builds with 0 errors / 0 warnings against
the 0.9.47 interop (`include\client\default`) AND against the 1.0.7 interop MelonLoader generated on 2026-10-25 after
Steam moved the local client to the test branch (`include\client\test`), and the default build loaded on 1.0.7 with
every Harmony patch applied (no patch error in `Latest.log`).

The fog prefix re-implements `BaseGameObject.UpdateVisibility(Team)` (0947:BaseGameObject.cs:588-617 =
106:569-598, identical bodies) with the chosen team and skips the original, so it does not rely on Il2CppInterop writing
a rewritten `ref` argument back. One deliberate difference: a null `Target` counts as "not visible" instead of throwing.

Notes on access level:
- `MP_Strategy.ShowTeamSelectMenu` is `private` in 0.9.47 and `protected` in 1.0.6; `FreeFlyCameraController.TargetPos /
  TargetRot` are private in both; `GameByteStreamReader.m_MemoryStream` is private. Il2CppInterop exposes them as public
  properties (verified with `ilspycmd` against the 0.9.47 interop: `MP_Strategy.ShowTeamSelectMenu` at line ~657,
  `FreeFlyCameraController.TargetPos/TargetRot` at ~308/321), so no reflection is needed on either branch.
- `NetworkLayer.ProcessMessage` is private static; it is patched by name (`[HarmonyPatch(typeof(NetworkLayer),
  "ProcessMessage")]`), same as Si_ModUI.
- `GameModeExt.ETeamSelect` is a `protected enum` in both; the interop makes it `Il2Cpp.GameModeExt.ETeamSelect`.
- `Team.BaseStructure` is the same public property in both; only its backing field changed (index in 0.9.47, GUID in
  1.0.6).
- Unity / InputSystem members (`Camera`, `Physics.Raycast/SphereCast`, `Keyboard`, `Mouse`, `EventSystem`, IMGUI) are
  engine API (Unity 6000.0 on both branches), not listed.
- Not used on purpose: `Unit.CurrentUnit` (write), `Player.ControlledUnit` (write), `StrategyMode.*`, `UsePoint`,
  `NetworkLayer.SendPlayerSelectUnit/Team`, any RPC. The only outgoing traffic is packet 125 (`H|0.1.0`) and
  `Player.SendChatMessage("/spectate off")`.

| Member | 0947 file:line | 106 file:line | Note |
|---|---|---|---|
| NetworkLayer.ProcessMessage(GameByteStreamReader, uint, NetworkID, bool) | NetworkLayer.cs:212 | NetworkLayer.cs:386 | Harmony prefix (Receive); private static, patched by name |
| NetworkLayer.SendPacket(GameByteStreamWriter, ENetworkPacketSend) | NetworkLayer.cs:1090 | NetworkLayer.cs:1289 | hello |
| GameByteStreamWriter.GetGameByteStreamWriter(uint, string, bool) | GameByteStreamWriter.cs:19 | GameByteStreamWriter.cs:19 |  |
| GameByteStreamWriter.WriteByte(byte) | GameByteStreamWriter.cs:207 | GameByteStreamWriter.cs:212 |  |
| GameByteStreamWriter.WriteString(string) | GameByteStreamWriter.cs:497 | GameByteStreamWriter.cs:502 |  |
| GameByteStreamReader.m_MemoryStream | GameByteStreamReader.cs:8 | GameByteStreamReader.cs:8 | private field, interop property |
| GameByteStreamReader.ReadByte() | GameByteStreamReader.cs:208 | GameByteStreamReader.cs:208 |  |
| GameByteStreamReader.ReadString() | GameByteStreamReader.cs:410 | GameByteStreamReader.cs:410 |  |
| EGameByteStreamType.String | EGameByteStreamType.cs:22 | EGameByteStreamType.cs:22 | marker tag byte |
| ENetworkPacketType (no value 125) | ENetworkPacketType.cs:1 | ENetworkPacketType.cs:1 | 0..42 (0947) / 0..43 (106) |
| ENetworkPacketSend.Reliable | ENetworkPacketSend.cs:4 | ENetworkPacketSend.cs:4 |  |
| NetworkID.SteamID | NetworkID.cs:19 | NetworkID.cs:19 | .m_SteamID (Steamworks) |
| NetworkGameClient.GetInServer() | NetworkGameClient.cs:148 | NetworkGameClient.cs:150 |  |
| NetworkGameClient.GetServerID() | NetworkGameClient.cs:37 | NetworkGameClient.cs:39 |  |
| Game.GetIsClient() | Game.cs:580 | Game.cs:589 |  |
| Game.GetIsServer() | Game.cs:574 | Game.cs:583 |  |
| Game.InMainMenu | Game.cs:268 | Game.cs:270 |  |
| Game.IsPauseMenuActive | Game.cs:320 | Game.cs:322 |  |
| Game.GetCameraEnabled() | Game.cs:642 | Game.cs:676 | ModUI detection + camera pause |
| Game.GetControlsEnabled() | Game.cs:629 | Game.cs:663 | prefix while our panel is open |
| Game.CurrentCamera | Game.cs:73 | Game.cs:75 |  |
| GameManager.OnEscape() | GameManager.cs:163 | GameManager.cs:163 | prefix (Escape) |
| Player.Players | Player.cs:15 | Player.cs:15 |  |
| Player.CurrentPlayer | Player.cs:59 | Player.cs:59 |  |
| Player.PlayerID | Player.cs:33 | Player.cs:33 |  |
| Player.PlayerName | Player.cs:36 | Player.cs:36 |  |
| Player.PlayerChannel | Player.cs:289 | Player.cs:293 | V packet match |
| Player.Team | Player.cs:71 | Player.cs:71 | read only |
| Player.ControlledUnit | Player.cs:115 | Player.cs:115 | read only, never assigned |
| Player.IsCommander | Player.cs:255 | Player.cs:255 |  |
| Player.SendChatMessage(string, bool) | Player.cs:547 | Player.cs:551 | Return to game |
| Team.TeamName | Team.cs:33 | Team.cs:33 |  |
| Team.TeamShortName | Team.cs:39 | Team.cs:39 |  |
| Team.TeamColor | Team.cs:53 | Team.cs:53 |  |
| Team.Index | Team.cs:541 | Team.cs:511 |  |
| Team.Structures | Team.cs:148 | Team.cs:138 | HQ lookup |
| Team.BaseStructure (ObjectInfo) | Team.cs:272 | Team.cs:258 | backing field differs (index vs guid), property same |
| GameMode.CurrentGameMode | GameMode.cs:36 | GameMode.cs:38 |  |
| GameMode.GetShowCursor() | GameMode.cs:159 | GameMode.cs:181 | cursor restore on panel close |
| GameModeExt.ETeamSelect | GameModeExt.cs:32 | GameModeExt.cs:32 | NONE / SELECT_TEAM |
| GameModeExt.EMissionState | GameModeExt.cs:40 | GameModeExt.cs:40 | INIT |
| GameModeExt.ETeamsVersus.NONE | GameModeExt.cs:7 | GameModeExt.cs:7 |  |
| GameModeExt.TeamsVersus | GameModeExt.cs:65 | GameModeExt.cs:65 |  |
| GameModeExt.BaseTeamSetups | GameModeExt.cs:57 | GameModeExt.cs:57 | match teams |
| GameModeExt.GetTeamSetup(Team) | GameModeExt.cs:96 | GameModeExt.cs:96 | active setup check |
| BaseTeamSetup.Team | BaseTeamSetup.cs:48 | BaseTeamSetup.cs:38 |  |
| MP_Strategy.HandleUI() | MP_Strategy.cs:1698 | MP_Strategy.cs:1740 | prefix (faction menu) |
| MP_Strategy.ShowTeamSelectMenu | MP_Strategy.cs:106 | MP_Strategy.cs:127 | private (0947) / protected (106): interop property |
| MP_Strategy.MissionState | MP_Strategy.cs:157 | MP_Strategy.cs:178 |  |
| MP_Strategy.TeamSelectActionPerformed() | MP_Strategy.cs:2922 | MP_Strategy.cs:2908 | prefix (swallow) |
| MP_Strategy.RoleSelectActionPerformed() | MP_Strategy.cs:2937 | MP_Strategy.cs:2916 | prefix (swallow) |
| StrategyHandler.Instance | Silica.UI/StrategyHandler.cs:27 | Silica.UI/StrategyHandler.cs:27 |  |
| StrategyHandler.CurrentStrategyScreen | Silica.UI/StrategyHandler.cs:23 | Silica.UI/StrategyHandler.cs:23 |  |
| StrategyHandler.SwitchTo(StrategyScreenType, GameModeExt) | Silica.UI/StrategyHandler.cs:34 | Silica.UI/StrategyHandler.cs:34 |  |
| StrategyScreenType {None, LayoutSelection, Faction, Loadout, Respawn, NextMap, End} | Silica.UI/StrategyScreenType.cs:3 | Silica.UI/StrategyScreenType.cs:3 | identical |
| UIManager.TextInputActive | Silica.UI/UIManager.cs:67 | Silica.UI/UIManager.cs:71 |  |
| MenuManager.Instance | Silica.UI.Menus/MenuManager.cs:14 | Silica.UI.Menus/MenuManager.cs:14 |  |
| MenuManager.GetCurrent() | Silica.UI.Menus/MenuManager.cs:215 | Silica.UI.Menus/MenuManager.cs:215 |  |
| MenuManager.IsAnyMenuOpened(bool) | Silica.UI.Menus/MenuManager.cs:237 | Silica.UI.Menus/MenuManager.cs:237 | postfix while our panel is open |
| DebugConsole.IsOpen | DebugTools/DebugConsole.cs:3539 | DebugTools/DebugConsole.cs:4048 |  |
| GameInput.GetPreferredInput() | Silica/GameInput.cs:268 | Silica/GameInput.cs:296 | prefix while our panel is open |
| GameInput.InputMap.UI | Silica/GameInput.cs:22 | Silica/GameInput.cs:23 |  |
| GameInput.UpdateInput() | Silica/GameInput.cs:304 | Silica/GameInput.cs:332 |  |
| FreeFlyCameraController.Instance | FreeFlyCameraController.cs:50 | FreeFlyCameraController.cs:50 |  |
| FreeFlyCameraController.OrderedLateUpdate(float) | FreeFlyCameraController.cs:90 | FreeFlyCameraController.cs:90 | prefix + postfix (camera drive) |
| FreeFlyCameraController.EnableMovement | FreeFlyCameraController.cs:21 | FreeFlyCameraController.cs:21 |  |
| FreeFlyCameraController.Camera | FreeFlyCameraController.cs:12 | FreeFlyCameraController.cs:12 | diagnostics |
| FreeFlyCameraController.IsActive | FreeFlyCameraController.cs:54 | FreeFlyCameraController.cs:54 | diagnostics, read only |
| FreeFlyCameraController.TargetPos / TargetRot | FreeFlyCameraController.cs:44 | FreeFlyCameraController.cs:44 | private, interop property; hand-back to Free |
| BaseGameObject.UpdateVisibility(Team) | BaseGameObject.cs:588 | BaseGameObject.cs:569 | prefix, installed on first team perspective |
| BaseGameObject.UpdateVisibilityAll() | BaseGameObject.cs:579 | BaseGameObject.cs:560 |  |
| BaseGameObject.IsVisible | BaseGameObject.cs:376 | BaseGameObject.cs:333 | tags / picking under a fog perspective |
| BaseGameObject.IsDestroyed | BaseGameObject.cs:382 | BaseGameObject.cs:339 |  |
| BaseGameObject.WorldPhysicalCenter | BaseGameObject.cs:303 | BaseGameObject.cs:260 |  |
| BaseGameObject.PhysicalBounds | BaseGameObject.cs:319 | BaseGameObject.cs:276 |  |
| BaseGameObject.ObjectInfo | BaseGameObject.cs:105 | BaseGameObject.cs:46 |  |
| BaseGameObject.DamageManager | BaseGameObject.cs:144 | BaseGameObject.cs:73 |  |
| ObjectInfo.DisplayName | ObjectInfo.cs:18 | ObjectInfo.cs:18 | unit name in tags |
| DamageManager.Health01 | DamageManager.cs:290 | DamageManager.cs:292 | HP bar |
| FogOfWar.GetCurrentFogTeam() | FogOfWar.cs:197 | FogOfWar.cs:209 | postfix, installed on first team perspective |
| Unit.Camera (UnitCameraController) | Unit.cs:81 | Unit.cs:81 |  |
| Unit.ControlledBy | Unit.cs:170 | Unit.cs:170 | read only |
| Unit.UnitAimAt | Unit.cs:224 | Unit.cs:229 |  |
| Unit.InCompartment | Unit.cs:337 | Unit.cs:342 |  |
| Unit.GetViewDirection() | Unit.cs:1346 | Unit.cs:1415 | aim fallback |
| UnitAimAt.AimAtPosition | UnitAimAt.cs:26 | UnitAimAt.cs:26 |  |
| UnitCompartment.OwnerUnit | UnitCompartment.cs:75 | UnitCompartment.cs:100 |  |
| Vehicle (class : Unit) | Vehicle.cs:10 | Vehicle.cs:11 | TryCast |
| VehicleCameraController.DistanceMin/DistanceMax | VehicleCameraController.cs:14 | VehicleCameraController.cs:14 | file identical in both |
| VehicleCameraController.LocalTransform/LocalOffset/WorldOffset | VehicleCameraController.cs:31 | VehicleCameraController.cs:31 |  |
| VehicleCameraController.CollisionRadius | VehicleCameraController.cs:44 | VehicleCameraController.cs:44 |  |
| Soldier.CameraReference | Soldier.cs:130 | Soldier.cs:136 |  |
| Soldier.HumanHandsAnimator | Soldier.cs:123 | Soldier.cs:129 |  |
| HumanHandsAnimator.WeaponViewOffset | HumanHandsAnimator.cs:36 | HumanHandsAnimator.cs:36 |  |
| CreatureDecapod.CameraReference | CreatureDecapod.cs:68 | CreatureDecapod.cs:69 | alien first-person anchor |
| Structure (class : BaseGameObject) | Structure.cs:11 | Structure.cs:13 |  |
| GamePhysics.TRACELAYERMASK_IGNOREDEBRISANDUNITS | GamePhysics.cs:583 | GamePhysics.cs:587 | camera collision |
| BaseGameObject.Target | BaseGameObject.cs:152 | BaseGameObject.cs:81 | fog prefix: own visibility computation |
| BaseGameObject.GetIsRendered(bool, bool) | BaseGameObject.cs:620 | BaseGameObject.cs:601 | protected in the game, public in the interop (compiles on both interop sets) |
| BaseGameObject.SetVisibility(bool, bool, bool) | BaseGameObject.cs:654 | BaseGameObject.cs:635 | fog prefix |
| Team.DetectedTargets | Team.cs:334 | Team.cs:304 | fog prefix (IndexOf) |
| Team.GetIsTargetAtIndexActivelyVisible(int) | Team.cs:1235 | Team.cs:1214 | fog prefix |
| GameMode.GetIsObjectAlwaysVisible(BaseGameObject, Team) | GameMode.cs:331 | GameMode.cs:368 | fog prefix |
| Target (class : MonoBehaviour, isActiveAndEnabled) | Target.cs:6 | Target.cs:6 | fog prefix |
| Team.Units | Team.cs:156 | Team.cs:146 | AI unit picking / AI tags |
| BaseGameObject.Team | BaseGameObject.cs:162 | BaseGameObject.cs:91 | fog prefix, AI target label |
