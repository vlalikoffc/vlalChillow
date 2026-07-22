# Match world bootstrap (post-Found)

Source: ConnectAsClient capture against a **real phone host** (`run-20260722_073504.log`, captures `20260722_003523_*` / `003532_*`), DiffableCs / ISIL, plus dedicated-host work.

Policy: decode → understand → **codec builders**. Never memcpy capture blobs into dedicated host TX.

### DestroyWorldObject (`fuo=201` / `fuu`) — drops, shield, respawn ghosts

Client TX is **4 bytes**: `flags=None` + opcode `0xC9` + **`i16` objectId** only
(`20260722_011733_155_match_op201_len4.bin` = id=385; also 386/257/387 in same retest).

ISIL: `fuu.bnlb` → `fzo.bogj` (ReadInt16); host `dwr.bfwb` → `dvk.DestroyWorldObject(short)`.

**Bug (run-20260722_081608):** dedicated logged `unhandled opcode DestroyWorldObject — dump` while
still relaying CreateWorldObject / WorldObjectRpc id=4 (WeaponDropManager). Client destroyed
locally; peers never got Destroy → **weapon drops stacked**, **spawn-protection shield stayed /
duped**, **old pawn ids lingered** (radar/occlusion ghosts through walls + nameplate mixups).

**Fix:** codec `BuildDestroyWorldObject` (HasServerTime + i16) → `TX relay DestroyWorldObject`
to INIT-ready peers; unregister `LivingPawns`; stop State ticks for that id. On new pawn CWO for
same owner, also TX Destroy for any stale LivingPawn ids (heal races / missed Destroy).

Retest markers: `TX relay DestroyWorldObject id=… → peers=K`; no more `unhandled opcode DestroyWorldObject`.

### Death vs respawn (critical — `run-20260722_105939` false wipe)

**`DestroyWorldObject` is NOT a death.** Clients Destroy+Create their LivingPawn on **every phase
transition** (Warmup→PreStart→Prep, and between rounds). The phone host does the same on the wire
(gold `run-20260722_100157`: `Destroy id=0x81` then `Create id=0x82` for the same owner, **no
round end**). Treating a bare Destroy as a kill (commit `b0b0918`) wiped T during PreStart and
fired `RoundEnd … round=0` **before round 1** — CT “instantly wins as if time ran out”.

**Signals (evidence):**

| Pattern | Meaning | Evidence (`run-20260722_105939`) |
|---------|---------|----------------------------------|
| `Destroy id=X` then `Create id=Y` same owner, **no `death` prop** | phase **respawn** — never a kill | PreStart 040043: Destroy 257 → Create 258; also 385→386 |
| `Destroy` + actor `SetProperty death=1` (+ opponent `kills`/`fair_kills`/`round_kills`++) | **combat death** | Live 040101: Destroy 258 → `death=1` actor2, `kills`/`fair_kills`/`round_kills=1` actor3 |
| `round_kills=0` / `round_assists=0` (reset to 0) | round **reset**, not a kill | 040043 before PreStart respawn |

**Host rule (implemented):**
- Combat death = actor **`death` SetProperty non-zero** → `NoteActorDeath` → wipe check. Valid in
  any `AllowsWipeCheck` phase (so a real kill or a leaver still ends PreStart/Prep), but a bare
  Destroy never reaches it.
- Destroy only **arms a grace** during **RoundLive / BombPlanted** (`DestroyMayBeCombatDeath`). If
  no respawn CWO for that owner within `DestroyDeathGrace` (1.5s) **and** no `death` prop resolved
  it, count it as a combat elimination (fallback for a missing death prop). Warmup/PreStart/Prep
  Destroys are ignored for death entirely.
- A fighting-pawn CWO for an owner = respawn: cancel the grace, clear `DeadActors` + local `death`.
- `EnterPurchasePhase` clears `death` props **before** picking `bomberId` — otherwise last round’s
  `death=1` makes every living T look dead → `bomberId=0` (smoking gun round 2 Prep) → the fighter
  is treated as a corpse and Prep hangs (`Prep extended … awaiting spawn`).

Retest markers: `pawn Destroy owner=N in WarmupWillFinish — armed … not an immediate wipe` must
**not** appear (PreStart is not a combat phase → no arm); no `death actor=… via DestroyWorldObject`;
Live kill shows `death actor=… via SetProperty` then immediate `wipe … RoundEnd`.

