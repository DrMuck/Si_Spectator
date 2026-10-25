# Si_Spectator server: in-game test checklist

Server-side checks for the first live sessions. Client items are in `docs/TEST_CHECKLIST_CLIENT.md`.
Set `"Debug": true` in `UserData/cfg/Spectator/Si_Spectator_Config.json` and run `!spectator reload` to get
`[debug]` lines in `MelonLoader\Latest.log` (packets sent, AFK removals, mods found).

Tick each item and note the log line or what you saw.

## 0. Start

- [ ] `Latest.log` has `[Spectator] Si_Spectator v0.1.0 loaded: ... transport on, hardening on`.
- [ ] `Latest.log` has `[Spectator] Si_AFKManager guard installed` (when Si_AFKManager is installed).
- [ ] Si_ModUI's `Public frame` block lists `Si_Spectator_Server ... /spectate(/spec) /spectate view /spectate fow /spectate status /play`.
- [ ] No exception mentioning `Spectator` anywhere in the log, also after the first map load.

## 1. Permissions

- [ ] As admin: `/spectate status` says "You may spectate".
- [ ] As a non-admin who is not listed: `/spectate` is refused with one line ("not allowed").
- [ ] `!spectator allow <that player>`: the player gets "An admin allowed you to spectate"; the config file now
      lists `{ "steamId": ..., "name": ... }`.
- [ ] That player can now `/spectate`.
- [ ] `!spectator deny <name>` (also while the player is offline): removed from the file; a running spectator
      stays and the reply suggests `!spectator kick`.
- [ ] `!spectator all on`: anyone can `/spectate`. `!spectator all off` restores it.
- [ ] `!spectator max 1`, then two allowed players `/spectate`: the second is refused with "slots are taken (1/1)".
- [ ] `!spectator force <second player>` still works and logs "forced ... past the slot cap".
- [ ] `!spectator max 0` = unlimited.
- [ ] A non-admin typing `!spectator list` gets the Admin Mod's "is not an admin".

## 2. Entering

- [ ] Infantry player `/spectate` mid-round: unit disappears for everyone, the player shows as having no team on
      the scoreboard, the all-chat line "X is now spectating." appears (`Announce`).
- [ ] Player in a vehicle (driver) `/spectate`: the vehicle is released/destroyed as for a team change, no error.
- [ ] **Commander** `/spectate`: log says "commander seat freed: <team>"; no "removed from commander seat" popup on
      the ex-commander; that team's AI commander takes over after its wait time (or a new player can take the seat).
- [ ] Ex-commander spectator disconnects: the team does NOT keep a ghost commander (AI or a player can command).
- [ ] Before round start: a player who pressed Commander (lottery) or typed `!commander` (CommMgmt) and then
      `/spectate` is not seated at round start, and CommMgmt's lottery runs for the other teams without an error.
- [ ] A player with an Elo `!reserve` who then spectates is no longer in `!reserve` (the queue list).
- [ ] `/spectate` while already spectating: "already spectating", nothing else happens.

## 3. While spectating

- [ ] The spectator receives no unit when the round (re)starts, and the faction screen does not auto-assign a team.
- [ ] Team chat from the spectator reaches only other team-less players; normal (all) chat works both ways.
- [ ] Server nearly full, spectator idle > 7 min in a running round: NOT kicked by Si_AFKManager (with Debug on:
      "AFK tracker: removed spectator").
- [ ] Round end and restart: still spectating (`/spectate status`), with the client mod `S|on` arrives again.
- [ ] Map change: still spectating after the new map loads.
- [ ] `/spectate view <player>` with the client mod: the client follows that player. Without the client mod: the
      reply says the client mod is needed.
- [ ] `/spectate fow sol|cent|alien|all`: with the client mod the perspective switches; a faction that is not in
      the match is refused ("not in this match").
- [ ] `/spectate view` / `fow` while NOT spectating: refused ("Only while spectating").
- [ ] No `dropped ... from spectator` lines appear for a vanilla client. If any appear, note the packet type: a
      legitimate client action is being blocked.

## 4. Leaving

- [ ] `/play` (or `/spectate off`): the faction screen appears, picking a faction spawns the player normally.
- [ ] Picking a faction on the vanilla screen directly while spectating (vanilla client "bench"): log says
      "joined <team>: spectating ended"; `/spectate status` says "You may spectate".
- [ ] `!reserve <team>` (Elo) or `!team` (BTB) on a spectator: moved, spectating ended, no error.
- [ ] `!spectator kick <name>`: the player is back on the faction screen and told so.
- [ ] Spectator disconnects and reconnects: joins as a normal player (not spectating).

## 5. Si_ModUI panel

- [ ] Player tab: the Spectate switch shows ON while spectating, OFF otherwise (may lag up to a minute unless you
      just used a command).
- [ ] View-player picker sends `/spectate view <name>`; the fog switch highlights the last choice.
- [ ] "You" table shows spectating / allowed / client mod version.
- [ ] Admin tab: allow / force pickers list online players; deny and kick list names only (no Steam IDs anywhere).
- [ ] Turning `Enabled` off in the file + `!spectator reload` greys the mod out (`enabled: false`).

## 6. Both branches

- [ ] Repeat sections 0, 2 (commander) and 4 once on a 0.9.47 (default branch) server with the default build.
