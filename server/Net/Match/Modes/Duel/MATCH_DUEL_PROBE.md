# Duel probe — Block (first-to-8)

**Source:** ConnectAsClient spectator (`probeTeam=Spectator`) against phone host  
**Session:** `20260724_041*` / `server/latest.log`  
**Captures:** `server/bin/Release/net8.0/captures/20260724_041*` (+ lobby ops)  
**Decode:** `python3 server/tools/decode_allies_probe.py` via `collect_events(..., ["20260724_041*"])`  
**Host:** `192.168.1.73:7777` (lobby `7778`); probe LAN `192.168.1.68`

Evidence-only. Do **not** invent C2, bomb plant, Live clocks, or Allies/Ranked bags.

---

## Identity

| Key | Observed |
|-----|----------|
| Lobby `GameModeId` | `Duel` |
| Lobby `SelectedLevels` | `[Block]` (later switched to `[Cableway]` after match end) |
| Match `C0` | `Duel` |
| Match `C1` | `Block` |
| `game_type` | `byte:2` |
| `C3` | `byte:2` (JoinRoom Found) |
| `region` | `local` |
| `maxActors` | 4 |

### Scene managers (CreateWorldObject at join)

Present:

| id | type |
|----|------|
| 1 | `GameRpcHelper` |
| 2 | `NextLevelVoteRpcHelper` |
| 3 | `WaitForNextGameRpcHelper` |
| 4 | `WeaponDropManager` |
| 5 | `GrenadeManager` |
| 6 | `ChatManager` |

**Absent:** `BombManager` (zero Create / Rpc hits in this run). **No `C2=40`** plant phase.

### Actors

| actor | name | role | notes |
|------:|------|------|-------|
| 1 | влал | human Tr | `uid=Offline` |
| 2 | probe | Spectator | ConnectAsClient; no pawn / no State |
| 3 | Bot Bill | bot Ct | `uid=Bot`, `botDifficulty=byte:3` |
| 4 | Bot Ian | bot Tr | `uid=Bot`, `botDifficulty=byte:3` |

---

## Phase FSM (gold)

JoinRoom briefly shows `C2=0`, then room bags drive the match.

### Open sequence

| stime | Δ ms | C2 | Phase | Bag keys / notes |
|------:|-----:|----:|-------|------------------|
| 429761144 | — | **10** | WaitingPlayers | `current_loadout`, `current_round_modifier_id=null`, `C2` (len=114) |
| 429772763 | 11619 | **11** | DeathMatchPreWarmup | `Time`, `C2` (len=28) — FFA kills before real rounds; `Time≈now` |
| 429792812 | 20049 | **21** | WarmUp | `Time`, `C2` (len=28) — preceded by **score wipe** (see below) |

C2=11 duration ≈ **20 s** wall (bots scored FFA kills/score while C2=11).

### Score wipe → WarmUp

Immediately before `C2=21` (same stime `429792812`), host SetProperty resets all fighters:

- `kills=0`, `assists=0`, `death=0`, `score=0` for actors 1/3/4

Pre-wipe on C2=11, Bot Ian had climbed to `kills=4` / `score=8`. Those values do **not** carry into scored rounds.

### Per-round loop (R1–R9)

Every scored round observed:

1. **`ReCreateSceneManager` ×2** (op 202, len=8 each) — same stime as the C2=22 bag  
2. **`C2=22`** bag — freeze / buy (~**5 s**, not Allies ~10 s)  
3. **`C2=31`** — combat (eliminate) while room C2 stays **31**  
4. On wipe: player kill props → **`GameRpcHelper` field=1** (Rpc len=20, payload=0B) → **`C2=101`** + `WinTeam` + `TrScore` or `CtScore`  
5. Pause **~5.2 s** (`101→22` stime Δ ≈ 5196–5213 ms) → next round (no WarmUp after R1)

### Match end

| stime | Δ ms | C2 | Notes |
|------:|-----:|----:|-------|
| 429942033 | — | **101** | `TrScore=8`, `WinTeam` team=Tr — first-to-8 reached |
| 429947266 | 5233 | **201** | FinalHud: `Time`, `FinalPlayers=bytes[4]` (`01 02 03 04`), `FinalWinTeam={isDraw=false,isGiveUp=false,team=1}`, `C2` (len=95) |
| ~429949619 | — | — | match channel `RemoteConnectionClose` (expected teardown) |