### Ping — server RoundTripTime (not client echo)

Client SetProperty `ping` int is often ≈0/1/2 on LAN. Phone host transport stores LiteNetLib
latency (`grp.OnNetworkLatencyUpdate`); dedicated `fwv` stub is empty. Dedicated now writes
**`peer.RoundTripTime`** as actor prop `ping` (same key/type) on inbound ping + on
`NetworkLatencyUpdateEvent`. Log: `ping from RTT actor=N rtt=…ms` / `(server RTT=…ms, not client echo)`.

### Radar through walls (dedicated host)

Screenshot retest: red enemy blip through geometry on our host; phone LAN host does not wallhack.
Client radar uses `RadarManager` + `OcclusionControl` / `PlayerOcclusionController` (local occlusion).
No evidence of a separate host FOW filter inventable from wire. Dominant host bug was **missing
DestroyWorldObject** → undestroyed pawn/drop/shield objects keep feeding radar/occlusion at stale
poses. Fix Destroy relay (+ stale-pawn heal) first; retest radar vs phone host. Do **not** invent
LOS filtering of WorldObjectState.

### Match flow — Allies loop (user + decompile)

Screenshot “WAITING FOR OTHER PLAYERS…” with both players on the tab = stuck at **C2=10**
(`MatchC2States.WaitingPlayers`). Phone master runs RankedDefuse `cga` and publishes C2 via
`bbs.omx`; dedicated advances past WaitingPlayers when both Tr+Ct are present.

Screenshot **“MATCH WILL START IN: N…”** with freeze + buy cart = **C2=22**
(`WarmupWillFinish` / `cnr`) — short countdown, **not** movable WarmUp. Do not stretch this
across warmup+prep (~15s); that was our bug when C2=21/31 were mis-read as one freeze.

**C2 ids** (from `RankedDefuseController.wkf` + `xvv` on state classes / `ckg` immediates):

| C2 | Meaning | Evidence |
|----|---------|----------|
| 10 | WaitingPlayers | Init unlock |
| **21** | **WarmUp** | `GameState/WarmUp` — **movable** разминка |
| **22** | **WarmupWillFinish** | `cnr.xvv=22` — freeze «MATCH WILL START IN»; sets `bomberId` |
| **31** | **PurchasePhase** | `cnq.xvv=31`, `GameState/PurchasePhase` — visible 10s prep |
| 40 | Bomb planted | `cnl.xvv=40`; fuse ~40s |
| 101 | MatchStarted / live | `cnp.xvv=101` — **also** Allies phone-host round-end C2 (gold) |
| 111 | RoundEnd (Ranked registry) | `ckq.xvv=111` — RankedDefuseController only; **not** Allies phone-host round end |
| **201** | **FinalHud** | `cjf.xvv=201` — **match-wide WIN / итоги chrome** + reads `FinalWinTeam`. **Never** use for mid-match round end. |
| 205 | MatchResults | `ckc.xvv=205` — full-match итоги only after all rounds |

**Allies test loop (source of truth):**
Warmup `21` **10s movable** → PreStart `22` **3s** freeze countdown (+ `bomberId`) →
Prep `31` **10s** (every round, including first) → Live `101` **90s** → round-end bag
(C2 stays **101** + `WinTeam`/`TrScore`…) **silent 5s** → Prep… × **3** rounds.
Round end UI is **победа / поражение** from local team vs nested `WinTeam.team` while
`cnp` still owns the state (C2=101). Do **not** TX C2=201 for rounds — that opens
`FinalHud` (green match WIN, empty scores). Do **not** invent ban/draw unless decompile
shows real draw use for rounds (`cid.Draw=7` exists but is unused here).

**Phase order + timers (host-owned, per `MatchFlowState.Phase`):** `WaitingPlayers`(10) →
`Warmup`(21, 10s movable) → `WarmupWillFinish`/PreStart(22, 3s freeze + `bomberId`; clients
Destroy+Create pawns here — **not** a wipe) → `PurchasePhase`(31, 10s, `Round`/`bomberId`/`Time`) →
`RoundLive`(101, 90s, money $800) → wipe **or** timeout → `RoundEndPause` (C2 stays 101 + WinTeam
bag, silent 5s) → next `PurchasePhase`(31) … × 3 rounds → `MatchResults`(205). The **first**
`RoundEnd` must be `round=1`+ from a real Live outcome — never `round=0` from a PreStart Destroy.

