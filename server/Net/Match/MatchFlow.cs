using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net.Match;

/// <summary>
/// Dedicated match phase machine — Allies phone-host loop (Warmup → PreStart → Prep → Live → RoundEnd).
/// Extracted from GameMatchHost for clearer transitions and future mode hooks.
/// </summary>
public enum MatchFlowPhase : byte
{
    WaitingPlayers = 0,
    Warmup = 1,
    /// <summary>C2=22 short freeze countdown + bomberId assign (phone <c>cnr</c>).</summary>
    WarmupWillFinish = 2,
    PurchasePhase = 3,
    RoundLive = 4,
    BombPlanted = 5,
    RoundEndPause = 6,
    MatchOver = 7,
    // DeathMatch / TDM phases (C0=DeathMatch). Separate from Ranked2v2 bomb loop above —
    // no prep / bomb / multi-round. See Modes/DeathMatch/GameMatchHost.DeathMatch.cs.
    /// <summary>C2=21 WarmUp (<c>_startingDuration</c>) — phone gold ≈3s before Live.</summary>
    DeathMatchWarmup = 8,
    /// <summary>C2=30 continuous TDM live (5min); kills bump room TrScore/CtScore, no wipe.</summary>
    DeathMatchLive = 9,
    /// <summary>C2=200 end bag published (FinalWinTeam+MvpPlayer); holding before FinalHud.</summary>
    DeathMatchEnded = 10,
    /// <summary>C2=201 FinalHud published; holding before teardown C2=255.</summary>
    DeathMatchFinalHud = 11,
    /// <summary>Allies half-time intro C2=111 after round 7 (not round-end UI).</summary>
    HalfTimeIntro = 12,
    /// <summary>Allies half-time swap C2=112 (<c>swapped_team</c> + score flip).</summary>
    HalfTimeSwap = 13,
    /// <summary>Allies half-time transition C2=113 before round 8 PreStart.</summary>
    HalfTimeTransition = 14,
    /// <summary>Allies C2=11 DeathMatchPreWarmup / freeforall after WaitingPlayers (first match start).</summary>
    AlliesPreWarmup = 15,
}

