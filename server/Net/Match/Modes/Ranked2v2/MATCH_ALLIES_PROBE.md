# Allies / Ranked2v2 probe — Sandstone 2x2

**Source:** ConnectAsClient `allies-probe` as Tr (`--team tr`) against phone host  
**Sessions:** `run-20260723_215727` (lobby) / `run-20260723_2206…` (match)  
**Captures:** `server/bin/Release/net8.0/captures/20260723_2157*` / `2206*`  
**Decode tool:** `server/tools/decode_allies_probe.py` (SetProperties C2 + stime deltas)

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

## Gold C2 + stime timeline (RX `407424176+`)

Deltas from decoded `SetProperties` room bags (`match_rx*` captures).

| stime | Δ ms | C2 | Phase | Notes |
|-------|------|-----|-------|-------|
| 407424176 | — | **10** | WaitingPlayers | both teams joined |
| 407432220 | 8044 | **11** | PreWarmup / freeforall | ~**8s** |
| 407472265 | 40045 | **21** | WarmUp | first round only; Δ includes phone lobby wait |
| 407475405 | 3140 | **22** | PreStart R1 | bomberId, ReCreate 4/5/6/8, money=800 |
| 407485574 | 10169 | **31** | Prep | Ct/Tr_RoundStartPlayersCount — ~**10s** after C2=22 |
| *(no bag)* | ~14324 | *(Live)* | Combat | gold: Prep `Time` deadline expires → live (no C2 TX) |
| 407499898 | — | **40** | BombPlanted | manual plant field=1/2 (not Escalation field=3); bag has **no Time** |
| 407522337 | 22439 | **101** | Round end | WinTeam + TrScore/CtScore + CoLosses |
| 407528375 | 6038 | **22** | PreStart R2 | skip WarmUp — ~**6s** after round-end bag |
| 407538367 | 9992 | **31** | Prep | 22→31 ≈**10s** every round |
| … | … | **22→31→(silent Live)→101** | Rounds 2–6 | same loop; no fixed round clock |
| 407884809 | — | **101** | R7 end | score 4:3 Tr leading |
| 407890833 | 6024 | **111** | Half-time intro | ~**5s** (111→112 stime) |
| 407895913 | 5080 | **112** | swapped_team | server SetProperty team flip + score perspective |
| 407896999 | 1086 | **113** | Half-time transition | ~**1s** |
| 407904034 | 7035 | **22** | PreStart R8 | new T-side bomberId — ~**7s** after 113 |
| 407914208 | 10174 | **31** | Prep | Ct=2 Tr=1 after swap |
| … | … | **101** | R8+ | until first-to-8 |

### Dedicated timer constants (`AlliesFlowParams`)

| Constant | Seconds | Evidence |
|----------|---------|----------|
| PreWarmup C2=11 | 8 | RX 10→11 ≈8044 ms |
| WarmUp C2=21 | 3 | RX 21→22 ≈3140 ms |
| PreStart C2=22 | **10** | RX 22→31 ≈10.0 s (NOT generic 3s PreStart) |
| Prep C2=31 | 10 | RX 22→31 stime ≈10 s; only phase with wire countdown `Time` |
| RoundEndPause | 6 | RX 101→22 ≈6.0 s |
| HalfTimeIntro 111 | 5 | RX 111→112 ≈5080 ms |
| HalfTimeSwap 112 | 1 | RX 112→113 ≈1086 ms |
| HalfTimeTransition 113 | 7 | RX 113→22 ≈7035 ms |
| BombFuse | 40 | family default after C2=40 |

**No fixed Live round clock** — round ends on wipe / manual plant / defuse / explode only.

## `Time` field semantics (decoded gold RX bags)

Units: **bfqt seconds** (`Environment.TickCount / 1000.0`), same as dedicated `ServerTimeSeconds()`.

| Phase | C2 | `Time` on wire | `RoundStartTime` | Client-visible countdown? |
|-------|-----|----------------|------------------|---------------------------|
| PreWarmup | 11 | **anchor** = nowSec | — | no (host waits ~8s internally) |
| WarmUp | 21 | **anchor** = nowSec | — | no (~3s internal, R1 only) |
| PreStart | 22 | **anchor** = nowSec | **same as Time** | no (~10s internal before Prep) |
| Prep | 31 | **deadline** = nowSec + ~10s | — | **yes** — buy/spawn countdown |
| Live | — | *(no bag)* | — | no round clock |
| BombPlanted | 40 | *(no Time key)* | — | fuse from plant Rpc / client |
| Round end | 101 | **anchor** = nowSec | — | round-end UI (WinTeam bag) |
| Half-time | 111/112/113 | **anchor** = nowSec each | — | no (host waits 5s/1s/7s) |

Gold examples (R1):

- C2=22 @ stime 407475405: `Time=407475.376`, `RoundStartTime=407475.376` (equal anchors)
- C2=31 @ stime 407485574: `Time=407485.565` (= PreStart anchor + **10.189s** deadline)
- Next combat: **no** SetProperties until plant C2=40 or round-end C2=101

Dedicated bug (fixed): sending PreStart/WarmUp `Time` as deadline + a fake Live C2=101 bag stacked
“starting match” UI on top of the Prep countdown (`RoundStartTime` from PreStart minus Live `Time` ≈ 20s phantom timer).

## Phase sequence (dedicated host)

```
C2=10 → C2=11 (~8s) → C2=21 (~3s, R1 only) → C2=22 (~10s) → C2=31 (~10s prep countdown) →
(silent Live) → (manual plant C2=40?) → C2=101 round end + WinTeam (~6s pause) → C2=22 …
After R7: C2=101 → C2=111 (~5s) → team flip → C2=112 (~1s) → C2=113 (~7s) → C2=22 R8 …
```

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
4. C2=113 (~7s) → PreStart round 8

## Escalation divergence

| | Allies / Ranked2v2 | Escalation |
|--|-------------------|------------|
| FSM file | `GameMatchHost.Allies.cs` | `GameMatchHost.Escalation.cs` |
| PreStart C2=22 | ~10s + bomberId | ~8s, no bomberId, BombSite |
| Plant | Manual carry field=1/2 during Live | Auto-plant field=3 |
| Live | silent after Prep deadline; no round clock | C2=31 combat after auto-plant |
| Round end | C2=101 + WinTeam (only C2=101 use) | C2=101 + WinTeam |
| Half-time | 111→112→113 after R7 | (not in Escalation probe) |
| Win condition | First to 8 | MR-N via `/set round` |

## Implementation

- `GameMatchHost.Allies.cs` — dedicated FSM (Escalation-style branch)
- `GameMatchHost.Ranked2v2HalfTime.cs` — half-time 111/112/113 + forced team swap
- `AlliesFlowParams` in `MatchWorld.cs` — probe-verified timers