### Round-end wire — phone-host gold (`run-20260722_100157`)

ConnectAsClient vs real phone host; 4 outcomes; each round-end is **one** room
`SetProperties` actor=0 **len=151** (body 6 keys), optionally preceded by actor
`SetProperty` `mvp` Int.

**Order (exact):**
1. `Time` — Double (current `stime/1000`, not a deadline)
2. Winner flat score — `TrScore` **or** `CtScore` Int (only the winning side’s key)
3. Loser `*CoLosses` Int (streak++)
4. Winner `*CoLosses` Int (= **0**, streak reset)
5. `WinTeam` — Hashtable / PropertiesRecord (5 Byte fields)
6. `C2` — Byte **101** (`MatchStarted`) — **not** 111, **not** 201

**WinTeam nested (Bytes, gold key names):**

| Key | Meaning |
|-----|---------|
| `team` | Winning `cux` (1=Tr, 2=Ct) — **not** `winTeam` |
| `mvpPlayer` | MVP actor nr |
| `mvpCode` | `cns`: 1=PlantingBomb, 2=DefusingBomb, 3=MostEliminations |
| `resultRoundType` | 0 on all 4 gold outcomes |
| `resultRoundActor` | 0 on all 4 gold outcomes — **not** `resultActor`, not copied from mvp |

**Actor `mvp` (bbo.cabd):** `SetProperty` on MVP actor, Int = cumulative MVP count, **before**
the room bag. Gold showed actor1 `mvp=1` then `mvp=2` on T wipe / bomb; CT MVP rounds
had WinTeam.mvpPlayer only in capture — dedicated still increments `mvp` for the
chosen mvpPlayer.

**Decoded templates (user truth ↔ wire):**

| Round | Outcome | Score key | CoLosses | WinTeam.team | mvpPlayer | mvpCode |
|------|---------|-----------|----------|--------------|-----------|---------|
| 1 | Kill CT → T | TrScore=1 | Ct=1, Tr=0 | 1 | 1 | 3 MostElim |
| 2 | Kill T → CT | CtScore=1 | Tr=1, Ct=0 | 2 | 3 | 3 MostElim |
| 3 | Bomb explode → T | TrScore=2 | Ct=1, Tr=0 | 1 | 1 | 1 Plant |
| 4 | Defuse → CT | CtScore=2 | Tr=1, Ct=0 | 2 | 3 | 2 Defuse |

Captures: `*_149_*_len151` / `*_211_*` / `*_288_*` / `*_373_*`; mvp `*_148_*` / `*_287_*`.

**Mismatch vs old dedicated (C2=111 path) — fixed:**

| Old dedicated | Phone-host gold |
|---------------|-----------------|
| C2=111 (or worse 201 FinalHud) | C2=**101** |
| nested `Score={Tr,Ct}` Hashtable | flat `TrScore` **or** `CtScore` only |
| no CoLosses | `TrCoLosses` + `CtCoLosses` both present |
| WinTeam key `winTeam` | key **`team`** |
| `resultActor` = mvpPlayer | key **`resultRoundActor`** = **0** |
| no actor `mvp` SetProperty | actor `mvp` Int++ before room bag |
| included `Round` in end bag | **no** `Round` in end bag |

**Bug history:** `C2=201 + WinTeam + Score` → FinalHud green WIN / 0:0. Wrong nested
`Score` + C2=111 also diverged from Allies phone. Retest marker:
`RoundEnd C2=101 … TrScore=/CtScore= … WinTeam … (phone-host WinTeam+TrScore/CtScore; not FinalHud C2=201)`.

**FetchServerTime / clock units (do not mix):**
- Host `fyf.bodw` / dedicated: `fuo=3` with `HasServerTime` + **`Environment.TickCount` ms**
  (`04 03 <i32 LE>`). Client request `00 02`.
