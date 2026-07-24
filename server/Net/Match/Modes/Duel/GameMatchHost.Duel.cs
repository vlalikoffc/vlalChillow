using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using StandChillow.LanServer.Net.Match.Logic.Defuse;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Duel host flow — gold <c>MATCH_DUEL_PROBE.md</c> (<c>20260724_041*</c>):
/// C2=10 → C2=11 FFA (~20s) → score wipe → C2=21 → per round
/// ReCreate×2 → C2=22 (~5s freeze) → C2=31 combat → wipe → GameRpcHelper field=1 →
/// C2=101 WinTeam → ~5s → next 22; first-to-8 → C2=201 FinalHud.
/// No BombManager / C2=40. Weapons: Escalation-style — clients own loadout; host does
/// <b>not</b> invent <c>current_loadout</c> / modifier schedule (no known builder).
/// </summary>
public sealed partial class GameMatchHost
{
    private const string DuelModeId = "Duel";

    private bool IsDuelRoom(MatchRoom room)
    {
        if (string.Equals(_matchGameModeId, DuelModeId, StringComparison.Ordinal))
            return true;
        lock (_roomGate)
        {
            return room.ActorProps.TryGetValue((0, MatchRoomPropKeys.C0), out var c0)
                && c0.Kind == LobbyVariantKind.String
                && string.Equals(c0.String, DuelModeId, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Duel phase machine — branched from <c>TickMatchFlowRoom</c> when C0=Duel.
    /// </summary>
    private void TickDuelFlowRoom(MatchRoom room)
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
                if (!MatchHostSettings.MatchStartArmed)
                    return;
            }
        }

        if (phase == MatchFlowPhase.MatchOver)
        {
            TryFireMatchOverLobbyReturn(room, ends);
            return;
        }

        if (phase == MatchFlowPhase.DeathMatchFinalHud)
        {
            if (DateTime.UtcNow < ends)
                return;
            ArmDuelLobbyReturnAfterFinalHud(room);
            return;
        }

        // Combat wipe on C2=31 only (PurchasePhase). No bomb / plant path.
        if (MatchFlowRules.AllowsWipeCheck(phase))
        {
            if (pendingReason is not null)
            {
                EnterRoundEndPause(room, pendingWinner, pendingReason);
                return;
            }

            if (TryResolveWipeImmediate(room, bombPlanted: false))
                return;
        }

        // Host-only combat safety timeout while wire stays on C2=31 (no Live Time TX).
        if (phase == MatchFlowPhase.PurchasePhase)
        {
            if (DateTime.UtcNow < ends)
                return;
            Console.WriteLine(
                "[match-host] duel: round timeout → CT win " +
                $"(PhaseEndsUtc={ends:O} dur={DuelFlowParams.RoundDuration.TotalSeconds:0}s " +
                "C2=31 combat; no Live Time on wire)");
            PostServerDebugChat(
                $"таймаут раунда {DuelFlowParams.RoundDuration.TotalSeconds:0}s → CT");
            EnterRoundEndPause(room, MatchTeam.Ct, "timeout");
            return;
        }

        if (DateTime.UtcNow < ends)
            return;

        switch (phase)
        {
            case MatchFlowPhase.WaitingPlayers:
                EnterDuelPreWarmup(room);
                break;
            case MatchFlowPhase.AlliesPreWarmup:
                WipeDuelFfaScores(room);
                EnterDuelWarmUp(room);
                break;
            case MatchFlowPhase.Warmup:
                EnterDuelPreStart(room);
                break;
            case MatchFlowPhase.WarmupWillFinish:
                if (TryExtendDuelFreezeForAwaitingSpawn(room))
                    break;
                EnterDuelCombat(room);
                break;
            case MatchFlowPhase.RoundEndPause:
                ContinueAfterRoundEndDuel(room);
                break;
        }
    }

