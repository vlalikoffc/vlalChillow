# Allies / Ranked2v2 probe — Sandstone 2x2

**Source:** ConnectAsClient `allies-probe` as Tr (`--team tr`) against phone host  
**Sessions:** `run-20260723_215727` (lobby) / `run-20260723_2206…` (match); **+ full open `run-20260724_091723` / `latest.log`**  
**Captures:** `server/bin/Release/net8.0/captures/20260723_2157*` / `2206*`; **+ `20260724_0217*` … `20260724_0229*`**  
**Decode tool:** `server/tools/decode_allies_probe.py` (SetProperties C2 + stime deltas; hard-coded patterns still 20260723 — decode 20260724 via `collect_events` + `20260724_02*` globs)

## Gold notes — 2026-07-24 full open (`20260724_0217*` / `022*`, phone Ranked2v2 / Sandstone 2x2)

Evidence-only from this ConnectAsClient run (phone host `192.168.1.73`). Do **not** invent Live C2 or round-clock bags.

### Phase sequence (R1 open → loop)

- **R1:** `10 → 11 → 21 → 22 → 31 → (no room C2 bag) → 40? → 101(+WinTeam)`
- **R2+:** `22 → 31 → (no room C2 bag) → 40? → 101(+WinTeam)`
- After R7: `101 → 111 → 112 → 113 → 22` (half-time), then same loop until first-to-8 → `201` → `255`
- **No Live `C2=101` without `WinTeam`.** Every `C2=101` bag in this run is len≈151 with `WinTeam` (round end). Combat does **not** get a separate Live C2.
- Wire room `C2` stays at **31** from Prep TX until plant `40` or round-end `101`. First R1 kill (`DestroyWorldObject` + `round_kills`) at **+12190 ms** after C2=31; R1 31→101 ≈ **110383 ms** with **zero** intervening room SetProperties.

### C2=22 bag

- Keys: `Time`, `Round`, `RoundStartTime`, `bomberId`, `C2` (len=77)
- **`Time == RoundStartTime`** every round (`ΔTime-RST = 0.000`)
- Both ≈ `stime/1000` (**fresh now**, not a future deadline)

### C2=22 → C2=31 (exact stime Δ ms / ΔTime s)

| Round | Δstime ms | ΔTime s |
|------:|----------:|--------:|
| R1 | 10170 | 10.192 |
| R2 | 10169 | 10.193 |
| R3 | 10180 | 10.204 |
| R4 | 10170 | 10.195 |
| R5 | 10166 | 10.192 |
| R6 | 10009 | 10.028 |
| R7 | 10178 | 10.197 |
| R8 | 10171 | 10.198 |
| R9 | 10176 | 10.199 |
| R10 | 10168 | 10.195 |
| R11 | 10170 | 10.192 |

≈ **10.0–10.2 s** host wait (R6 slightly short).

### C2=31 bag

- Keys: `Time`, `Ct_RoundStartPlayersCount`, `Tr_RoundStartPlayersCount`, `C2` (len=90)
- `Time` ≈ `stime/1000` (**fresh now**, **not** `now+90` / **not** a far-future deadline on wire)
- Kills / pawn destroy happen while room C2 is still **31** (see R1 +12.19 s)

### Round clock on wire

- Phone host does **not** TX a Live bag with `Time=now+90` (or any future Live clock).
- All room `Time` values in this run are ≈ now (`Time − stime/1000` within ~±40 ms).
- Round timer during combat is **absent on wire** (local / not refreshed by host SetProperties).

### Bomb plant / defuse / explode (BombManager Rpc + C2=40)

- Round-start / reset: `BombManager` **field=1** len≈48 payload≈28B (near C2=22/31)
- Plant: **field=3** len=41 payload=21B → within **~8–188 ms** room **`C2=40` len=14, keys=`C2` only** (no `Time`)
- During planted (observed):
  - **field=4** len=28 payload=8B
  - **field=5** len=24 payload=4B
  - **field=6** len=26 payload=6B
- Correlated ends (this run):
  - Explode ≈ **+40006 ms** after field=3 → field=6 + `C2=101` `WinTeam` mvpCode=`PlantingBomb` (R6)
  - Defuse path: field=4 then field=6+field=5 → `C2=101` mvpCode=`DefusingBomb` (R5, R10)
  - Plant then wipe: field=4/5 activity, **no** field=6 before `C2=101` mvpCode=`MostEliminations` (R7)

### Why dedicated bugs match this gold

