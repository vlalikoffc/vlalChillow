# Allies / Ranked2v2 probe — Sandstone 2x2

**Source:** ConnectAsClient `allies-probe` as Tr (`--team tr`) against phone host  
**Sessions:** `run-20260723_215727` (lobby) / `run-20260723_2206…` (match)  
**Log:** `server/latest.log`  
**Captures:** `server/bin/Release/net8.0/captures/20260723_2157*` / `2206*`

## Lobby / match selection

| Key | Value |
|-----|-------|
| GameModeId | `Ranked2v2` |
| SelectedLevels | `Sandstone 2x2` |
| Match C0 | `Ranked2v2` |
| Match C1 | `Sandstone 2x2` |

## Win condition (design)

- **First to 8 round wins** — not “only 8 rounds”, not MR-8 first-to-5.
- Default dedicated: `WinsNeeded=8` (`/set wins N`).
- Match continues past round 7 until one side reaches 8 wins.

## Phase timeline (stime C2 table)

| stime (approx) | Round | C2 | Notes |
|----------------|-------|-----|-------|
| 407424176 | — | **10** | WaitingPlayers (both teams joined) |
| 407432220 | — | **11** | DeathMatchPreWarmup / freeforall (~3s) |
| 407475405 | — | **21** | WarmUp (first round only) |
| 407475405 | 1 | **22** | PreStart: Round, RoundStartTime, **bomberId**, ReCreate 4/5/6/8, money=800 |
| 407477… | 1 | **31** | Prep: Ct/Tr_RoundStartPlayersCount, BombManager field=1 |
| 40748… | 1 | **40** | BombPlanted (manual plant — not Escalation auto-plant field=3) |
| 407484809 | 1 | **101** | Round end bag: WinTeam + TrScore/CtScore + CoLosses, **not C2=111** |
| … | 2–6 | **22→31→Live→101** | Skip WarmUp; PreStart every round |
| 407884809 | 7 | **101** | Round 7 end — score **4:3** (Tr leading) |
| 407890833 | 7 | **111** | **Half-time intro** (~5s) — mid-match, not round-end UI |
| 407895913 | 7 | — | Host SetProperty **team flip** actor 1 Tr→Ct, money=800 |
| 407895913 | 7 | **112** | `swapped_team=true`, **CtScore=4 TrScore=3** (perspective flip), CoLosses=0 |
| 407896999 | 7 | **113** | Half-time transition (~7s) |
| 407904034 | **8** | **22** | PreStart: Round=8, bomberId=3 (new T side), ReCreate 4/5/6/8 |
| 407909562 | 8 | — | Actor 3 Ct→Tr SetProperty + Tr_Tr pawn respawn |
| 407914208 | 8 | **31** | Prep (Ct_RoundStartPlayersCount=2, Tr=1) |
| … | 8+ | **101** | Round wins continue until first-to-8 |

## Round end bag (C2=101)

Phone Allies round end uses **C2=101** (`MatchStarted` id) + nested `WinTeam` — **not** C2=111.

Typical keys (len≈151):

1. `Time`
2. Winner flat score (`TrScore` or `CtScore` first)
3. Loser `CoLosses`
4. Winner `CoLosses=0` (streak reset)
5. `WinTeam` `{ team, mvpPlayer, mvpCode, resultRoundType=0, resultRoundActor=0 }`
6. `C2=101`

Dedicated host also TX both `TrScore` + `CtScore` to stay-in peers.

## Half-time team swap (after round 7)

Gold sequence: **111 → (team SetProperty) → 112 → 113 → 22**

Server-authoritative rules (dedicated host):

1. Remember each fighter’s team before swap.
2. After C2=111 pause: **force SetProperty `team`** on every Tr/Ct actor (Tr↔Ct).
3. C2=112 bag: `swapped_team=true`, flip `TrScore`↔`CtScore`, reset CoLosses.
4. Do **not** rely on client-side swap (original client buggy — one side may not swap).
5. Example from probe: влал+ерзат were T → become CT; арсен+денис were CT → become T.

## bomberId

Present on Allies PreStart C2=22 (unlike Escalation which omits bomberId and auto-plants field=3).

## Escalation divergence

| | Allies / Ranked2v2 | Escalation |
|--|-------------------|------------|
| Plant | Manual carry plant field=1/2 during Live | Auto-plant field=3 ~8s after C2=22 |
| Round end C2 | 101 + WinTeam | 101 + WinTeam (same family) |
| Half-time | 111→112→113 after R7 | (not observed in this probe) |
| Win condition | First to 8 (default) | MR-N via `/set round` |

## Dedicated implementation

- `GameMatchHost.Ranked2v2*.cs` — live FSM
- `GameMatchHost.Ranked2v2HalfTime.cs` — half-time 111/112/113 + forced team swap
- `AlliesFlowParams` in `MatchWorld.cs` — constants and timers