No `C2=255` bag observed before disconnect in this spectator run.

### Contrast vs Allies (Ranked2v2)

| | **Duel (this gold)** | **Allies gold** |
|--|----------------------|-----------------|
| Combat room C2 | **31** until round end | **31** until plant `40` or round end |
| Round-end C2 | **101** + `WinTeam` | **101** + `WinTeam` |
| Buy / freeze `22→31` | ≈ **5.0–5.2 s** | ≈ **10.0–10.2 s** |
| Plant | **none** (no BombManager, no C2=40) | BombManager + `C2=40` |
| C2=31 bag | `Time`, `C2` only (len=28) | + roster `Ct_/Tr_RoundStartPlayersCount` |
| C2=101 bag | `Time`, `TrScore`\|`CtScore`, `WinTeam`, `C2` (len=119) | + CoLosses; longer bag |

Probe log labels C2=31 as “PurchasePhase” and C2=101 as “MatchStarted/Live” — on Duel wire, **31 = combat** and **101 = round-end** (same semantic as Allies round-end).

---

## Full C2 + stime table (decoded `20260724_041*`)

All room `Time` values are **≈ now** (`Time − stime/1000` within ~±50 ms). On every C2=22 bag: **`Time == RoundStartTime`** (`ΔTime-RST = 0`).

| stime | Δ ms | C2 | Round | Time | RST | notes |
|------:|-----:|----:|------:|-----:|----:|-------|
| 429761144 | — | 10 | | | | WaitingPlayers |
| 429772763 | 11619 | 11 | | 429772.749 | | FFA PreWarmup |
| 429792812 | 20049 | 21 | | 429792.804 | | WarmUp after wipe |
| 429795939 | 3127 | **22** | 1 | 429795.922 | 429795.922 | loadout PW=64 SW=11 |
| 429801127 | **5188** | **31** | | 429801.115 | | combat |
| 429805703 | 4576 | **101** | | 429805.687 | | TrScore=1 WinTeam=Tr |
| 429810902 | 5199 | 22 | 2 | 429810.883 | 429810.883 | loadout PW=65 SW=11 |
| 429816092 | **5190** | 31 | | 429816.080 | | |
| 429822116 | 6024 | 101 | | 429822.103 | | **CtScore=1** WinTeam=Ct |
| 429827321 | 5205 | 22 | 3 | 429827.298 | 429827.298 | **OnlyKnifesModifier** |
| 429832515 | **5194** | 31 | | 429832.505 | | |
| 429836468 | 3953 | 101 | | 429836.448 | | TrScore=2 |
| 429841665 | 5197 | 22 | 4 | 429841.644 | 429841.644 | loadout PW=51 SW=11 |
| 429846857 | **5192** | 31 | | 429846.845 | | |
| 429850810 | 3953 | 101 | | 429850.789 | | TrScore=3 |
| 429856009 | 5199 | 22 | 5 | 429855.987 | 429855.987 | loadout PW=46 SW=12 |
| 429861199 | **5190** | 31 | | 429861.185 | | |
| 429867639 | 6440 | 101 | | 429867.626 | | TrScore=4 |
| 429872852 | 5213 | 22 | 6 | 429872.818 | 429872.818 | **OnlyHeadshotsModifier** |
| 429877837 | **4985** | 31 | | 429877.825 | | |
| 429881581 | 3744 | 101 | | 429881.564 | | TrScore=5 |
| 429886777 | 5196 | 22 | 7 | 429886.759 | 429886.759 | loadout PW=11 SW=0 |
| 429891969 | **5192** | 31 | | 429891.959 | | |
| 429896542 | 4573 | 101 | | 429896.525 | | TrScore=6 |
| 429901738 | 5196 | 22 | 8 | 429901.721 | 429901.721 | loadout PW=49 SW=12 |
| 429906933 | **5195** | 31 | | 429906.919 | | |
| 429918352 | 11419 | 101 | | 429918.332 | | TrScore=7 |
| 429923554 | 5202 | 22 | 9 | 429923.531 | 429923.531 | **OnlyGrenadesModifier** |
| 429928749 | **5195** | 31 | | 429928.736 | | |
| 429942033 | 13284 | 101 | | 429942.020 | | **TrScore=8** |
| 429947266 | 5233 | **201** | | 429947.217 | | FinalHud |

