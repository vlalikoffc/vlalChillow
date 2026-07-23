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

## Full gold TX table (decoded captures — every room C2 bag)

Run: `python3 server/tools/decode_allies_probe.py server/bin/Release/net8.0/captures`

Probe joined mid-match (R3 plant visible first); R1 open sequence (10→11→21→22→31) from prior decode of same session family in §Gold C2 summary below.

| stime | Δ ms | C2 | Round | Time | RST | ΔTime-prev | len | keys |
|-------|------|-----|-------|------|-----|------------|-----|------|
| 407424176 | — | 10 | | | | | 14 | C2 |
| 407572190 | 148014 | 40 | | | | | 14 | C2 |
| 407612011 | 39821 | 101 | | 407612.005 | | | 151 | Time,TrScore,CtCoLosses,TrCoLosses,WinTeam,C2 |
| 407618048 | 6037 | 22 | 3 | 407618.021 | 407618.021 | 6.016 | 77 | Time,Round,RoundStartTime,bomberId,C2 |
| 407628049 | 10001 | 31 | | 407628.037 | | **10.016** | 90 | Time,Ct_RoundStartPlayersCount,Tr_RoundStartPlayersCount,C2 |
| 407640296 | 12247 | 101 | | 407640.278 | | 12.241 | 151 | Time,TrScore,CtCoLosses,TrCoLosses,WinTeam,C2 |
| 407646342 | 6046 | 22 | 4 | 407646.310 | 407646.310 | 6.032 | 77 | Time,Round,RoundStartTime,bomberId,C2 |
| 407656509 | 10167 | 31 | | 407656.497 | | **10.187** | 90 | Time,Ct_RoundStartPlayersCount,Tr_RoundStartPlayersCount,C2 |
| 407670001 | 13492 | 101 | | 407669.991 | | 13.494 | 151 | Time,TrScore,CtCoLosses,TrCoLosses,WinTeam,C2 |
| 407676039 | 6038 | 22 | 5 | 407676.013 | 407676.013 | 6.022 | 77 | Time,Round,RoundStartTime,bomberId,C2 |
| 407686221 | 10182 | 31 | | 407686.211 | | **10.198** | 90 | Time,Ct_RoundStartPlayersCount,Tr_RoundStartPlayersCount,C2 |
| 407796590 | 110369 | 101 | | 407796.579 | | 110.368 | 151 | Time,CtScore,TrCoLosses,CtCoLosses,WinTeam,C2 |
| 407802629 | 6039 | 22 | 6 | 407802.605 | 407802.605 | 6.026 | 77 | Time,Round,RoundStartTime,bomberId,C2 |
| 407812629 | 10000 | 31 | | 407812.617 | | **10.012** | 90 | Time,Ct_RoundStartPlayersCount,Tr_RoundStartPlayersCount,C2 |
| 407830272 | 17643 | 40 | | | | | 14 | C2 |
| 407855312 | 25040 | 101 | | 407855.307 | | 42.690 | 151 | Time,CtScore,TrCoLosses,CtCoLosses,WinTeam,C2 |
| 407861352 | 6040 | 22 | 7 | 407861.314 | 407861.314 | 6.007 | 77 | Time,Round,RoundStartTime,bomberId,C2 |
| 407871523 | 10171 | 31 | | 407871.514 | | **10.200** | 90 | Time,Ct_RoundStartPlayersCount,Tr_RoundStartPlayersCount,C2 |
| 407884809 | 13286 | 101 | | 407884.794 | | 13.280 | 151 | Time,TrScore,CtCoLosses,TrCoLosses,WinTeam,C2 |
| 407890833 | 6024 | 111 | | 407890.818 | | 6.024 | 28 | Time,C2 |
| 407895913 | 5080 | 112 | | 407895.898 | | 5.080 | 101 | Time,CtScore,TrScore,swapped_team,CtCoLosses,TrCoLosses,C2 |
| 407896999 | 1086 | 113 | | 407896.982 | | 1.084 | 28 | Time,C2 |
| 407904034 | 7035 | 22 | 8 | 407904.003 | 407904.003 | 7.021 | 77 | Time,Round,RoundStartTime,bomberId,C2 |
| 407914208 | 10174 | 31 | | 407914.198 | | **10.195** | 90 | Time,Ct_RoundStartPlayersCount,Tr_RoundStartPlayersCount,C2 |
| 407936831 | 22623 | 101 | | 407936.818 | | 22.620 | 151 | Time,CtScore,TrCoLosses,CtCoLosses,WinTeam,C2 |
| 407942877 | 6046 | 22 | 9 | 407942.839 | 407942.839 | 6.021 | 77 | Time,Round,RoundStartTime,bomberId,C2 |

