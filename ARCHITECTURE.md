# StandChillow LAN — project architecture

Goal: **no multi-thousand-line god-files**, clear homes for lobby / discovery / match / modes, so ~15 agents can work in parallel without stepping on each other.

## Top-level layout

| Path | Owns | Notes |
|------|------|--------|
| `server/` | Dedicated LAN server (C#) | Only place that binds UDP / answers clients |
| `server/Startup/` | Dedicated-host and ConnectAsClient startup paths | Entry-point orchestration only |
| `server/Dashboard/` | CLI rendering and console command routing | Shared game commands stay in `GameNetHost` |
| `server/Diagnostics/` | Verbose switch + async capture sink | **Never** do blocking disk I/O from a network hot path |
| `server/Bans/` | Ban registry + `banlist.json` store + deterministic nickname→id + duration/message parsing | Shared by lobby **and** match; no sockets, no wire |
| `tests/` | Socket-free protocol regression tests | xUnit; run in CI on all desktop OSes |
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

## Bans (shared layer)

`server/Bans/` owns ban records (nick + IPs + deterministic public id), the seen-player
directory (`nick ↔ id ↔ IP`) and their text. Lobby and match only **ask** it at three entry
points — never on the packet path:

| Gate | Where | Keys |
|------|-------|------|
| Lobby join / lobby reconnect | `GameNetHost.HandleJoin` | nick + IP |
| Match connect | `GameMatchHost.ConnectionRequestEvent` | IP only (no nick yet) |
| Match join / mid-match reconnect | `GameMatchHost.HandleJoinRoom` (before `TryFindRejoinActor`) | nick + IP |

The last gate matters: a mid-match reconnect adopts the old actor and never runs a normal
join, so a ban checked only at the first connect could be bypassed (e.g. leave VPN →
reconnect from the banned address). Admins are exempt by nickname at every gate; banning
an admin is rejected. The install token is deliberately **not** used — nick + IP only.
Ban keys are never the public id: `/banid` is a selector that resolves to nick + IPs.

`MatchPublicId` derives a stable `1…100 000 000` id from the nickname (SHA-256 of the
normalized nick); it carries no state, so `/banid` survives restarts. The host also
substitutes it for the client-authored actor property `uid` (players send the `Offline`
placeholder from the gold capture) so the in-game player list shows the same number
`/ids` and `/banid` use; the Server actor carries its own id too. The server nickname is
reserved (lobby and match reject it, derived from `LobbySession.ServerPlayerName` so a rename
moves the reservation), the server is never bannable, and its id is never written as a ban
tag. `PlayerDirectory` persists `nick ↔ id ↔ IP` in `banlist.json` so `/banid` still
resolves a player who already left. `banlist.json` lives beside `config.json` and is
git-ignored.

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

## Performance rules (hot paths)

These are the invariants that keep a 10+ player room smooth; break them and the host melts:

1. **No blocking I/O inside packet handlers.** No `File.*`, no `Console.WriteLine` on the
   per-packet path (it is a synchronous write to terminal + 2 logs). Use
   `Diagnostics.LogVerbose` or per-event logs only. Diagnostics go through
   `CaptureSink` / `RunLog`, which are async + bounded.
2. **Never let one peer's queue grow without bound.** All sends go through
   `GameMatchHost.Send*`, which checks `GetPacketsCountInReliableQueue` and drops game ticks
   for slow peers. Don't call `peer.Send` directly.
3. **A broadcast is a snapshot loop.** Iterate `_peers` once per payload; do not nest
   per-recipient loops inside per-payload loops.
4. **Health line is the first place to look:** `[health] peers=… reliableQueue=… slowPeers=…
   droppedSends=… rttMax=… captures=…` every 10 s.

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
