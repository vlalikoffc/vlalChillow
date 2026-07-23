# Mode: DeathMatch

**Owner agent:** mode-deathmatch (see `decompiled/AGENT_SWARM.md`)

**Status:** IMPLEMENTED — dedicated host drives WarmUp → Live → End from phone gold.

**SelectedLevels (probe run-20260722_213859):** Perimeter, Hanari, Favelas, Arena, Calypso, Sandyards, TrainingOutside, Village

## TDM = short start → 5 min → team with most kills wins

No bomb, no prep phase, no multi-round wipe (that is Ranked2v2 / Allies). One continuous round;
players respawn (new `CreateWorldObject`) after each death.

### C2 phase machine (phone gold `/tmp/tdm-probe.log`)

| C2  | MatchC2States           | Meaning                                                                 |
|-----|-------------------------|------------------------------------------------------------------------|
| 10  | `WaitingPlayers`        | Room open, waiting for both teams.                                      |
| 11  | `DeathMatchPreWarmup`   | Phone freeforall (~40s gold); **dedicated skips** → WarmUp when both ready. |
| 21  | `WarmUp`                | `_startingDuration` — phone gold ≈**3s** (RX#2192→#2383). **Not** 30s.  |
| 30  | `DeathMatchLive`        | Continuous TDM (~5min gold). Room `TrScore`/`CtScore` bump per kill.    |
| 200 | `DeathMatchEnded`       | End bag: `FinalWinTeam`{isDraw,isGiveUp,team}, `MvpPlayer`, `FinalPlayers?`, `Time`. |
| 201 | `FinalHud`              | Match WIN/LOSE screen (reads `FinalWinTeam`).                           |
| 255 | `MatchTeardown`         | Post-FinalHud teardown, then disconnect.                               |

No C2=22 on TDM (that is Defuse `_roundStartingTime`). Long lock on C2=21 was a host miswire.

### Dedicated flow (`GameMatchHost.DeathMatch.cs`)

`WaitingPlayers(10)` → both teams present → `WarmUp(21)` **3s** (`DeathMatchFlowParams.Warmup`,
phone gold / `_startingDuration`) → `Live(30)` 5min (`DeathMatchFlowParams.MatchDuration`) →
`End(200)` (~3s `EndBagHold`) → `FinalHud(201)` (~6s `FinalHudPause`) → `Teardown(255)` →
`MatchOver`. Log marker: `[tdm-flow]` with `C2=` + `dur=` + `src=`.
`TickMatchFlowRoom` branches to `TickDeathMatchFlowRoom` when `C0=DeathMatch`; Ranked2v2 loop untouched.

### Kill / score model (gold RX#3782–3791) — host is the room MASTER

**Critical (bug fix run-20260722_2303 / user report "0 HP freeze, no death HUD, no respawn"):**
the victim client does **not** self-author its death on the wire. Gold and the dedicated
`latest.log` both show, per kill:

1. the **attacker** fires a pawn damage `WorldObjectRpc` **`field=5`** (77/78B) on the **victim's**
   pawn (multiple hits, non-lethal each — RX#3782 / `latest.log` RX id=386 from the killer's peer);
2. the **master** publishes the victim `DestroyWorldObject` (RX#3785) + `death`++ (RX#3788);
3. the master bumps the **opposing (killer) team** flat room score — Tr victim → `CtScore++`,
   Ct victim → `TrScore++` — via `SetProperty actor=0` (`NoteDeathMatchKill`, RX#3787);
4. the **killer** owns and TX its own `kills`/`fair_kills`/`score` (host relays; RX#3789–3791).

On the dedicated host **we are the master**, so we author 2+3. The victim, left at 0 HP, only
streams `WorldObjectState` (frozen) until it receives the Destroy of its own pawn → then it shows
the death HUD and respawns via a fresh `CreateWorldObject`. When the old host merely relayed the
`field=5` damage + the killer's `kills`, the victim never died → frozen at 0 HP, no HUD, no
respawn, team score stuck at 0 (kill "not counted"). **No** `EnterRoundEnd` / wipe.

**Master death authoring (`GameMatchHost.DeathMatchCombat.cs`):**
- `NoteDeathMatchDamage` records attacker→victim from each `field=5` hit (`LastCombatDamage`);
- the killer's `kills`++ (`NoteDeathMatchKillByKiller`) resolves the victim from that attribution
  and calls `AuthorDeathMatchDeath` → TX victim pawn `DestroyWorldObject` + `death`++ (cumulative,
  **not** reset on TDM respawn) + team score, de-duped per victim (`LastAuthoredDeath`);
- fallback: a Live pawn Destroy with no respawn (or a relayed `death` prop) still reaches
  `NoteActorDeath` → `AuthorDeathMatchDeath` (same de-dup) for kills that arrive without a fresh
  `field=5` (grenade / fall / race).

Do **not** decode the `field=5` payload to guess HP — the kill is confirmed by the first-class
`kills` counter, `field=5` is used only for victim attribution.

**Master death signature incl. RadarManager refresh (bug fix run-20260722_2324 / user report
"kills count but HUD/everything disappears, camera frozen where it was, no death screen /
respawn"):** with 2 above the kill *counted* (score + kill feed) but the victim's death **view**
never engaged — pawn destroyed (HUD gone, camera frozen) yet no death screen / respawn. Diffing
gold vs `latest.log` at every kill, the **only** master-authored packet the old host omitted was a
**RadarManager `Rpc(7)`** refresh — `oet(dwa)`, 0-payload full radar/occlusion rebuild
(`Client/Chillow/StandChillow/Netcode/SceneManagers/RadarManager.cs` `[Rpc(7)] oet`). Gold sends
**two** per kill, bracketing the death (`gaa=3`, `field=7`, no payload):

- guest-killer kill (RX#3782–3793): `field=5` → `Destroy` → **radar7 (RX#3786)** → `CtScore` →
  `death` → killer `kills`/`fair_kills`/`score` → **radar7 (RX#3793)**;
- host-killer kill (RX#4259–4269, **no `field=5`** to the victim — the host is the damage
  authority): killer props → `TrScore` → `Destroy` → **radar7 (RX#4266)** → `death` →
  **radar7 (RX#4269)**.

Because gold sends the radar refresh on **every** death (host-killer and guest-killer alike) it is
part of the master death authoring, not a side effect of `field=5`. `AuthorDeathMatchDeath` now
TX it (via `MatchCodec.BuildRadarManagerDeathRefreshRpc`, our RadarManager scene id
`MatchSceneManagers.RadarManagerObjectId`=6 — clients already RX radar RPCs on id=6) in gold order:
`Destroy → radar7 → team score → death → radar7`. Codec builder, not a blob replay.

**Open gap if this is insufficient:** should the victim view still not engage, the remaining
difference is the **damage authority model** — in gold the master computes lethality/HP from the
`field=5` `oqk(dur attacker, bzw damage, dwa)` RPC and the host-killer path proves the master owns
death, whereas our host infers the kill from the killer's `kills`++ (which can fire when the
victim's own HP has not locally reached 0). Re-implementing master-side HP from `field=5` `bzw`
damage is the next step, but the `bzw` layout is **not reversed** yet — do not invent it.

### Match end (gold RX#18692)

`FinalWinTeam` nested `{ isDraw:Bool, isGiveUp:Bool, team:Byte }` — `team` is the side with more
kills (`MatchTeam`; gold `team=1`=Tr won 17–10), `isDraw` when scores equal. `MvpPlayer` (Byte) =
actor with most `kills`. See **unknowns** below for `FinalPlayers`.

### Free buy / loadout — weapon & character selection (fix run-20260723)

**Symptom:** in TDM the buy menu is free but a joiner spawned with a **random/default weapon** —
their pick was ignored.

**Root cause:** the pawn `CreateWorldObject` trailing is **client-authored** and carries the spawn
pose (44B) **plus a length-prefixed loadout/character tag** appended after it (empty on a default
45B trailing; e.g. `"AgentCTLincoln"` in a 59B `Ct_Ct` trailing — gold RX#223, and
`latest.log` `id=386 owner=3 trailLen=59`). `HandleCreateWorldObject` used to **rebuild** a
canonical 45B pose via `BuildPawnSpawnPayload`, which **dropped the loadout tag**, so the echo went
out at `len=63` (45B trailing) regardless of input → default loadout on every peer (and the late-
join snapshot, since `LivingPawn.TrailingPayload` was the rebuilt one).

**Fix:** relay the client trailing **verbatim** (owner authors its own pawn incl. buy/character;
this is normal live relay, not a capture replay or an invented field). Pose is still parsed for the
host standing-State ticks, but the bytes echoed + stored are the original. `latest.log` marker
`[tdm-buy] pawn CWO owner=… loadout='…' — relayed verbatim`. Money is **not** blocked/pushed in TDM
(free; screenshot stays $18000). Ranked (paid) is unaffected — same client-authored pawn path.

### 100/101/102 host handling (match-critical keys)

Opcodes are **envelopes**; keys are strings through the one `fzp/fzq` variant codec. Host state +
relay, per key class:

| Class | Keys | Host action |
|-------|------|-------------|
| Match flow (room, actor 0) | `C2`, `Time`, `TrScore`, `CtScore`, `FinalWinTeam`, `MvpPlayer` | **host-authored** by the TDM phase machine + `AuthorDeathMatchDeath`; also mirrored into `room.ActorProps`. |
| Combat counters (per actor) | `kills`, `fair_kills`, `death`, `score`, `round_kills` | applied to state **and** relayed; `kills`++ / `death` drive the master-death path (logged `[tdm-death] prop actor=… key old→new …`). |
| Identity | `uid`, `team`, `avatar`, `badgeId`, `from_lobby`, `ping` | applied + relayed (ping = server RTT). |
| Buy / cosmetic | `glovesId_Ct`, `glovesId_Tr`, `money` | applied + relayed as-is; logged `[tdm-buy] prop …`. Weapon/character itself is **not** a prop — it rides the pawn CWO trailing (above). |
| Unknown | anything else | **generic relay** (state + broadcast) + logged if buy-like, so it can be reversed — **not** invented. |

### Silent-kill diagnostics ([tdm-death])

The primary TDM kill signal is the killer's `kills`++, which resolves the victim from the most
recent `field=5` damage attribution. If that attribution is **missing/stale** the kill was
previously **dropped silently** (this game's victim never self-Destroys, so the Destroy-grace
fallback never fires → no HUD/respawn/score — the "silent Destroy" symptom). Now:

- every step is traced with `[tdm-death]` (damage attribution, `kills`/`death` old→new,
  `AuthorDeathMatchDeath` ENTER / SKIP-reason / PROCEED, each TX);
- a **single-enemy fallback** resolves the victim when attribution is lost **and** exactly one
  living enemy fighter exists (unambiguous; no invented wire). Ambiguous (2v2+) cases are logged
  loudly instead of guessing.

Grep the next run: `grep -E '\[tdm-death\]|\[tdm-buy\]' latest.log`.

### Unknowns (not faked)

- **`FinalPlayers`** — phone gold serializes a `ByteArray` (len 3 in the 3-actor probe). Inner
  layout (per-player scoreboard summary?) is **not reversed**, so the dedicated host **omits** it
  (end bag count 4 vs phone 5). Do not invent a layout.
- **Free buy / economy** — TDM money model not captured here; host does not push `money` in TDM
  (Ranked pushes `RoundStartMoney`). Left unimplemented until gold shows the buy semantics.

**Family:** dm — bomb-family modes may share phase *interfaces* later; implementations stay in this folder only. **Do not import sibling Modes/\***.

## Allowed edit paths

- `server/Net/Match/Modes/DeathMatch/**`
- Evidence notes under `decompiled/` that are mode-specific (do not invent wire)

## Forbidden

- Editing other `Modes/<Other>/` trees
- Pasting capture bytes into host replies
- Inventing C2 / WinTeam / timer fields without decompile or gold log evidence