- Client `dws.bfwi` / `NetManager.bfqt`: `(TickCount + offset) / 1000` → **seconds**.
- Room `Time` / `RoundStartTime` doubles = that **seconds** clock (`stime/1000`).
- WorldObjectRpc `timeValue` likewise seconds (phone capture ≈ `HasServerTime/1000`).
- Retest smoking gun was treating ms vs seconds as one unit → ~2 min timer / freeze at ~1:50
  (warmup+prep+live span). Keep header ms; keep room doubles in seconds.

**Bomb give (evidence — not invent):**
- Room prop `bomberId` Int (`bcb.cadb` / `opg` write, `opi` read) — random living Tr.
- Phone **master** path: `cnr` (C2=22) calls `bcb.opg` then `BombManager.nyn(dur)`;
  `DefuseController` also `opi` → `nyn`. `nyn` is **not** `[Rpc]` — local master inventory give;
  BombManager Rpcs 1–6 are plant/defuse pose, not give.
- `cnq` PurchasePhase does **not** call `nyn` / `opi`.
- Dedicated: set `bomberId` on C2=22 (and reaffirm on prep). **No evidenced host Rpc/CWO** to
  grant bomb — do not invent. Missing bomb after only C2=31+`bomberId` is expected;
  missing prep/C2=22 is the primary symptom. If bomb still absent after C2=22 on dedicated,
  need phone-host capture of post-`nyn` inventory sync before implementing give wire.

**Room props (Allies round end — gold):** flat `TrScore`/`CtScore` Int + `TrCoLosses`/
`CtCoLosses` Int + `WinTeam` nested Bytes (`team`/`mvpPlayer`/`mvpCode`/`resultRoundType`/
`resultRoundActor`) + `C2=101`. Nested `Score={Tr,Ct}` (`bbs.onq`) is a different path
(FinalHud / other) — do not use for Allies round end.

**Win conditions (host):** all CT dead → T; all T dead → CT unless bomb planted; planted fuse
timeout → T; live timeout → CT. BombManager Rpc 1/2 → enter C2=40; Rpc 6 `nzu(bool,…)` →
bool true = defuse → **immediate** CT win; bool false = explode FX → T win (do not hang on
fuse after defuse). Rpc 5 `nzg` logged only.

**Disconnect / ActorLeft (fuo=51 / fuq):** on peer disconnect/timeout — **immediate** full
remove (roster + actor props + LivingPawns Destroy + `ActorLeftEvent` with actorNr + null
optionals). Smoking gun `run-20260722_093346`: actors 2+3 left, rejoin got 4/5 while ghosts
stayed → scoreboard CT+T duplicates. Retest: `TX ActorLeftEvent actor=N`; Found after rejoin
must not list stale nrs. Mid-round last-of-team leave: wipe check treats **empty opposing
team** (totals after remove) as round end — not hang until live timeout. Round-end bag is
phone-host C2=101 path (not FinalHud).

**Test timings** (`MatchFlowTestParams`): Warmup **10s** → WarmupWillFinish **3s** →
PurchasePhase **10s** → Round **90s** × **3**, round-end pause **5s**, bomb fuse **40s**,
round-start money **$800**.

**Late / GIP join:** Found may list actors (names only). Peer-local INIT still TX `C2=10` but
must **not** overwrite live `room.RoomC2`. After INIT: `TX match-state snapshot` (room flow
props + other actors’ props) + `TX living-pawn snapshot` (Entity CWO + rpc=1). Existing peers
still get `ActorJoinedEvent` on Found.

Retest markers: `TX FetchServerTimeResponse`; `Warmup C2=21` → `PreStart C2=22 …s countdown bomberId=…` →
`Prep C2=31 10s bomberId=…` → `RoundLive C2=101 … money=800` → wipe/plant/defuse/timeout →
`RoundEnd C2=101 … TrScore=/CtScore= … mvpPlayer=…` → … → `MatchResults C2=205`.
Disconnect: `TX ActorLeftEvent actor=N` then immediate wipe `RoundEnd` if last of team.
Defuse: `nzu defused=True → CT win queued` then `RoundEnd` (no fuse hang). Late join:
`TX match-state snapshot` + `TX living-pawn snapshot`; no `RoomC2` clobber to 10 mid-match.

### WorldObjectRpc id=4 log rate-limit

WeaponDropManager Rpc floods like State — log first 8 + every 50th + 3s summary
(`WorldObjectRpc id=4 summary: …`).

---

## Init gate — LevelLoadingView / `InitWaiting`

