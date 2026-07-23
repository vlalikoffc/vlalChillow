# Allies / Ranked2v2 probe — Sandstone 2x2

**Source:** ConnectAsClient `allies-probe` as Tr (`--team tr`) against phone host  
**Sessions:** `run-20260723_215727` (lobby) / `run-20260723_2206…` (match)  
**Captures:** `server/bin/Release/net8.0/captures/20260723_2157*` / `2206*`  
**Decode tool:** `server/tools/decode_allies_probe.py` (SetProperties C2 + stime deltas)

**Live client correction (2.06 OBT F1):** phone gold split C2=22 (anchor) + C2=31 (buy deadline),
but the dedicated client treats **C2=22 as the buy/purchase phase** with a visible countdown.
Dedicated host merges buy onto **C2=22 only** (no C2=31), skips C2=11 freeforall, and sends
**C2=101 Live + Time** for a **90s** round clock.

## Lobby / match selection

| Key | Value |
|-----|-------|
| GameModeId | `Ranked2v2` |
| SelectedLevels | `Sandstone 2x2` |
| Match C0 | `Ranked2v2` |
| Match C1 | `Sandstone 2x2` |

## Win condition

- **First to 8 round wins** — not MR-8 cap, not first-to-5.
- Default dedicated: `WinsNeeded=8` (`/set wins N`).
- Half-time after **round 7** — match continues until first-to-8.

## Gold C2 + stime timeline (RX `407424176+`) — phone host

Deltas from decoded `SetProperties` room bags (`match_rx*` captures).

| stime | Δ ms | C2 | Phase | Notes |
|-------|------|-----|-------|-------|
| 407424176 | — | **10** | WaitingPlayers | both teams joined |
| 407432220 | 8044 | **11** | PreWarmup / freeforall | ~**8s** — **skipped on dedicated** |
| 407472265 | 40045 | **21** | WarmUp | first round only |
| 407475405 | 3140 | **22** | PreStart R1 | bomberId, ReCreate 4/5/6/8, money=800 |
| 407485574 | 10169 | **31** | Prep | Ct/Tr_RoundStartPlayersCount — **not used on dedicated** |
| *(no bag)* | ~14324 | *(Live)* | Combat | phone: silent Live after C2=31 deadline |
| 407499898 | — | **40** | BombPlanted | manual plant field=1/2 |
| 407522337 | 22439 | **101** | Round end | WinTeam + scores |
| … | … | **22→(31)→101** | Rounds 2–6 | phone loop |
| 407884809 | — | **101** | R7 end | score 4:3 Tr leading |
| 407890833 | 6024 | **111** | Half-time intro | |
| 407895913 | 5080 | **112** | swapped_team | team flip + score perspective |
| 407896999 | 1086 | **113** | Half-time transition | |
| 407904034 | 7035 | **22** | PreStart R8 | new T-side bomberId |

### Dedicated phase map (corrected for live client)

| Step | Wire C2 | Host phase | Time on wire | Client UI |
|------|---------|------------|--------------|-----------|
| `/set start` (R1) | **21** | Warmup | anchor | warm-up ~3s |
| Every round buy | **22** | PurchasePhase | **deadline** = now + prep | buy/spawn countdown |
| Live | **101** | RoundLive | **deadline** = now + 90s | 1:30 round clock |
| Manual plant | **40** | BombPlanted | fuse deadline | bomb + fuse all peers |
| Round end | **101** | RoundEndPause | anchor + WinTeam | round-end UI |
| After R7 | **111→112→113** | HalfTime* | anchor each | side swap |

**Not sent:** C2=11 (freeforall), C2=31 (phone split prep — client uses C2=22 for buy).

### Dedicated timer constants (`AlliesFlowParams`)

| Constant | Seconds | Evidence / setting |
|----------|---------|-------------------|
| WarmUp C2=21 | 3 | gold RX 21→22 ≈3140 ms; R1 only |
| Prep C2=22 | 10 | `/set prep`; buy countdown on C2=22 |
| RoundDuration Live | **90** | `/set roundtime`; C2=101 + Time deadline |
| RoundEndPause | 6 | gold RX 101→22 ≈6.0 s |
| HalfTimeIntro 111 | 5 | gold RX 111→112 ≈5080 ms |
| HalfTimeSwap 112 | 1 | gold RX 112→113 ≈1086 ms |
| HalfTimeTransition 113 | 7 | gold RX 113→22 ≈7035 ms |
| BombFuse | 40 | C2=40 + Time fuse deadline; BombManager Rpc fan-out |

