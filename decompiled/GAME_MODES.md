# Game modes catalog

Source: ConnectAsClient probe **`run-20260722_213859`** (`GameModeId` × `SelectedLevels`).  
Server default today: **Ranked2v2** / **Sandstone 2x2** (Allies).

## Modes

| GameModeId | Folder | Status | SelectedLevels |
|------------|--------|--------|----------------|
| DeathMatch | `Modes/DeathMatch/` | stub | Perimeter, Hanari, Favelas, Arena, Calypso, Sandyards, TrainingOutside, Village |
| RankedDefuse | `Modes/RankedDefuse/` | stub | Prison, Hanami, Rust, Dune, Breeze, Province, Sandstone |
| Ranked2v2 | `Modes/Ranked2v2/` | **live** | Prison 2x2 … Sandstone 2x2 |
| Ranked2v2Alt | `Modes/Ranked2v2Alt/` | stub | * 2x2 Alt |
| Defuse | `Modes/Defuse/` | stub | Prison…Sandstone |
| Duel | `Modes/Duel/` | stub | Block, Cableway, Pipeline, Bridge, Pool, Temple, Yard |
| ArmsRace | `Modes/ArmsRace/` | stub | same as DeathMatch |
| FreeForAll | `Modes/FreeForAll/` | stub | Prison…Sandstone |
| Escalation | `Modes/Escalation/` | stub | Prison…Province (+ Sandstone) |

## Families (for future shared interfaces)

| Family | Modes | Notes |
|--------|-------|--------|
| Bomb | RankedDefuse, Ranked2v2, Ranked2v2Alt, Defuse, Escalation | Similar phase skeleton; **different win rules** — separate implementations |
| DeathMatch-like | DeathMatch, ArmsRace | Map pool overlap; distinct scoring |
| FFA | FreeForAll | |
| Duel | Duel | Own map pool |

Do **not** cross-import mode folders. Shared phase/clock types live under `Match/Kernel/` and `MatchFlow*.cs`.

## Wiring

- Lobby / gap room prop `C0` = `GameModeId` string; `C1` = selected level string (`MatchGapDefaults`).  
- Registry: `MatchModeRegistry.Create(gameModeId)`.  
- Only Ranked2v2 currently drives the host phase machine (partials under its folder).