UI string key: `LevelLoadingView/InitWaiting` (“Waiting for initialization”).

**Proven unlock (phone host → probe, 2026-07-22):**

1. Thin **Found ~148B** with room props (`C0`/`C1`/…, `C2=0`) + actor **names only** (empty `gak` props). Fat Found (~3423B) is **not** required for unlock.
2. Post-Found host TX, all **ReliableOrdered** + **`HasServerTime`**:
   1. SetInternalProperty nick (actor1)
   2. SetProperty uid=`Offline`, badgeId, from_lobby, **avatar** (JPEG ByteArray), ping
   3. CreateWorldObject ×8 scene managers (lens 25,34,36,86,83,24,23,23)
   4. SetProperties actor=`0` **`C2=FF0A`** (len=14) — **this clears InitWaiting**
   5. SetProperty money (then later team / pawn)
3. Fighting pawn is **after** team assign — not part of init.

Dedicated previously matched managers+C2 bytes but **skipped host avatar** and sent joiner props / Server props in Found — phone order is host identity **including avatar** before managers+C2.

### Crash — `MatchHostActor` type initializer (run-20260722_074123)

After thin Found the dedicated host TX’d SIP nick + uid/badge/from_lobby, then died:

`The type initializer for '…MatchHostActor' threw an exception.`

**Cause:** `PlaceholderAvatarJpeg` was a `static readonly` field initialized with `Convert.FromBase64String(...)` on a **broken base64** string (length ≢ 0 mod 4 → `FormatException`). Consts (`ActorNr`, `Name`, `BootstrapUid`, …) are **inlined**, so the cctor only ran on first access to the JPEG field — mid-bootstrap, before managers/C2. InitWaiting never cleared; joiner still echoed uid/badge/avatar/ping.

**Fix:** build JPEG in a helper that catches `FormatException` and falls back to a minimal SOI…EOI stub. Parse/handle logs now flatten `InnerException`.

### Joiner-ready gate before C2 (timing)

Phone host waits ~9s (Unity load) after Found, then TX identity→managers→C2. Dedicated must **not** unlock C2 while the joiner is still loading.

**Gate (flags, not a fixed sleep):** after Found, log `await joiner` and **hold** host identity/managers/C2 until the joiner has sent early identity:

- required: `uid` + `from_lobby`
- plus: `avatar` **or** `ping` (joiner always sends both after load; either completes the gate)

Then TX host identity → managers×8 → **C2=10** (`INIT unlocked`) → money → Server Spectator.

**Soft timeout:** 15s — if joiner never sends, log loudly and force bootstrap anyway.

Retest markers: `await joiner` → `joiner identity ready` / `soft-timeout` → `INIT unlocked`.

### Multi-joiner / N-player INIT (run-20260722_075838)

**Bug:** `WorldBootstrapped` was **room-global**. Joiner 2 set it; joiner 3 soft-timeout / identity-ready hit early-return **without TX** and left `BootstrapPending` true → spam `sending host bootstrap` forever, InitWaiting forever.

**Fix:** per-peer `BootstrapSent` / `BootstrapPending`. Soft-timeout and identity-ready both call the same `TrySendWorldBootstrap` and **must TX** managers+C2 on **that** LiteNetLib connection unless that peer already got INIT. Late joiners also get a living-pawn snapshot (codec rebuild of existing Entity CWOs + rpc=1).

**всем всё (match relay):** after INIT, SetProperty / SetProperties broadcast to **all** match peers (HasServerTime rebuild). CreateWorldObject / WorldObjectRpc / WorldObjectState go to **INIT-ready** peers (`BootstrapSent`) so late joiners are not double-fed. WorldObjectState: **relay** owner fat Unreliable ticks to other ready peers; skip host thin standing ticks once owner streams State.

**Lobby vs match:** lobby JoinResponse stays Server+self illusion (N>4 bypass). Match Found lists real actors (shared truth); `MaxActorsHint ≥ actor count`.

### Dynamic join notify (run-20260722_080741) — alone + ghost

**Bug:** Each client only knew the world **as of its own Found**. Late joiner (actor3) got living-pawn snapshot of 257 → saw влал. Early joiner (actor2) never learned about actor3 (Found had `actors=2`, no `ActorJoinedEvent`) → alone. Actor3 also got **live** CWO 257 (BroadcastRoom while still pre-INIT) **plus** snapshot → transparent ghost + real body.