| Symptom | Gold explanation |
|---------|------------------|
| Timer stuck ~1:50 | Client may show a local Ranked clock; phone sends **no** Live `Time` refresh. Inventing `C2=101` Live + `Time=now+90` is **not** phone behavior and poisons round-end (`101` = WinTeam only). |
| Plant ignored | Gold plant = **field=3 Rpc** + **C2=40 C2-only**. Missing Rpc fan-out or requiring Live/`Time` on C2=40 diverges. |
| “Unreal” Time | Phone `Time` is always **≈ now**. `Time=now+N` deadlines on 22/31/Live are invented vs this capture. |

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

## Dedicated host (gold-faithful — post-2026-07-24 rewrite)

Phone gold is the spec. Dedicated must **not** invent Live C2=101, BuyClientClockPad deadlines, or a 1s C2=31 flash.

| Step | Wire | Host |
|------|------|------|
| **C2=22** | `Time == RoundStartTime ≈ now` (anchor); ReCreate, bomberId, Round, money(R1) | Wait buy wall (`/set prep`, default **10s**). CLI: **PREP · C2=22** |
| **C2=31** | Roster keys + `Time ≈ now` | **Stay** in PurchasePhase/combat on C2=31 until plant or round end |
| **Combat** | no further room C2 until 40/101 | Host-side round timeout → CT; plant **field=3** on C2=31 |
| **C2=40** | `C2` only | Fan-out BombManager field=3 Rpc |
| **C2=101** | WinTeam bag only | Round end |
| **Economy** | — | CS-style round/kill/plant/defuse payouts (`MatchEconomy`, cap $10k); CoLosses drive loss streak |

**Rejected inventions** (checkpoint `403f7cb` / `9e4d4c2` / `bc21cb6`): C2=22 future deadline + BuyClientClockPad, BuyEndGrace, 1s PostBuyPhase C2=31 flash, Live C2=101 without WinTeam + `Time=now+90`.

## Patterns (gold phone wire)

- **C2=22:** `Time == RoundStartTime` (anchor); host waits ~10s (stime 22→31 ≈ 10000–10182 ms)
- **C2=31:** `Time ≈ now`; combat stays on 31 until plant `40` or round-end `101`
- **No Live SetProperties** between C2=31 and C2=40/101
- **C2=40:** len=14, `C2` only — no `Time` / `RoundStartTime`
- **C2=101 round end:** len=151, `Time` anchor + scores + `WinTeam`; **never** sent as Live round clock
- **101→22:** stime Δ ≈ **6037–6046 ms** (round-end pause)

## Dedicated vs gold

| Dedicated choice | Gold wire | Why |
|------------------|-----------|-----|
| Skip C2=11 freeforall | Gold sends C2=11 ~8s after C2=10 | OK — `/set start` → C2=21 WarmUp (R1) |
| C2=22 PreStart **anchor** (`Time == RoundStartTime`) | Same | **Never** put buy deadline on C2=22 |
| C2=31 combat **anchor** `Time≈now`; stay on 31 | Same | Combat / kills / plant while C2=31 |
| No Live bag / no 90s clock on wire | Same | Round ends wipe/plant/defuse/explode/host timeout |
| C2=40 C2-only + BombManager field=3 fan-out | Gold C2=40 len=14 + field=3 Rpc | All peers must see bomb |
| C2=101 + WinTeam round end only | Same | **Never** TX C2=101 Live without WinTeam |

**Rejected inventions** (`9e4d4c2`, `bc21cb6`, buy-pad checkpoint): C2=22 deadline, drop/stay-not on C2=31, C2=101 Live + 90s round clock. See §Regression below.

## Regression: `9e4d4c2` / `bc21cb6` / buy-pad checkpoint (~19 s prep, Live 101)

These invents put a **deadline** on C2=22 and/or TX **C2=101 Live** without WinTeam / flash C2=31 then Live.

**Why client showed ~19 s prep / broken round score:**

1. C2=22 carried a **padded buy deadline** (invented — gold uses **anchor** `Time == RoundStartTime` on 22).
2. Host stacked BuyEndGrace + 1s PostBuyPhase and/or Live C2=101.
3. C2=101 Live (`Time=now+90`, no WinTeam) delayed / broke round-end scoring UI — C2=101 is **WinTeam round end only**.

**Restored dedicated FSM:** `/set start` → skip C2=11 → C2=21 (R1) → C2=22 PreStart **anchor** → ~10s → C2=31 combat **stay** → field=3 plant C2=40 → C2=101 WinTeam.

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
| PreStart C2=22 | **10** (`/set prep`) | Host wait; wire `Time` = **anchor** (== `RoundStartTime`) |
| Combat C2=31 | host RoundDuration | Wire `Time` = **anchor** now; **stay** until 40/101 |
| RoundEndPause | 6 | After C2=101 WinTeam |
| HalfTimeIntro 111 | 5 | Unchanged |
| HalfTimeSwap 112 | 1 | Unchanged |
| HalfTimeTransition 113 | 7 | Unchanged |
| BombFuse | 40 | Host-side after C2=40 |