## Dedicated override (buy on C2=22 — post-`7af999c`)

Phone gold: C2=22 **anchor**, C2=31 **deadline** (~10s buy). Dedicated user override:

| Step | Wire | Host |
|------|------|------|
| **C2=22** | `Time` = **buy deadline** (`now+prep`), `RoundStartTime` = now; ReCreate, bomberId, Round, money(R1) | Wait buy duration (`/set prep`, default 10s). CLI: **PREP · C2=22** |
| **C2=31** | Roster keys + **anchor** `Time` (no new deadline) | Background bag then **immediate** silent Live — **no** second buy countdown |
| **Live** | no bag | Host round timeout → CT; plant **RoundLive only** |
| **Economy** | — | CS-style round/kill/plant/defuse payouts (`MatchEconomy`, cap $10k); CoLosses drive loss streak |

Do **not** stack deadline on both 22 and 31 (~19s bug from `9e4d4c2`/`bc21cb6`). Buy lives on **22 only**; 31 is post-buy anchor.

## Patterns (gold phone wire, not dedicated)

- **C2=22 (gold):** `Time == RoundStartTime` (anchor); host waits ~10s (stime 22→31 ≈ 10000–10182 ms) — **no wire countdown on phone**
- **C2=31 (gold):** `Time` deadline only; `ΔTime-prev` from C2=22 anchor ≈ **10.0 s** — phone’s visible buy timer
- **Live:** no SetProperties between C2=31 deadline and C2=40 or C2=101
- **C2=40:** len=14, `C2` only — no `Time` / `RoundStartTime`
- **C2=101 round end:** len=151, `Time` anchor + scores + `WinTeam`; **never** sent as Live round clock
- **101→22:** stime Δ ≈ **6037–6046 ms** (round-end pause)

## User vs gold (dedicated host)

| Dedicated choice | Gold wire | Why |
|------------------|-----------|-----|
| Skip C2=11 freeforall | Gold sends C2=11 ~8s after C2=10 | OK — `/set start` → C2=21 WarmUp (R1) |
| C2=22 PreStart **anchor** (`Time == RoundStartTime`) | Same | **Never** put buy deadline on C2=22 |
| C2=31 Prep **deadline** ~10s | Same — only visible buy timer | Keep every round |
| Silent Live (no C2 bag, no 90s clock) | Same — no Live SetProperties | Round ends wipe/plant/defuse/explode only |
| C2=40 C2-only + BombManager Rpc fan-out | Gold C2=40 len=14 + host WorldObjectRpc | All peers must see bomb |
| C2=101 + WinTeam round end only | Same | **Never** TX C2=101 Live without WinTeam |

**Rejected inventions** (`9e4d4c2`, `bc21cb6`): C2=22 deadline, drop C2=31, C2=101 Live + 90s round clock. See §Regression below.

## Regression: `9e4d4c2` / `bc21cb6` (~19 s prep, delayed round score)

Both invents put a **deadline** on C2=22 and/or TX **C2=101 Live** without WinTeam.

**Why client showed ~19 s prep:**

1. C2=22 carried a **10 s buy deadline** (invented — gold uses **anchor** `Time == RoundStartTime` on 22).
2. Host still ran a second ~10 s window (C2=31 Prep, or leftover PreStart `PhaseEndsUtc`) → **~10 + ~10 ≈ 19–20 s** stacked countdown.
3. C2=101 Live (`Time=now+90`, no WinTeam) delayed / broke round-end scoring UI — C2=101 is **WinTeam round end only**.

**Restored dedicated FSM:** `/set start` → skip C2=11 → C2=21 (R1) → C2=22 PreStart **anchor** → C2=31 Prep **deadline** ~10s only → silent Live → C2=40 (C2 only) + BombManager Rpc fan-out → C2=101 WinTeam.

## Gold C2 + stime timeline (RX `407424176+` — summary incl. R1)

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

