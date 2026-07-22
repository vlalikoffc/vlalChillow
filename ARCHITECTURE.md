# StandChillow LAN — project architecture

Goal: **no multi-thousand-line god-files**, clear homes for lobby / discovery / match / modes, so ~15 agents can work in parallel without stepping on each other.

## Top-level layout

| Path | Owns | Notes |
|------|------|--------|
| `server/` | Dedicated LAN server (C#) | Only place that binds UDP / answers clients |
| `server/Lan/` | Discovery (Bonjour V2 / UDP 5056) | Stay out of match host |
| `server/Net/Lobby/` | Lobby LiteNetLib + codecs | Stay out of match host |
| `server/Net/Match/` | Match codec, world constants, flow rules, **modes** | |
| `server/Net/Match/Host/` | Match host partials (bootstrap, world relay, broadcast) | Split from `GameMatchHost` |
| `server/Net/Match/Kernel/` | Shared clock / future phase machine | Modes call kernel; modes never call modes |
| `server/Net/Match/Modes/<GameModeId>/` | **One folder per mode** | Agent ownership boundary |
| `server/Net/GameMatchHost.cs` | Thin orchestrator (~350 lines) | Partials elsewhere |
| `server/Net/GameMatchClient.cs` | ConnectAsClient learning | Do not paste captures into host TX |
| `decompiled/` | RE dumps, protocol docs, agent maps | Not compiled into server |
| `decompiled/tools/` | RE tooling (Ghidra etc.) | **RE agent owns** — do not fight installs |
| `server/**/captures/`, `logs/` | Runtime captures / run logs | Evidence only; never memcpy into replies |

## Match host split (was 2568-line god-file)

`GameMatchHost` is a **partial class**:

| File | Role |
|------|------|
| `server/Net/GameMatchHost.cs` | Bind, poll, opcode dispatch, rate-limit |
| `Host/MatchRoom.cs`, `MatchPeerState.cs`, `MatchLivingPawn.cs` | Room / peer types |
| `Host/GameMatchHost.HandshakeJoin.cs` | Handshake + JoinRoom Found |
| `Host/GameMatchHost.Bootstrap.cs` | Joiner gate + managers + C2=10 + snapshots |
| `Host/GameMatchHost.Properties.cs` | SetProperty / SetProperties |
| `Host/GameMatchHost.WorldObjects.cs` | CWO / Destroy / Rpc / State (drop `exceptSender` lives here) |
| `Host/GameMatchHost.Broadcast.cs` | Broadcast / stime / Send / DumpCapture |
| `Modes/Ranked2v2/GameMatchHost.Ranked2v2*.cs` | Allies phase + RoundEnd gold path |

**Target size:** hundreds of lines per file, not thousands.

## Modes

Nine `GameModeId`s (probe `run-20260722_213859`) each have `server/Net/Match/Modes/<Id>/`:

- `Ranked2v2` — implemented (current LAN default / Allies)
- Others — stub `*Mode.cs` that log unimplemented and **refuse to drive unknown wire**

Registry: `Modes/MatchModeRegistry.cs`. Interface: `IMatchMode` — modes must not cross-import.

Bomb-family (similar phase skeleton later): RankedDefuse, Ranked2v2, Ranked2v2Alt, Defuse, Escalation — share **kernel interfaces**, separate implementations.

## Protocol fidelity

- Do not invent opcodes, fields, auth strings, or discovery packets
- Do not paste capture blobs into dedicated host replies
- Evidence → decode → codec builders
- Ranked2v2 gold RoundEnd (`C2=101` + WinTeam) and WeaponDrop `exceptSender` must not regress

## Docs map

| Doc | Purpose |
|-----|---------|
| This file | Repo layout + conventions |
| `decompiled/AGENT_SWARM.md` | 15-agent ownership map |
| `decompiled/GAME_MODES.md` | Mode × map catalog |
| `decompiled/MATCH_WORLD.md` | Wire / init / RoundEnd evidence (behavioral) |
| `decompiled/LAN_DISCOVERY_PROTOCOL.md` | UDP 5056 |
| `decompiled/LAN_LOBBY_PROTOCOL.md` | Lobby |

## Convention: where does new code go?

1. New mode win rule / phase → `Modes/<ModeId>/` only  
2. Shared timer/deadline ownership → `Kernel/`  
3. New match opcode handler → `Host/` partial (not lobby/discovery)  
4. New lobby op → `Net/Lobby/`  
5. Discovery change → `Lan/`  
6. Decompile / Ghidra → `decompiled/` (+ tools tree for RE agent)