`22→31` freeze ≈ **4.985–5.194 s**. Combat length on C2=31 is variable (eliminate), not a fixed round clock on wire.

---

## Round features

### Win condition

- **First to 8** round wins (`TrScore` reached **8** → FinalHud). Final score this run: **Tr 8 : Ct 1**.

### Modifiers

Seen on C2=22 bags (modifier rounds omit `current_loadout` on the same bag):

| Round | `current_round_modifier_id` | `used_round_modifier_ids` |
|------:|----------------------------|---------------------------|
| 3 | `OnlyKnifesModifier` | `[OnlyKnifesModifier]` |
| 6 | `OnlyHeadshotsModifier` | `[OnlyKnifesModifier, OnlyHeadshotsModifier]` |
| 9 | `OnlyGrenadesModifier` | `[OnlyKnifesModifier, OnlyHeadshotsModifier, OnlyGrenadesModifier]` |

Non-modifier rounds send `current_round_modifier_id=null` + **`current_loadout`** (`PrimaryWeapon`, `SecondaryWeapon`, `OtherSlots`) — forced loadout changes each such round (see table).

### `GameRpcHelper` field=1

Around each round end (after kill props, before or with C2=101): SCENE Rpc `GameRpcHelper` **field=1**, gaa=3, payload **0B**, len=20. Observed every scored round in this run.

### Kill / score player props

Observed on fighters (not Spectator):

| prop | when |
|------|------|
| `kills` | cumulative |
| `fair_kills` | cumulative (human rounds; seen on влал) |
| `round_kills` | per round; reset to 0 on each C2=22 |
| `score` | rises with kills (often +2 per kill in samples) |
| `mvp` | increments for round winner |
| `death` | victim; wipe ends round |

C2=11 FFA used `kills` / `round_kills` / `score` / `death` then wiped before WarmUp.

### WinTeam bag (C2=101)

Example shape (every round-end bag len=119):

```
Time ≈ now
TrScore | CtScore   # only the scoring side’s key present in bag
WinTeam = { team, mvpPlayer, mvpCode=0, resultRoundType=0, resultRoundActor=0 }
C2 = 101
```

No CoLosses / bomberId / plant mvp codes in this Duel capture.

---

## Cableway follow-up (same lobby session)

After FinalHud + `RemoteConnectionClose`:

1. Lobby `SelectedLevels` → `[Cableway]` (op7)  
2. `SearchingStarted` true → false  
3. **op9** `GameHostingState` again (`20260724_041837_010_client_op9_len19.bin`)  
4. Probe log: **`match follow already started — ignore duplicate hosting signal`**

**No second match JoinRoom / Handshake** after disconnect. Cableway match may have started on the phone; **this probe did not rejoin**. No Cableway C0/C1/C2 gold from this session.

---

## Dedicated Duel stub — implement later (gold checklist)

Short list from this capture only:

- [ ] Identity: `C0=Duel`, map `C1`, `game_type=2`; scene set **without** BombManager  
- [ ] Open: `C2=10` → `C2=11` (FFA, `Time≈now`) → score wipe → `C2=21` (`Time≈now`)  
- [ ] Round: `ReCreateSceneManager`×2 → `C2=22` (`Time==RST≈now`, Round, loadout **or** modifier+used list) → wait **~5 s** → `C2=31` combat (`Time≈now`)  
- [ ] Eliminate → `GameRpcHelper` field=1 → `C2=101` + WinTeam + TrScore/CtScore (`Time≈now`); pause **~5 s** → next 22  
- [ ] First-to-8 → `C2=201` FinalHud (`FinalWinTeam`, `FinalPlayers`) then teardown  
- [ ] **Do not** TX C2=40 / BombManager; **do not** copy Allies ~10 s prep or Live C2=101 clock  
- [ ] Support forced `current_loadout` + observed modifiers (`OnlyKnifes` / `OnlyHeadshots` / `OnlyGrenades` + `used_round_modifier_ids`)

---

## Capture index

- Match RX/TX prefix: `20260724_041507` … `20260724_041823`  
- Lobby after end: `20260724_041832_007` … `20260724_041837_010` (Cableway + op9, no rematch join)  
- Log: `server/latest.log` (Duel Block spectator run)