## `Time` field semantics (dedicated host)

Units: **bfqt seconds** (`Environment.TickCount / 1000.0`).

| Phase | C2 | `Time` on wire | Client-visible countdown? |
|-------|-----|----------------|----------------------------|
| WarmUp | 21 | anchor = nowSec | no (~3s internal, R1 only) |
| Prep/buy | 22 | **deadline** = nowSec + prep | **yes** — buy timer |
| Live | 101 | **deadline** = nowSec + 90s | **yes** — 1:30 round clock |
| BombPlanted | 40 | **deadline** = nowSec + fuse | fuse for all peers |
| Round end | 101 | anchor = nowSec | round-end UI (WinTeam bag) |
| Half-time | 111/112/113 | anchor = nowSec each | no |

Gold phone host used C2=22 anchor + separate C2=31 deadline — live client 2.06 maps buy to C2=22
with countdown; prior dedicated doc mislabeled C2=22 as «PreStart» and C2=31 as buy.

## Phase sequence (dedicated host)

```
C2=10 → C2=21 (~3s, R1 only) → C2=22 (~10s buy) → C2=101 Live (90s) →
(manual plant C2=40 + BombManager Rpc?) → C2=101 round end (~6s pause) → C2=22 …
After R7: C2=101 → C2=111 → team flip → C2=112 → C2=113 → C2=22 R8 …
```

| C2 | len≈ | Key order (room SetProperties) | Side TX |
|----|------|----------------------------------|---------|
| 10 | 14 | `C2` | bootstrap |
| 21 | 28 | `Time`, `C2` | anchor; R1 only |
| 22 | 90+ | `Time`, `Round`, `RoundStartTime`, `bomberId`, `Ct/Tr_RoundStartPlayersCount`, `C2` | ReCreate 4/5/6/8 + money=800 |
| 101 | — | `Time`, `Round`, `RoundStartTime`, `RoundCount`, `C2` | Live 90s round clock |
| 40 | 28+ | `C2`, `Time`, `RoundStartTime` | manual plant + fuse; host BombManager Rpc fan-out |
| 101 | 151 | `Time`, scores, `WinTeam`, `C2` | round end (WinTeam bag) |
| 111–113 | … | half-time bags | side swap after R7 |

**Ranked poison (must not inherit blindly):** generic Ranked C2=31 prep-only path, C2=11 freeforall,
PreStart anchor on C2=22 without buy deadline, silent Live without round clock. Allies uses
`GameMatchHost.Allies.cs` only.

## Bomb plant sync

Manual carry plant (field=1/2 during Live):

1. Host accepts plant Rpc during RoundLive only.
2. Host TX **C2=40 + Time fuse deadline + RoundStartTime** to all peers.
3. Host **fan-out BombManager plant Rpc** (`BroadcastInitReady`) with planter payload — peers
   need the Rpc for bomb mesh/fuse, not only C2=40 (planter had local apply; others did not).

Escalation auto-plant (field=3) uses the same fan-out pattern; Allies reuses it for manual plants.

## Round end bag (C2=101)

Phone Allies round end uses **C2=101** + nested `WinTeam` — **not** C2=111.

Typical keys (len≈151):

1. `Time`
2. Winner flat score (`TrScore` or `CtScore` first)
3. Loser `CoLosses`
4. Winner `CoLosses=0`
5. `WinTeam` `{ team, mvpPlayer, mvpCode, resultRoundType=0, resultRoundActor=0 }`
6. `C2=101`

## Half-time team swap (after round 7)

Gold: **111 → (SetProperty team) → 112 → 113 → 22**

Server-authoritative:

1. C2=111 pause (~5s)
2. **Force SetProperty `team`** on every Tr/Ct actor (Tr↔Ct) + money=800
3. C2=112: `swapped_team=true`, flip `TrScore`↔`CtScore`, reset CoLosses
4. C2=113 (~7s) → Prep C2=22 round 8

## Implementation

- `GameMatchHost.Allies.cs` — sole Allies FSM: WarmUp→Prep C2=22→Live C2=101→plant→round-end
- `GameMatchHost.Ranked2v2HalfTime.cs` — half-time 111/112/113 + forced team swap
- `GameMatchHost.Ranked2v2RoundEnd.cs` — shared round-end; Allies bag via `BuildAlliesRoundEndRoomProps`
- `GameMatchHost.WorldObjects.cs` — plant observe + host BombManager fan-out (no peer-only relay)
- `AlliesFlowParams` in `MatchWorld.cs` — WarmUp/Prep/RoundDuration/BombFuse timers