/// <summary>Per-room match clock — C2 / Time / Round / scores / wipe state.
/// Filled by observe-then-forward on RX (MATCH_ARCHITECTURE.md): <see cref="DeadActors"/>,
/// <see cref="BombPlantedUtc"/>+fuse, <see cref="Phase"/> + <see cref="PhaseEndsUtc"/>
/// (host C2; wipe/defuse/explode/plant override timers). Living pawns live on the room.
/// </summary>
public sealed class MatchFlowState
{
    public MatchFlowPhase Phase { get; set; } = MatchFlowPhase.WaitingPlayers;
    public DateTime PhaseEndsUtc { get; set; } = DateTime.MinValue;
    /// <summary>1-based round index while prep / live / between rounds.</summary>
    public int RoundIndex { get; set; }
    public int ScoreTr { get; set; }
    public int ScoreCt { get; set; }
    /// <summary>Consecutive round losses — phone-host <c>TrCoLosses</c>/<c>CtCoLosses</c>.</summary>
    public int CoLossesTr { get; set; }
    public int CoLossesCt { get; set; }
    public bool BombPlanted { get; set; }
    /// <summary>
    /// UTC moment the host accepted a real <c>BombManager</c> plant Rpc during
    /// <see cref="MatchFlowPhase.RoundLive"/> (fuse start). Explode only when
    /// <c>BombPlanted</c> and <c>UtcNow &gt;= BombPlantedUtc + BombFuse</c> (~40s).
    /// <see cref="DateTime.MinValue"/> = no fuse — never invent / re-anchor.
    /// </summary>
    public DateTime BombPlantedUtc { get; set; } = DateTime.MinValue;
    /// <summary>
    /// Legacy one-bomb/round latch. Must stay <c>false</c> — never defer PreStart/Prep
    /// plants into Live (deferred leftovers invented C2=40 / free T wins). Cleared on
    /// WarmUp / PreStart / HardReset / RoundEnd with the other bomb flags.
    /// </summary>
    public bool PendingBombPlant { get; set; }
    public int BomberActorNr { get; set; }
    /// <summary>
    /// Actor who actually planted the bomb this round (may differ from
    /// <see cref="BomberActorNr"/>: the bomb is a carried entity and can be handed off or
    /// dropped). Evidence for the plant MVP; 0 = no plant this round.
    /// </summary>
    public byte RoundPlanterActorNr { get; set; }
    /// <summary>
    /// Actor who actually defused the bomb this round — evidence for the defuse MVP. Previously
    /// the award fell back to «first Ct in dictionary order», which is not the defuser.
    /// 0 = no defuse this round.
    /// </summary>
    public byte RoundDefuseActorNr { get; set; }
    /// <summary>Per-round eliminations (actor → kills), written by the host master.</summary>
    /// <remarks>
    /// The bomb family publishes <c>round_kills</c> actor props exactly like Duel does, and those
    /// stay the wire truth. This mirror exists because a leaver's props can be dropped mid-round;
    /// the award must survive that.
    /// </remarks>
    public Dictionary<byte, int> RoundKills { get; } = new();
    /// <summary>
    /// Fighter actor nrs marked dead this round. Populated ONLY by a proven combat death —
    /// actor <c>death</c> SetProperty (non-zero), or the Live/BombPlanted no-respawn fallback.
    /// A bare pawn <c>DestroyWorldObject</c> is NOT a death (phase respawn) — see
    /// <see cref="PendingCombatDestroy"/> and MATCH_WORLD.md "death vs respawn".
    /// </summary>
    public HashSet<byte> DeadActors { get; } = new();
    /// <summary>
    /// Fighters that actually entered the current round (actor → team), frozen when combat
    /// starts (C2=31 for the bomb family, Live for classic Ranked).
    /// </summary>
    /// <remarks>
    /// Wipe must be judged against <b>who is in this round</b>, not against every team prop the
    /// room has ever seen. Issue #33: a player who left, or who never spawned this round, kept a
    /// stale <c>team</c> prop and made the "alive" counts wrong — one client saw a wipe, others did
    /// not, and the round ended on some peers only. Frozen per round and torn down with the bomb
    /// authority, so all four bomb-family modes (and classic Ranked) answer the same question the
    /// same way.
    /// </remarks>
    public Dictionary<byte, MatchTeam> RoundRoster { get; } = new();
    /// <summary>
    /// Owners whose LivingPawn was Destroyed during <see cref="MatchFlowPhase.RoundLive"/> /
    /// <see cref="MatchFlowPhase.BombPlanted"/> with no respawn yet, keyed to the Destroy time.
    /// Deferred combat-death fallback: if no CreateWorldObject re-spawns the owner within
    /// <c>MatchFlowTestParams.DestroyDeathGrace</c> (and no <c>death</c> prop already resolved it),
    /// count it as an elimination. During Warmup/PreStart/Prep this stays empty — those Destroys
    /// are phase respawns, never kills.
    /// </summary>
    public Dictionary<byte, DateTime> PendingCombatDestroy { get; } = new();
    /// <summary>Pending round-end reason when wipe/plant fires mid-phase.</summary>
    public string? PendingEndReason { get; set; }
    public MatchTeam PendingWinner { get; set; }
    /// <summary>One 5s prep extension when a living fighter picked team but has no pawn yet.</summary>
    public bool PrepSpawnExtensionUsed { get; set; }
    /// <summary>
    /// Legacy Allies buy-pad gate fields — cleared on PreStart/combat; unused on gold path
    /// (C2=22 Time=RST=now → stay on C2=31). Kept so older snapshots/deserializers stay stable.
    /// </summary>
    public bool AlliesBuyEndGraceArmed { get; set; }
    /// <summary>Legacy — unused on gold Allies path. Cleared on PreStart / combat.</summary>
    public DateTime AlliesBuyLiveNotBeforeUtc { get; set; } = DateTime.MinValue;
    /// <summary>Legacy — unused on gold Allies path. Cleared on PreStart / combat.</summary>
    public DateTime AlliesBuyZeroUtc { get; set; } = DateTime.MinValue;
    /// <summary>Legacy — unused on gold Allies path. Cleared on PreStart / combat.</summary>
    public double AlliesBuyClientZeroSec { get; set; }
    /// <summary>
    /// Escalation: C2=31 combat bag already published this round (after C2=40 auto-plant gap).
    /// Cleared on PreStart / RoundEnd.
    /// </summary>
    public bool EscalationCombatStarted { get; set; }
    /// <summary>Escalation active plant site (0/1) — published as room <c>BombSite</c> on C2=22.</summary>
    public byte EscalationBombSite { get; set; }
    /// <summary>
    /// <see cref="RoundIndex"/> that already committed a RoundEnd score bump. Prevents
    /// double +1 when wipe/defuse/timeout race while phase is stuck or Prep still allows
    /// combat signals. 0 = none committed this match.
    /// </summary>
    public int RoundEndCommittedRound { get; set; }
    /// <summary>
    /// Allies half-time swap completed (C2=112 + server-forced team flip). Set once per match.
    /// </summary>
    public bool TeamsSwapped { get; set; }
    /// <summary>
    /// After <see cref="MatchFlowPhase.MatchOver"/> / MatchResults: when
    /// <see cref="PhaseEndsUtc"/> elapses, host tears down 7777 and returns lobby to idle
    /// (mode/map kept). Armed once per series end; cleared when fired or on HardReset.
    /// </summary>
    public bool LobbyReturnPending { get; set; }
    /// <summary>
    /// Last accepted BombManager plant Rpc payload (Allies field=3) — kept while
    /// <see cref="BombPlanted"/> so late-join / reconnect peers get the same plant fan-out
    /// snapshot (C2=40 room props alone do not spawn the bomb mesh). Cleared with bomb authority.
    /// </summary>
    public byte[]? LastBombPlantPayload { get; set; }
    public byte LastBombPlantField { get; set; }
    public byte LastBombPlantRpcId { get; set; } = 2;
    public double LastBombPlantTimeValue { get; set; }

