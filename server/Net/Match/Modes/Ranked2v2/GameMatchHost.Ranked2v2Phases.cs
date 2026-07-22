using System.Buffers.Binary;
using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

public sealed partial class GameMatchHost
{
    // Ranked2v2 / Allies phase transitions (Warmup→…→Live/Bomb).
    private void TryBeginMatchFlowIfBothTeams(MatchRoom room)
    {
        lock (_roomGate)
        {
            if (room.Flow.Phase != MatchFlowPhase.WaitingPlayers)
                return;
            if (!BothFightingTeamsPresent(room))
                return;
        }
        Console.WriteLine(
            "[match-host] match-flow: both teams have ≥1 player — will start Warmup on next tick");
    }

    private static bool BothFightingTeamsPresent(MatchRoom room)
    {
        var hasTr = false;
        var hasCt = false;
        foreach (var ((actor, key), value) in room.ActorProps)
        {
            if (key != MatchRoomPropKeys.Team || actor == MatchHostActor.ActorNr)
                continue;
            if (value.Kind != LobbyVariantKind.Byte)
                continue;
            var team = (MatchTeam)value.Byte;
            if (team == MatchTeam.Tr) hasTr = true;
            if (team == MatchTeam.Ct) hasCt = true;
        }
        return hasTr && hasCt;
    }

    private void TickMatchFlow()
    {
        List<MatchRoom> rooms;
        lock (_roomGate)
            rooms = _roomsByPassword.Values.ToList();

        foreach (var room in rooms)
        {
            ProcessPendingCombatDestroys(room);
            TickMatchFlowRoom(room);
        }
    }

    private void TickMatchFlowRoom(MatchRoom room)
    {
        MatchFlowPhase phase;
        DateTime ends;
        string? pendingReason;
        MatchTeam pendingWinner;
        lock (_roomGate)
        {
            phase = room.Flow.Phase;
            ends = room.Flow.PhaseEndsUtc;
            pendingReason = room.Flow.PendingEndReason;
            pendingWinner = room.Flow.PendingWinner;
            if (phase == MatchFlowPhase.WaitingPlayers)
            {
                if (!BothFightingTeamsPresent(room))
                    return;
            }
            else if (phase == MatchFlowPhase.MatchOver)
                return;
            else if (pendingReason is not null && MatchFlowRules.AllowsWipeCheck(phase))
            {
                // Wipe / early end queued from death/plant path — do not wait for phase timer.
            }
            else if (DateTime.UtcNow < ends)
                return;
        }

        if (pendingReason is not null && MatchFlowRules.AllowsWipeCheck(phase))
        {
            EnterRoundEndPause(room, pendingWinner, pendingReason);
            return;
        }

        switch (phase)
        {
            case MatchFlowPhase.WaitingPlayers:
                EnterWarmup(room);
                break;
            case MatchFlowPhase.Warmup:
                EnterWarmupWillFinish(room);
                break;
            case MatchFlowPhase.WarmupWillFinish:
                EnterPurchasePhase(room, nextRound: true);
                break;
            case MatchFlowPhase.PurchasePhase:
                if (TryExtendPrepForAwaitingSpawn(room))
                    break;
                EnterRoundLive(room);
                break;
            case MatchFlowPhase.RoundLive:
                EnterRoundEndPause(room, MatchTeam.Ct, "timeout");
                break;
            case MatchFlowPhase.BombPlanted:
                EnterRoundEndPause(room, MatchTeam.Tr, "bomb-explode");
                break;
            case MatchFlowPhase.RoundEndPause:
                ContinueAfterRoundEnd(room);
                break;
        }
    }

