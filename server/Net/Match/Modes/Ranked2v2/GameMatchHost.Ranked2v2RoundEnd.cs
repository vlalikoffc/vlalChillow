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
    // Phone-host gold RoundEnd (C2=101 WinTeam) + MVP pick.
    private void EnterRoundEndPause(MatchRoom room, MatchTeam winner, string reason)
    {
        int round, scoreTr, scoreCt, coLossesTr, coLossesCt;
        int beforeTr = 0, beforeCt = 0;
        byte mvpNr, mvpCode;
        int mvpCount = 0;
        string scoreBumpKey = "";
        lock (_roomGate)
        {
            // Ignore duplicate end while already pausing / over.
            if (room.Flow.Phase is MatchFlowPhase.RoundEndPause or MatchFlowPhase.MatchOver)
            {
                Console.WriteLine(
                    $"[match-host] match-flow: RoundEnd IGNORED (already {room.Flow.Phase}) " +
                    $"reason={reason} — no score bump");
                return;
            }

            round = room.Flow.RoundIndex;
            beforeTr = room.Flow.ScoreTr;
            beforeCt = room.Flow.ScoreCt;
            // Same-round double-fire (defuse + wipe / timeout race): commit score once.
            if (round > 0 && room.Flow.RoundEndCommittedRound == round)
            {
                Console.WriteLine(
                    $"[match-host] match-flow: RoundEnd IGNORED (round {round} already scored) " +
                    $"reason={reason} TrScore={beforeTr} CtScore={beforeCt} — no score bump");
                room.Flow.PendingEndReason = null;
                room.Flow.Phase = MatchFlowPhase.RoundEndPause;
                room.Flow.PhaseEndsUtc = DateTime.UtcNow + MatchFlowTestParams.RoundEndPause;
                room.Flow.BombPlanted = false;
                room.Flow.BombPlantedUtc = DateTime.MinValue;
                room.Flow.PendingBombPlant = false;
                room.LastCombatDamage.Clear();
                return;
            }

            if (winner == MatchTeam.Tr)
            {
                room.Flow.ScoreTr++;
                room.Flow.CoLossesCt++;
                room.Flow.CoLossesTr = 0;
                scoreBumpKey = MatchRoomPropKeys.TrScore;
            }
            else if (winner == MatchTeam.Ct)
            {
                room.Flow.ScoreCt++;
                room.Flow.CoLossesTr++;
                room.Flow.CoLossesCt = 0;
                scoreBumpKey = MatchRoomPropKeys.CtScore;
            }
            else
            {
                Console.WriteLine(
                    $"[match-host] match-flow: RoundEnd IGNORED (winner={winner} not Tr/Ct) " +
                    $"reason={reason} — no score bump");
                return;
            }

            room.Flow.RoundEndCommittedRound = round;
            scoreTr = room.Flow.ScoreTr;
            scoreCt = room.Flow.ScoreCt;
            coLossesTr = room.Flow.CoLossesTr;
            coLossesCt = room.Flow.CoLossesCt;
            (mvpNr, mvpCode) = PickRoundMvp(room, winner, reason);
            if (mvpNr != 0)
            {
                mvpCount = ReadActorIntProp(room, mvpNr, MatchRoomPropKeys.Mvp);
                if (mvpCount < 0)
                    mvpCount = 0;
                mvpCount++;
                room.ActorProps[(mvpNr, MatchRoomPropKeys.Mvp)] = LobbyVariant.FromInt(mvpCount);
            }
            room.Flow.Phase = MatchFlowPhase.RoundEndPause;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + MatchFlowTestParams.RoundEndPause;
            room.Flow.BombPlanted = false;
            room.Flow.BombPlantedUtc = DateTime.MinValue;
            room.Flow.PendingBombPlant = false;
            room.Flow.EscalationCombatStarted = false;
            room.Flow.PendingEndReason = null;
            room.Flow.PendingCombatDestroy.Clear();
            room.LastCombatDamage.Clear();
        }

        Console.WriteLine(
            $"[match-host] match-flow: bomb authority clear (RoundEnd) " +
            $"BombPlanted=false BombPlantedUtc=cleared PendingBombPlant=false reason={reason}");
        DestroyTrackedRoundEntities(room, reason: $"RoundEnd:{reason}");
        Console.WriteLine(
            $"[match-host] match-flow: score bump +1 {scoreBumpKey} " +
            $"reason={reason} round={round} winner={winner} " +
            $"TrScore={beforeTr}→{scoreTr} CtScore={beforeCt}→{scoreCt}");

        // Phone-host gold (run-20260722_100157 len≈151): actor mvp SetProperty first, then
        // room SetProperties: Time, {Tr|Ct}Score, {loser}CoLosses, {winner}CoLosses, WinTeam, C2=101.
        // Dedicated also always includes BOTH TrScore+CtScore in the bag so stay-in clients
        // cannot stick on a prior 1:0 after a CT win (gold bag alone only carries winner key).
        if (mvpNr != 0)
        {
            var mvpPkt = MatchCodec.BuildSetProperty(
                NextServerTime(), mvpNr, MatchRoomPropKeys.Mvp, LobbyVariant.FromInt(mvpCount));
            BroadcastRoom(room, mvpPkt, tag: "match_tx_SetProperty");
            Console.WriteLine(
                $"[match-host] match-flow TX SetProperty actor={mvpNr} mvp={mvpCount}");
        }

        var nowSec = ServerTimeSeconds();
        var winTeamProps = BuildWinTeamProps(winner, mvpNr, mvpCode);
        var roomProps = BuildRoundEndRoomProps(
            nowSec, winner, scoreTr, scoreCt, coLossesTr, coLossesCt, winTeamProps);
        // Gold bag (both scores + WinTeam + C2=101) to all room peers — no exceptSender.
        BroadcastRoomProps(room, roomProps, reason: $"RoundEnd {reason} round={round}");
        // Explicit TrScore+CtScore SetProperty to ALL connected match peers so stay-in
        // clients match late-join snapshot (rejoin was the only path that had both scores).
        BroadcastMatchScoresToAllPeers(room, scoreTr, scoreCt);
        var pause = MatchFlowTestParams.RoundEndPause;
        Console.WriteLine(
            $"[match-host] match-flow: RoundEnd C2={MatchC2States.MatchStarted} round={round} " +
            $"winner={winner} reason={reason} " +
            $"TrScore={scoreTr} CtScore={scoreCt} " +
            $"TrCoLosses={coLossesTr} CtCoLosses={coLossesCt} " +
            $"mvpPlayer={mvpNr} mvpCode={mvpCode} " +
            $"pause={pause.TotalSeconds:0}s src=MATCH_WORLD silent " +
            $"(WinTeam bag + both score SetProperty to all peers; then {pause.TotalSeconds:0}s " +
            $"before PreStart C2=22; not FinalHud C2={MatchC2States.FinalHud})");

        if (reason is "bomb-defuse")
            PostServerDebugChat("бомба обезврежена");
        else if (reason is "bomb-explode" or "bomb-explode-rpc")
            PostServerDebugChat("бомба взорвалась");

        var winSideShort = winner == MatchTeam.Tr ? "T" : winner == MatchTeam.Ct ? "CT" : "?";
        PostServerDebugChat(
            $"раунд {round}: {winSideShort} ({FormatRoundEndReasonRu(reason)}) {scoreTr}:{scoreCt}");

        var winSide = winner == MatchTeam.Tr ? "ATTACK (T)" : winner == MatchTeam.Ct ? "DEFENSE (CT)" : "—";
        var mvpName = mvpNr != 0 ? ResolveActorName(mvpNr) : null;
        Dashboard.DashboardHub.PostRoundResult(
            $"Round {round}: {winSide} win  ·  {scoreTr}:{scoreCt}  ({reason})",
            winner, mvpNr, mvpName, isFinal: false);
    }

    /// <summary>
    /// Allies phone-host round-end room bag order (gold len≈151):
    /// Time, winner flat score, loser CoLosses, winner CoLosses=0 streak reset, WinTeam, C2=101.
    /// Dedicated always publishes <b>both</b> <c>TrScore</c> and <c>CtScore</c> (winner first),
    /// then follows with <see cref="BroadcastMatchScoresToAllPeers"/> so stay-in peers never
    /// keep a stale opposing score after RoundEnd.
    /// </summary>
    private static List<(string Key, LobbyVariant Value)> BuildRoundEndRoomProps(
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
        // Winner score key first (gold order), then the other team score (dedicated stay-in).
        if (winner == MatchTeam.Tr)
        {
            props.Add((MatchRoomPropKeys.TrScore, LobbyVariant.FromInt(scoreTr)));
            props.Add((MatchRoomPropKeys.CtScore, LobbyVariant.FromInt(scoreCt)));
            props.Add((MatchRoomPropKeys.CtCoLosses, LobbyVariant.FromInt(coLossesCt)));
            props.Add((MatchRoomPropKeys.TrCoLosses, LobbyVariant.FromInt(coLossesTr)));
        }
        else
        {
            props.Add((MatchRoomPropKeys.CtScore, LobbyVariant.FromInt(scoreCt)));
            props.Add((MatchRoomPropKeys.TrScore, LobbyVariant.FromInt(scoreTr)));
            props.Add((MatchRoomPropKeys.TrCoLosses, LobbyVariant.FromInt(coLossesTr)));
            props.Add((MatchRoomPropKeys.CtCoLosses, LobbyVariant.FromInt(coLossesCt)));
        }

        props.Add((MatchRoomPropKeys.WinTeam, LobbyVariant.FromProps(winTeamProps)));
        props.Add((MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.MatchStarted)));
        return props;
    }

    private void ContinueAfterRoundEnd(MatchRoom room)
    {
        int round;
        lock (_roomGate)
            round = room.Flow.RoundIndex;

        int scoreTr, scoreCt;
        lock (_roomGate)
        {
            scoreTr = room.Flow.ScoreTr;
            scoreCt = room.Flow.ScoreCt;
        }

        // MR-N: first to WinsNeeded (=N/2+1), or all N rounds played (draw possible N/2:N/2).
        if (MatchHostSettings.IsMatchSeriesOver(round, scoreTr, scoreCt))
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
            var why = scoreTr >= MatchHostSettings.WinsNeeded || scoreCt >= MatchHostSettings.WinsNeeded
                ? $"first-to-{MatchHostSettings.WinsNeeded} (MR-{MatchHostSettings.TotalRounds})"
                : $"full series {MatchHostSettings.TotalRounds} rounds";
            Console.WriteLine(
                $"[match-host] match-flow: MatchResults C2={MatchC2States.MatchResults} " +
                $"Tr={scoreTr} Ct={scoreCt} after round={round} — {why} " +
                "(FinalWinTeam wire unknown — C2 only)");
            return;
        }

        Console.WriteLine(
            $"[match-host] match-flow: next-round after RoundEnd pause — " +
            $"enter PreStart C2={MatchC2States.WarmupWillFinish} " +
            $"(round {round}→{round + 1}; series MR-{MatchHostSettings.TotalRounds} " +
            $"first-to-{MatchHostSettings.WinsNeeded}; gold — never skip C2=22)");
        EnterWarmupWillFinish(room);
    }

    /// <summary>
    /// Nested <c>WinTeam</c> for round end — gold keys <c>team</c>/<c>mvpPlayer</c>/<c>mvpCode</c>/
    /// <c>resultRoundType</c>/<c>resultRoundActor</c> as Byte. <c>mvpCode</c> = <see cref="MatchMvpCodes"/>.
    /// <c>resultRoundActor</c> is 0 on all four Allies gold outcomes (not mirrored from mvpPlayer).
    /// </summary>
    private static List<(string Key, LobbyVariant Value)> BuildWinTeamProps(
        MatchTeam winner,
        byte mvpNr,
        byte mvpCode) =>
    [
        (MatchWinTeamKeys.Team, LobbyVariant.FromByte((byte)winner)),
        (MatchWinTeamKeys.MvpPlayer, LobbyVariant.FromByte(mvpNr)),
        (MatchWinTeamKeys.MvpCode, LobbyVariant.FromByte(mvpCode)),
        (MatchWinTeamKeys.ResultRoundType, LobbyVariant.FromByte(0)),
        (MatchWinTeamKeys.ResultRoundActor, LobbyVariant.FromByte(0)),
    ];

    /// <summary>
    /// Pick MVP actor + <c>cns</c> code from round-end reason (phone <c>cnp.WinTeam</c> paths).
    /// Defuse → DefusingBomb + living/any Ct; plant/explode → PlantingBomb + bomberId;
    /// wipe/timeout → MostEliminations + max <c>round_kills</c> on winning team.
    /// </summary>
    private static (byte MvpActorNr, byte MvpCode) PickRoundMvp(
        MatchRoom room,
        MatchTeam winner,
        string reason)
    {
        byte code;
        byte preferred = 0;
        if (reason is "bomb-defuse")
        {
            code = MatchMvpCodes.DefusingBomb;
            preferred = PickTeamActor(room, MatchTeam.Ct, preferAlive: true);
        }
        else if (reason is "bomb-explode" or "bomb-explode-rpc")
        {
            code = MatchMvpCodes.PlantingBomb;
            preferred = room.Flow.BomberActorNr > 0
                && GetActorTeam(room, (byte)room.Flow.BomberActorNr) == MatchTeam.Tr
                    ? (byte)room.Flow.BomberActorNr
                    : PickTeamActor(room, MatchTeam.Tr, preferAlive: false);
        }
        else
        {
            // wipe-ct / wipe-tr / timeout / disconnect wipe — cnp uses MostEliminations.
            code = MatchMvpCodes.MostEliminations;
            preferred = PickTopRoundKillsActor(room, winner);
        }

        if (preferred == 0)
            preferred = PickTeamActor(room, winner, preferAlive: false);
        return (preferred, preferred == 0 ? MatchMvpCodes.None : code);
    }

    private static byte PickTopRoundKillsActor(MatchRoom room, MatchTeam team)
    {
        byte best = 0;
        var bestKills = -1;
        foreach (var ((actor, key), value) in room.ActorProps)
        {
            if (key != MatchRoomPropKeys.Team || actor == MatchHostActor.ActorNr)
                continue;
            if (value.Kind != LobbyVariantKind.Byte || (MatchTeam)value.Byte != team)
                continue;
            var kills = ReadActorIntProp(room, actor, MatchRoomPropKeys.RoundKills);
            if (kills < 0)
                kills = ReadActorIntProp(room, actor, MatchRoomPropKeys.Kills);
            if (kills < 0)
                kills = 0;
            if (kills > bestKills || (kills == bestKills && (best == 0 || actor < best)))
            {
                bestKills = kills;
                best = actor;
            }
        }
        return best;
    }

    private static byte PickTeamActor(MatchRoom room, MatchTeam team, bool preferAlive)
    {
        byte fallback = 0;
        foreach (var ((actor, key), value) in room.ActorProps)
        {
            if (key != MatchRoomPropKeys.Team || actor == MatchHostActor.ActorNr)
                continue;
            if (value.Kind != LobbyVariantKind.Byte || (MatchTeam)value.Byte != team)
                continue;
            fallback = actor;
            if (preferAlive && !MatchFlowRules.IsActorDead(room.Flow, room.ActorProps, actor))
                return actor;
            if (!preferAlive)
                return actor;
        }
        return fallback;
    }

    private static int ReadActorIntProp(MatchRoom room, byte actorNr, string key)
    {
        if (!room.ActorProps.TryGetValue((actorNr, key), out var v))
            return -1;
        return v.Kind switch
        {
            LobbyVariantKind.Int => v.Int,
            LobbyVariantKind.Byte => v.Byte,
            _ => -1,
        };
    }

    /// <summary>
    /// Mid-prep rejoin: Tr picked team while <c>bomberId=0</c> (prior T disconnect) — assign now.
    /// </summary>
}
