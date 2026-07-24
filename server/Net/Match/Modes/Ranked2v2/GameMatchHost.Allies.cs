using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using StandChillow.LanServer.Net.Match.Logic.Defuse;

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
    /// Live. Plant accepted in active round phases (buy/Live); equip noise in first 2s of
    /// C2=22 ignored. Fan-out + exceptSender relay. Half-time after R7; first-to-8.
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

            // Shared DefuseTimer — same plantUtc+BombFuse rule as Escalation.
            var now = DateTime.UtcNow;
            var tick = DefuseTimer.TickFuse(
                bombPlantedUtc, now, out var fuseEnd, out var elapsed,
                AlliesFlowParams.BombFuse);
            if (tick == FuseTickResult.Running)
            {
                if (ends != fuseEnd)
                {
                    lock (_roomGate)
                        room.Flow.PhaseEndsUtc = fuseEnd;
                }
                return;
            }

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
                // Buy (C2=22) → Live only after client-zero AND LiveNotBeforeUtc.
                if (TryExtendAlliesBuyForAwaitingSpawn(room))
                    break;
                if (!TryPassAlliesBuyLiveGate(room))
                    return;
                EnterAlliesLive(room);
                break;
            case MatchFlowPhase.PurchasePhase:
                // Dead path for Allies (C2=31 skipped); still require LiveNotBeforeUtc.
                if (!TryPassAlliesBuyLiveGate(room))
                    return;
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
        var ends = DefuseTimer.ArmDeadline(dur);
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
    /// C2=22 = buy. Arm <see cref="MatchFlowState.PhaseEndsUtc"/> to client UI zero only
    /// (BuyPhase wall). Live is gated by <see cref="MatchFlowState.AlliesBuyLiveNotBeforeUtc"/>
    /// (= zero wall + <see cref="AlliesFlowParams.BuyEndGrace"/>). Wire <c>Time</c> =
    /// now+10−pad so UI shows ~10s. Do not arm PhaseEndsUtc from wire deadline (≈now+1).
    /// </summary>
    private void EnterAlliesPreStart(MatchRoom room)
    {
        int round;
        int bomberId;
        var dur = AlliesFlowParams.BuyPhase;
        lock (_roomGate)
        {
            round = room.Flow.RoundIndex + 1;
            if (round < 1) round = 1;
            room.Flow.RoundIndex = round;
            room.Flow.Phase = MatchFlowPhase.WarmupWillFinish;
            // PhaseEndsUtc set after buy bag TX — starting it here made host ~1s early
            // (setup+broadcast) while client still showed full 10s.
            room.Flow.PhaseEndsUtc = DateTime.MaxValue;
            room.Flow.AlliesBuyEndGraceArmed = false;
            room.Flow.AlliesBuyLiveNotBeforeUtc = DateTime.MinValue;
            room.Flow.AlliesBuyZeroUtc = DateTime.MinValue;
            room.Flow.AlliesBuyClientZeroSec = 0;
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

        ClearBombAuthority(room, $"Allies buy C2=22 round={round} bomberId={bomberId}");
        BroadcastAlliesReCreateSceneManagers(room);
        var nowSec = ServerTimeSeconds();
        // latest.log: Time=now+10 → client UI ~19s (bfqt ≈9s behind). Wire now+10−pad.
        // Pad is Allies-optional on DefuseTimer (Escalation uses pad=0).
        var wireDeadline = DefuseTimer.BuyWireDeadlineSec(
            nowSec, dur, AlliesFlowParams.BuyClientClockPad);
        var clientZeroSec = DefuseTimer.BuyClientZeroSec(nowSec, dur);
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(wireDeadline)),
            (MatchRoomPropKeys.Round, LobbyVariant.FromInt(round)),
            // Equal to Time so client cannot stack (Time−RST)+(Time−now).
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(wireDeadline)),
            (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmupWillFinish)),
        ], reason: $"Allies buy C2=22 round={round}", phaseDeadlineSec: wireDeadline);

        // Step 1: wall until client-zero. LiveNotBefore = zeroUtc + BuyEndGrace
        // (NOT UtcNow+grace at TX — that expires during the 10s buy → instant Live at 0).
        var remainToClientZero = DefuseTimer.RemainToClientZeroSec(
            clientZeroSec, ServerTimeSeconds(), dur);
        var zeroUtc = DateTime.UtcNow + TimeSpan.FromSeconds(remainToClientZero);
        var liveNotBefore = zeroUtc + AlliesFlowParams.BuyEndGrace;
        // Clamp: LiveNotBefore must be strictly after zero wall.
        if (liveNotBefore <= zeroUtc)
            liveNotBefore = zeroUtc + TimeSpan.FromMilliseconds(500);
        lock (_roomGate)
        {
            room.Flow.AlliesBuyClientZeroSec = clientZeroSec;
            room.Flow.PhaseEndsUtc = zeroUtc;
            room.Flow.AlliesBuyZeroUtc = zeroUtc;
            room.Flow.AlliesBuyLiveNotBeforeUtc = liveNotBefore;
            room.Flow.AlliesBuyEndGraceArmed = false;
        }

        if (round <= 1)
            SetAllFightersMoney(room, MatchFlowTestParams.RoundStartMoney);
        ClearFighterDeathFlags(room);
        DestroyTrackedRoundEntities(room, reason: "Allies buy C2=22");

        Console.WriteLine(
            $"[match-host] allies: buy armed zeroSec={clientZeroSec:0.###} " +
            $"wallToZero={remainToClientZero:0.###}s Time=RST={wireDeadline:0.###} " +
            $"pad={AlliesFlowParams.BuyClientClockPad:0} " +
            $"LiveNotBeforeUtc={liveNotBefore:O} (zero+{AlliesFlowParams.BuyEndGrace.TotalMilliseconds:0}ms) " +
            $"zeroUtc={zeroUtc:O} → Live C2=101 (round={round} bomberId={bomberId})");
        PostServerDebugChat($"Закуп · раунд {round} (10s)");
    }

    /// <summary>
    /// Buy→Live gate after client-zero. First observation always floors
    /// <see cref="MatchFlowState.AlliesBuyLiveNotBeforeUtc"/> to at least
    /// <c>UtcNow + BuyEndGrace</c> (covers expired/MinValue LiveNotBefore no-ops),
    /// re-points <see cref="MatchFlowState.PhaseEndsUtc"/>, returns false.
    /// Live only when <c>UtcNow &gt;= AlliesBuyLiveNotBeforeUtc</c> after that arm.
    /// </summary>
    private bool TryPassAlliesBuyLiveGate(MatchRoom room)
    {
        var nowSec = ServerTimeSeconds();
        double clientZeroSec;
        lock (_roomGate)
            clientZeroSec = room.Flow.AlliesBuyClientZeroSec;

        // Hard rule: never Live while client buy UI epoch is still in the future.
        if (clientZeroSec > 0 && nowSec < clientZeroSec)
            return false;

        var now = DateTime.UtcNow;
        DateTime liveNotBefore;
        DateTime zeroUtc;
        bool justArmed;
        double delayLeftMs;
        lock (_roomGate)
        {
            zeroUtc = room.Flow.AlliesBuyZeroUtc;
            liveNotBefore = room.Flow.AlliesBuyLiveNotBeforeUtc;
            justArmed = !room.Flow.AlliesBuyEndGraceArmed;

            if (justArmed)
            {
                // First buy-zero tick: guarantee a real wall-clock hold from NOW.
                // No-op cases this kills:
                //  - LiveNotBefore was UtcNow+grace at TX → already past when buy ends
                //  - LiveNotBefore MinValue → old gate treated as pass
                //  - poll jitter: now already >= precomputed LiveNotBefore
                var floor = now + AlliesFlowParams.BuyEndGrace;
                if (liveNotBefore < floor)
                    liveNotBefore = floor;
                if (zeroUtc > DateTime.MinValue && liveNotBefore <= zeroUtc)
                    liveNotBefore = zeroUtc + AlliesFlowParams.BuyEndGrace;
                room.Flow.AlliesBuyLiveNotBeforeUtc = liveNotBefore;
                room.Flow.AlliesBuyEndGraceArmed = true;
                // Outer tick: if (UtcNow < PhaseEndsUtc) return — wait until LiveNotBefore.
                room.Flow.PhaseEndsUtc = liveNotBefore;
                delayLeftMs = (liveNotBefore - now).TotalMilliseconds;
            }
            else
            {
                delayLeftMs = (liveNotBefore - now).TotalMilliseconds;
                if (now < liveNotBefore)
                    room.Flow.PhaseEndsUtc = liveNotBefore;
            }
        }

        if (justArmed || now < liveNotBefore)
        {
            if (justArmed)
            {
                Console.WriteLine(
                    $"[match-host] allies: buy zero reached; delayLeftMs={delayLeftMs:0.###} " +
                    $"LiveNotBeforeUtc={liveNotBefore:O} zeroUtc={zeroUtc:O} " +
                    $"zeroSec={clientZeroSec:0.###} nowSec={nowSec:0.###}");
            }
            return false;
        }

        var sinceZeroMs = zeroUtc > DateTime.MinValue
            ? (now - zeroUtc).TotalMilliseconds
            : (now - (liveNotBefore - AlliesFlowParams.BuyEndGrace)).TotalMilliseconds;
        Console.WriteLine(
            $"[match-host] allies: Live allowed after sinceZeroMs={sinceZeroMs:0.###} " +
            $"zeroSec={clientZeroSec:0.###} nowSec={nowSec:0.###} " +
            $"LiveNotBeforeUtc={liveNotBefore:O}");
        return true;
    }

    /// <summary>Unused — C2=31 skipped.</summary>
    private void EnterAlliesPostBuy(MatchRoom room) => EnterAlliesLive(room);

    /// <summary>
    /// Live — TX C2=101 MatchStarted (no WinTeam) to leave C2=22 buy UI. Silent Live left
    /// clients counting buy Time to 0 while host was already RoundLive.
    /// </summary>
    private void EnterAlliesLive(MatchRoom room)
    {
        lock (_roomGate)
        {
            if (room.Flow.BombPlanted
                && room.Flow.BombPlantedUtc != DateTime.MinValue
                && room.Flow.Phase == MatchFlowPhase.BombPlanted)
                return;
            // Hard gate: never TX Live/C2=101 from buy until LiveNotBeforeUtc.
            if (room.Flow.Phase is MatchFlowPhase.WarmupWillFinish or MatchFlowPhase.PurchasePhase)
            {
                var zeroSec = room.Flow.AlliesBuyClientZeroSec;
                var nowSecGate = ServerTimeSeconds();
                if (zeroSec > 0 && nowSecGate < zeroSec)
                {
                    Console.WriteLine(
                        "[match-host] allies: EnterAlliesLive BLOCKED — client buy UI not at 0 " +
                        $"nowSec={nowSecGate:0.###} zeroSec={zeroSec:0.###}");
                    return;
                }
                var nowGate = DateTime.UtcNow;
                var liveNotBefore = room.Flow.AlliesBuyLiveNotBeforeUtc;
                // MinValue or still-future LiveNotBefore → block (never treat unset as pass).
                if (liveNotBefore == DateTime.MinValue || nowGate < liveNotBefore)
                {
                    var floor = nowGate + AlliesFlowParams.BuyEndGrace;
                    if (liveNotBefore < floor)
                        liveNotBefore = floor;
                    room.Flow.AlliesBuyLiveNotBeforeUtc = liveNotBefore;
                    room.Flow.AlliesBuyEndGraceArmed = true;
                    room.Flow.PhaseEndsUtc = liveNotBefore;
                    Console.WriteLine(
                        "[match-host] allies: EnterAlliesLive BLOCKED — LiveNotBefore " +
                        $"until={liveNotBefore:O} now={nowGate:O}");
                    return;
                }
            }
        }
        ClearBombAuthority(room, "Allies Live entry");

        SyncDeadActorsFromDeathProps(room);
        if (TryResolveWipeImmediate(room, bombPlanted: false))
            return;

        int round;
        int bomberId;
        double clientZeroSec;
        DateTime zeroUtc;
        var roundDur = AlliesFlowParams.RoundDuration;
        var ends = DefuseTimer.ArmDeadline(roundDur);
        lock (_roomGate)
        {
            round = room.Flow.RoundIndex;
            clientZeroSec = room.Flow.AlliesBuyClientZeroSec;
            zeroUtc = room.Flow.AlliesBuyZeroUtc;
            if (room.Flow.BomberActorNr <= 0)
            {
                var pick = PickBomberActorNr(room);
                if (pick > 0)
                    room.Flow.BomberActorNr = pick;
            }
            bomberId = room.Flow.BomberActorNr;
            room.Flow.Phase = MatchFlowPhase.RoundLive;
            room.Flow.PhaseEndsUtc = ends;
            room.Flow.AlliesBuyClientZeroSec = 0;
            room.Flow.AlliesBuyEndGraceArmed = false;
            room.Flow.AlliesBuyLiveNotBeforeUtc = DateTime.MinValue;
            room.Flow.AlliesBuyZeroUtc = DateTime.MinValue;
            room.Flow.PendingEndReason = null;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
        }

        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + roundDur.TotalSeconds;
        var sinceZeroMs = zeroUtc > DateTime.MinValue
            ? (DateTime.UtcNow - zeroUtc).TotalMilliseconds
            : (clientZeroSec > 0 ? (nowSec - clientZeroSec) * 1000.0 : -1);
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.MatchStarted)),
            (MatchRoomPropKeys.Round, LobbyVariant.FromInt(round)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
        ], reason: $"Allies Live C2=101 round={round} (no WinTeam)", phaseDeadlineSec: deadline);

        Console.WriteLine(
            $"[match-host] allies: Live TX; sinceZeroMs={sinceZeroMs:0.###} " +
            $"C2=101 round={round} bomberId={bomberId} " +
            $"zeroSec={clientZeroSec:0.###} nowSec={nowSec:0.###} " +
            $"RST={nowSec:0.###} Time={deadline:0.###} dur={roundDur.TotalSeconds:0}s " +
            "(only after client buy UI 0 + grace; round-end WinTeam is separate C2=101)");
        PostServerDebugChat($"Live · раунд {round}");
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
    /// Allies plant phases: buy C2=22 / C2=31 flash / RoundLive. Reject WarmUp/waiting/end.
    /// First 2s of C2=22 = bomber equip field=1 noise (not a real plant).
    /// </summary>
    private bool IsAlliesPlantablePhase(MatchRoom room, MatchFlowPhase phase)
    {
        if (!IsAlliesRoom(room))
            return phase == MatchFlowPhase.RoundLive;

        if (phase is MatchFlowPhase.RoundLive or MatchFlowPhase.PurchasePhase)
            return true;

        if (phase != MatchFlowPhase.WarmupWillFinish)
            return false;

        // Equip Rpc at buy start — ignore until 2s into the 10s buy window.
        // During buy, PhaseEndsUtc = buyStart + BuyPhase (grace is a later re-arm).
        DateTime ends;
        bool graceArmed;
        lock (_roomGate)
        {
            ends = room.Flow.PhaseEndsUtc;
            graceArmed = room.Flow.AlliesBuyEndGraceArmed;
        }
        if (graceArmed)
            return true;
        var buyStart = ends - AlliesFlowParams.BuyPhase;
        return DateTime.UtcNow >= buyStart + TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// Allies manual plant — accept buy/Live (after equip grace). Fan-out + caller relays.
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
            if (!IsAlliesPlantablePhase(room, fromPhase))
            {
                Console.WriteLine(
                    $"[match-host] allies: BombManager plant field={sourceField} " +
                    $"IGNORED — phase={fromPhase} (equip grace or not in round)");
                return -1;
            }

            var plantUtc = DateTime.UtcNow;
            room.Flow.PendingBombPlant = false;
            room.Flow.BombPlanted = true;
            room.Flow.BombPlantedUtc = plantUtc;
            room.Flow.Phase = MatchFlowPhase.BombPlanted;
            room.Flow.PhaseEndsUtc = DefuseTimer.FuseEndsUtc(plantUtc, AlliesFlowParams.BombFuse);
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
            // Extend client-zero + LiveNotBefore; grace still BuyEndGrace after new zero.
            room.Flow.AlliesBuyEndGraceArmed = false;
            var zeroUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            room.Flow.PhaseEndsUtc = zeroUtc;
            room.Flow.AlliesBuyZeroUtc = zeroUtc;
            room.Flow.AlliesBuyLiveNotBeforeUtc = zeroUtc + AlliesFlowParams.BuyEndGrace;
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
        lock (_roomGate)
            room.Flow.AlliesBuyClientZeroSec = deadline;
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