**No Live C2 bag / no wire round clock** — combat on C2=31; host may end on internal timer. `/set roundtime` does **not** invent a Live C2=101 bag.

## `Time` field semantics (decoded gold RX bags)

Units: **bfqt seconds** (`Environment.TickCount / 1000.0`), same as dedicated `ServerTimeSeconds()`.

| Phase | C2 | `Time` on wire | `RoundStartTime` | Client-visible countdown? |
|-------|-----|----------------|------------------|---------------------------|
| PreWarmup | 11 | **anchor** = nowSec | — | no (host waits ~8s internally) |
| WarmUp | 21 | **anchor** = nowSec | — | no (~3s internal, R1 only) |
| PreStart | 22 | **anchor** = nowSec | **same as Time** | host waits ~10s (no wire deadline) |
| Combat | 31 | **anchor** = nowSec | — | combat; no room C2 until 40/101 |
| BombPlanted | 40 | *(no Time key)* | — | fuse from plant Rpc / client |
| Round end | 101 | **anchor** = nowSec | — | round-end UI (WinTeam bag) |
| Half-time | 111/112/113 | **anchor** = nowSec each | — | no (host waits 5s/1s/7s) |

Gold examples (R1):

- C2=22 @ stime 407475405: `Time=407475.376`, `RoundStartTime=407475.376` (equal anchors)
- C2=31 @ stime 407485574: `Time=407485.565` (≈ now; ≈ PreStart + 10.2s wall)
- Next combat: **no** SetProperties until plant C2=40 or round-end C2=101

Dedicated must not send PreStart/WarmUp `Time` as deadline, flash C2=31 then Live C2=101, or invent Live `Time=now+90`.
**`9e4d4c2` / `bc21cb6` / buy-pad checkpoint** reintroduced those — all superseded by this gold.

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

Dedicated host: on planter **field=3** Rpc during C2=31 combat, TX **C2=40 only** + **`BroadcastInitReady` BombManager plant Rpc** with planter payload. Fuse tracked server-side via `BombPlantedUtc` (~40s); no invented C2=40 `Time` (gold had none). Field=1 near 22/31 = reset — relay only.

## Phase map (phone gold ↔ dedicated host ↔ UI)

Every inter-round loop (R2+): **C2=22 → C2=31 (combat stay) → 40? → 101** — never skip C2=22; never Live C2=101 without WinTeam.
Phone R1: C2=11 (~8s) then C2=21 (~3s). **Dedicated R1:** skip C2=11 → C2=21 only.

| Phone C2 | Dedicated `MatchFlowPhase` | Wire `Time` | Server CLI / console | Client UI (2.06 OBT F1) |
|----------|---------------------------|-------------|----------------------|-------------------------|
| 10 | WaitingPlayers | — | WAITING PLAYERS | lobby / waiting |
| 11 | *(skipped)* | — | — | phone freeforall only; dedicated does not TX |
| 21 | Warmup | anchor | WARM-UP · Round 1 | warm-up (R1 only) |
| **22** | WarmupWillFinish | **anchor** = now | **PREP · Round N · C2=22** | buy/freeze (~10s host wall) |
| **31** | PurchasePhase | **anchor** = now | **LIVE · Round N · C2=31** | combat until plant/end |
| 40 | BombPlanted | *(no Time)* | BOMB PLANTED | fuse from plant Rpc |
| 101 | RoundEndPause | anchor | ROUND END · Round N | round-end + WinTeam bag |
| 111–113 | HalfTime* | anchor each | HALF-TIME · … | side swap after R7 |

**Operator note:** CLI used to label C2=22 as «MATCH STARTING» — that was misleading.
Gold sends C2=22 before **every** round’s prep; it is per-round PreStart, not a match-open banner.

### In-round timer bugs (history — do not reintroduce)

| Cause | Evidence | Fix |
|-------|----------|-----|
| Ranked `EnterRoundLive` TX **C2=101 + `Time`=now+RoundDuration** | Generic Ranked path | Allies: **no** Live bag; combat stays C2=31 |
| PreStart **`Time`=deadline** + BuyClientClockPad | Phantom ~19s buy | Allies: anchor `Time==RST=now` on C2=22 |
| 1s C2=31 flash then Live 101 | buy-pad checkpoint | Allies: stay on C2=31 for combat |

