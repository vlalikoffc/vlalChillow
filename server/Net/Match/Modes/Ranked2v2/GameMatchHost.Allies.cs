using LiteNetLib;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using StandChillow.LanServer.Net.Match.Kernel;
using StandChillow.LanServer.Net.Match.Logic.Defuse;

namespace StandChillow.LanServer.Net;

public sealed partial class GameMatchHost
{
    private const string AlliesModeId = "Ranked2v2";
    private const string AlliesAltModeId = "Ranked2v2Alt";
    private const string RankedDefuseModeId = "RankedDefuse";
    private const string DefuseModeId = "Defuse";

    /// <summary>
    /// Bomb-round family that shares the Allies gold FSM (C2=22→31→40?→101, host 109s combat).
    /// The id list lives in <see cref="MatchPlantPolicy.BombFamilyModeIds"/> — one list, so a rule
    /// written once lands in Ranked2v2 / Ranked2v2Alt / RankedDefuse / Defuse identically.
    /// Escalation &amp; TDM stay separate.
    /// </summary>
    private static bool IsAlliesFamilyModeId(string? modeId) =>
        MatchPlantPolicy.IsBombFamilyMode(modeId);

    private bool IsAlliesRoom(MatchRoom room)
    {
        if (IsEscalationRoom(room) || IsDeathMatchRoom(room) || IsDuelRoom(room))
            return false;
        lock (_roomGate)
        {
            if (room.ActorProps.TryGetValue((0, MatchRoomPropKeys.C0), out var c0)
                && c0.Kind == LobbyVariantKind.String
                && IsAlliesFamilyModeId(c0.String))
                return true;
        }
        return IsAlliesFamilyModeId(_matchGameModeId);
    }

    private string AlliesFamilyLogTag(MatchRoom room)
    {
        lock (_roomGate)
        {
            if (room.ActorProps.TryGetValue((0, MatchRoomPropKeys.C0), out var c0)
                && c0.Kind == LobbyVariantKind.String
                && c0.String is { Length: > 0 } id)
                return id switch
                {
                    AlliesAltModeId => "allies-alt",
                    AlliesModeId => "allies",
                    RankedDefuseModeId => "ranked-defuse",
                    DefuseModeId => "defuse",
                    _ => "allies",
                };
        }
        return _matchGameModeId switch
        {
            AlliesAltModeId => "allies-alt",
            RankedDefuseModeId => "ranked-defuse",
            DefuseModeId => "defuse",
            _ => "allies",
        };
    }

    private TimeSpan RoundEndPauseFor(MatchRoom room) =>
        IsDuelRoom(room) ? DuelFlowParams.RoundEndPause
        : IsAlliesRoom(room) ? AlliesFlowParams.RoundEndPause
        : MatchFlowTestParams.RoundEndPause;

    /// <summary>
    /// Allies / Ranked2v2 — phone gold <c>MATCH_ALLIES_PROBE.md</c> (2026-07-24):
    /// C2=22 (<c>Time == RoundStartTime ≈ now</c>) → ~10s wall → C2=31 combat bag
    /// (<c>Time ≈ now</c>, stay on 31) → plant field=3 → C2=40 C2-only → C2=101 WinTeam only.
    /// No Live C2=101 without WinTeam; no wire Live <c>Time=now+90</c>.
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
                        room.Flow.Phase = MatchFlowPhase.PurchasePhase;
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