**Fix:**
1. On JoinRoom Found: TX **`ActorJoinedEvent` (fuo=50 / `fup`)** to all *other* match peers — `gak` (nr+name, empty props) + `hasGuid=false` (ISIL `fup.bnkb`). Early peers learn the new human dynamically.
2. Pawn CWO echo + rpc=1 + State relay/standing: only **`BootstrapSent`** peers. Late joiner gets existing pawns **once** via living-pawn snapshot at their INIT.
3. Log markers: `TX ActorJoinedEvent actor=N → peers=K`, `TX relay CreateWorldObject id=… → peers=K`, `TX relay WorldObjectState … → peers=K`.
4. WorldObjectState RX/TX console spam rate-limited (first 8 + every 50th + 3s summary) — same idea as ConnectAsClient.

Retest: 2 phones → `TX ActorJoinedEvent actor=3 → peers=1` when #2 joins; after #3 CWO `TX relay CreateWorldObject id=385 → peers=2`; #2 must see #3; #3 must see one body for 257 (no ghost). Quiet log: `WorldObjectState summary: rx=… suppressed≈…`.

Retest: 2 phones → both `TX world bootstrap for joiner=N` + `INIT unlocked`; 3rd phone same. Markers per joiner: `await joiner` → `trigger=joiner-ready|soft-timeout` → `TX world bootstrap for joiner=N`.

### Compressed avatar (client → host)

Phone TX SetProperty `avatar` with `flags=IsCompressed`: `uncLen(i32)` + **LZ4 block** of `(actor + key + fzp ByteArray)`.

Host echoes **uncompressed** `HasServerTime` SetProperty — do **not** dump-only ignore.

## Teams (`cux` → actor prop `team`)

| Byte | Name | Notes |
|------|------|-------|
| 0 | None | before pick |
| 1 | Tr | T |
| 2 | Ct | CT — OBT phone-host learn (`FF02`) |
| 3 | Spectator | observers; dedicated **Server** (wire assign, no UI) |

### OBT note (2026-07-22)

Spectator is **disabled in the phone UI** — joiner was **forced onto CT** (`team` → `FF02`, then `Ct_Ct` pawn). Enum still has Spectator=3; dedicated sets Server’s team on the wire without UI.

## Team select stuck (run-20260722_074631) — root cause

Past InitWaiting, joiner picks ATTACK (`team=b1`). Dedicated previously:

1. Host-spawned `CreateWorldObject` **id=129** `Tr_Tr` (modeled on phone **host-self** spawn in `003536_*`, owner=1)
2. **Ignored** joiner `CreateWorldObject` len=59
3. Left `WorldObjectRpc` unhandled; never sent `WorldObjectState`

Decoded joiner CWO (`20260722_004652_042_*`):

| Field | Value |
|-------|-------|
| flags | `None` (client→host) |
| kind | Entity |
| owner | 2 |
| **objectId** | **257** (not 129) |
| name | `Tr_Tr` (5 chars) |
| fusTag | 101 (`0x65` `'e'` → ASCII looks like `Tr_Tre`) |
| trailing | 45B spawn pose (client’s own coords) |

Joiner then `WorldObjectRpc` **id=257 rpc=2**. Prefab name was already correct — stuck UI was **ignore + id mismatch + missing rpc/state**, not `Tr_Tr` vs `Tr_Tre`.

### Fix (dedicated)

1. On team SetProperty: **do not** host-spawn id=129 for joiner — log await joiner CWO
2. On joiner Entity pawn CWO (`Tr_Tr`/`Ct_Ct`): **echo** HasServerTime (codec rebuild from parsed fields) + **rpc=1** post-create (phone `003536_026`) + register living pawn
3. Echo inbound `WorldObjectRpc` with HasServerTime
4. Unreliable `WorldObjectState` standing ticks (~50ms) for registered pawn ids; relay owner State if sent

Retest markers: `await joiner CreateWorldObject` → `TX echo CreateWorldObject … id=257` → `TX WorldObjectRpc rpc=1` → `TX echo WorldObjectRpc` → `TX WorldObjectState Unreliable`.

## Sequence (phone host → joiner) — probe ground truth