    private void ContinueAfterRoundEndDuel(MatchRoom room)
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
            EnterDuelFinalHud(room, scoreTr, scoreCt, round);
            return;
        }

        Console.WriteLine(
            $"[match-host] duel: next PreStart C2={MatchC2States.WarmupWillFinish} " +
            $"(round {round}→{round + 1}; skip WarmUp)");
        EnterDuelPreStart(room);
    }

    /// <summary>
    /// C2=11 DeathMatchPreWarmup / FFA prelude — gold ≈20s wall, then score wipe → WarmUp.
    /// </summary>
    private void EnterDuelPreWarmup(MatchRoom room)
    {
        var dur = DuelFlowParams.PreWarmup;
        var ends = DefuseTimer.ArmDeadline(dur);
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
            room.Flow.ScoreTr = 0;
            room.Flow.ScoreCt = 0;
            room.Flow.CoLossesTr = 0;
            room.Flow.CoLossesCt = 0;
        }
        MatchHostSettings.MatchStartArmed = false;
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.DeathMatchPreWarmup)),
        ], reason: "Duel PreWarmup C2=11");
        Console.WriteLine(
            $"[match-host] duel: PreWarmup C2={MatchC2States.DeathMatchPreWarmup} " +
            $"dur={dur.TotalSeconds:0}s (FFA; score wipe before WarmUp)");
        PostServerDebugChat("FFA · C2=11");
    }

    /// <summary>
    /// Gold: before C2=21 host resets kills/assists/death/score on all fighters (FFA wipe).
    /// </summary>
    private void WipeDuelFfaScores(MatchRoom room)
    {
        List<byte> fighters;
        lock (_roomGate)
        {
            fighters = room.Actors
                .Select(a => a.Nr)
                .Where(nr => nr != MatchHostActor.ActorNr)
                .Where(nr =>
                {
                    var t = GetActorTeam(room, nr);
                    return t is MatchTeam.Tr or MatchTeam.Ct;
                })
                .ToList();
            foreach (var nr in fighters)
            {
                room.ActorProps[(nr, MatchRoomPropKeys.Kills)] = LobbyVariant.FromInt(0);
                room.ActorProps[(nr, MatchRoomPropKeys.Assists)] = LobbyVariant.FromInt(0);
                room.ActorProps[(nr, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(0);
                room.ActorProps[(nr, MatchRoomPropKeys.Score2)] = LobbyVariant.FromInt(0);
                room.ActorProps[(nr, MatchRoomPropKeys.RoundKills)] = LobbyVariant.FromInt(0);
            }
        }

        foreach (var nr in fighters)
        {
            BroadcastActorIntProp(room, nr, MatchRoomPropKeys.Kills, 0);
            BroadcastActorIntProp(room, nr, MatchRoomPropKeys.Assists, 0);
            BroadcastActorIntProp(room, nr, MatchRoomPropKeys.Death, 0);
            BroadcastActorIntProp(room, nr, MatchRoomPropKeys.Score2, 0);
            BroadcastActorIntProp(room, nr, MatchRoomPropKeys.RoundKills, 0);
        }

        Console.WriteLine(
            $"[match-host] duel: FFA score wipe actors=[{string.Join(",", fighters)}] " +
            "(kills/assists/death/score/round_kills → 0)");
    }

    private void BroadcastActorIntProp(MatchRoom room, byte actorNr, string key, int value)
    {
        var pkt = MatchCodec.BuildSetProperty(
            NextServerTime(), actorNr, key, LobbyVariant.FromInt(value));
        BroadcastRoom(room, pkt, tag: "match_tx_SetProperty");
    }

    /// <summary>C2=21 WarmUp — gold ≈3.1s then first PreStart C2=22.</summary>
    private void EnterDuelWarmUp(MatchRoom room)
    {
        var dur = DuelFlowParams.WarmUp;
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
        }
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmUp)),
        ], reason: "Duel WarmUp C2=21");
        Console.WriteLine(
            $"[match-host] duel: WarmUp C2={MatchC2States.WarmUp} dur={dur.TotalSeconds:0}s " +
            "(after FFA wipe; R1 only)");
        PostServerDebugChat("WarmUp");
    }

    /// <summary>
    /// C2=22 freeze — gold: ReCreateSceneManager ×2, <c>Time == RoundStartTime ≈ now</c>,
    /// Round. Host waits ~5s then C2=31. No <c>current_loadout</c> / modifiers (no builder;
    /// Escalation also leaves weapons to clients).
    /// </summary>
    private void EnterDuelPreStart(MatchRoom room)
    {
        int round;
        var dur = DuelFlowParams.Freeze;
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
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
            room.LastCombatDamage.Clear();
            foreach (var a in room.Actors)
            {
                if (a.Nr != MatchHostActor.ActorNr)
                    room.ActorProps[(a.Nr, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(0);
            }
        }

        BroadcastDuelReCreateSceneManagers(room);
        ResetDuelRoundKills(room);
        var nowSec = ServerTimeSeconds();
        // Gold: Time == RoundStartTime == now. Omit loadout/modifier keys until builder known.
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.Round, LobbyVariant.FromInt(round)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmupWillFinish)),
        ], reason: $"Duel freeze C2=22 round={round}");

        ClearFighterDeathFlags(room);
        DestroyTrackedRoundEntities(room, reason: "Duel freeze C2=22");

        Console.WriteLine(
            $"[match-host] duel: freeze C2={MatchC2States.WarmupWillFinish} " +
            $"Time=RST={nowSec:0.###} wall={dur.TotalSeconds:0}s → C2=31 combat " +
            $"(round={round}; no current_loadout TX — client weapons / TODO modifiers)");
        PostServerDebugChat($"Заморозка · раунд {round} ({dur.TotalSeconds:0}s)");
    }

    private void ResetDuelRoundKills(MatchRoom room)
    {
        List<byte> fighters;
        lock (_roomGate)
        {
            fighters = room.Actors
                .Select(a => a.Nr)
                .Where(nr => nr != MatchHostActor.ActorNr)
                .Where(nr =>
                {
                    var t = GetActorTeam(room, nr);
                    return t is MatchTeam.Tr or MatchTeam.Ct;
                })
                .ToList();
            foreach (var nr in fighters)
                room.ActorProps[(nr, MatchRoomPropKeys.RoundKills)] = LobbyVariant.FromInt(0);
        }
        foreach (var nr in fighters)
            BroadcastActorIntProp(room, nr, MatchRoomPropKeys.RoundKills, 0);
    }

    /// <summary>
    /// C2=31 combat — gold bag: <c>Time ≈ now</c>, <c>C2</c> only (no roster counts).
    /// Stay until wipe / host timeout → C2=101.
    /// </summary>
    private void EnterDuelCombat(MatchRoom room)
    {
        SyncDeadActorsFromDeathProps(room);
        if (TryResolveWipeImmediate(room, bombPlanted: false))
            return;

        int round;
        var roundDur = DuelFlowParams.RoundDuration;
        var ends = DefuseTimer.ArmDeadline(roundDur);
        lock (_roomGate)
        {
            round = room.Flow.RoundIndex;
            if (round < 1) round = 1;
            room.Flow.Phase = MatchFlowPhase.PurchasePhase;
            room.Flow.PhaseEndsUtc = ends;
            room.Flow.PendingEndReason = null;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
        }

        var nowSec = ServerTimeSeconds();
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.PurchasePhase)),
        ], reason: $"Duel combat C2=31 round={round}");
        Console.WriteLine(
            $"[match-host] duel: combat C2={MatchC2States.PurchasePhase} round={round} " +
            $"Time={nowSec:0.###} hostRound={roundDur.TotalSeconds:0}s " +
            "(stay on C2=31 until wipe / WinTeam 101; no plant 40)");
        PostServerDebugChat($"Live · раунд {round} · C2=31");
    }

    /// <summary>
    /// Gold C2=201 FinalHud: Time, FinalPlayers (actor-nr bytes), FinalWinTeam, C2.
    /// Then brief hold → lobby return (dedicated rematch path).
    /// </summary>
    private void EnterDuelFinalHud(MatchRoom room, int scoreTr, int scoreCt, int round)
    {
        byte[] finalPlayers;
        byte winnerTeam;
        bool isDraw;
        var pause = DuelFlowParams.FinalHudPause;
        lock (_roomGate)
        {
            finalPlayers = room.Actors
                .Select(a => a.Nr)
                .OrderBy(n => n)
                .ToArray();
            isDraw = scoreTr == scoreCt;
            winnerTeam = isDraw
                ? (byte)MatchTeam.None
                : scoreTr > scoreCt ? (byte)MatchTeam.Tr : (byte)MatchTeam.Ct;
            room.Flow.Phase = MatchFlowPhase.DeathMatchFinalHud;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + pause;
            room.Flow.LobbyReturnPending = false;
        }

        var nowSec = ServerTimeSeconds();
        var finalWinTeam = new List<(string Key, LobbyVariant Value)>
        {
            (MatchFinalWinTeamKeys.IsDraw, LobbyVariant.FromBool(isDraw)),
            (MatchFinalWinTeamKeys.IsGiveUp, LobbyVariant.FromBool(false)),
            (MatchFinalWinTeamKeys.Team, LobbyVariant.FromByte(winnerTeam)),
        };
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.FinalPlayers, LobbyVariant.FromBytes(finalPlayers)),
            (MatchRoomPropKeys.FinalWinTeam, LobbyVariant.FromProps(finalWinTeam)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.FinalHud)),
        ], reason: $"Duel FinalHud Tr={scoreTr} Ct={scoreCt}");

        Console.WriteLine(
            $"[match-host] duel: FinalHud C2={MatchC2States.FinalHud} after round={round} " +
            $"Tr={scoreTr} Ct={scoreCt} winnerTeam={winnerTeam} " +
            $"FinalPlayers=[{string.Join(" ", finalPlayers.Select(b => b.ToString("X2")))}] " +
            $"hold={pause.TotalSeconds:0}s → lobby return");

        var winner = (MatchTeam)winnerTeam;
        var winSide = isDraw ? "DRAW" : winner == MatchTeam.Tr ? "ATTACK (T)" : "DEFENSE (CT)";
        Dashboard.DashboardHub.PostRoundResult(
            $"MATCH OVER: {winSide}  ·  T {scoreTr} : {scoreCt} CT",
            winner, mvpNr: 0, mvpName: null, isFinal: true);
        PostServerDebugChat($"Финал {scoreTr}:{scoreCt}");
    }

    private void ArmDuelLobbyReturnAfterFinalHud(MatchRoom room)
    {
        lock (_roomGate)
        {
            room.Flow.Phase = MatchFlowPhase.MatchOver;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + MatchOverReturnToLobbyDelay;
            room.Flow.LobbyReturnPending = true;
        }
        Console.WriteLine(
            $"[match-host] duel: FinalHud done — lobby return in " +
            $"{MatchOverReturnToLobbyDelay.TotalSeconds:0}s");
    }

    /// <summary>
    /// ReCreateSceneManager ×2 — gold every C2=22. Ids = WeaponDrop(4) + Grenade(5)
    /// (Duel scene set has no BombManager / Radar to recreate).
    /// </summary>
    private void BroadcastDuelReCreateSceneManagers(MatchRoom room)
    {
        var stime = NextServerTime();
        foreach (var id in DuelFlowParams.RecreateSceneManagerIds)
        {
            var pkt = MatchCodec.BuildReCreateSceneManager(stime, id);
            var n = BroadcastInitReady(room, pkt, tag: "match_tx_ReCreateSceneManager");
            Console.WriteLine(
                $"[match-host] duel: ReCreateSceneManager id={id} → peers={n}");
        }
    }

    /// <summary>
    /// Gold round-end before C2=101: GameRpcHelper field=1, gaa=3, empty payload.
    /// </summary>
    private void BroadcastDuelGameRpcHelperRoundEnd(MatchRoom room)
    {
        var stime = NextServerTime();
        var pkt = MatchCodec.BuildWorldObjectRpc(
            stime,
            objectId: DuelFlowParams.GameRpcHelperObjectId,
            rpcId: 1,
            gaaTarget: 3,
            field: DuelFlowParams.GameRpcHelperFieldRoundEnd,
            timeValue: stime / 1000.0,
            payload: null);
        var n = BroadcastInitReady(room, pkt, tag: "match_tx_GameRpcHelper_roundEnd");
        Console.WriteLine(
            $"[match-host] duel: GameRpcHelper field={DuelFlowParams.GameRpcHelperFieldRoundEnd} " +
            $"(0-payload) → peers={n}");
    }

    /// <summary>
    /// Gold C2=101 bag len≈119: Time, winner score only, WinTeam, C2 — no CoLosses.
    /// </summary>
    private static List<(string Key, LobbyVariant Value)> BuildDuelRoundEndRoomProps(
        double nowSec,
        MatchTeam winner,
        int scoreTr,
        int scoreCt,
        List<(string Key, LobbyVariant Value)> winTeamProps)
    {
        var props = new List<(string Key, LobbyVariant Value)>
        {
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
        };
        if (winner == MatchTeam.Tr)
            props.Add((MatchRoomPropKeys.TrScore, LobbyVariant.FromInt(scoreTr)));
        else
            props.Add((MatchRoomPropKeys.CtScore, LobbyVariant.FromInt(scoreCt)));

        props.Add((MatchRoomPropKeys.WinTeam, LobbyVariant.FromProps(winTeamProps)));
        props.Add((MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.MatchStarted)));
        return props;
    }

    /// <summary>
    /// Defer C2=31 once during C2=22 freeze if a fighter picked team but has not spawned.
    /// </summary>
    private bool TryExtendDuelFreezeForAwaitingSpawn(MatchRoom room)
    {
        if (!FightersAwaitingSpawn(room))
            return false;
        bool already;
        lock (_roomGate)
        {
            already = room.Flow.PrepSpawnExtensionUsed;
            if (already)
                return false;
            room.Flow.PrepSpawnExtensionUsed = true;
            room.Flow.PhaseEndsUtc = DefuseTimer.ArmDeadline(DuelFlowParams.Freeze);
        }
        Console.WriteLine(
            "[match-host] duel: freeze extended once — fighter awaiting spawn");
        return true;
    }
}
