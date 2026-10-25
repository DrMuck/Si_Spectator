# Si_Spectator client – in-game test checklist

**Setup**
- Client: the game on the branch of the installed `Si_Spectator.dll`. The local client is on the test branch (Release 1.0.7) since 2026-10-25; the test build is deployed in `Mods\`.
- Server: `Si_Spectator_Server.dll` for its branch, plus the Admin Mod.
- You need to be an admin, or on the `Allowed` list.
- A second player or bots on a team help for the follow tests.

**Logs**
- Session log: `C:\SteamLibrary\steamapps\common\Silica\UserData\Si_Spectator\session-*.log`, newest file.
- The same lines appear in `MelonLoader\Latest.log` with the prefix `[Spectator]`.

| # | Step | Expected | Session log |
|---|---|---|---|
| 1 | Start the game and reach the main menu. | Nothing visible. | `READY 0.1.0 - spectator client. Marker SiSpec/1, packet 125. Keys: panel I, camera C, fog F, tags K, hide H, ...`. If it still says `panel P` / `tags N`, the settings migration did not run: report it. No `transport disabled`. |
| 2 | Join the server and wait about 5 s. | Nothing visible: you are not spectating yet. | `Connected (...)`, `HELLO 1 (client 0.1.0)`, `STATE from server <ver>: off, allowed yes\|no`. If only `HELLO 1..5` appear followed by "No S after 5 hellos", the server mod is missing. |
| 3 | Pick a faction, then type `/spectate` in chat. | The unit disappears and no faction screen comes back. The cursor is locked and you fly freely with WASD / Space / Ctrl / Shift / wheel. The HUD line at the top reads `SPECTATING Free ...`. | `STATE ... on`, `SPECTATING (server S\|on). Runtime check 1: FreeFly.transform IS / is NOT Game.CurrentCamera.transform ...` (**report this line**), `Faction menu suppressed (was SELECT_TEAM)`, possibly `Closed strategy screen Faction (STARTED)`. |
| 4 | Wait 10 s, then press Tab and M. | The faction screen stays away. Tab = large minimap, M = map (runtime check 3: does M open with no team?). | No new `LEFT` line. |
| 5 | Press `]` several times, then `[`. | It goes to the next player with a unit, in team order, in Shoulder view (behind the right shoulder). The view turns where that player aims. `[` goes back. | `Target <name> (<team>) (next)`, `Camera Shoulder (next player)`. |
| 6 | Press `C` repeatedly. | The modes cycle FollowFps → Orbit → Overview → Fixed → Free → .... **FollowFps**: eye view; the own head must not block it (raise `FpsNearClip` if it does); does the eye follow the head animation? (runtime check 4). **Orbit**: the mouse orbits, the wheel zooms. **Overview**: top-down; wheel / Space / Ctrl change the height, WASD pans, Q / E rotate. **Fixed**: frozen. **Free**: vanilla free fly, continuing from the last pose without a jump. | `Camera <Mode> (key C)` for each. |
| 7 | Follow a player into a vehicle as driver, then as gunner / passenger. | Driver: a third-person orbit behind the vehicle, turning with the driver's aim. Gunner: the same vehicle camera, turning with the gunner's aim. | No `ERROR` lines. |
| 8 | While following, let the player die and respawn. | The camera holds and the HUD shows `[waiting for <name> to (re)spawn]`. After the respawn it continues on the new unit. | No mode change logged. |
| 9 | In Free, look at a player and left-click. Click again, or press G. | The first click shows `Selected <name> - click again or press G to follow` and the tag gets `> <`. The second click follows in Shoulder. | `Target <name> (click)`, `Camera Shoulder (second click)`. |
| 10 | Press Escape in a follow mode, then in Free. | In a follow mode → Free, and no pause menu. In Free → the pause menu opens normally. Close it. | `Camera Free (Escape)`. |
| 11 | Press `K`, then `K` again. | Name tags over every player unit: name in team colour, unit name, HP bar. The commander's HQ shows `CMD <name>`. Tags vanish and come back. The voice channel does NOT switch (N is no longer ours). | `Name tags off`, `Name tags on`. |
| 12 | Press `F` repeatedly. | The fog perspective cycles All → each team → All. Under a team: enemies that team has not detected disappear from the 3D view and their tags vanish; the minimap and large map show that team's fog. Under All: everything. | `Fog perspective patches installed ...` (first time only), `Fog perspective <team> (key F)`. No `own visibility computation failed` line. **If enemies stay visible under a team view, report it** (runtime check 5). |
| 13 | Press `I`. During a map vote, check that opening the panel does not cast a vote. | The panel opens on the left with the cursor. Camera, fog and tag buttons work. Click a player in the list → follow. Clicks never reach the game (no shooting, no menus behind it). The cameras keep moving. | `Panel opened`, `Target ... (panel)`, `Camera ... (panel list)`. |
| 14 | Press `I` again, then open and close it with Escape and with a click outside. | It closes each way. The cursor locks again and free fly works at once. | `Panel closed`. |
| 15 | Press `H`, then `H` again. | Every overlay (tags, HUD line, panel) hides, then comes back. | `Overlays hidden` / `shown`. |
| 16 | Press F6 (Si_ModUI) while following. | The ModUI panel opens and our camera freezes. I / C / F do nothing while it is open. Close ModUI → our camera continues. | No key lines while ModUI is open. |
| 17 | Open chat (Enter) and type `ickfh[]g`. | The letters go into chat; no mode, fog or tag changes. | Nothing. |
| 18 | Run `/spectate view <other player>` and `/spectate fow <team>` in chat. | It views that player in Shoulder, and the fog perspective switches to that team. | `Target <name> (server V)`, `Camera Shoulder (server V)`, `Fog perspective <team> (server F)`. |
| 19 | Wait for the round end and the next map (scene load). | The end screen / map vote stays usable. After the load the faction screen may flash briefly, then it is gone again and you are in Free. | `Scene loaded: <map> (spectating resumes when the server confirms)`, `State Resuming`/`LEFT spectating (scene ...)`, `HELLO 1`, `STATE ... on`, `SPECTATING (server S\|on, resumed after scene load)`. |
| 20 | Before the round starts (layout vote / countdown) while spectating. | The battle-layout vote stays (you can vote). Once versus is set, no faction screen opens during the countdown. | `Closed strategy screen Faction (INIT)` possibly. |
| 21 | Press "Return to game" in the panel (or `/spectate off`). | The panel closes and the faction screen comes back (also before round start). Pick a faction → you play normally. The free cam is back to vanilla and the fog shows everything. | `Return to game: sent /spectate off.`, `STATE ... off`, `LEFT spectating (server S\|off), now Idle.` |
| 22 | While spectating, have an admin put you on a team (`!team`, Elo `!reserve`). | Spectating ends at once and you play on that team. | `LEFT spectating (joined team <team>)` (or `server S\|off`). |
| 23 | Disconnect while spectating, then rejoin. | No leftover effects in the main menu. After rejoining the server re-sends the state. | `LEFT spectating (disconnected)`, `Disconnected.`, then step 2 again. |

**Solo test against the AI** (no second player needed; the server's AI commanders play the other side)

| # | Step | Expected | Session log |
|---|---|---|---|
| 24 | Join a server alone, take the commander seat of a team, build a few units (soldiers and a vehicle), then type `/spectate`. | You become a spectator in Free; the AI takes over your team. The panel shows "AI units: ON". | `SPECTATING (server S\|on)`. |
| 25 | In Free, aim at one of the AI units (yours or the enemy's) and left-click, then click again (or press G). Cycle C through FollowFps / Shoulder / Orbit / Overview. Turn on "AI tags" in the panel. Let the unit die. | The first click shows `Selected AI · <unit> (<team>)`. The second click follows it in Shoulder; the header reads `AI · <unit> (<team>)`; the modes work as for a player (vehicle = orbit behind it). AI tags show unit names in team colour within TagRange. When the unit dies the camera returns to Free. `[` / `]` still only step over players. | `Target AI · <unit> (<team>) (click)`, `Camera Shoulder (second click), target AI · ...`, `AI tags on (panel)`, `AI target AI · ... destroyed.`, `Camera Free (AI target destroyed)`. |

**Report back**
- The Runtime check 1 line (step 3).
- Steps 4, 6, 7 and 12 observations.
- Any `ERROR` line in the session log. Errors are rate-limited to one per 10 s, so look for the first one.
