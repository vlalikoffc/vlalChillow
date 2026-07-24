using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using StandChillow.LanServer.Net.Match.Logic.Defuse;

namespace StandChillow.LanServer.Net;

public sealed partial class GameMatchHost
{
    private const string EscalationModeId = "Escalation";

    private bool IsEscalationRoom(MatchRoom room)
    {
        if (string.Equals(_matchGameModeId, EscalationModeId, StringComparison.Ordinal))
            return true;
        lock (_roomGate)
        {
            return room.ActorProps.TryGetValue((0, MatchRoomPropKeys.C0), out var c0)
                && c0.Kind == LobbyVariantKind.String
                && string.Equals(c0.String, EscalationModeId, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Escalation phase machine — gold <c>run-20260723_140524</c>
    /// (<c>MATCH_ESCALATION_PROBE.md</c>): C2=22 prep → auto-plant C2=40 → C2=31 combat
    /// (bomb already planted). Round ends on defuse / wipe / fuse — <b>no</b> fixed round clock.
    /// </summary>
    private void TickEscalationFlowRoom(MatchRoom room)
    {
        MatchFlowPhase phase;
        DateTime ends;
        DateTime bombPlantedUtc;
        string? pendingReason;
        MatchTeam pendingWinner;
        bool bombPlanted;
        bool combatStarted;
        lock (_roomGate)
        {
            phase = room.Flow.Phase;
            ends = room.Flow.PhaseEndsUtc;
            bombPlantedUtc = room.Flow.BombPlantedUtc;
            pendingReason = room.Flow.PendingEndReason;
            pendingWinner = room.Flow.PendingWinner;
            bombPlanted = room.Flow.BombPlanted;
            combatStarted = room.Flow.EscalationCombatStarted;
            if (phase == MatchFlowPhase.WaitingPlayers)
            {
                if (!BothFightingTeamsPresent(room))
                    return;
                if (!MatchHostSettings.MatchStartArmed)
                    return;
            }
            else if (phase == MatchFlowPhase.MatchOver)
            {
                // Handled below after lock.
            }
        }

        if (phase == MatchFlowPhase.MatchOver)
        {
            TryFireMatchOverLobbyReturn(room, ends);
            return;
        }

        if (MatchFlowRules.AllowsWipeCheck(phase))
        {
            if (pendingReason is not null)
            {
                EnterRoundEndPause(room, pendingWinner, pendingReason);
                return;
            }

            if (TryResolveWipeImmediate(room, bombPlanted || phase == MatchFlowPhase.BombPlanted))
                return;
        }

        // Fuse: only after auto-plant (plantUtc set). No RoundLive timeout fantasy.
        // Shared DefuseTimer — Escalation gold plantUtc+BombFuse (Logic/Defuse).
        if (bombPlanted && bombPlantedUtc != DateTime.MinValue
            && (phase == MatchFlowPhase.BombPlanted || combatStarted))
        {
            if (combatStarted)
            {
                var now = DateTime.UtcNow;
                var tick = DefuseTimer.TickFuse(
                    bombPlantedUtc, now, out var fuseEnd, out var elapsed,
                    EscalationFlowParams.BombFuse);
                if (tick == FuseTickResult.Expired)
                {
                    Console.WriteLine(
                        "[match-host] escalation: bomb fuse expired → T win " +
                        $"(plantUtc={bombPlantedUtc:O} elapsed={elapsed:F1}s " +
                        $"fuse={EscalationFlowParams.BombFuse.TotalSeconds:0}s)");
                    PostServerDebugChat(
                        $"взрыв fuse={elapsed:F1}s (plant+{EscalationFlowParams.BombFuse.TotalSeconds:0}s)");
                    EnterRoundEndPause(room, MatchTeam.Tr, "bomb-explode");
                    return;
                }

                if (ends != fuseEnd)
                {
                    lock (_roomGate)
                        room.Flow.PhaseEndsUtc = fuseEnd;
                }
                return;
            }

            // Post-plant gap before C2=31 combat bag.
            if (DateTime.UtcNow < ends)
                return;
            EnterEscalationCombat(room);
            return;
        }

        if (DateTime.UtcNow < ends)
            return;

        switch (phase)
        {
            case MatchFlowPhase.WaitingPlayers:
                EnterWarmup(room);
                break;
            case MatchFlowPhase.Warmup:
                EnterEscalationPreStart(room);
                break;
            case MatchFlowPhase.WarmupWillFinish:
                EnterEscalationAutoPlant(room);
                break;
            case MatchFlowPhase.RoundEndPause:
                ContinueAfterRoundEndEscalation(room);
                break;
        }
    }

    private void ContinueAfterRoundEndEscalation(MatchRoom room)
    {
        int round;
        int scoreTr, scoreCt;
        lock (_roomGate)
        {
            round = room.Flow.RoundIndex;
            scoreTr = room.Flow.ScoreTr;
            scoreCt = room.Flow.ScoreCt;
        }

        if (MatchHostSettings.IsMatchSeriesOver(round, scoreTr, scoreCt))
        {
            EnterMatchResultsAndArmLobbyReturn(room, scoreTr, scoreCt, "escalation", round);
            return;
        }

        Console.WriteLine(
            $"[match-host] escalation: next round PreStart C2={MatchC2States.WarmupWillFinish} " +
            $"(round {round}→{round + 1}; skip WarmUp)");
        EnterEscalationPreStart(room);
    }

    /// <summary>
    /// Escalation PreStart C2=22 — gold: ReCreateSceneManager ×4, Round++, BombSite, money,
    /// pawn respawn. <b>No bomberId</b> (no carry plant). Duration ≈8s (visible prep/buy).
    /// </summary>
    private void EnterEscalationPreStart(MatchRoom room)
    {
        int round;
        byte bombSite;
        var dur = EscalationFlowParams.PreStart;
        var ends = DefuseTimer.ArmDeadline(dur);
        lock (_roomGate)
        {
            round = room.Flow.RoundIndex + 1;
            if (round < 1) round = 1;
            room.Flow.RoundIndex = round;
            room.Flow.Phase = MatchFlowPhase.WarmupWillFinish;
            room.Flow.PhaseEndsUtc = ends;
            room.Flow.PendingEndReason = null;
            room.Flow.PrepSpawnExtensionUsed = false;
            room.Flow.EscalationCombatStarted = false;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
            room.LastCombatDamage.Clear();
            bombSite = PickEscalationBombSite(room, round);
            room.Flow.EscalationBombSite = bombSite;
            foreach (var a in room.Actors)
            {
                if (a.Nr != MatchHostActor.ActorNr)
                    room.ActorProps[(a.Nr, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(0);
            }
        }

        ClearBombAuthority(room, $"Escalation PreStart C2=22 round={round} site={bombSite}");
        BroadcastEscalationReCreateSceneManagers(room);
        var nowSec = ServerTimeSeconds();
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.Round, LobbyVariant.FromInt(round)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.BombSite, LobbyVariant.FromByte(bombSite)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmupWillFinish)),
        ], reason: $"Escalation PreStart round={round} site={bombSite}");
        SetAllFightersMoney(room, MatchFlowTestParams.RoundStartMoney);
        ClearFighterDeathFlags(room);
        DestroyTrackedRoundEntities(room, reason: "Escalation PreStart");
        Console.WriteLine(
            $"[match-host] escalation: PreStart C2={MatchC2States.WarmupWillFinish} " +
            $"dur={dur.TotalSeconds:0}s round={round}/{MatchFlowTestParams.TotalRounds} " +
            $"BombSite={bombSite} money={MatchFlowTestParams.RoundStartMoney} " +
            "(no bomberId; ReCreate 4/5/6/8)");
        PostServerDebugChat($"Prep · раунд {round} · site {bombSite}");
    }

    /// <summary>
    /// Host auto-plant — BombManager field=3 + C2=40. Fuse starts here (~40s); no pawn carry.
    /// </summary>
    private void EnterEscalationAutoPlant(MatchRoom room)
    {
        byte bombSite;
        lock (_roomGate)
        {
            if (room.Flow.BombPlanted || room.Flow.Phase == MatchFlowPhase.BombPlanted)
            {
                Console.WriteLine("[match-host] escalation: auto-plant skipped — already planted");
                return;
            }
            bombSite = room.Flow.EscalationBombSite;
        }

        var (px, py, pz) = bombSite == 0
            ? EscalationFlowParams.PrisonBombSite0
            : EscalationFlowParams.PrisonBombSite1;
        var plantUtc = DateTime.UtcNow;
        var gap = EscalationFlowParams.PostPlantAnnounce;
        lock (_roomGate)
        {
            room.Flow.BombPlanted = true;
            room.Flow.BombPlantedUtc = plantUtc;
            room.Flow.PendingBombPlant = false;
            room.Flow.Phase = MatchFlowPhase.BombPlanted;
            room.Flow.PhaseEndsUtc = DefuseTimer.ArmDeadline(gap, plantUtc);
            room.Flow.EscalationCombatStarted = false;
        }

        var stime = NextServerTime();
        var plantPkt = MatchCodec.BuildBombManagerEscalationAutoPlantRpc(stime, px, py, pz);
        var nPlant = BroadcastInitReady(room, plantPkt, tag: "match_tx_BombManager_autoPlant");
        var nowSec = ServerTimeSeconds();
        var fuseSec = EscalationFlowParams.BombFuse.TotalSeconds;
        var deadline = DefuseTimer.FuseWireDeadlineSec(nowSec, EscalationFlowParams.BombFuse);
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.BombPlanted)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
        ], reason: "Escalation auto-plant C2=40", phaseDeadlineSec: deadline);
        Console.WriteLine(
            $"[match-host] escalation: auto-plant field={MatchFlowTestParams.BombManagerFieldEscalationAutoPlant} " +
            $"pos=({px:F2},{py:F2},{pz:F2}) site={bombSite} → C2=40 " +
            $"fuse={fuseSec:0}s gap→C2=31={gap.TotalSeconds:0}s TX peers={nPlant}");
        PostServerDebugChat($"бомба на site {bombSite} (fuse {fuseSec:0}s)");
    }

    /// <summary>
    /// C2=31 combat bag — bomb already planted; fuse continues from <see cref="MatchFlowState.BombPlantedUtc"/>.
    /// No C2=101 Live; round ends only via defuse / wipe / explode.
    /// </summary>
    private void EnterEscalationCombat(MatchRoom room)
    {
        DateTime plantUtc;
        lock (_roomGate)
        {
            if (room.Flow.EscalationCombatStarted)
                return;
            if (!room.Flow.BombPlanted || room.Flow.BombPlantedUtc == DateTime.MinValue)
            {
                Console.WriteLine(
                    "[match-host] escalation: C2=31 combat blocked — no real plantUtc");
                return;
            }
            plantUtc = room.Flow.BombPlantedUtc;
            room.Flow.EscalationCombatStarted = true;
            room.Flow.Phase = MatchFlowPhase.PurchasePhase;
            room.Flow.PhaseEndsUtc = DefuseTimer.FuseEndsUtc(plantUtc, EscalationFlowParams.BombFuse);
        }

        SyncDeadActorsFromDeathProps(room);
        if (TryResolveWipeImmediate(room, bombPlanted: true))
            return;

        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.PurchasePhase)),
        ], reason: "Escalation combat C2=31");
        var fuseRemain = (DefuseTimer.FuseEndsUtc(plantUtc, EscalationFlowParams.BombFuse)
            - DateTime.UtcNow).TotalSeconds;
        Console.WriteLine(
            $"[match-host] escalation: combat C2={MatchC2States.PurchasePhase} " +
            $"fuseRemaining≈{Math.Max(0, fuseRemain):F1}s " +
            "(no round clock — ends on defuse/wipe/explode only)");
        PostServerDebugChat("Live · defuse/wipe/explode");
    }

    private void BroadcastEscalationReCreateSceneManagers(MatchRoom room)
    {
        var stime = NextServerTime();
        foreach (var id in EscalationFlowParams.RecreateSceneManagerIds)
        {
            var pkt = MatchCodec.BuildReCreateSceneManager(stime, id);
            var n = BroadcastInitReady(room, pkt, tag: "match_tx_ReCreateSceneManager");
            Console.WriteLine(
                $"[match-host] escalation: ReCreateSceneManager id={id} → peers={n}");
        }
    }

    /// <summary>
    /// Gold alternates BombSite 0/1; exact pick algorithm unknown — toggle each round from 1.
    /// </summary>
    private static byte PickEscalationBombSite(MatchRoom room, int round)
    {
        _ = room;
        return (byte)(round % 2 == 1 ? 1 : 0);
    }
}