        // Combat on C2=31 (PurchasePhase): host-side round clock only — no Live Time TX.
        if (phase == MatchFlowPhase.PurchasePhase)
        {
            if (DateTime.UtcNow < ends)
                return;
            Console.WriteLine(
                "[match-host] allies: round timeout → CT win " +
                $"(PhaseEndsUtc={ends:O} dur={AlliesFlowParams.RoundDuration.TotalSeconds:0}s " +
                "C2=31 combat; no Live Time on wire)");
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
                // C2=22 buy wall (~10s) → C2=31 combat (stay on 31).
                if (TryExtendAlliesBuyForAwaitingSpawn(room))
                    break;
                EnterAlliesCombat(room);
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
        // 2.07 gold len≈28: Time, Duration, C2 — anchor Time (no RoundStartTime on WarmUp).
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.Duration, LobbyVariant.FromDouble(dur.TotalSeconds)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmUp)),
        ], reason: "Allies WarmUp C2=21");
        Console.WriteLine(
            $"[match-host] allies: WarmUp C2={MatchC2States.WarmUp} dur={dur.TotalSeconds:0}s " +
            "(R1 only; skip C2=11; gold RX 21→22 ≈3s)");
        PostServerDebugChat("WarmUp");
    }

    /// <summary>
    /// C2=22 PreStart / buy — gold: <c>Time == RoundStartTime ≈ now</c> (anchor, never a
    /// future deadline). Host waits <see cref="AlliesFlowParams.BuyPhase"/> (~10s) then
    /// TX C2=31 combat.
    /// </summary>
    private void EnterAlliesPreStart(MatchRoom room)
    {
        int round;
        int bomberId;
        var dur = AlliesFlowParams.BuyPhase;
        var ends = DefuseTimer.ArmDeadline(dur);
        lock (_roomGate)
        {
            round = room.Flow.RoundIndex + 1;
            if (round < 1) round = 1;
            room.Flow.RoundIndex = round;
            room.Flow.Phase = MatchFlowPhase.WarmupWillFinish;
            room.Flow.PhaseEndsUtc = ends;
            // Clear legacy buy-pad / Live-gate fields (unused on gold path).
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

        // Round start (buy C2=22) — the MVP counter is per round, not per match.
        ResetBombModeRoundKills(room);
        ClearBombAuthority(room, $"Allies buy C2=22 round={round} bomberId={bomberId}");
        BroadcastAlliesReCreateSceneManagers(room);
        var nowSec = ServerTimeSeconds();
        // Gold: Time == RoundStartTime == now (ΔTime-RST = 0). Never wire now+buy − pad.
        // 2.07 gold (#27): every C2 bag carries `Duration` (seconds); `Time` stays an anchor.
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.Duration, LobbyVariant.FromDouble(dur.TotalSeconds)),
            (MatchRoomPropKeys.Round, LobbyVariant.FromInt(round)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmupWillFinish)),
        ], reason: $"Allies buy C2=22 round={round}");

        if (round <= 1)
            SetAllFightersMoney(room, MatchFlowTestParams.RoundStartMoney);
        ClearFighterDeathFlags(room);
        DestroyTrackedRoundEntities(room, reason: "Allies buy C2=22");

        Console.WriteLine(
            $"[match-host] allies: buy C2={MatchC2States.WarmupWillFinish} " +
            $"Time=RST={nowSec:0.###} (anchor) wall={dur.TotalSeconds:0}s → C2=31 combat " +
            $"(round={round} bomberId={bomberId})");
        PostServerDebugChat($"Закуп · раунд {round} ({dur.TotalSeconds:0}s)");
    }

    /// <summary>
    /// C2=31 combat — gold bag: <c>Time ≈ now</c>, roster counts, <c>C2=31</c>.
    /// Remain in <see cref="MatchFlowPhase.PurchasePhase"/> until plant C2=40 or round-end
    /// C2=101 WinTeam. No Live C2=101 / no wire round-clock Time.
    /// </summary>
    private void EnterAlliesCombat(MatchRoom room)
    {
        lock (_roomGate)
        {
            if (room.Flow.BombPlanted
                && room.Flow.BombPlantedUtc != DateTime.MinValue
                && room.Flow.Phase == MatchFlowPhase.BombPlanted)
                return;
        }

        SyncDeadActorsFromDeathProps(room);
        if (TryResolveWipeImmediate(room, bombPlanted: false))
            return;

        int round;
        int ctCount;
        int trCount;
        int bomberId;
        var roundDur = AlliesFlowParams.RoundDuration;
        var ends = DefuseTimer.ArmDeadline(roundDur);
        lock (_roomGate)
        {
            round = room.Flow.RoundIndex;
            if (round < 1) round = 1;
            if (room.Flow.BomberActorNr <= 0)
            {
                var pick = PickBomberActorNr(room);
                if (pick > 0)
                    room.Flow.BomberActorNr = pick;
            }
            bomberId = room.Flow.BomberActorNr;
            room.Flow.Phase = MatchFlowPhase.PurchasePhase;
            room.Flow.PhaseEndsUtc = ends;
            room.Flow.AlliesBuyClientZeroSec = 0;
            room.Flow.AlliesBuyEndGraceArmed = false;
            room.Flow.AlliesBuyLiveNotBeforeUtc = DateTime.MinValue;
            room.Flow.AlliesBuyZeroUtc = DateTime.MinValue;
            room.Flow.PendingEndReason = null;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
            // #33: the wipe must be judged against who actually entered this round —
            // a pawn on the field is the proof of entry (see MatchFlowRules.FreezeRoundRoster).
            MatchFlowRules.FreezeRoundRoster(room.Flow, room.ActorProps);
            (trCount, ctCount) = CountFightingTeamRoster(room);
        }

        var nowSec = ServerTimeSeconds();
        // Gold: Time ≈ stime/1000 (fresh now) — not now+10 / not now+90. 2.07 adds `Duration`
        // (combat 110.0 in gold); the client derives its countdown from this, not from Time (#27).
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            // Gold C2=31 Duration is 110.0 (client 1:50) while the host deadline stays 109s.
            (MatchRoomPropKeys.Duration,
                LobbyVariant.FromDouble(AlliesFlowParams.ClientCombatCountdownSeconds)),
            (MatchRoomPropKeys.CtRoundStartPlayersCount, LobbyVariant.FromInt(ctCount)),
            (MatchRoomPropKeys.TrRoundStartPlayersCount, LobbyVariant.FromInt(trCount)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.PurchasePhase)),
        ], reason: $"Allies combat C2=31 round={round}");
        Console.WriteLine(
            $"[match-host] allies: combat C2={MatchC2States.PurchasePhase} " +
            $"round={round} TrCount={trCount} CtCount={ctCount} bomberId={bomberId} " +
            $"Time={nowSec:0.###} (anchor) hostRound={roundDur.TotalSeconds:0}s " +
            "(stay on C2=31 until plant 40 or WinTeam 101; no Live bag)");
        PostServerDebugChat($"Live · раунд {round} · C2=31");
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
    /// The one plant entry point for <b>every</b> bomb mode — <c>Ranked2v2</c>, <c>Ranked2v2Alt</c>,
    /// <c>RankedDefuse</c>, <c>Defuse</c> (one bomb family) and the classic Ranked/Escalation loop.
    /// </summary>
    /// <remarks>
    /// Issue #33 — there is no «only the assigned bomber may plant» gate. The bomb is carried: the
    /// bomber can drop it, death drops it, another attacker picks it up and plants. What decides is
    /// <see cref="MatchPlantPolicy"/> alone (one plant/round, combat phase, attackers only), and it
    /// is asked exactly once — here. The wire differences that are evidence, not policy (family
    /// plants on <c>C2=31</c> with a <c>C2</c>-only <c>C2=40</c> bag and a Rpc fan-out; classic
    /// plants on Live with a <c>Time</c>/<c>RoundStartTime</c> bag) stay, driven by
    /// <c>alliesFamily</c> — no per-mode branch.
    /// </remarks>
    private MatchPlantDecision TryEnterBombPlanted(
        MatchRoom room,
        byte sourceField,
        byte[]? plantPayload = null,
        double plantTimeValue = 0,
        byte rpcId = 2,
        byte gaaTarget = 2,
        byte planterActorNr = 0,
        NetPeer? planterPeer = null)
    {
        var alliesFamily = IsAlliesRoom(room);
        var tag = alliesFamily ? AlliesFamilyLogTag(room) : "match-flow";

        MatchFlowPhase fromPhase;
        bool handoff;
        lock (_roomGate)
        {
            fromPhase = room.Flow.Phase;
            var deny = MatchPlantPolicy.Judge(
                room.Flow,
                alliesFamily,
                planterActorNr,
                MatchFlowRules.GetActorTeam(room.ActorProps, planterActorNr));
            if (deny != MatchPlantPolicy.Deny.None)
            {
                var relay = MatchPlantPolicy.RelayPoseOnly(deny);
                Console.WriteLine(
                    $"[match-host] {tag}: BombManager plant field={sourceField} " +
                    $"from actor={planterActorNr} REFUSED — {MatchPlantPolicy.Describe(deny)} " +
                    $"(phase={fromPhase}; relay={(relay ? "pose only" : "no")}; no C2=40)");
                return MatchPlantDecision.Refused(deny, fromPhase);
            }

            // Handoff, not an identity check: a teammate who picked the dropped bomb up (or the
            // one it dropped onto) is a legal planter. Logged so a capture shows the truth.
            handoff = MatchPlantPolicy.IsHandoff(room.Flow, planterActorNr);

            var plantUtc = DateTime.UtcNow;
            room.Flow.PendingBombPlant = false;
            room.Flow.BombPlanted = true;
            room.Flow.BombPlantedUtc = plantUtc;
            // The MVP must credit whoever actually planted (the bomb is carried and can be
            // handed off) — BomberActorNr is only the round's assigned bomber.
            room.Flow.RoundPlanterActorNr = planterActorNr;
            room.Flow.Phase = MatchFlowPhase.BombPlanted;
            // Evidence, not policy: the family fuse constant differs from the classic one
            // (AlliesFlowParams / DefuseFlowParams vs MatchFlowTestParams), so pick by family.
            room.Flow.PhaseEndsUtc = DefuseTimer.FuseEndsUtc(
                plantUtc, alliesFamily ? AlliesFlowParams.BombFuse : MatchFlowTestParams.BombFuse);
            // Keep planter Rpc for late-join / reconnect peers (C2=40 alone is not enough).
            if (plantPayload is { Length: > 0 })
            {
                room.Flow.LastBombPlantPayload = (byte[])plantPayload.Clone();
                room.Flow.LastBombPlantField = sourceField;
                room.Flow.LastBombPlantRpcId = rpcId == 0 ? (byte)2 : rpcId;
                room.Flow.LastBombPlantTimeValue = plantTimeValue;
            }
            else
            {
                room.Flow.LastBombPlantPayload = null;
                room.Flow.LastBombPlantField = 0;
                room.Flow.LastBombPlantRpcId = 2;
                room.Flow.LastBombPlantTimeValue = 0;
            }
        }

        DateTime plantedAt;
        lock (_roomGate)
            plantedAt = room.Flow.BombPlantedUtc;

        // Family gold C2=40 carries C2 only; the classic loop also re-anchors Time/RoundStartTime.
        // Both bags are captured wire shapes — the split is evidence, not policy.
        if (alliesFamily)
        {
            BroadcastRoomProps(room,
            [
                (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.BombPlanted)),
            ], reason: $"{tag} BombPlanted field={sourceField} C2=40 fromPhase={fromPhase}");
        }
        else
        {
            var nowSec = ServerTimeSeconds();
            var deadline = DefuseTimer.FuseWireDeadlineSec(
                nowSec, alliesFamily ? AlliesFlowParams.BombFuse : MatchFlowTestParams.BombFuse);
            BroadcastRoomProps(room,
            [
                (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.BombPlanted)),
                (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
                (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            ], reason: $"BombPlanted field={sourceField}", phaseDeadlineSec: deadline);
        }

        var fanOutPeers = 0;
        if (alliesFamily && plantPayload is { Length: > 0 })
        {
            fanOutPeers = FanOutAlliesBombPlantRpc(
                room, sourceField, plantPayload, plantTimeValue, rpcId, gaaTarget, planterPeer);
        }
        else if (alliesFamily)
        {
            Console.WriteLine(
                $"[match-host] {tag}: plant ACCEPTED but payload empty — " +
                "C2=40 only; peers may miss bomb mesh");
        }

        var fuseSec = (alliesFamily ? AlliesFlowParams.BombFuse : MatchFlowTestParams.BombFuse)
            .TotalSeconds;
        Console.WriteLine(
            $"[match-host] {tag}: plant field={sourceField} → C2=40 " +
            $"planter={planterActorNr} handoff={handoff} " +
            $"(fromPhase={fromPhase}; plantUtc={plantedAt:O} fuse={fuseSec:0}s; " +
            $"Rpc fan-out peers={fanOutPeers})");
        PostServerDebugChat($"бомба установлена (fuse {fuseSec:0}s)");
        ApplyPlantEconomy(room, planterActorNr);
        return new MatchPlantDecision(
            Accepted: true,
            Deny: MatchPlantPolicy.Deny.None,
            FromPhase: fromPhase,
            Handoff: handoff,
            FanOutPeers: fanOutPeers);
    }

    /// <summary>
    /// Host-authoritative BombManager plant Rpc to every INIT-ready peer <b>except the planter</b>
    /// — planter's rpc/gaa/field/payload (do not hardcode); gold RX len41 near C2=40.
    /// Returns the number of peers that received the packet.
    /// </summary>
    /// <remarks>
    /// The planter already applied the plant locally (<c>gaa=AllCached</c> means «do not wait for
    /// the host»); sending it a rebuilt copy made the phone apply the same plant twice and throw
    /// «Invalid bomber» (2026-11-13 stack, see <see cref="MatchBombRpcDelivery"/>).
    /// </remarks>
    private int FanOutAlliesBombPlantRpc(
        MatchRoom room,
        byte sourceField,
        byte[] plantPayload,
        double plantTimeValue,
        byte rpcId,
        byte gaaTarget,
        NetPeer? planterPeer = null)
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
        // Plant Rpc is decisive: C2=40 is already out, so a dropped Rpc leaves that peer with a
        // planted bomb state but no bomb mesh (#33). Never droppable — and never doubled.
        var n = 0;
        var nReady = 0;
        var heldBack = 0;
        var gated = 0;
        foreach (var (p, st) in _peers)
        {
            if (st.Room != room || !st.BootstrapSent)
                continue;
            nReady++;
            if (MatchWorldGate.HoldsTraffic(st.BootstrapSent, st.WorldReady))
            {
                // No scene yet: the plant is handed over when its gate opens (SyncBombPlantStateToPeer).
                gated++;
                continue;
            }
            if (!MatchBombRpcDelivery.ShouldSendPlantRpc(st.BootstrapSent, ReferenceEquals(p, planterPeer)))
            {
                heldBack++;
                continue;
            }
            if (SendCritical(p, pkt))
                n++;
        }
        Console.WriteLine(
            $"[match-host] allies: BombManager plant fan-out field={sourceField} " +
            $"rpc={rpcId} gaaIn={gaaTarget} payloadLen={plantPayload.Length} " +
            $"→ peers={n} initReady={nReady} planterExcluded={heldBack} gated={gated}" +
            (nReady >= 2 && n < nReady - heldBack ? " WARN incomplete fan-out" : ""));
        return n;
    }

    /// <summary>
    /// Late-join / reconnect: if bomb is already planted, push the same BombManager plant Rpc
    /// the planter's peers got on plant (C2=40 room snapshot alone does not spawn bomb UI).
    /// Reuses stored planter field/rpc/payload — no invented opcodes.
    /// </summary>
    private void SyncBombPlantStateToPeer(NetPeer peer, MatchRoom room)
    {
        if (!IsAlliesRoom(room))
            return;

        bool planted;
        byte[]? payload;
        byte field;
        byte rpcId;
        double timeVal;
        lock (_roomGate)
        {
            planted = room.Flow.BombPlanted
                && (room.Flow.Phase == MatchFlowPhase.BombPlanted
                    || room.RoomC2 == MatchC2States.BombPlanted);
            payload = room.Flow.LastBombPlantPayload;
            field = room.Flow.LastBombPlantField;
            rpcId = room.Flow.LastBombPlantRpcId;
            timeVal = room.Flow.LastBombPlantTimeValue;
        }

        if (!planted)
            return;

        if (payload is not { Length: > 0 } || field == 0)
        {
            Console.WriteLine(
                $"[match-host] {AlliesFamilyLogTag(room)}: reconnect bomb sync SKIPPED — " +
                "planted but no stored plant Rpc payload (C2=40 only)");
            return;
        }

        var stime = NextServerTime();
        var pkt = MatchCodec.BuildWorldObjectRpc(
            stime,
            objectId: MatchFlowTestParams.BombManagerObjectId,
            rpcId: rpcId == 0 ? (byte)2 : rpcId,
            gaaTarget: 2,
            field: field,
            timeValue: timeVal > 0 ? timeVal : stime / 1000.0,
            payload: payload);
        SendAndDump(peer, pkt);
        Console.WriteLine(
            $"[match-host] {AlliesFamilyLogTag(room)}: reconnect bomb sync → peer " +
            $"field={field} rpc={rpcId} payloadLen={payload.Length} (C2=40 already in snapshot)");
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
    internal static List<(string Key, LobbyVariant Value)> BuildAlliesRoundEndRoomProps(
        double nowSec,
        MatchTeam winner,
        int scoreTr,
        int scoreCt,
        int coLossesTr,
        int coLossesCt,
        List<(string Key, LobbyVariant Value)> winTeamProps,
        double durationSec = 6.0)
    {
        var props = new List<(string Key, LobbyVariant Value)>
        {
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            // 2.07 gold: round-end bag also carries Duration (6.0) — issue #27.
            (MatchRoomPropKeys.Duration, LobbyVariant.FromDouble(durationSec)),
        };
        // Gold 2.07 C2=101 is winner-shaped: only the winner's score key, then
        // {loser}CoLosses, {winner}CoLosses, WinTeam, C2 (13/13 round-end bags).
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
    /// Defer C2=31 once during C2=22 buy if a fighter picked team but has not spawned —
    /// extend host wall only; wire <c>Time</c> stays an anchor (now), never a deadline.
    /// </summary>
    private bool TryExtendAlliesBuyForAwaitingSpawn(MatchRoom room)
    {
        if (!FightersAwaitingSpawn(room))
            return false;

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
        }

        var nowSec = ServerTimeSeconds();
        // Issue #29: no bomberId in this extra bag — 2.07 gold carries it only in C2=22.
        var props = new List<(string Key, LobbyVariant Value)>
        {
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.Duration, LobbyVariant.FromDouble(AlliesFlowParams.BuyPhase.TotalSeconds)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
        };
        BroadcastRoomProps(room, props, reason: "Allies buy spawn-extend 5s");
        Console.WriteLine(
            "[match-host] allies: buy C2=22 extended 5s — spawn wait (Time=RST=now anchor)");
        return true;
    }
}
