# Si_Spectator

Spectator mode for Silica multiplayer (MP_Strategy). Admins, and players an admin designates, leave their team and
watch the match without a unit. The mod has two parts:

| Part | File | Runs on | Role |
|---|---|---|---|
| Server | `Server/` → `Si_Spectator_Server.dll` | dedicated server (Mono, MelonLoader) | Who may spectate, the team-less state, guards, packet relay, Si_ModUI export, chat commands |
| Client | `Client/` → `Si_Spectator.dll` | game client (Il2Cpp) | Cameras, name tags, fog perspective, panel. See [docs/CLIENT.md](docs/CLIENT.md) |
| Shared | `Shared/Protocol.cs` | both | Wire format of packet 125 |

The server part works alone. Without the client mod a spectator only gets the vanilla "bench" view (see Known limits).

Design: [docs/PLAN.md](docs/PLAN.md). Game API research with decompile line references: [docs/API_NOTES.md](docs/API_NOTES.md).
In-game checks: [docs/TEST_CHECKLIST.md](docs/TEST_CHECKLIST.md) (server) and `docs/TEST_CHECKLIST_CLIENT.md` (client).

## Commands

Player commands (prefix `/`, `!` or `.`; the chat line is hidden):

| Command | Effect |
|---|---|
| `/spectate` (alias `/spec`) | Toggle spectator mode |
| `/spectate on` / `/spectate off` | Enter / leave |
| `/play` | Same as `/spectate off` |
| `/spectate status` | Whether you may spectate, the slots, whether your client mod said hello |
| `/spectate view <player>` | Only while spectating. Sends packet `V` to your own client: follow that player. Needs the client mod |
| `/spectate fow <all\|sol\|cent\|alien>` | Only while spectating. Sends packet `F` to your own client: that team's fog perspective. The team must be in the match. Needs the client mod |

Admin command `!spectator` (Admin Mod power **Teams**):

| Command | Effect |
|---|---|
| `!spectator allow <player>` | Add an online player to the allowed list (saved) |
| `!spectator deny <player>` | Remove from the allowed list (matches stored names, so it works for offline players). A running spectator is not removed |
| `!spectator list` | Current spectators and the allowed list (names only) |
| `!spectator force <player>` | Put a player into spectator mode. Ignores the allowed list and the slot cap (logged) |
| `!spectator kick <player>` | End a player's spectator mode; they get the faction screen |
| `!spectator all on\|off` | Everyone may spectate (saved) |
| `!spectator max <n>` | Spectator slots, 0 = unlimited (saved). Nobody already spectating is removed |
| `!spectator reload` | Re-read the config file |
| `!spectator status` | Settings summary |

Player names are matched case-insensitively: an exact name first, otherwise a unique part of a name.

