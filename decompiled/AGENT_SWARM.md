# Agent swarm map (≈15 agents)

Parallel work requires **hard folder ownership**. Do not edit outside your zone unless coordinating a shared kernel change.

## Roles

| # | Agent | Owns (edit freely) | Hands off |
|---|--------|--------------------|-----------|
| 1 | **arch** | `ARCHITECTURE.md`, this file, folder scaffolds, cross-cutting conventions | Mode implementations, Ghidra install |
| 2 | **lobby** | `server/Net/Lobby/**`, `GameNetHost.cs` lobby path | Match host / modes |
| 3 | **discovery** | `server/Lan/**`, discovery docs | Match / lobby codecs |
| 4 | **match-host-core** | `server/Net/GameMatchHost.cs`, `Match/Host/**` | Mode-specific RoundEnd / win rules |
| 5 | **codec** | `Match/MatchCodec.cs`, envelope parsers/builders | Inventing fields |
| 6 | **world-relay** | WorldObject CWO/Destroy/Rpc/State in Host partials | Mode FSM |
| 7 | **connectasclient** | `GameMatchClient.cs`, probe scripts / learn mode | Host TX path (no capture paste) |
| 8 | **mode-ranked2v2** | `Match/Modes/Ranked2v2/**` | Other Modes/* |
| 9 | **mode-rankeddefuse** | `Match/Modes/RankedDefuse/**` | Other Modes/* |
| 10 | **mode-ranked2v2alt** | `Match/Modes/Ranked2v2Alt/**` | Other Modes/* |
| 11 | **mode-defuse** | `Match/Modes/Defuse/**` | Other Modes/* |
| 12 | **mode-escalation** | `Match/Modes/Escalation/**` | Other Modes/* |
| 13 | **mode-deathmatch** | `Match/Modes/DeathMatch/**` | Other Modes/* |
| 14 | **mode-armsrace / ffa / duel** | `Modes/ArmsRace`, `FreeForAll`, `Duel` (can split to 3 later) | Bomb-family modes |
| 15 | **re-tooling** | `decompiled/tools/**`, Ghidra/MCP, Il2Cpp dumps | `server/` runtime behavior |

Optional specialists (spin from 15 when needed):

- **probe-miner** — gold log mining → evidence comments only  
- **timer-kernel** — `Match/Kernel/**` clocks/phases (no invented wire durations)  
- **docs-protocol** — `decompiled/LAN_*.md`, `MATCH_WORLD.md` evidence sections  

## Mode agents (rules)

1. Only edit your `Modes/<Id>/` tree (+ evidence notes you own).  
2. Shared needs go through **kernel** or **match-host-core** — open a clear interface, do not copy-paste sibling mode code.  
3. Stub modes: compile, log unimplemented, **do not TX invented C2/Time/WinTeam**.  
4. Ranked2v2 agent: protect gold RoundEnd + drop `exceptSender` regressions.

## RE agent boundary

Sibling RE agent owns Ghidra / MCP / `decompiled/tools/ghidra*`. Architecture and mode agents write **docs under `decompiled/`** for ownership maps; they do **not** install or reconfigure Ghidra.

## Build gate

Any agent changing `server/`: `dotnet build -c Release` must stay green.
