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
    /// Fighter actor nrs marked dead this round. Populated ONLY by a proven combat death —
    /// actor <c>death</c> SetProperty (non-zero), or the Live/BombPlanted no-respawn fallback.
    /// A bare pawn <c>DestroyWorldObject</c> is NOT a death (phase respawn) — see
    /// <see cref="PendingCombatDestroy"/> and MATCH_WORLD.md "death vs respawn".
    /// </summary>
    public HashSet<byte> DeadActors { get; } = new();
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
    /// Allies C2=22: true after client-zero while waiting for
    /// <see cref="AlliesBuyLiveNotBeforeUtc"/> (dashboard / logs). Cleared on PreStart / Live.
    /// </summary>
    public bool AlliesBuyEndGraceArmed { get; set; }
    /// <summary>
    /// Allies C2=22: wall-clock earliest moment Live/C2=101 may TX —
    /// set at buy bag to <c>clientZeroUtc + BuyEndGrace</c>. Independent of
    /// <see cref="PhaseEndsUtc"/> so buy-zero and post-zero grace cannot share one deadline.
    /// <see cref="DateTime.MinValue"/> = unset.
    /// </summary>
    public DateTime AlliesBuyLiveNotBeforeUtc { get; set; } = DateTime.MinValue;
    /// <summary>
    /// Allies C2=22: absolute <c>ServerTimeSeconds</c> when the padded buy UI hits 0
    /// (<c>wireDeadline + BuyClientClockPad</c> = bag now + BuyPhase). Live / C2=101 must
    /// not TX while <c>ServerTimeSeconds() &lt; AlliesBuyClientZeroSec</c>. 0 = unset.
    /// </summary>
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
    /// Warmup/PreStart/Prep clients Destroy+Create their pawn on every phase transition
    /// (respawn — the phone host does the same: gold <c>run-20260722_100157</c> Destroy id then
    /// Create id, no round end), so a Destroy there is never a kill. Only during a live round
    /// does a Destroy with no respawn mean the fighter was eliminated.
    /// </summary>
    public static bool DestroyMayBeCombatDeath(MatchFlowPhase phase, bool bombPlanted = false) =>
        phase is MatchFlowPhase.RoundLive
            or MatchFlowPhase.BombPlanted
            // TDM: a pawn Destroy during live with no respawn is a fallback kill signal
            // (primary is the actor death prop). Warmup Destroys are respawns, never kills.
            or MatchFlowPhase.DeathMatchLive
            // Escalation combat is C2=31 PurchasePhase with bomb already planted — Destroy
            // there is a kill (MATCH_ESCALATION_PROBE). Ranked Prep keeps bombPlanted=false.
            || (phase == MatchFlowPhase.PurchasePhase && bombPlanted);

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

    public static (int AliveTr, int AliveCt, int TotalTr, int TotalCt) CountAliveFighters(
        MatchFlowState flow,
        IReadOnlyDictionary<(byte Actor, string Key), LobbyVariant> actorProps)
    {
        var aliveTr = 0;
        var aliveCt = 0;
        var totalTr = 0;
        var totalCt = 0;
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
        // Empty-roster wipe only during Live/BombPlanted (real mid-round disconnect).
        // Prep: require observed deaths (alive==0 with total>0) — do not invent RoundEnd
        // from a missing opposing team during buy time.
        var allowEmptyRosterWipe = flow.Phase is MatchFlowPhase.RoundLive
            or MatchFlowPhase.BombPlanted;

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