| Constant | Seconds | Wire / host |
|----------|---------|-------------|
| PreWarmup C2=11 | 8 | Phone gold only — **dedicated skips** (no TX) |
| WarmUp C2=21 | 3 | R1 only after `/set start` |
| PreStart C2=22 | **10** | Host wait; wire `Time` = **anchor** (== `RoundStartTime`) — **never deadline** |
| Prep C2=31 | 10 | **Only** wire countdown `Time` deadline |
| RoundEndPause | 6 | After C2=101 WinTeam |
| HalfTimeIntro 111 | 5 | Unchanged |
| HalfTimeSwap 112 | 1 | Unchanged |
| HalfTimeTransition 113 | 7 | Unchanged |
| BombFuse | 40 | Host-side after C2=40 |

**No fixed Live round clock** — round ends on wipe / manual plant / defuse / explode only. `/set roundtime` does not invent a Live C2=101 bag.

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
**`9e4d4c2` and `bc21cb6` reintroduced this** (C2=22 deadline and/or C2=101 Live without WinTeam) — both reverted.

## Bomb plant sync (gold evidence)

Gold C2=40 bag: **len=14, `C2` only** (no fuse `Time` on wire) — stime 407572190 / 407830272.

Within **±650 ms** of C2=40, probe RX **WorldObjectRpc** from phone host to all peers:

| stime (Δ from C2=40) | len | capture |
|----------------------|-----|---------|
| 407572008 (−182 ms) | 41 | `…7920_WorldObjectRpc_len41.bin` (planter pose) |
| 407572008 (−182 ms) | 25 | `…7921_WorldObjectRpc_len25.bin` |
| 407572058 (−132 ms) | 25 | `…7926_WorldObjectRpc_len25.bin` |
| 407830123 (−149 ms) | 41 | `…19580_WorldObjectRpc_len41.bin` |
| 407830123 (−149 ms) | 25 | `…19581_WorldObjectRpc_len25.bin` |

Dedicated host: on planter Rpc during `RoundLive`, TX **C2=40 only** + **`BroadcastInitReady` BombManager plant Rpc** with planter payload (Escalation-style fan-out). Peer-only relay is dropped for Allies — host is authoritative. Fuse tracked server-side via `BombPlantedUtc` (~40s); no invented C2=40 `Time` (gold had none).

## Phase map (phone gold ↔ dedicated host ↔ UI)

Every inter-round loop (R2+): **C2=22 → C2=31 → silent Live → C2=101** — never skip C2=22.
Phone R1: C2=11 (~8s) then C2=21 (~3s). **Dedicated R1:** skip C2=11 → C2=21 only.

| Phone C2 | Dedicated `MatchFlowPhase` | Wire `Time` | Server CLI / console | Client UI (2.06 OBT F1) |
|----------|---------------------------|-------------|----------------------|-------------------------|
| 10 | WaitingPlayers | — | WAITING PLAYERS | lobby / waiting |
| 11 | *(skipped)* | — | — | phone freeforall only; dedicated does not TX |
| 21 | Warmup | anchor | WARM-UP · Round 1 | warm-up (R1 only) |
| **22** | WarmupWillFinish | **deadline** = now+prep (buy) | **PREP · Round N · C2=22** | buy/spawn countdown (dedicated) |
| 31 | PurchasePhase | **anchor** = nowSec | POST-BUY · Round N · C2=31 | background bag → Live (no 2nd timer) |
| *(none)* | RoundLive | *(no Live Time TX)* | LIVE · Round N | combat; host timeout; **no plant in Prep/buy** |
| 40 | BombPlanted | *(no Time)* | BOMB PLANTED | fuse from plant Rpc |
| 101 | RoundEndPause | anchor | ROUND END · Round N | round-end + WinTeam bag |
| 111–113 | HalfTime* | anchor each | HALF-TIME · … | side swap after R7 |

**Operator note:** CLI used to label C2=22 as «MATCH STARTING» — that was misleading.
Gold sends C2=22 before **every** round’s prep; it is per-round PreStart, not a match-open banner.

### In-round 2:00 → freeze (dedicated bug history)

| Cause | Evidence | Fix |
|-------|----------|-----|
| Ranked `EnterRoundLive` TX **C2=101 + `Time`=now+RoundDuration** | Generic Ranked path; client shows mode round clock (~90–120s) | Allies hard-branched: `EnterAlliesLive` — **no** room bag |
| PreStart/WarmUp **`Time`=deadline** stacked on Prep | Phantom «starting match» + prep timers | Allies: anchor `Time` on C2=11/21/22; **only** C2=31 sends deadline |
| Stale Prep `Time` used as Live round clock in CLI | Dashboard `TimeDeadline` fallback on RoundLive | CLI: no clock on Allies RoundLive; use `PhaseEndsUtc` only |