    /// <summary>
    /// Duel: loadout chosen for WaitingPlayers C2=10 bag (random preset; null modifier).
    /// Cleared on PreWarmup / rematch.
    /// </summary>
    public Modes.Duel.DuelLoadouts.Loadout? DuelLobbyLoadout { get; set; }
    /// <summary>Duel: active round loadout (preset or modifier's own).</summary>
    public Modes.Duel.DuelLoadouts.Loadout? DuelActiveLoadout { get; set; }
    /// <summary>Duel: active <c>current_round_modifier_id</c> wire string, or null.</summary>
    public string? DuelActiveModifierId { get; set; }
    /// <summary>
    /// Duel: cumulative <c>used_round_modifier_ids</c> for this match (probe accumulates).
    /// </summary>
    public List<string> DuelUsedRoundModifierIds { get; } = new();
    /// <summary>
    /// Duel OnlyGrenades round: infinite HE semantics when grenade throw path exists
    /// (return on throw; drops still remove). Set only when that modifier is selected.
    /// </summary>
    public bool DuelInfiniteHeGrenades { get; set; }
}

/// <summary>Shared wipe / spawn / alive-count rules for match flow.</summary>
public static class MatchFlowRules
{
    /// <summary>
    /// Phases where a <b>proven combat death</b> (actor <c>death</c> prop) or an empty opposing
    /// team (disconnect, Live/BombPlanted only) can end the round. Prep allows wipe from
    /// observed deaths; PreStart does <b>not</b> — empty-team wipe there invented free
    /// RoundEnd scores after disconnect (latest.log rounds 6–8). Bare Destroy during
    /// PreStart/Prep is still a respawn (see <see cref="DestroyMayBeCombatDeath"/>).
    /// </summary>
    public static bool AllowsWipeCheck(MatchFlowPhase phase) =>
        phase is MatchFlowPhase.RoundLive
            or MatchFlowPhase.BombPlanted
            or MatchFlowPhase.PurchasePhase;

