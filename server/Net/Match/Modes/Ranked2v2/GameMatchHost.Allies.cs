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
    /// Allies / Ranked2v2 phase machine — C2=22 buy 5s + C2=31 post-buy 5s (each with its own
    /// <c>Time</c> deadline; entering 31 replaces the 22 countdown — no ~19s stack). Then silent
    /// Live. Plant <b>RoundLive only</b> (buy C2=22/31 field=1 is bomber equip — not plant).
    /// Half-time 111→112→113 after round 7; first-to-8 wins.
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

        // Host-side round clock (no Live Time TX): client local timer hits 00:00 and hangs
        // unless we end the round. Duration = MatchHostSettings.RoundDuration (default 90s).
        if (phase == MatchFlowPhase.RoundLive)
        {
            if (DateTime.UtcNow < ends)
                return;
            Console.WriteLine(
                "[match-host] allies: round timeout → CT win " +
                $"(PhaseEndsUtc={ends:O} dur={AlliesFlowParams.RoundDuration.TotalSeconds:0}s)");
            PostServerDebugChat(
                $"таймаут раунда {AlliesFlowParams.RoundDuration.TotalSeconds:0}s → CT");
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
                EnterAlliesPreStart(room);
                return;
        }

        switch (phase)
        {
            case MatchFlowPhase.WaitingPlayers:
                // Dedicated: skip phone gold C2=11 freeforall — /set start → C2=21.
                EnterAlliesWarmUp(room);
                break;
            case MatchFlowPhase.Warmup:
                EnterAlliesPreStart(room);
                break;
            case MatchFlowPhase.WarmupWillFinish:
                // Buy (C2=22) ended — enter C2=31 with a fresh 5s Time deadline (not instant Live).
                if (TryExtendAlliesBuyForAwaitingSpawn(room))
                    break;
                EnterAlliesPostBuy(room);
                break;
            case MatchFlowPhase.PurchasePhase:
                EnterAlliesLive(room);
                break;
            case MatchFlowPhase.RoundEndPause:
                ContinueAfterRoundEndAllies(room);
                break;
        }
    }

    /// <summary>
    /// C2=21 — first round only after /set start; skip phone gold C2=11 freeforall.
    /// Round 2+ skips WarmUp (gold continues at PreStart C2=22).
    /// </summary>
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
        // Gold len≈28: Time, C2 — anchor; no RoundStartTime on WarmUp.
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmUp)),
        ], reason: "Allies WarmUp C2=21");
        Console.WriteLine(
            $"[match-host] allies: WarmUp C2={MatchC2States.WarmUp} dur={dur.TotalSeconds:0}s " +
            "(R1 only; skip C2=11; gold RX 21→22 ≈3s)");
        PostServerDebugChat("WarmUp");
    }

    /// <summary>
    /// C2=22 = real buy/prep: PreStart bag (ReCreate, Round++, bomberId, money on R1) plus
    /// visible buy <c>Time</c> deadline (default 5s via <see cref="AlliesFlowParams.BuyPhase"/>).
    /// Host waits buy duration — then C2=31 with a fresh 5s deadline (not instant Live).
    /// </summary>
    private void EnterAlliesPreStart(MatchRoom room)
    {
        int round;
        int bomberId;
        var dur = AlliesFlowParams.BuyPhase;
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

        ClearBombAuthority(room, $"Allies PreStart/buy C2=22 round={round} bomberId={bomberId}");
        BroadcastAlliesReCreateSceneManagers(room);
        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + dur.TotalSeconds;
        // RoundStartTime = phase start; Time = buy deadline (visible countdown).
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.Round, LobbyVariant.FromInt(round)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmupWillFinish)),
        ], reason: $"Allies buy C2=22 round={round}", phaseDeadlineSec: deadline);
        // Round 1: seed RoundStartMoney. Later rounds keep balances from round-end payouts.
        if (round <= 1)
            SetAllFightersMoney(room, MatchFlowTestParams.RoundStartMoney);
        ClearFighterDeathFlags(room);
        DestroyTrackedRoundEntities(room, reason: "Allies PreStart/buy");
        Console.WriteLine(
            $"[match-host] allies: buy C2={MatchC2States.WarmupWillFinish} " +
            $"round={round} bomberId={bomberId} dur={dur.TotalSeconds:0}s deadline={deadline:0.###} " +
            $"money={(round <= 1 ? MatchFlowTestParams.RoundStartMoney.ToString() : "keep")} " +
            "(host waits buy; then C2=31 with fresh 5s deadline)");
        PostServerDebugChat($"Prep/buy · раунд {round} ({dur.TotalSeconds:0}s)");
    }

    /// <summary>
    /// After C2=22 buy ends: TX C2=31 with roster keys + <b>fresh</b> <c>Time</c> deadline
    /// (replaces the C2=22 countdown — one visible timer at a time). Host waits post-buy
    /// duration then <see cref="EnterAlliesLive"/>.
    /// </summary>
    private void EnterAlliesPostBuy(MatchRoom room)
    {
        lock (_roomGate)
        {
            if (room.Flow.BombPlanted
                && room.Flow.BombPlantedUtc != DateTime.MinValue
                && room.Flow.Phase == MatchFlowPhase.BombPlanted)
                return;
        }
        ClearBombAuthority(room, "Allies post-buy C2=31");

        int round;
        int ctCount;
        int trCount;
        int bomberId;
        var dur = AlliesFlowParams.PostBuyPhase;
        var ends = DateTime.UtcNow + dur;
        lock (_roomGate)
        {
            round = room.Flow.RoundIndex;
            if (round < 1) round = 1;
            room.Flow.Phase = MatchFlowPhase.PurchasePhase;
            room.Flow.PhaseEndsUtc = ends;
            room.Flow.PendingEndReason = null;
            (trCount, ctCount) = CountFightingTeamRoster(room);
            bomberId = room.Flow.BomberActorNr;
        }

        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + dur.TotalSeconds;
        // Fresh deadline Time — client must not keep counting the old C2=22 deadline.
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.CtRoundStartPlayersCount, LobbyVariant.FromInt(ctCount)),
            (MatchRoomPropKeys.TrRoundStartPlayersCount, LobbyVariant.FromInt(trCount)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.PurchasePhase)),
        ], reason: $"Allies post-buy C2=31 round={round}", phaseDeadlineSec: deadline);
        Console.WriteLine(
            $"[match-host] allies: post-buy C2={MatchC2States.PurchasePhase} " +
            $"round={round} TrCount={trCount} CtCount={ctCount} bomberId={bomberId} " +
            $"dur={dur.TotalSeconds:0}s deadline={deadline:0.###} " +
            "(fresh Time — replaces C2=22 countdown; then Live)");
        PostServerDebugChat($"C2=31 · раунд {round} ({dur.TotalSeconds:0}s)");
    }

    /// <summary>
    /// Live — gold: no room bag after Prep; combat after buy. C2=101 is round-end only.
    /// No Live <c>Time</c> TX; host <see cref="MatchFlowState.PhaseEndsUtc"/> ends the round.
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
        var roundDur = AlliesFlowParams.RoundDuration;
        var ends = DateTime.UtcNow + roundDur;
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
            room.Flow.PhaseEndsUtc = ends;
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
            $"(silent wire — no C2/Time TX; wire C2={wireC2}; host timeout " +
            $"{roundDur.TotalSeconds:0}s → CT; C2=101 WinTeam only on round end)");
        PostServerDebugChat($"Live · раунд {round} (таймаут {roundDur.TotalSeconds:0}s)");
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
            EnterMatchResultsAndArmLobbyReturn(
                room, scoreTr, scoreCt, "allies", round,
                extra: $"first-to-{MatchHostSettings.WinsNeeded}");
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
    /// Allies manual plant — <b>RoundLive only</b>. Buy-phase field=1 is bomber equip Rpc
    /// (latest.log: WarmupWillFinish → false C2=40 at round start). Fan-out kept for Live plants.
    /// </summary>
    private int TryEnterAlliesBombPlanted(
        MatchRoom room,
        byte sourceField,
        byte[]? plantPayload = null,
        double plantTimeValue = 0,
        byte rpcId = 2,
        byte gaaTarget = 2,
        byte planterActorNr = 0)
    {
        MatchFlowPhase fromPhase;
        lock (_roomGate)
        {
            fromPhase = room.Flow.Phase;
            if (room.Flow.BombPlanted || room.Flow.Phase == MatchFlowPhase.BombPlanted)
            {
                Console.WriteLine(
                    $"[match-host] allies: BombManager plant field={sourceField} IGNORED — already planted");
                return -1;
            }
            if (room.Flow.PendingEndReason is not null)
            {
                Console.WriteLine(
                    $"[match-host] allies: BombManager plant field={sourceField} " +
                    $"IGNORED — pendingEnd={room.Flow.PendingEndReason}");
                return -1;
            }
            if (fromPhase is not MatchFlowPhase.RoundLive)
            {
                Console.WriteLine(
                    $"[match-host] allies: BombManager plant field={sourceField} " +
                    $"IGNORED — phase={fromPhase} (RoundLive only; buy field=1 ≠ plant)");
                return -1;
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
        ], reason: $"Allies BombPlanted field={sourceField} C2=40 fromPhase={fromPhase}");

        var fanOutPeers = 0;
        if (plantPayload is { Length: > 0 })
        {
            fanOutPeers = FanOutAlliesBombPlantRpc(
                room, sourceField, plantPayload, plantTimeValue, rpcId, gaaTarget);
        }
        else
        {
            Console.WriteLine(
                "[match-host] allies: plant ACCEPTED but payload empty — " +
                "C2=40 only; peers may miss bomb mesh");
        }

        var fuseSec = AlliesFlowParams.BombFuse.TotalSeconds;
        Console.WriteLine(
            $"[match-host] allies: manual plant field={sourceField} → C2=40 " +
            $"(fromPhase={fromPhase}; plantUtc={plantedAt:O} fuse={fuseSec:0}s; " +
            $"Rpc fan-out peers={fanOutPeers})");
        PostServerDebugChat($"бомба установлена (fuse {fuseSec:0}s)");
        ApplyPlantEconomy(room, planterActorNr);
        return fanOutPeers;
    }

    /// <summary>
    /// Host-authoritative BombManager plant Rpc to every INIT-ready peer — planter's
    /// rpc/gaa/field/payload (do not hardcode); gold RX len41 near C2=40.
    /// Returns INIT-ready peer count that received the packet.
    /// </summary>
    private int FanOutAlliesBombPlantRpc(
        MatchRoom room,
        byte sourceField,
        byte[] plantPayload,
        double plantTimeValue,
        byte rpcId,
        byte gaaTarget)
    {
        var stime = NextServerTime();
        var timeVal = plantTimeValue > 0 ? plantTimeValue : stime / 1000.0;
        // gaa on wire from planter is often Others(1)/AllCached(2); host must reach ALL peers
        // including CT — BroadcastInitReady ignores gaa filtering (Escalation auto-plant pattern).
        var pkt = MatchCodec.BuildWorldObjectRpc(
            stime,
            objectId: MatchFlowTestParams.BombManagerObjectId,
            rpcId: rpcId == 0 ? (byte)2 : rpcId,
            gaaTarget: 2,
            field: sourceField,
            timeValue: timeVal,
            payload: plantPayload);
        var n = BroadcastInitReady(room, pkt, tag: "match_tx_BombManager_alliesPlant");
        var nReady = CountInitReadyPeers(room);
        Console.WriteLine(
            $"[match-host] allies: BombManager plant fan-out field={sourceField} " +
            $"rpc={rpcId} gaaIn={gaaTarget} payloadLen={plantPayload.Length} " +
            $"→ peers={n} initReady={nReady}" +
            (nReady >= 2 && n < nReady ? " WARN incomplete fan-out" : "") +
            (nReady >= 2 && n < 2 ? " WARN peers<2" : ""));
        return n;
    }

    /// <summary>INIT-ready (<see cref="MatchPeerState.BootstrapSent"/>) peers in <paramref name="room"/>.</summary>
    private int CountInitReadyPeers(MatchRoom room)
    {
        var n = 0;
        foreach (var (_, st) in _peers)
        {
            if (st.Room == room && st.BootstrapSent)
                n++;
        }
        return n;
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

    /// <summary>
    /// Defer Live once during C2=22 buy if a fighter picked team but has not spawned —
    /// refresh buy <c>Time</c> deadline only (+5s).
    /// </summary>
    private bool TryExtendAlliesBuyForAwaitingSpawn(MatchRoom room)
    {
        if (!FightersAwaitingSpawn(room))
            return false;

        double deadline;
        int bomberId = 0;
        lock (_roomGate)
        {
            if (room.Flow.Phase != MatchFlowPhase.WarmupWillFinish)
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
        BroadcastRoomProps(room, props, reason: "Allies buy spawn-extend 5s", phaseDeadlineSec: deadline);
        Console.WriteLine(
            "[match-host] allies: buy C2=22 extended 5s — spawn wait (Time deadline refresh)");
        return true;
    }
}