Phone host sends **no** Live Time updates; dedicated must not re-add C2=101 Live bags.

## Phase sequence (dedicated host)

```
/set start → skip C2=11 → C2=21 (~3s, R1 only) →
C2=22 Time=RST=now (~prep /set, default 10s wall) →
C2=31 combat Time=now (stay) →
(field=3 plant C2=40 C2-only + BombManager Rpc fan-out) → C2=101 WinTeam (~6s) → C2=22 …
After R7: C2=101 → C2=111 (~5s) → team flip → C2=112 (~1s) → C2=113 (~7s) → C2=22 R8 …
Economy: round-end / kill / plant / defuse payouts (cap $10k); R1 money seed on C2=22 only.
```

Dedicated host must rebuild these bags with codecs — **never replay capture blobs**.

| C2 | len≈ | Key order (room SetProperties) | Side TX |
|----|------|----------------------------------|---------|
| 10 | 14 | `C2` | bootstrap |
| 11 | 28 | `Time`, `C2` | phone gold only — dedicated skips |
| 21 | 28 | `Time`, `C2` | anchor Time; R1 only |
| 22 | 77 | `Time`, `Round`, `RoundStartTime`, `bomberId`, `C2` | ReCreate 4/5/6/8 + actor money=800; **anchor** Time==RST |
| 31 | 90 | `Time`, `Ct_RoundStartPlayersCount`, `Tr_RoundStartPlayersCount`, `C2` | **anchor** Time; stay for combat |
| 40 | 14 | `C2` | field=3 plant; no Time |
| 101 | 151 | `Time`, `{winner}Score`, `{loser}CoLosses`, `{winner}CoLosses`, `WinTeam`, `C2` | MVP SetProperty first; then bag |
| 111 | 28 | `Time`, `C2` | half-time intro |
| 112 | 101 | `Time`, `CtScore`, `TrScore`, `swapped_team`, `CtCoLosses`, `TrCoLosses`, `C2` | after forced team SetProperty |
| 113 | 28 | `Time`, `C2` | half-time transition |

**Combat:** no room bag after C2=31 until plant or round end.

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
| PreStart C2=22 | ~10s + bomberId; Time=RST=now | ~8s, no bomberId, BombSite |
| Plant | field=3 during C2=31 combat | Auto-plant field=3 |
| Combat | stay on C2=31; no Live bag | C2=31 after auto-plant |
| Round end | C2=101 + WinTeam (only C2=101 use) | C2=101 + WinTeam |
| Half-time | 111→112→113 after R7 | (not in Escalation probe) |
| Win condition | First to 8 | MR-N via `/set round` |

## Implementation

- `GameMatchHost.Allies.cs` — **sole** Allies FSM: `/set start`→C2=21 (skip 11)→22 anchor→31 combat stay→field=3→40→101 WinTeam
- `GameMatchHost.WorldObjects.cs` — Allies field=3 plant observe → `TryEnterAlliesBombPlanted`; field=1/2 relay-only
- `GameMatchHost.Ranked2v2HalfTime.cs` — half-time 111/112/113 + forced team swap (Allies-only callers)
- `GameMatchHost.Ranked2v2RoundEnd.cs` — shared `EnterRoundEndPause` delegates bag shape to `BuildAlliesRoundEndRoomProps`; `ContinueAfterRoundEnd` redirects Allies → `ContinueAfterRoundEndAllies`
- `GameMatchHost.Ranked2v2Phases.cs` — `TryEnterBombPlanted` redirects Allies → `TryEnterAlliesBombPlanted`; generic Ranked path never runs for `Ranked2v2` C0
- `AlliesFlowParams` in `MatchWorld.cs` — probe-verified host timers (wire vs internal)

### Match over → lobby (dedicated)

When first-to-`WinsNeeded` fires: TX `C2=205` MatchResults, wait **5s**, then:

1. Disconnect match peers + HardReset Dedik room
2. **Stop** match LiteNetLib (UDP **7777** down until next `/play`)
3. Lobby idle: `SearchingStarted=false`, `hasHosting=0`, `GameInProgress=false` — **keep** `GameModeId` + `SelectedLevels`
4. Discovery flips back to waiting (map/mode extras). Next: `/play` → rebind 7777 + op9, then `/set start`

### Mid-match reconnect

Disconnect remembers fighting team by `userId`. On rejoin after INIT: host forces same `team` SetProperty; if no respawn CWO within **5s** → Spectator **once** (`ReconnectSpectatorFallbackDone`).

**Buy-pad / Live-101 inventions superseded** — gold 2026-07-24: C2=22 Time=RST=now, stay on C2=31, plant field=3, C2=101 WinTeam only.