    /// <summary>
    /// Phases where a bare <c>DestroyWorldObject</c> may indicate a combat elimination.
    /// Warmup/PreStart clients Destroy+Create on phase transition (respawn). Combat:
    /// RoundLive / BombPlanted / TDM Live / Allies+Escalation C2=31 PurchasePhase.
    /// Generic Ranked Prep (PurchasePhase, not Allies) keeps Destroy as respawn —
    /// pass <paramref name="purchasePhaseIsCombat"/> for Allies Ranked2v2.
    /// </summary>
    public static bool DestroyMayBeCombatDeath(
        MatchFlowPhase phase,
        bool bombPlanted = false,
        bool purchasePhaseIsCombat = false) =>
        phase is MatchFlowPhase.RoundLive
            or MatchFlowPhase.BombPlanted
            // TDM: a pawn Destroy during live with no respawn is a fallback kill signal
            // (primary is the actor death prop). Warmup Destroys are respawns, never kills.
            or MatchFlowPhase.DeathMatchLive
            // Escalation: C2=31 with bomb planted. Allies: C2=31 combat before/after plant.
            // Ranked Prep: PurchasePhase + purchasePhaseIsCombat=false → respawn only.
            || (phase == MatchFlowPhase.PurchasePhase && (bombPlanted || purchasePhaseIsCombat));

    public static bool IsActorDead(
        MatchFlowState flow,
        IReadOnlyDictionary<(byte Actor, string Key), LobbyVariant> actorProps,
        byte actorNr)
    {
        if (flow.DeadActors.Contains(actorNr))
            return true;
        if (!actorProps.TryGetValue((actorNr, MatchRoomPropKeys.Death), out var death))
            return false;
        return death.Kind switch
        {
            LobbyVariantKind.Int => death.Int != 0,
            LobbyVariantKind.Byte => death.Byte != 0,
            LobbyVariantKind.Bool => death.Bool,
            _ => false,
        };
    }