    private void EnterWarmup(MatchRoom room)
    {
        var ends = DateTime.UtcNow + MatchFlowTestParams.Warmup;
        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + MatchFlowTestParams.Warmup.TotalSeconds;
        lock (_roomGate)
        {
            room.Flow.Phase = MatchFlowPhase.Warmup;
            room.Flow.PhaseEndsUtc = ends;
            room.Flow.RoundIndex = 0;
            room.Flow.BombPlanted = false;
            room.Flow.PendingEndReason = null;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
        }
        // Time/RoundStartTime are doubles in NetManager.bfqt units (TickCount/1000 seconds).
        // FetchServerTime HasServerTime header stays raw TickCount ms — client dws.bfwi divides.
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmUp)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
        ]);
        Console.WriteLine(
            $"[match-host] match-flow: Warmup C2={MatchC2States.WarmUp} " +
            $"{MatchFlowTestParams.Warmup.TotalSeconds:0}s movable (разминка — not freeze banner)");
    }

    /// <summary>
    /// WarmupWillFinish C2=22 — short freeze «MATCH WILL START IN» + assign <c>bomberId</c>
    /// (phone <c>cnr</c> / <c>bcb.opg</c>). Master then calls <c>BombManager.nyn</c> locally;
    /// dedicated has no evidenced give Rpc — see MATCH_WORLD.md.
    /// </summary>
    private void EnterWarmupWillFinish(MatchRoom room)
    {
        int bomberId;
        lock (_roomGate)
        {
            room.Flow.Phase = MatchFlowPhase.WarmupWillFinish;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + MatchFlowTestParams.WarmupWillFinish;
            room.Flow.BombPlanted = false;
            room.Flow.PendingEndReason = null;
            bomberId = PickBomberActorNr(room);
            room.Flow.BomberActorNr = bomberId;
        }

        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + MatchFlowTestParams.WarmupWillFinish.TotalSeconds;
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmupWillFinish)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
        ]);
        Console.WriteLine(
            $"[match-host] match-flow: PreStart C2={MatchC2States.WarmupWillFinish} " +
            $"{MatchFlowTestParams.WarmupWillFinish.TotalSeconds:0}s countdown " +
            $"bomberId={bomberId} (cnr/bcb.opg; BombManager.nyn is master-local — no host Rpc invent)");
    }

    /// <summary>
    /// PurchasePhase C2=31 — visible 10s «подготовка к раунду» every round (including first).
    /// Re-picks <c>bomberId</c> on rounds after the first (warmup already assigned round 1).
    /// </summary>
    private void EnterPurchasePhase(MatchRoom room, bool nextRound)
    {
        int round;
        int bomberId;
        lock (_roomGate)
        {
            round = nextRound ? room.Flow.RoundIndex + 1 : room.Flow.RoundIndex;
            if (round < 1) round = 1;
            room.Flow.RoundIndex = round;
            room.Flow.Phase = MatchFlowPhase.PurchasePhase;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + MatchFlowTestParams.PurchasePhase;
            room.Flow.BombPlanted = false;
            room.Flow.PendingEndReason = null;
            room.Flow.PrepSpawnExtensionUsed = false;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
            // Clear last round's death flags LOCALLY before picking the bomber. PickBomberActorNr
            // skips IsActorDead; if the previous round's `death=1` prop is still set, every living
            // T looks dead → bomberId=0 (smoking gun round 2 Prep). Broadcast happens below via
            // ClearFighterDeathFlags.
            foreach (var a in room.Actors)
            {
                if (a.Nr != MatchHostActor.ActorNr)
                    room.ActorProps[(a.Nr, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(0);
            }
            // Round 1: keep bomber chosen on C2=22. Later rounds: pick fresh living Tr.
            if (round <= 1 && room.Flow.BomberActorNr > 0)
                bomberId = room.Flow.BomberActorNr;
            else
            {
                bomberId = PickBomberActorNr(room);
                room.Flow.BomberActorNr = bomberId;
            }
        }

        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + MatchFlowTestParams.PurchasePhase.TotalSeconds;
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.PurchasePhase)),
            (MatchRoomPropKeys.Round, LobbyVariant.FromInt(round)),
            (MatchRoomPropKeys.RoundCount, LobbyVariant.FromInt(MatchFlowTestParams.TotalRounds)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
        ]);
        ClearFighterDeathFlags(room);
        Console.WriteLine(
            $"[match-host] match-flow: Prep C2={MatchC2States.PurchasePhase} 10s " +
            $"round={round}/{MatchFlowTestParams.TotalRounds} bomberId={bomberId}");
    }

    private void EnterRoundLive(MatchRoom room)
    {
        // Prep/prestart deaths: do not start 90s live with a team already wiped.
        if (TryResolveWipeImmediate(room, bombPlanted: false))
            return;

        int round;
        int bomberId;
        var txBomberFix = false;
        lock (_roomGate)
        {
            round = room.Flow.RoundIndex;
            // Last chance: living T on roster but bomberId still 0 (late Tr join race).
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
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + MatchFlowTestParams.RoundDuration;
            room.Flow.BombPlanted = false;
            room.Flow.PendingEndReason = null;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
        }

        if (txBomberFix && bomberId > 0)
        {
            BroadcastRoomProps(room,
            [
                (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
            ]);
            Console.WriteLine(
                $"[match-host] match-flow: bomberId={bomberId} (fixed 0→T at RoundLive edge)");
        }

        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + MatchFlowTestParams.RoundDuration.TotalSeconds;
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.MatchStarted)),
            (MatchRoomPropKeys.Round, LobbyVariant.FromInt(round)),
            (MatchRoomPropKeys.RoundCount, LobbyVariant.FromInt(MatchFlowTestParams.TotalRounds)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
        ]);
        SetAllFightersMoney(room, MatchFlowTestParams.RoundStartMoney);
        ClearFighterDeathFlags(room);
        Console.WriteLine(
            $"[match-host] match-flow: RoundLive C2={MatchC2States.MatchStarted} " +
            $"round={round}/{MatchFlowTestParams.TotalRounds} " +
            $"duration={MatchFlowTestParams.RoundDuration.TotalSeconds:0}s " +
            $"money={MatchFlowTestParams.RoundStartMoney} bomberId={bomberId}");
    }

    private void TryEnterBombPlanted(MatchRoom room, byte sourceRpc)
    {
        lock (_roomGate)
        {
            if (room.Flow.Phase != MatchFlowPhase.RoundLive || room.Flow.BombPlanted)
                return;
            room.Flow.BombPlanted = true;
            room.Flow.Phase = MatchFlowPhase.BombPlanted;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + MatchFlowTestParams.BombFuse;
        }

        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + MatchFlowTestParams.BombFuse.TotalSeconds;
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.BombPlanted)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
        ]);
        Console.WriteLine(
            $"[match-host] match-flow: BombPlanted C2={MatchC2States.BombPlanted} " +
            $"fuse={MatchFlowTestParams.BombFuse.TotalSeconds:0}s (BombManager rpc={sourceRpc})");
    }

    /// <summary>
    /// BombManager Rpc(6) <c>nzu(bool, byte, float)</c> — payload starts with bool via <c>fzr.bojd</c>.
    /// true → defuse (<c>vxr</c>) → CT win; false → explode FX (<c>vxq</c>) → T win.
    /// Ends round immediately while in BombPlanted (no fuse hang).
    /// </summary>
    private void HandleBombManagerNzu(MatchRoom room, byte[] payload)
    {
        if (payload.Length < 1)
        {
            Console.WriteLine(
                "[match-host] match-flow: BombManager rpc=6 nzu — empty payload, ignore");
            return;
        }

        var defused = payload[0] != 0;
        lock (_roomGate)
        {
            if (room.Flow.Phase is not (MatchFlowPhase.BombPlanted or MatchFlowPhase.RoundLive))
                return;
            if (room.Flow.PendingEndReason is not null)
                return;
            if (!room.Flow.BombPlanted && room.Flow.Phase != MatchFlowPhase.BombPlanted)
                return;

            room.Flow.PendingWinner = defused ? MatchTeam.Ct : MatchTeam.Tr;
            room.Flow.PendingEndReason = defused ? "bomb-defuse" : "bomb-explode-rpc";
        }

        Console.WriteLine(
            $"[match-host] match-flow: BombManager rpc=6 nzu defused={defused} " +
            $"→ {(defused ? "CT" : "T")} win (immediate RoundEnd)");
        EnterRoundEndPause(
            room,
            defused ? MatchTeam.Ct : MatchTeam.Tr,
            defused ? "bomb-defuse" : "bomb-explode-rpc");
    }

    /// <summary>
    /// Immediate full remove on peer disconnect / timeout: drop roster slot, props, pawns,
    /// TX <c>ActorLeftEvent</c> (fuo=51), then wipe-check remaining fighters via phone-host
    /// round-end (C2=101 + WinTeam/TrScore…) — never leave ghosts that force rejoin to actorNr=4/5.
    /// </summary>
}