| # | Opcode | Name | Role |
|---|--------|------|------|
| 0 | 12 | JoinRoomResponse | Thin Found ~148B, `C2=0`, empty actor props |
| 1 | 102 | SetInternalProperty | Host nick — code **255** |
| 2 | 101 | SetProperty ×5 | Host uid / badge / from_lobby / **avatar** / ping |
| 3 | 200 | CreateWorldObject ×8 | Scene managers (`gac=255`) |
| 4 | 100 | SetProperties | Room **`C2=10`** (actor 0) — **InitWaiting unlock** |
| 5 | 101 | SetProperty | money |
| 6 | 101 / 200 | team + pawn | Host-self: team then CWO id=129; **joiner**: team then **joiner** CWO (own netId) echoed by host |
| 7 | 203 / 204 | Rpc / State | rpc=1 after create; State Unreliable stream |

Probe may TX Spectator early (fallback timer) before host bootstrap — ignore for host fidelity.

## Dedicated host behaviour (aligned)

1. Thin Found: room props + actor names; **no** actor props in gap (≈ phone 148B shape; Server name instead of `player_*`). Match lists **all** actors (shared truth); `MaxActorsHint ≥ count`. Lobby stays Server+self illusion.
2. **Await joiner** early identity (`uid` + `from_lobby` + `avatar|ping`), or 15s soft timeout — **per peer**.
3. Host SIP nick → uid=`Offline` → badge → from_lobby → **synthetic JPEG avatar** → ping (HasServerTime) — TX to **that** peer only.
4. SceneManager CreateWorldObject ×8 (same bodies as phone) — peer-local.
5. Room `C2=10` — **INIT unlocked** on that peer’s connection (`BootstrapSent`).
6. Host money → Server `team=Spectator(3)` → joiner money.
7. Inbound SetProperty team Tr/Ct → await joiner CWO (do not invent id=129).
8. On JoinRoom Found: **ActorJoinedEvent** to existing peers (dynamic roster). Joiner Entity CWO → echo + rpc=1 to **INIT-ready** peers; LZ4 avatar → echo uncompressed broadcast (all room peers); WorldObjectRpc → echo to ready peers; WorldObjectState → **relay** owner fat ticks to other ready peers (prefer over host thin standing).
9. Late joiner after INIT: **match-state snapshot** (live room C2/Time/Round/… + other actors’
   team/avatar/money) then **living-pawn snapshot** (existing Entity CWO + rpc=1) **once** to
   that peer. Peer-local `C2=10` INIT must not overwrite live `room.RoomC2`.
### Server Spectator without UI

- Assign `team=3` on actor1 after C2 (same SetProperty path). Prefer Spectator so Server never gets a fighting pawn.

### Pawn wire names + trailing (after fus tag 101)

| Team | Wire name (5 chars) | Capture | Pos (approx) | Quat (approx) |
|------|---------------------|---------|--------------|---------------|
| Tr | `Tr_Tr` | `20260721_234843_*_len63` / joiner `004652_042` | host Sandstone / joiner own | `0, +0.7071, 0, 0.7071` |
| Ct | `Ct_Ct` | `20260722_003536_024_*_len63` | `34.33, 2.094, 0.96` | `0, −0.7071, 0, 0.7071` |

ASCII dumps often look like `Tr_Tre` / `Ct_Cte` because fus tag `0x65` (`'e'`) follows the name — **not** a sixth letter in the type string.

Built by `MatchCodec.BuildPawnSpawnPayload` / echo path parses joiner trailing then rebuilds.

## Captures

- **Phone-host unlock (ground truth):** `20260722_003523_*` Found 148B → `20260722_003532_*` identity+avatar → managers → **C2=10** → money; team/pawn in `003536_*`
- Log: `server/bin/Release/net8.0/logs/run-20260722_073504.log`
- **Team-select stuck:** `run-20260722_074631.log` / `20260722_004652_*` — host spawned 129, ignored joiner 257, no State
- **Crash retest:** `run-20260722_074123.log` / `20260722_004134_*` — type init on bad avatar b64; no managers/C2
- Earlier Tr learn: `20260721_234843_*`
- CT forced: `20260722_000923_*`

Dedicated probe vs phone host: see [`HANDOFF.md`](HANDOFF.md) probe command.