    /// <summary>
    /// Living fighter on Tr/Ct picked team, is not dead, and has not TX CreateWorldObject this prep window.
    /// Dead corpses with no pawn must <b>not</b> trigger prep extension.
    /// </summary>
    public static bool FightersAwaitingSpawn(
        MatchFlowState flow,
        IReadOnlyDictionary<(byte Actor, string Key), LobbyVariant> actorProps,
        IEnumerable<byte> livingPawnOwners)
    {
        var spawnedOwners = livingPawnOwners.ToHashSet();
        foreach (var ((actor, key), value) in actorProps)
        {
            if (key != MatchRoomPropKeys.Team || actor == MatchHostActor.ActorNr)
                continue;
            if (value.Kind != LobbyVariantKind.Byte)
                continue;
            var team = (MatchTeam)value.Byte;
            if (team is not (MatchTeam.Tr or MatchTeam.Ct))
                continue;
            if (IsActorDead(flow, actorProps, actor))
                continue;
            if (!spawnedOwners.Contains(actor))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Freeze the fighters that entered this round. Call when combat starts; the roster then
    /// survives respawns and any later <c>team</c> prop churn (half-time, rejoins, spectators)
    /// until <see cref="ClearRoundRoster"/> at round end.
    /// </summary>
    public static void FreezeRoundRoster(
        MatchFlowState flow,
        IReadOnlyDictionary<(byte Actor, string Key), LobbyVariant> actorProps)
    {
        flow.RoundRoster.Clear();
        // Snapshot, deliberately. A late joiner's `team` prop arrives after this point and is not
        // added (cold start = dead for the running round). Spawn timing is NOT used as the entry
        // proof instead: clients create their pawn shortly after C2=31 (latest.log), so a fighter
        // who legitimately plays the round can still have no pawn here and would be dropped —
        // that would fake a wipe on the other side.
        foreach (var ((actor, key), value) in actorProps)
        {
            if (key != MatchRoomPropKeys.Team || actor == MatchHostActor.ActorNr)
                continue;
            if (value.Kind != LobbyVariantKind.Byte)
                continue;
            var team = (MatchTeam)value.Byte;
            if (team is not (MatchTeam.Tr or MatchTeam.Ct))
                continue;
            flow.RoundRoster[actor] = team;
        }
    }

    /// <summary>Tear the frozen roster down with the bomb authority (round end / PreStart).</summary>
    public static void ClearRoundRoster(MatchFlowState flow) => flow.RoundRoster.Clear();

    /// <summary>
    /// Drop one actor from the frozen roster because it <b>left the room</b>. Without this a player
    /// who disconnects mid-round stays in <see cref="MatchFlowState.RoundRoster"/> as «alive», so
    /// the opposing side can never be wiped and the round runs to the 109s timeout instead of
    /// ending on the kill that decided it (latest.log round 3).
    /// </summary>
    /// <remarks>
    /// Disconnect only. Switching to Spectator is <b>not</b> a leave: a fighter who died and then
    /// flipped his team prop must stay in the roster as dead, otherwise the side empties and
    /// «everyone is dead» turns into a different, invented wipe.
    /// </remarks>
    public static void RemoveFromRoundRoster(MatchFlowState flow, byte actorNr) =>
        flow.RoundRoster.Remove(actorNr);

    /// <summary>
    /// Is this actor part of the round that is being played? An empty roster means «not frozen
    /// yet» and accepts everyone (Warmup / pre-combat), so the check can be used unconditionally.
    /// </summary>
    /// <remarks>
    /// Issue #33: a pawn that belongs to somebody who never entered this round (late joiner still
    /// loading, spectator with a lingering pawn, player who already left) must never be turned into
    /// a combat death — that fake kill cost a round on the peers that saw the Destroy while the
    /// others kept the player alive.
    /// </remarks>
    public static bool IsRoundParticipant(MatchFlowState flow, byte actorNr) =>
        actorNr != 0
        && actorNr != MatchHostActor.ActorNr
        && (flow.RoundRoster.Count == 0 || flow.RoundRoster.ContainsKey(actorNr));

    /// <summary>
    /// Alive/total counts per side for the <b>current round</b>. Uses the frozen
    /// <see cref="MatchFlowState.RoundRoster"/> when it is populated, so a stale team prop (a
    /// player who left, never spawned, or rejoined) cannot fake a wipe (#33). Falls back to the
    /// live team props before combat freezes the roster.
    /// </summary>
    public static (int AliveTr, int AliveCt, int TotalTr, int TotalCt) CountAliveFighters(
        MatchFlowState flow,
        IReadOnlyDictionary<(byte Actor, string Key), LobbyVariant> actorProps)
    {
        var aliveTr = 0;
        var aliveCt = 0;
        var totalTr = 0;
        var totalCt = 0;

        if (flow.RoundRoster.Count > 0)
        {
            foreach (var (actor, team) in flow.RoundRoster)
            {
                if (team == MatchTeam.Tr) totalTr++;
                else totalCt++;
                if (IsActorDead(flow, actorProps, actor))
                    continue;
                if (team == MatchTeam.Tr) aliveTr++;
                else aliveCt++;
            }
            return (aliveTr, aliveCt, totalTr, totalCt);
        }

        foreach (var ((actor, key), value) in actorProps)
        {
            if (key != MatchRoomPropKeys.Team || actor == MatchHostActor.ActorNr)
                continue;
            if (value.Kind != LobbyVariantKind.Byte)
                continue;
            var team = (MatchTeam)value.Byte;
            if (team is not (MatchTeam.Tr or MatchTeam.Ct))
                continue;
            if (team == MatchTeam.Tr) totalTr++;
            else totalCt++;
            if (IsActorDead(flow, actorProps, actor))
                continue;
            if (team == MatchTeam.Tr) aliveTr++;
            else aliveCt++;
        }
        return (aliveTr, aliveCt, totalTr, totalCt);
    }

    /// <summary>
    /// Phone-host wipe rules: all CT dead → T; all T dead → CT unless bomb planted;
    /// empty team after disconnect counts as wipe.
    /// </summary>
    public static bool TryResolveWipe(
        MatchFlowState flow,
        IReadOnlyDictionary<(byte Actor, string Key), LobbyVariant> actorProps,
        bool bombPlanted,
        out MatchTeam winner,
        out string reason)
    {
        winner = MatchTeam.None;
        reason = "";
        if (!AllowsWipeCheck(flow.Phase))
            return false;
        if (flow.PendingEndReason is not null)
            return false;

        var (aliveTr, aliveCt, totalTr, totalCt) = CountAliveFighters(flow, actorProps);
        var planted = bombPlanted || flow.BombPlanted;
        // Whole-team leave. This must work wherever the wipe check is allowed at all, including
        // the bomb family, whose combat phase is PurchasePhase (C2=31) — limiting it to
        // RoundLive/BombPlanted meant «every CT disconnected» produced no result and the round
        // rolled on to the timeout. Buy time (no combat yet) still cannot invent a result:
        // AllowsWipeCheck above is false there, so we never reach this.
        var allowEmptyRosterWipe = AllowsWipeCheck(flow.Phase);

        if (allowEmptyRosterWipe && totalTr > 0 && totalCt == 0)
        {
            flow.PendingWinner = MatchTeam.Tr;
            flow.PendingEndReason = "wipe-ct";
            winner = MatchTeam.Tr;
            reason = "wipe-ct";
            return true;
        }

        if (allowEmptyRosterWipe && totalCt > 0 && totalTr == 0)
        {
            if (planted)
                return false;
            flow.PendingWinner = MatchTeam.Ct;
            flow.PendingEndReason = "wipe-tr";
            winner = MatchTeam.Ct;
            reason = "wipe-tr";
            return true;
        }

        if (totalCt > 0 && aliveCt == 0)
        {
            flow.PendingWinner = MatchTeam.Tr;
            flow.PendingEndReason = "wipe-ct";
            winner = MatchTeam.Tr;
            reason = "wipe-ct";
            return true;
        }

        if (totalTr > 0 && aliveTr == 0)
        {
            if (planted)
                return false;
            flow.PendingWinner = MatchTeam.Ct;
            flow.PendingEndReason = "wipe-tr";
            winner = MatchTeam.Ct;
            reason = "wipe-tr";
            return true;
        }

        return false;
    }

    /// <summary>
    /// A fighter changed his <c>team</c> prop after the round was frozen. Inside a running round
    /// that is a death, never a transfer: switching to the other side is punished with a death,
    /// and switching to Spectator means he stops playing («пропадает», can only watch).
    /// </summary>
    /// <remarks>
    /// The frozen entry is kept (and the actor is marked dead) instead of being removed. Removing
    /// him would drop the side out of the count entirely — with both totals at zero the
    /// empty-team rule no longer applies and «everyone on this side is gone» would quietly become
    /// «no result, wait for the timeout». Kept as dead, the wipe still resolves.
    /// </remarks>
    /// <returns>
    /// <c>true</c> when this was a mid-round switch (the caller must count the actor as dead);
    /// <c>false</c> for a no-op: not in this round, same team, or a phase where no result is being
    /// played out (buy time / warmup — the client respawns, and that is not a death).
    /// </returns>
    public static bool IsMidRoundTeamSwitch(
        MatchFlowState flow,
        byte actorNr,
        MatchTeam newTeam,
        out MatchTeam frozenTeam)
    {
        frozenTeam = MatchTeam.None;
        if (!flow.RoundRoster.TryGetValue(actorNr, out frozenTeam))
            return false;
        if (frozenTeam == newTeam)
            return false;
        return AllowsWipeCheck(flow.Phase);
    }

    public static MatchTeam GetActorTeam(
        IReadOnlyDictionary<(byte Actor, string Key), LobbyVariant> actorProps,
        byte actorNr)
    {
        if (actorProps.TryGetValue((actorNr, MatchRoomPropKeys.Team), out var v)
            && v.Kind == LobbyVariantKind.Byte)
            return (MatchTeam)v.Byte;
        return MatchTeam.None;
    }
}
