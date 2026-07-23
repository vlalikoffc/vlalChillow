using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

public sealed partial class GameMatchHost
{
    private const string AlliesModeId = "Ranked2v2";

    private bool IsAlliesRoom(MatchRoom room)
    {
        if (IsEscalationRoom(room) || IsDeathMatchRoom(room))
            return false;
        lock (_roomGate)
        {
            if (room.ActorProps.TryGetValue((0, MatchRoomPropKeys.C0), out var c0)
                && c0.Kind == LobbyVariantKind.String
                && string.Equals(c0.String, AlliesModeId, StringComparison.Ordinal))
                return true;
        }
        return string.Equals(_matchGameModeId, AlliesModeId, StringComparison.Ordinal);
    }

    private TimeSpan RoundEndPauseFor(MatchRoom room) =>
        IsAlliesRoom(room) ? AlliesFlowParams.RoundEndPause : MatchFlowTestParams.RoundEndPause;

    /// <summary>
    /// Allies / Ranked2v2 — user-directed override (<c>MATCH_ALLIES_PROBE.md</c> §User-directed):
    /// C2=21 WarmUp (R1) → C2=22 Prep/buy (deadline) → C2=101 Live (90s, no WinTeam) →
    /// manual plant C2=40 + Rpc fan-out → C2=101 round end + WinTeam; half-time 111→112→113 after R7.
    /// No C2=11, no separate PreStart, no C2=31 — one wire countdown at a time.
    /// </summary>
    private void TickAlliesFlowRoom(MatchRoom room)
    {
        MatchFlowPhase phase;
        DateTime ends;
        DateTime bombPlantedUtc;
        string? pendingReason;
        MatchTeam pendingWinner;
        bool bombPlanted;
        lock (_roomGate)
        {
            phase = room.Flow.Phase;
            ends = room.Flow.PhaseEndsUtc;
            bombPlantedUtc = room.Flow.BombPlantedUtc;
            pendingReason = room.Flow.PendingEndReason;
            pendingWinner = room.Flow.PendingWinner;
            bombPlanted = room.Flow.BombPlanted;
            if (phase == MatchFlowPhase.WaitingPlayers)
            {
                if (!BothFightingTeamsPresent(room))
                    return;
                if (!MatchHostSettings.MatchStartArmed)
                    return;
            }
            else if (phase == MatchFlowPhase.MatchOver)
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

        if (phase == MatchFlowPhase.BombPlanted || bombPlanted)
        {
            if (!bombPlanted || bombPlantedUtc == DateTime.MinValue)
            {
                ClearBombAuthority(room, "allies fantasy-missing-plantUtc");
                lock (_roomGate)
                {
                    if (room.Flow.Phase == MatchFlowPhase.BombPlanted)
                        room.Flow.Phase = MatchFlowPhase.RoundLive;
                }
                return;
            }

            var fuseEnd = bombPlantedUtc + AlliesFlowParams.BombFuse;
            var now = DateTime.UtcNow;
            if (now < fuseEnd)
            {
                if (ends != fuseEnd)
                {
                    lock (_roomGate)
                        room.Flow.PhaseEndsUtc = fuseEnd;
                }
                return;
            }

            var elapsed = (now - bombPlantedUtc).TotalSeconds;
            Console.WriteLine(
                "[match-host] allies: bomb fuse expired → T win " +
                $"(plantUtc={bombPlantedUtc:O} elapsed={elapsed:F1}s " +
                $"fuse={AlliesFlowParams.BombFuse.TotalSeconds:0}s)");
            PostServerDebugChat(
                $"взрыв fuse={elapsed:F1}s (plant+{AlliesFlowParams.BombFuse.TotalSeconds:0}s)");
            EnterRoundEndPause(room, MatchTeam.Tr, "bomb-explode");
            return;
        }

        if (phase == MatchFlowPhase.RoundLive)
        {
            if (DateTime.UtcNow < ends)
                return;
            EnterRoundEndPause(room, MatchTeam.Ct, "timeout");
            return;
        }

        if (DateTime.UtcNow < ends)
            return;

        switch (phase)
        {
            case MatchFlowPhase.HalfTimeIntro:
                EnterHalfTimeSwap(room);
                return;
            case MatchFlowPhase.HalfTimeSwap:
                EnterHalfTimeTransition(room);
                return;
            case MatchFlowPhase.HalfTimeTransition:
                EnterAlliesPrep(room);
                return;
        }

        switch (phase)
        {
            case MatchFlowPhase.WaitingPlayers:
                EnterAlliesWarmUp(room);
                break;
            case MatchFlowPhase.Warmup:
                EnterAlliesPrep(room);
                break;
            case MatchFlowPhase.PurchasePhase:
                if (TryExtendAlliesPrepForAwaitingSpawn(room))
                    break;
                EnterAlliesLive(room);
                break;
            case MatchFlowPhase.RoundEndPause:
                ContinueAfterRoundEndAllies(room);
                break;
        }
    }

    /// <summary>C2=21 — first round only after /set start; skip C2=11 freeforall.</summary>
    private void EnterAlliesWarmUp(MatchRoom room)
    {
        var dur = AlliesFlowParams.WarmUp;
        var ends = DateTime.UtcNow + dur;
        var nowSec = ServerTimeSeconds();
        lock (_roomGate)
        {
            room.Flow.Phase = MatchFlowPhase.Warmup;
            room.Flow.PhaseEndsUtc = ends;
            room.Flow.RoundIndex = 0;
            room.Flow.PendingEndReason = null;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
            room.Flow.RoundEndCommittedRound = 0;
            room.Flow.TeamsSwapped = false;
        }
        ClearBombAuthority(room, "Allies WarmUp C2=21");
        MatchHostSettings.MatchStartArmed = false;
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmUp)),
        ], reason: "Allies WarmUp C2=21");
        Console.WriteLine(
            $"[match-host] allies: WarmUp C2={MatchC2States.WarmUp} dur={dur.TotalSeconds:0}s " +
            "(R1 only; skip C2=11; user override → straight to C2=21)");
        PostServerDebugChat("WarmUp");
    }

    /// <summary>
    /// Prep/buy C2=22 — ReCreate 4/5/6/8, Round++, bomberId, money, roster counts.
    /// Wire <c>Time</c> = buy deadline only (no C2=31, no separate PreStart anchor phase).
    /// </summary>
    private void EnterAlliesPrep(MatchRoom room)
    {
        lock (_roomGate)
        {
            if (room.Flow.BombPlanted
                && room.Flow.BombPlantedUtc != DateTime.MinValue
                && room.Flow.Phase == MatchFlowPhase.BombPlanted)
                return;
        }
        ClearBombAuthority(room, "Allies Prep entry");

        int round;
        int ctCount;
        int trCount;
        int bomberId;
        var dur = AlliesFlowParams.Prep;
        var ends = DateTime.UtcNow + dur;
        lock (_roomGate)
        {
            round = room.Flow.RoundIndex + 1;
            if (round < 1) round = 1;
            room.Flow.RoundIndex = round;
            room.Flow.Phase = MatchFlowPhase.PurchasePhase;
            room.Flow.PhaseEndsUtc = ends;
            room.Flow.PendingEndReason = null;
            room.Flow.PrepSpawnExtensionUsed = false;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
            room.LastCombatDamage.Clear();
            foreach (var a in room.Actors)
            {
                if (a.Nr != MatchHostActor.ActorNr)
                    room.ActorProps[(a.Nr, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(0);
            }
            bomberId = PickBomberActorNr(room);
            room.Flow.BomberActorNr = bomberId;
            (trCount, ctCount) = CountFightingTeamRoster(room);
        }

        BroadcastAlliesReCreateSceneManagers(room);
        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + dur.TotalSeconds;
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.Round, LobbyVariant.FromInt(round)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
            (MatchRoomPropKeys.CtRoundStartPlayersCount, LobbyVariant.FromInt(ctCount)),
            (MatchRoomPropKeys.TrRoundStartPlayersCount, LobbyVariant.FromInt(trCount)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmupWillFinish)),
        ], reason: $"Allies Prep/buy round={round} C2=22", phaseDeadlineSec: deadline);
        SetAllFightersMoney(room, MatchFlowTestParams.RoundStartMoney);
        ClearFighterDeathFlags(room);
        DestroyTrackedRoundEntities(room, reason: "Allies Prep C2=22");
        Console.WriteLine(
            $"[match-host] allies: Prep/buy C2={MatchC2States.WarmupWillFinish} " +
            $"dur={dur.TotalSeconds:0}s round={round} TrCount={trCount} CtCount={ctCount} " +
            $"bomberId={bomberId} money={MatchFlowTestParams.RoundStartMoney} " +
            "(single wire countdown on C2=22; no C2=31)");
        PostServerDebugChat($"Prep · раунд {round} (C2=22)");
    }

    /// <summary>
    /// Live — C2=101 MatchStarted + <c>Time</c> round deadline (default 90s via
    /// <see cref="MatchHostSettings.RoundDuration"/>). Same C2 as round-end but
    /// <b>without</b> WinTeam/scores (Ranked <c>EnterRoundLive</c> shape).
    /// </summary>
    private void EnterAlliesLive(MatchRoom room)
    {
        lock (_roomGate)
        {
            if (room.Flow.BombPlanted
                && room.Flow.BombPlantedUtc != DateTime.MinValue
                && room.Flow.Phase == MatchFlowPhase.BombPlanted)
                return;
        }
        ClearBombAuthority(room, "Allies Live entry");

        SyncDeadActorsFromDeathProps(room);
        if (TryResolveWipeImmediate(room, bombPlanted: false))
            return;

        int round;
        int bomberId;
        var txBomberFix = false;
        var dur = AlliesFlowParams.RoundDuration;
        lock (_roomGate)
        {
            round = room.Flow.RoundIndex;
            if (room.Flow.BomberActorNr <= 0)
            {
                var pick = PickBomberActorNr(room);
                if (pick > 0)
                {
                    room.Flow.BomberActorNr = pick;
                    txBomberFix = true;
                }
            }
            bomberId = room.Flow.BomberActorNr;
            room.Flow.Phase = MatchFlowPhase.RoundLive;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + dur;
            room.Flow.PendingEndReason = null;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
        }

        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + dur.TotalSeconds;
        var liveProps = new List<(string Key, LobbyVariant Value)>
        {
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.MatchStarted)),
            (MatchRoomPropKeys.Round, LobbyVariant.FromInt(round)),
            (MatchRoomPropKeys.RoundCount, LobbyVariant.FromInt(MatchFlowTestParams.TotalRounds)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
        };
        if (bomberId > 0)
            liveProps.Add((MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)));
        BroadcastRoomProps(room, liveProps,
            reason: $"Allies Live round={round} C2=101 (no WinTeam)", phaseDeadlineSec: deadline);

        if (txBomberFix && bomberId > 0)
        {
            BroadcastRoomProps(room,
            [
                (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
            ], reason: $"Allies Live bomberId-fix={bomberId}");
        }

        Console.WriteLine(
            $"[match-host] allies: Live C2={MatchC2States.MatchStarted} round={round} " +
            $"duration={dur.TotalSeconds:0}s deadline={deadline:0.###} bomberId={bomberId} " +
            "(round clock — no WinTeam; round-end bag adds WinTeam separately)");
        PostServerDebugChat(
            $"Live · раунд {round} ({(int)dur.TotalMinutes:00}:{dur.Seconds:00})");
    }

    private void ContinueAfterRoundEndAllies(MatchRoom room)
    {
        int round;
        int scoreTr, scoreCt;
        lock (_roomGate)
        {
            round = room.Flow.RoundIndex;
            scoreTr = room.Flow.ScoreTr;
            scoreCt = room.Flow.ScoreCt;
        }

        if (MatchHostSettings.IsAlliesMatchOver(scoreTr, scoreCt))
        {
            lock (_roomGate)
            {
                room.Flow.Phase = MatchFlowPhase.MatchOver;
                room.Flow.PhaseEndsUtc = DateTime.MaxValue;
            }
            BroadcastRoomProps(room,
            [
                (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.MatchResults)),
            ], reason: $"MatchResults Tr={scoreTr} Ct={scoreCt}");
            Console.WriteLine(
                $"[match-host] allies: MatchResults C2={MatchC2States.MatchResults} " +
                $"Tr={scoreTr} Ct={scoreCt} after round={round} first-to-{MatchHostSettings.WinsNeeded}");
            return;
        }

        if (NeedsAlliesHalfTime(room, round))
        {
            Console.WriteLine(
                $"[match-host] allies: half-time after round {round} score {scoreTr}:{scoreCt} " +
                "— C2=111→112→113 then Prep C2=22");
            EnterHalfTimeIntro(room);
            return;
        }

        Console.WriteLine(
            $"[match-host] allies: next Prep C2={MatchC2States.WarmupWillFinish} " +
            $"(round {round}→{round + 1}; skip WarmUp C2=21)");
        EnterAlliesPrep(room);
    }

    /// <summary>
    /// Allies manual plant — gold len≈14: C2=40 only (no Time / RoundStartTime on wire).
    /// Host tracks <see cref="MatchFlowState.BombPlantedUtc"/> for fuse authority and fan-outs
    /// the planter's BombManager Rpc so non-planter peers see bomb mesh (gold RX len41+25 near C2=40).
    /// </summary>
    private void TryEnterAlliesBombPlanted(
        MatchRoom room,
        byte sourceField,
        byte[]? plantPayload = null,
        double plantTimeValue = 0)
    {
        MatchFlowPhase fromPhase;
        lock (_roomGate)
        {
            fromPhase = room.Flow.Phase;
            if (room.Flow.BombPlanted || room.Flow.Phase == MatchFlowPhase.BombPlanted)
            {
                Console.WriteLine(
                    $"[match-host] allies: BombManager plant field={sourceField} IGNORED — already planted");
                return;
            }
            if (room.Flow.PendingEndReason is not null)
            {
                Console.WriteLine(
                    $"[match-host] allies: BombManager plant field={sourceField} " +
                    $"IGNORED — pendingEnd={room.Flow.PendingEndReason}");
                return;
            }
            if (fromPhase is not MatchFlowPhase.RoundLive)
            {
                Console.WriteLine(
                    $"[match-host] allies: BombManager plant field={sourceField} " +
                    $"IGNORED — phase={fromPhase} (RoundLive only)");
                return;
            }

            var plantUtc = DateTime.UtcNow;
            room.Flow.PendingBombPlant = false;
            room.Flow.BombPlanted = true;
            room.Flow.BombPlantedUtc = plantUtc;
            room.Flow.Phase = MatchFlowPhase.BombPlanted;
            room.Flow.PhaseEndsUtc = plantUtc + AlliesFlowParams.BombFuse;
        }

        DateTime plantedAt;
        lock (_roomGate)
            plantedAt = room.Flow.BombPlantedUtc;
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.BombPlanted)),
        ], reason: $"Allies BombPlanted field={sourceField} C2=40");

        if (plantPayload is { Length: > 0 })
            FanOutAlliesBombPlantRpc(room, sourceField, plantPayload, plantTimeValue);

        var fuseSec = AlliesFlowParams.BombFuse.TotalSeconds;
        Console.WriteLine(
            $"[match-host] allies: manual plant field={sourceField} → C2=40 only " +
            $"(gold len≈14; plantUtc={plantedAt:O} host fuse={fuseSec:0}s; " +
            $"Rpc fan-out={(plantPayload is { Length: > 0 })})");
        PostServerDebugChat($"бомба установлена (fuse {fuseSec:0}s)");
    }

    /// <summary>
    /// Host-authoritative BombManager plant Rpc to every INIT-ready peer — gold shows host TX
    /// WorldObjectRpc len41/25 to all peers within ~650ms of C2=40; planter had local apply.
    /// </summary>
    private void FanOutAlliesBombPlantRpc(
        MatchRoom room,
        byte sourceField,
        byte[] plantPayload,
        double plantTimeValue)
    {
        var stime = NextServerTime();
        var timeVal = plantTimeValue > 0 ? plantTimeValue : stime / 1000.0;
        var pkt = MatchCodec.BuildWorldObjectRpc(
            stime,
            objectId: MatchFlowTestParams.BombManagerObjectId,
            rpcId: 2,
            gaaTarget: 2,
            field: sourceField,
            timeValue: timeVal,
            payload: plantPayload);
        var n = BroadcastInitReady(room, pkt, tag: "match_tx_BombManager_alliesPlant");
        Console.WriteLine(
            $"[match-host] allies: BombManager plant fan-out field={sourceField} " +
            $"payloadLen={plantPayload.Length} → peers={n}");
    }

    /// <summary>
    /// Gold round-end bag len≈151: Time, winner score, loser CoLosses, winner CoLosses=0,
    /// WinTeam, C2=101 — winner score key only (not both TrScore+CtScore in one bag).
    /// </summary>
    private static List<(string Key, LobbyVariant Value)> BuildAlliesRoundEndRoomProps(
        double nowSec,
        MatchTeam winner,
        int scoreTr,
        int scoreCt,
        int coLossesTr,
        int coLossesCt,
        List<(string Key, LobbyVariant Value)> winTeamProps)
    {
        var props = new List<(string Key, LobbyVariant Value)>
        {
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
        };
        if (winner == MatchTeam.Tr)
        {
            props.Add((MatchRoomPropKeys.TrScore, LobbyVariant.FromInt(scoreTr)));
            props.Add((MatchRoomPropKeys.CtCoLosses, LobbyVariant.FromInt(coLossesCt)));
            props.Add((MatchRoomPropKeys.TrCoLosses, LobbyVariant.FromInt(coLossesTr)));
        }
        else
        {
            props.Add((MatchRoomPropKeys.CtScore, LobbyVariant.FromInt(scoreCt)));
            props.Add((MatchRoomPropKeys.TrCoLosses, LobbyVariant.FromInt(coLossesTr)));
            props.Add((MatchRoomPropKeys.CtCoLosses, LobbyVariant.FromInt(coLossesCt)));
        }

        props.Add((MatchRoomPropKeys.WinTeam, LobbyVariant.FromProps(winTeamProps)));
        props.Add((MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.MatchStarted)));
        return props;
    }

    /// <summary>Defer Live once if a fighter picked team but has not spawned — refresh Prep C2=22 Time only.</summary>
    private bool TryExtendAlliesPrepForAwaitingSpawn(MatchRoom room)
    {
        if (!FightersAwaitingSpawn(room))
            return false;

        double deadline;
        int round;
        int bomberId = 0;
        lock (_roomGate)
        {
            if (room.Flow.Phase != MatchFlowPhase.PurchasePhase)
                return false;
            if (room.Flow.PrepSpawnExtensionUsed)
                return false;
            room.Flow.PrepSpawnExtensionUsed = true;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            round = room.Flow.RoundIndex;
            if (room.Flow.BomberActorNr <= 0)
            {
                var pick = PickBomberActorNr(room);
                if (pick > 0)
                    room.Flow.BomberActorNr = pick;
            }
            bomberId = room.Flow.BomberActorNr;
        }

        var nowSec = ServerTimeSeconds();
        deadline = nowSec + 5.0;
        var props = new List<(string Key, LobbyVariant Value)>
        {
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmupWillFinish)),
        };
        if (bomberId > 0)
            props.Add((MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)));
        BroadcastRoomProps(room, props, reason: "Allies Prep spawn-extend 5s C2=22", phaseDeadlineSec: deadline);
        Console.WriteLine(
            $"[match-host] allies: Prep C2=22 extended 5s — spawn wait round={round}");
        return true;
    }
}
