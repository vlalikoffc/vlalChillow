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
        byte mvpNr, mvpCode;
        int mvpCount = 0;
        lock (_roomGate)
        {
            // Ignore duplicate end while already pausing / over.
            if (room.Flow.Phase is MatchFlowPhase.RoundEndPause or MatchFlowPhase.MatchOver)
                return;

            round = room.Flow.RoundIndex;
            if (winner == MatchTeam.Tr)
            {
                room.Flow.ScoreTr++;
                room.Flow.CoLossesCt++;
                room.Flow.CoLossesTr = 0;
            }
            else if (winner == MatchTeam.Ct)
            {
                room.Flow.ScoreCt++;
                room.Flow.CoLossesTr++;
                room.Flow.CoLossesCt = 0;
            }
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
            room.Flow.PendingEndReason = null;
        }

        // Phone-host gold (run-20260722_100157 len≈151): actor mvp SetProperty first, then
        // room SetProperties: Time, {Tr|Ct}Score, {loser}CoLosses, {winner}CoLosses, WinTeam, C2=101.
        // NOT nested Score={Tr,Ct}, NOT C2=111/201, NOT winTeam/resultActor key names.
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
        BroadcastRoomProps(room, roomProps);
        Console.WriteLine(
            $"[match-host] match-flow: RoundEnd C2={MatchC2States.MatchStarted} round={round} " +
            $"winner={winner} reason={reason} " +
            $"TrScore={scoreTr} CtScore={scoreCt} " +
            $"TrCoLosses={coLossesTr} CtCoLosses={coLossesCt} " +
            $"mvpPlayer={mvpNr} mvpCode={mvpCode} " +
            $"(phone-host WinTeam+TrScore/CtScore; not FinalHud C2={MatchC2States.FinalHud}; " +
            $"silent pause {MatchFlowTestParams.RoundEndPause.TotalSeconds:0}s)");
    }

    /// <summary>
    /// Allies phone-host round-end room bag order (gold len≈151):
    /// Time, winner flat score, loser CoLosses, winner CoLosses=0 streak reset, WinTeam, C2=101.
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

    private void ContinueAfterRoundEnd(MatchRoom room)
    {
        int round;
        lock (_roomGate)
            round = room.Flow.RoundIndex;

        if (round >= MatchFlowTestParams.TotalRounds)
        {
            lock (_roomGate)
            {
                room.Flow.Phase = MatchFlowPhase.MatchOver;
                room.Flow.PhaseEndsUtc = DateTime.MaxValue;
            }
            BroadcastRoomProps(room,
            [
                (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.MatchResults)),
            ]);
            Console.WriteLine(
                $"[match-host] match-flow: MatchResults C2={MatchC2States.MatchResults} " +
                $"after {MatchFlowTestParams.TotalRounds} rounds " +
                "(FinalWinTeam wire unknown — C2 only)");
            return;
        }

        EnterPurchasePhase(room, nextRound: true);
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