If client still flashes ~2:00 then stalls: client Ranked2v2 mode config may default ~120s locally when prep
`Time` expires while wire C2=31 — server sends **no** Live Time updates (gold-faithful), so local countdown
freezes. Phone host behaves the same on wire; dedicated must not re-add C2=101 Live bags to «fix» it.

## Phase sequence (dedicated host)

```
/set start → skip C2=11 → C2=21 (~3s, R1 only) →
C2=22 BUY DEADLINE (~prep /set, default 10s; RoundStartTime=now) →
C2=31 post-buy ANCHOR (roster keys, no new deadline) → silent Live promptly →
(manual plant C2=40 RoundLive-only + BombManager Rpc fan-out) → C2=101 WinTeam (~6s) → C2=22 …
After R7: C2=101 → C2=111 (~5s) → team flip → C2=112 (~1s) → C2=113 (~7s) → C2=22 R8 …
Economy: round-end / kill / plant / defuse payouts (cap $10k); R1 money seed on C2=22 only.
```

Phone gold reference (not dedicated buy placement): C2=10→11→21→22(anchor)→31(deadline)→silent Live.

Dedicated host must rebuild these bags with codecs — **never replay capture blobs**.

| C2 | len≈ | Key order (room SetProperties) | Side TX |
|----|------|----------------------------------|---------|
| 10 | 14 | `C2` | bootstrap |
| 11 | 28 | `Time`, `C2` | phone gold only — dedicated skips |
| 21 | 28 | `Time`, `C2` | anchor Time; R1 only |
| 22 | 77 | `Time`, `Round`, `RoundStartTime`, `bomberId`, `C2` | ReCreate 4/5/6/8 + actor money=800; **anchor** Time |
| 31 | 90 | `Time`, `Ct_RoundStartPlayersCount`, `Tr_RoundStartPlayersCount`, `C2` | **only** deadline Time |
| 40 | 14 | `C2` | manual plant; no Time |
| 101 | 151 | `Time`, `{winner}Score`, `{loser}CoLosses`, `{winner}CoLosses`, `WinTeam`, `C2` | MVP SetProperty first; then bag |
| 111 | 28 | `Time`, `C2` | half-time intro |
| 112 | 101 | `Time`, `CtScore`, `TrScore`, `swapped_team`, `CtCoLosses`, `TrCoLosses`, `C2` | after forced team SetProperty |
| 113 | 28 | `Time`, `C2` | half-time transition |

**Live:** no room bag — Prep deadline expiry → `RoundLive` internally.

**Ranked poison (must not inherit):** generic `EnterRoundLive` C2=101 + 90s clock, PreStart `Time=deadline`, bomb plant `Time` fuse on C2=40, `ContinueAfterRoundEnd` → `EnterWarmupWillFinish` (3s PreStart). Allies uses `GameMatchHost.Allies.cs` only.

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

- `GameMatchHost.Allies.cs` — **sole** Allies FSM: `/set start`→C2=21 (skip 11)→22 anchor→31 deadline→silent Live→40+Rpc→101 WinTeam
- `GameMatchHost.WorldObjects.cs` — observe plant Rpc → `TryEnterBombPlanted`; Allies host fan-out, drop peer relay
- `GameMatchHost.Ranked2v2HalfTime.cs` — half-time 111/112/113 + forced team swap (Allies-only callers)
- `GameMatchHost.Ranked2v2RoundEnd.cs` — shared `EnterRoundEndPause` delegates bag shape to `BuildAlliesRoundEndRoomProps`; `ContinueAfterRoundEnd` redirects Allies → `ContinueAfterRoundEndAllies`
- `GameMatchHost.Ranked2v2Phases.cs` — `TryEnterBombPlanted` redirects Allies → `TryEnterAlliesBombPlanted`; generic Ranked path never runs for `Ranked2v2` C0
- `AlliesFlowParams` in `MatchWorld.cs` — probe-verified host timers (wire vs internal)

**`bc21cb6` user-override reverted** — that commit reintroduced C2=22 deadline + C2=101 Live 90s (same ~19s / round-score failure class as `9e4d4c2`).