No other installed mod registers `spectate`, `spec`, `play` or `spectator` (checked 2026-10-25 against the
Silica_data-bomb sources, the Projects\Si_* sources and every DLL in the server's `Mods` folder). The player
commands are registered with `HelperMethods.RegisterPlayerCommand`. The admin command is added straight to
`AdminMethods.AdminCommands` (the Si_Spawn pattern), so it gets no `sam_spectator` console alias that could collide.

## Config

`UserData/cfg/Spectator/Si_Spectator_Config.json`, written with defaults on first start:

| Field | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Master switch. Off: nobody can enter, not even with `force`. People already spectating stay |
| `AllowAll` | `false` | Everyone may `/spectate` |
| `AllowAdmins` | `true` | Admin Mod admins may always `/spectate` |
| `Allowed` | `[]` | Designated players: `{ "steamId": 7656..., "name": "..." }`. The name is informational and is refreshed when the player enters |
| `MaxSpectators` | `4` | Slots, `0` = unlimited. Admins obey it too; `!spectator force` ignores it |
| `Announce` | `true` | All-chat line when someone enters or leaves spectator mode |
| `Debug` | `false` | Verbose log lines (`[debug]`) |

`allow`, `deny`, `all` and `max` save the file at once.

## How a spectator is treated

- **Remembered by Steam ID**, never by `Team == null`: every lobby player and every mid-round joiner on the faction
  screen is team-less too. Spectators stay spectators across round restarts and map changes. Disconnecting ends it.
- **Entering**, in this order:
  1. If the player holds a commander seat: `SetCommander(team, null)` + `RPC_SynchCommander(team, kicked: false)` by
     reflection (protected in 1.0.6, private in 0.9.47). A team change does not free the seat, and a team-less
     ex-commander who disconnects would leave a ghost commander that blocks the AI takeover. `kicked: false` means
     no mutiny popup; the vanilla AI takeover countdown starts.
  2. Remove the player from the vanilla commander lottery, Si_CommManagement's `commanderApplicants` and
     Si_EloMatchmaking's `!reserve` queue (reflection, type looked up by name, skipped when the mod is absent).
  3. The vanilla eliminated-team sequence: `SendPlayerSelectUnit(player, null)`, `ControlledUnit = null`,
     `Team = null`, `SendPlayerSelectTeam(player, null)`. The server's own `OnPlayerChangedTeam` destroys the unit.
  4. Add the Steam ID, send `S|on` to the player, announce.
- **Team-less**: no team, no unit (`GetUnitPrefabForPlayer` returns null for no team), no commander seat. Team chat of
  a spectator reaches only other team-less players; spectators never receive a team's team chat.
- **Picking a faction ends spectating.** A `GameEvents.OnPlayerChangedTeam` subscriber clears the flag (and sends
  `S|off`) whenever a spectator gets a non-null team: the faction screen, Elo `!reserve` and BTB `!team` all go
  through it. Spectating is not enforced against a join; there is no `GetCanJoinTeam` patch (0.9.47 has no such
  method, and Elo/BTB replace the join handler anyway).
- **Leaving** (`/spectate off`, `/play`, `!spectator kick`): the flag is cleared and `S|off` sent. The player is
  team-less and picks a faction on the vanilla screen.
- `S|on` is sent again on every hello from the client, about 3 s after a round restart and about 6 s after a scene
  load.

## Interactions with other mods

| Mod | What Si_Spectator does |
|---|---|
| Si_AFKManager | It counts team-less players as AFK and kicks at 7 min when the server is nearly full. A postfix on `AwayFromKeyboard.OnUpdate` removes spectators from its private `AFKTracker` list, so their count never passes 1 |
| Si_CommManagement | Spectators are removed from `commanderApplicants` on entry (an applicant with no team would make its round-start lottery throw) |
| Si_EloMatchmaking | Removed from the `!reserve` queue on entry (a mirrored swap partner loses its approval). Elo's own seating skips team-less players. `!reserve` by a spectator moves them and ends spectating |
| Si_BasicTeamBalance | `!team` on a spectator moves them and ends spectating |
| Si_ModUI | Rows for every command (below). Packet 126 (ModUI) and 125 (us) pass through each other's hooks |
| Si_AntiCheat | Untouched; it only observes `ProcessMessage` |

## Hardening

A `[HarmonyPriority(Priority.First)]` prefix on the server's `NetworkLayer.ProcessMessage` peeks the packet type
byte (stream position restored in `finally`) and drops these types when their sender (`__2`, authoritative on the
server) is a spectator: `PlayerSelectTeam`, `PlayerSelectUnit`, `ObjectUpdate`, `ObjectRPC`, `ObjectClientDamageHit`,
`ObjectSetHealth`, `Suicide`, `UnitGroup`, `AITask`. A vanilla client never sends them without a unit; a modified client
could (unit hijack, orders, damage). Chat, `GameModeRPC` (needed to re-join), Ping, Heartbeat, Voice, 125 and 126 pass.
Each drop is logged once per player and type. Packets of non-spectators are never touched beyond the peek. Every
patch catches its own exceptions and lets the game path run.

## Transport

Packet type byte 125, string marker `SiSpec/1`, one string body of at most 1800 ASCII characters, reliable. Same
mechanics as Si_ModUI (126). At start the server checks that 125 is not an `ENetworkPacketType` value (1.0.6 uses
0..43, 0.9.47 uses 0..42) and disables the transport with a log line otherwise. The game ignores unknown packet types,
so a client mod talking to a server without this mod (or the reverse) is harmless.

| Direction | Message | When |
|---|---|---|
| client → server | `H\|<clientVersion>` | after join and every scene load; at most 1 per second per client is accepted |
| client → server | `K\|pose` | phase 2 (ignored) |
| server → client | `S\|on/off\|<allowed 0/1>\|<serverVersion>` | on hello, on every change |
| server → client | `V\|<networkId>\|<channel>` | `/spectate view` |
| server → client | `F\|<all or teamShortName>` | `/spectate fow` |

Every body is validated before parsing (`Shared/Protocol.cs`); anything malformed is ignored.

## Si_ModUI export

`ModUIData()` (cached; rebuilt when the config, the spectator set or the allowed names change) and
`ModUIPlayerData(steamId)`:

- Player rows: info lines (mode, count/slots, who may spectate), `/spectate` ON/OFF switch with the player's own
  value, `/spectate view` player picker, `/spectate fow` switch (ALL / SOL / CENTAURI / ALIEN, the player's last
  choice), `/spectate status`, and a per-player "You" table (spectating, allowed, client mod version).
- Admin rows: `!spectator allow` player picker, `!spectator deny` choice of allowed names, `!spectator force` player
  picker, `!spectator kick` choice of current spectators, `!spectator all` ON/OFF, `!spectator max` value,
  `!spectator list`, `!spectator reload`.
- `enabled` follows `Enabled`. Labels and values contain names only, never Steam IDs.

## Build

```
cd Server
dotnet build -c Release                           # test branch (1.0.x)  -> bin\Release\netstandard2.1\
dotnet build -c Release -p:SilicaBranch=default   # default branch (0.9.47) -> bin\Release\default\
cd ..\Tests
dotnet test                                       # protocol, rules, state, command parsing
```

`include\default` and `include\test` (git-ignored) hold the reference DLLs, copied from `Si_Spawn\include`.
`include\test\SilicaCore.dll` and `AstarPathfindingProject.dll` were refreshed on 2026-10-25 from the local 1.0.6
dedicated server (also in `Projects\_silica_refs\test`).

refcheck after every build or game update (0 missing members is the bar):

```
dotnet C:\tmp\refcheck\bin\Release\net8.0\refcheck.dll "C:\Users\schwe\Projects\_silica_refs\default" "" Server\bin\Release\default\Si_Spectator_Server.dll
dotnet C:\tmp\refcheck\bin\Release\net8.0\refcheck.dll "C:\Users\schwe\Projects\_silica_refs\test" "" Server\bin\Release\netstandard2.1\Si_Spectator_Server.dll
```

Everything that differs between the branches (`SetCommander` / `RPC_SynchCommander` visibility) is called by
reflection, so the same source builds and runs on both.

## Deploy

- Only while `Get-Process Silica` is empty. Copy the DLL of the server's branch to `<server>\Mods\Si_Spectator_Server.dll`.
- Never mix branches: the default build goes to 0.9.47 servers, the test build to 1.0.x servers.
- Start the server with `launch.bat` or `Silica.exe --melonloader.disablestartscreen`. `MelonLoader\Latest.log` should show
  `[Spectator] Si_Spectator v0.1.0 loaded: ... transport on, hardening on`. Si_ModUI's `Public frame` line should
  list `Si_Spectator_Server`.
- Dist for the public server: `Projects\_dist\Si_Spectator_0.1.0_2026-10-25\{default,test}\Mods\`.

## Known limits

- With only spectators online the round never starts: `MissionState INIT` keeps resetting its timer when no team has
  players.
- Spectators count as players for the game's "no players for 300 s" check, so they keep a round alive.
- On 0.9.47 servers with Si_PacketChunker, a mid-round joiner may hold incomplete detection lists, so a team's fog
  view on that client can show too little.
- The admin slot cap applies to admins too (`force` yourself to bypass it).
- `deny` and `all off` do not remove anyone who is already spectating; use `!spectator kick`.
- **Server-only "bench" experience** (no client mod): the vanilla client shows the blurred faction-select screen
  over a frozen view. The player cannot dismiss it and cannot fly; clicking a faction re-joins (and ends spectating).
  Team chat with other team-less players works. That is a bench, not a spectator camera
  ([docs/API_NOTES.md](docs/API_NOTES.md) §3).
- Not yet verified in a live round with players; see the checklist.
