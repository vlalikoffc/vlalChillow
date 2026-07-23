using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

public sealed partial class GameMatchHost
{
    // Allies half-time after round 7: C2=111 → team swap + C2=112 → C2=113 → Prep C2=22.
    // Gold: allies-probe run-20260723_215727 (Sandstone 2x2 / Ranked2v2).

    private bool NeedsAlliesHalfTime(MatchRoom room, int roundIndex)
    {
        if (IsEscalationRoom(room) || IsDeathMatchRoom(room))
            return false;
        lock (_roomGate)
        {
            if (room.Flow.TeamsSwapped)
                return false;
        }
        return roundIndex == AlliesFlowParams.HalfTimeAfterRound;
    }

    private void EnterHalfTimeIntro(MatchRoom room)
    {
        var dur = AlliesFlowParams.HalfTimeIntro;
        var ends = DateTime.UtcNow + dur;
        lock (_roomGate)
        {
            room.Flow.Phase = MatchFlowPhase.HalfTimeIntro;
            room.Flow.PhaseEndsUtc = ends;
        }

        var nowSec = ServerTimeSeconds();
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.RoundEnd)),
        ], reason: "HalfTime intro C2=111");
        Console.WriteLine(
            $"[match-host] match-flow: HalfTime intro C2={MatchC2States.RoundEnd} " +
            $"dur={dur.TotalSeconds:0}s after round={AlliesFlowParams.HalfTimeAfterRound} " +
            "(not round-end UI — mid-match swap)");
        PostServerDebugChat("перерыв · смена сторон");
    }

    /// <summary>
    /// Server-authoritative team flip + score perspective swap. Gold C2=112 bag:
    /// <c>swapped_team=true</c>, <c>CtScore</c>/<c>TrScore</c> flipped, CoLosses reset.
    /// Every fighting actor gets host <c>SetProperty team</c> (Tr↔Ct) — do not rely on client swap.
    /// </summary>
    private void EnterHalfTimeSwap(MatchRoom room)
    {
        ForceSwapAllFighterTeams(room);

        int scoreTr, scoreCt;
        lock (_roomGate)
        {
            var tr = room.Flow.ScoreTr;
            var ct = room.Flow.ScoreCt;
            room.Flow.ScoreTr = ct;
            room.Flow.ScoreCt = tr;
            room.Flow.CoLossesTr = 0;
            room.Flow.CoLossesCt = 0;
            room.Flow.TeamsSwapped = true;
            room.Flow.BomberActorNr = 0;
            scoreTr = room.Flow.ScoreTr;
            scoreCt = room.Flow.ScoreCt;
            room.Flow.Phase = MatchFlowPhase.HalfTimeSwap;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + AlliesFlowParams.HalfTimeSwapHold;
        }

        var nowSec = ServerTimeSeconds();
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.CtScore, LobbyVariant.FromInt(scoreCt)),
            (MatchRoomPropKeys.TrScore, LobbyVariant.FromInt(scoreTr)),
            (MatchRoomPropKeys.SwappedTeam, LobbyVariant.FromBool(true)),
            (MatchRoomPropKeys.CtCoLosses, LobbyVariant.FromInt(0)),
            (MatchRoomPropKeys.TrCoLosses, LobbyVariant.FromInt(0)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.HalfTimeSwap)),
        ], reason: "HalfTime swap C2=112");
        BroadcastMatchScoresToAllPeers(room, scoreTr, scoreCt);
        Console.WriteLine(
            $"[match-host] match-flow: HalfTime swap C2={MatchC2States.HalfTimeSwap} " +
            $"swapped_team=true TrScore={scoreTr} CtScore={scoreCt} CoLosses reset " +
            "(server-forced team flip on all fighters)");
    }

    private void EnterHalfTimeTransition(MatchRoom room)
    {
        var dur = AlliesFlowParams.HalfTimeTransition;
        var ends = DateTime.UtcNow + dur;
        lock (_roomGate)
        {
            room.Flow.Phase = MatchFlowPhase.HalfTimeTransition;
            room.Flow.PhaseEndsUtc = ends;
        }

        var nowSec = ServerTimeSeconds();
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.HalfTimeTransition)),
        ], reason: "HalfTime transition C2=113");
        Console.WriteLine(
            $"[match-host] match-flow: HalfTime transition C2={MatchC2States.HalfTimeTransition} " +
            $"dur={dur.TotalSeconds:0}s → Prep C2=22 round={AlliesFlowParams.HalfTimeAfterRound + 1}");
    }

    /// <summary>
    /// Host <c>SetProperty team</c> for every Tr/Ct fighter — opposite side. Also resets money
    /// to round-start (gold half-time money=800 on swap actors).
    /// </summary>
    private void ForceSwapAllFighterTeams(MatchRoom room)
    {
        List<(byte Actor, MatchTeam NewTeam)> swaps;
        lock (_roomGate)
        {
            swaps = new List<(byte, MatchTeam)>();
            foreach (var ((actor, key), value) in room.ActorProps)
            {
                if (key != MatchRoomPropKeys.Team || actor == MatchHostActor.ActorNr)
                    continue;
                if (value.Kind != LobbyVariantKind.Byte)
                    continue;
                var team = (MatchTeam)value.Byte;
                if (team == MatchTeam.Tr)
                    swaps.Add((actor, MatchTeam.Ct));
                else if (team == MatchTeam.Ct)
                    swaps.Add((actor, MatchTeam.Tr));
            }
        }

        var money = MatchFlowTestParams.RoundStartMoney;
        foreach (var (actor, newTeam) in swaps)
        {
            var teamVal = LobbyVariant.FromByte((byte)newTeam);
            lock (_roomGate)
                room.ActorProps[(actor, MatchRoomPropKeys.Team)] = teamVal;

            var teamPkt = MatchCodec.BuildSetProperty(
                NextServerTime(), actor, MatchRoomPropKeys.Team, teamVal);
            BroadcastRoom(room, teamPkt, tag: "match_tx_SetProperty_team_halfswap");

            var moneyVal = LobbyVariant.FromInt(money);
            lock (_roomGate)
                room.ActorProps[(actor, MatchRoomPropKeys.Money)] = moneyVal;
            var moneyPkt = MatchCodec.BuildSetProperty(
                NextServerTime(), actor, MatchRoomPropKeys.Money, moneyVal);
            BroadcastRoom(room, moneyPkt, tag: "match_tx_SetProperty");

            Console.WriteLine(
                $"[match-host] match-flow: half-time team flip actor={actor} → {newTeam} money={money}");
        }
    }

    private void BroadcastAlliesReCreateSceneManagers(MatchRoom room)
    {
        var stime = NextServerTime();
        foreach (var id in AlliesFlowParams.RecreateSceneManagerIds)
        {
            var pkt = MatchCodec.BuildReCreateSceneManager(stime, id);
            var n = BroadcastInitReady(room, pkt, tag: "match_tx_ReCreateSceneManager");
            Console.WriteLine(
                $"[match-host] match-flow: ReCreateSceneManager id={id} → peers={n}");
        }
    }
}
