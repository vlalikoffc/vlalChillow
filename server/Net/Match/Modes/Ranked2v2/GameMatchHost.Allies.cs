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
    /// Allies / Ranked2v2 phase machine — gold <c>allies-probe</c> run-20260723_215727
    /// (<c>MATCH_ALLIES_PROBE.md</c>): C2=11→21→22→31→Live/combat→101 round end;
    /// manual plant C2=40; half-time 111→112→113 after round 7; first-to-8 wins.
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

        // Allies: no fixed round clock — ends on wipe / plant / defuse / explode only.
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
                EnterAlliesPreStart(room);
                return;
        }

        switch (phase)
        {
            case MatchFlowPhase.WaitingPlayers:
                EnterAlliesPreWarmup(room);
                break;
            case MatchFlowPhase.AlliesPreWarmup:
                EnterAlliesWarmUp(room);
                break;
            case MatchFlowPhase.Warmup:
                EnterAlliesPreStart(room);
                break;
            case MatchFlowPhase.WarmupWillFinish:
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

    private void EnterAlliesPreWarmup(MatchRoom room)
    {
        var dur = AlliesFlowParams.PreWarmup;
        var ends = DateTime.UtcNow + dur;
        var nowSec = ServerTimeSeconds();
        lock (_roomGate)
        {
            room.Flow.Phase = MatchFlowPhase.AlliesPreWarmup;
            room.Flow.PhaseEndsUtc = ends;
            room.Flow.RoundIndex = 0;
            room.Flow.PendingEndReason = null;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
            room.Flow.RoundEndCommittedRound = 0;
            room.Flow.TeamsSwapped = false;
        }
        ClearBombAuthority(room, "Allies PreWarmup C2=11");
        MatchHostSettings.MatchStartArmed = false;
        // Gold len≈28: Time, C2 — anchor Time only (host waits ~8s internally).
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.DeathMatchPreWarmup)),
        ], reason: "Allies PreWarmup C2=11");
        Console.WriteLine(
            $"[match-host] allies: PreWarmup C2={MatchC2States.DeathMatchPreWarmup} " +
            $"dur={dur.TotalSeconds:0}s (gold RX 10→11 ≈8s)");
        PostServerDebugChat("freeforall");
    }

    /// <summary>C2=21 — first round only; skip on round 2+ (gold skips WarmUp after R1).</summary>
    private void EnterAlliesWarmUp(MatchRoom room)
    {
        var dur = AlliesFlowParams.WarmUp;
        var ends = DateTime.UtcNow + dur;
        var nowSec = ServerTimeSeconds();
        lock (_roomGate)
        {
            room.Flow.Phase = MatchFlowPhase.Warmup;
            room.Flow.PhaseEndsUtc = ends;
            room.Flow.PendingEndReason = null;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
        }
        ClearBombAuthority(room, "Allies WarmUp C2=21");
        // Gold len≈28: Time, C2 — anchor; no RoundStartTime on WarmUp.
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmUp)),
        ], reason: "Allies WarmUp C2=21");
        Console.WriteLine(
            $"[match-host] allies: WarmUp C2={MatchC2States.WarmUp} dur={dur.TotalSeconds:0}s " +
            "(first round only; gold RX 21→22 ≈3s)");
        PostServerDebugChat("WarmUp");
    }

    /// <summary>
    /// PreStart C2=22 — ReCreate 4/5/6/8, Round++, bomberId, money. Gold ≈10s before Prep C2=31.
    /// </summary>
    private void EnterAlliesPreStart(MatchRoom room)
    {
        int round;
        int bomberId;
        var dur = AlliesFlowParams.PreStart;
        var ends = DateTime.UtcNow + dur;
        lock (_roomGate)
        {
            round = room.Flow.RoundIndex + 1;
            if (round < 1) round = 1;
            room.Flow.RoundIndex = round;
            room.Flow.Phase = MatchFlowPhase.WarmupWillFinish;
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
        }

        ClearBombAuthority(room, $"Allies PreStart C2=22 round={round} bomberId={bomberId}");
        BroadcastAlliesReCreateSceneManagers(room);
        var nowSec = ServerTimeSeconds();
        // Gold C2=22: Time == RoundStartTime == nowSec; ~10s host wait before Prep (no wire countdown).
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.Round, LobbyVariant.FromInt(round)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmupWillFinish)),
        ], reason: $"Allies PreStart round={round}");
        SetAllFightersMoney(room, MatchFlowTestParams.RoundStartMoney);
        ClearFighterDeathFlags(room);
        DestroyTrackedRoundEntities(room, reason: "Allies PreStart");
        Console.WriteLine(
            $"[match-host] allies: PreStart C2={MatchC2States.WarmupWillFinish} " +
            $"dur={dur.TotalSeconds:0}s round={round} bomberId={bomberId} money={MatchFlowTestParams.RoundStartMoney} " +
            "(gold RX 22→31 ≈10s; manual plant field=1/2 during Live)");
        PostServerDebugChat($"PreStart · раунд {round} (C2=22)");
    }

    /// <summary>PurchasePhase C2=31 — gold len≈90; only phase with wire countdown Time deadline.</summary>
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
            round = room.Flow.RoundIndex;
            if (round < 1) round = 1;
            room.Flow.Phase = MatchFlowPhase.PurchasePhase;
            room.Flow.PhaseEndsUtc = ends;
            room.Flow.PendingEndReason = null;
            room.Flow.PrepSpawnExtensionUsed = false;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
            room.LastCombatDamage.Clear();
            (trCount, ctCount) = CountFightingTeamRoster(room);
            bomberId = room.Flow.BomberActorNr;
        }

        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + dur.TotalSeconds;
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.CtRoundStartPlayersCount, LobbyVariant.FromInt(ctCount)),
            (MatchRoomPropKeys.TrRoundStartPlayersCount, LobbyVariant.FromInt(trCount)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.PurchasePhase)),
        ], reason: $"Allies Prep round={round}", phaseDeadlineSec: deadline);
        Console.WriteLine(
            $"[match-host] allies: Prep C2={MatchC2States.PurchasePhase} dur={dur.TotalSeconds:0}s " +
            $"round={round} TrCount={trCount} CtCount={ctCount} bomberId={bomberId} " +
            "(only wire countdown; client buy timer)");
        PostServerDebugChat($"Prep · раунд {round} (C2=31)");
    }

    /// <summary>
    /// Live — gold: no room bag after Prep C2=31; combat starts when Prep <c>Time</c> deadline
    /// expires. C2=101 is round-end only (WinTeam bag). No round clock in Live.
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
            room.Flow.PhaseEndsUtc = DateTime.MaxValue;
            room.Flow.PendingEndReason = null;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
        }

        if (txBomberFix && bomberId > 0)
        {
            BroadcastRoomProps(room,
            [
                (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
            ], reason: $"Allies Live bomberId-fix={bomberId}");
        }

        byte wireC2;
        lock (_roomGate)
            wireC2 = room.RoomC2;
        Console.WriteLine(
            $"[match-host] allies: Live round={round} bomberId={bomberId} " +
            $"(silent — no C2/Time TX; wire C2={wireC2} stays PurchasePhase until plant/round-end; " +
            "no round clock — wipe/plant/defuse/explode only; C2=101 is round-end only)");
        PostServerDebugChat($"Live · раунд {round} (без таймера)");
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
                "— C2=111→112→113 then PreStart");
            EnterHalfTimeIntro(room);
            return;
        }

        Console.WriteLine(
            $"[match-host] allies: next PreStart C2={MatchC2States.WarmupWillFinish} " +
            $"(round {round}→{round + 1}; skip WarmUp)");
        EnterAlliesPreStart(room);
    }

    /// <summary>
    /// Allies manual plant — gold len≈14: C2=40 only (no Time / RoundStartTime on wire).
    /// Host still tracks <see cref="MatchFlowState.BombPlantedUtc"/> for fuse authority.
    /// </summary>
    private void TryEnterAlliesBombPlanted(MatchRoom room, byte sourceField)
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
        var fuseSec = AlliesFlowParams.BombFuse.TotalSeconds;
        Console.WriteLine(
            $"[match-host] allies: manual plant field={sourceField} → C2=40 only " +
            $"(gold len≈14; plantUtc={plantedAt:O} host fuse={fuseSec:0}s)");
        PostServerDebugChat($"бомба установлена (fuse {fuseSec:0}s)");
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

    /// <summary>Defer Live once if a fighter picked team but has not spawned — refresh Prep Time only.</summary>
    private bool TryExtendAlliesPrepForAwaitingSpawn(MatchRoom room)
    {
        if (!FightersAwaitingSpawn(room))
            return false;

        double deadline;
        int bomberId = 0;
        lock (_roomGate)
        {
            if (room.Flow.Phase != MatchFlowPhase.PurchasePhase)
                return false;
            if (room.Flow.PrepSpawnExtensionUsed)
                return false;
            room.Flow.PrepSpawnExtensionUsed = true;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
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
        };
        if (bomberId > 0)
            props.Add((MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)));
        BroadcastRoomProps(room, props, reason: "Allies Prep spawn-extend 5s", phaseDeadlineSec: deadline);
        Console.WriteLine(
            "[match-host] allies: Prep extended 5s — spawn wait (Time deadline only; no RoundStartTime)");
        return true;
    }
}
