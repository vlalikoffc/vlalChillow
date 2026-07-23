using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

/// <summary>
/// DeathMatch / TDM flow (C0=<c>DeathMatch</c>) — phone gold <c>/tmp/tdm-probe.log</c>:
/// WaitingPlayers(10) → [phone C2=11 PreWarmup skipped] → WarmUp(21, ~3s) →
/// Live(30, 5min; kills bump room TrScore/CtScore) → End(200) → FinalHud(201) →
/// Teardown(255). Single continuous round — NO C2=22 / prep / bomb / round-wipe
/// (Defuse / Ranked2v2 loop stays in <c>Modes/Ranked2v2/</c>).
/// </summary>
public sealed partial class GameMatchHost
{
    private const string DeathMatchModeId = "DeathMatch";

    /// <summary>True when this room's <c>C0</c> (or current lobby selection) is DeathMatch.</summary>
    private bool IsDeathMatchRoom(MatchRoom room)
    {
        lock (_roomGate)
        {
            if (room.ActorProps.TryGetValue((0, MatchRoomPropKeys.C0), out var v)
                && v.Kind == LobbyVariantKind.String
                && v.String is not null)
                return string.Equals(v.String, DeathMatchModeId, StringComparison.Ordinal);
            return string.Equals(_matchGameModeId, DeathMatchModeId, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// DeathMatch phase machine — branched away from <c>TickMatchFlowRoom</c> when C0=DeathMatch.
    /// </summary>
    private void TickDeathMatchFlowRoom(MatchRoom room)
    {
        MatchFlowPhase phase;
        DateTime ends;
        lock (_roomGate)
        {
            phase = room.Flow.Phase;
            ends = room.Flow.PhaseEndsUtc;

            if (phase == MatchFlowPhase.WaitingPlayers)
            {
                if (!BothFightingTeamsPresent(room))
                    return;
                if (!MatchHostSettings.MatchStartArmed)
                    return;
            }
            else if (phase == MatchFlowPhase.MatchOver)
                return;
            else if (DateTime.UtcNow < ends)
                return;
        }

        switch (phase)
        {
            case MatchFlowPhase.WaitingPlayers:
                EnterDeathMatchWarmup(room);
                break;
            case MatchFlowPhase.DeathMatchWarmup:
                EnterDeathMatchLive(room);
                break;
            case MatchFlowPhase.DeathMatchLive:
                EnterDeathMatchEnd(room, reason: "time");
                break;
            case MatchFlowPhase.DeathMatchEnded:
                EnterDeathMatchFinalHud(room);
                break;
            case MatchFlowPhase.DeathMatchFinalHud:
                EnterDeathMatchTeardown(room);
                break;
            // Ranked2v2 phases must never appear here (branch guards on C0) — ignore if they do.
        }
    }

    /// <summary>
    /// C2=21 WarmUp — DeathmatchController <c>_startingDuration</c>. Phone gold RX#2192
    /// {Time, C2=21} → Live ≈3.1s later (not 30s). Resets team room scores + death flags
    /// (per-player kills/assists/score resets are client-driven).
    /// </summary>
    private void EnterDeathMatchWarmup(MatchRoom room)
    {
        var nowSec = ServerTimeSeconds();
        var dur = DeathMatchFlowParams.Warmup;
        var deadline = nowSec + dur.TotalSeconds;
        lock (_roomGate)
        {
            room.Flow.Phase = MatchFlowPhase.DeathMatchWarmup;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + dur;
            room.Flow.RoundIndex = 0;
            room.Flow.ScoreTr = 0;
            room.Flow.ScoreCt = 0;
            room.Flow.BombPlanted = false;
            room.Flow.PendingEndReason = null;
            room.Flow.PendingWinner = MatchTeam.None;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
            room.LastCombatDamage.Clear();
            room.LastAuthoredDeath.Clear();
        }

        MatchHostSettings.MatchStartArmed = false;

        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmUp)),
        ]);
        ClearFighterDeathFlags(room);
        Console.WriteLine(
            $"[match-host] tdm-flow: WarmUp C2={MatchC2States.WarmUp} " +
            $"dur={dur.TotalSeconds:0}s src=_startingDuration/phone-gold≈3s " +
            "(skip C2=11 PreWarmup; no C2=22 on TDM; scores reset)");
        PostServerDebugChat("WarmUp");
    }

    /// <summary>
    /// C2=30 continuous TDM live — <c>_deathMatchDuration</c>, phone gold RX#2383→end ≈300s.
    /// Each combat death bumps the killer team's room <c>TrScore</c>/<c>CtScore</c>
    /// (see <see cref="NoteDeathMatchKill"/>). No round-end / wipe.
    /// </summary>
    private void EnterDeathMatchLive(MatchRoom room)
    {
        var nowSec = ServerTimeSeconds();
        var dur = DeathMatchFlowParams.MatchDuration;
        var deadline = nowSec + dur.TotalSeconds;
        lock (_roomGate)
        {
            room.Flow.Phase = MatchFlowPhase.DeathMatchLive;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + dur;
            room.Flow.ScoreTr = 0;
            room.Flow.ScoreCt = 0;
            room.Flow.PendingEndReason = null;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
            room.LastCombatDamage.Clear();
            room.LastAuthoredDeath.Clear();
        }

        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.DeathMatchLive)),
        ]);
        Console.WriteLine(
            $"[match-host] tdm-flow: Live C2={MatchC2States.DeathMatchLive} " +
            $"dur={dur.TotalMinutes:0}min src=_deathMatchDuration/phone-gold≈300s " +
            "(kills → room TrScore/CtScore; most kills wins at time)");
        PostServerDebugChat("Live · TDM");
    }

    /// <summary>
    /// A combat death during TDM live — bump the OPPOSING (killer) team's room score.
    /// Phone gold: Tr victim Destroy/death=1 → host <c>SetProperty actor=0 CtScore++</c>
    /// (RX#3787). Killer <c>kills</c>/<c>fair_kills</c>/<c>score</c> are client-owned props the
    /// host already relays; the host owns only the flat team room score. Never ends the match here.
    /// </summary>
    private void NoteDeathMatchKill(MatchRoom room, byte victimActorNr)
    {
        string scoreKey;
        int scoreVal;
        int scoreTr, scoreCt;
        MatchTeam victimTeam;
        lock (_roomGate)
        {
            if (room.Flow.Phase != MatchFlowPhase.DeathMatchLive)
                return;
            victimTeam = GetActorTeam(room, victimActorNr);
            if (victimTeam is not (MatchTeam.Tr or MatchTeam.Ct))
                return;

            if (victimTeam == MatchTeam.Tr)
            {
                room.Flow.ScoreCt++;
                scoreKey = MatchRoomPropKeys.CtScore;
                scoreVal = room.Flow.ScoreCt;
            }
            else
            {
                room.Flow.ScoreTr++;
                scoreKey = MatchRoomPropKeys.TrScore;
                scoreVal = room.Flow.ScoreTr;
            }
            room.ActorProps[(0, scoreKey)] = LobbyVariant.FromInt(scoreVal);
            scoreTr = room.Flow.ScoreTr;
            scoreCt = room.Flow.ScoreCt;
        }

        var pkt = MatchCodec.BuildSetProperty(
            NextServerTime(), actorNr: 0, scoreKey, LobbyVariant.FromInt(scoreVal));
        BroadcastRoom(room, pkt, tag: "match_tx_SetProperty");
        Console.WriteLine(
            $"[match-host] tdm-flow: kill victim={victimActorNr}/{victimTeam} → {scoreKey}={scoreVal} " +
            $"(TrScore={scoreTr} CtScore={scoreCt}; respawn awaits new CreateWorldObject)");
    }

    /// <summary>
    /// C2=200 end bag — phone gold RX#18692 {Time, FinalWinTeam{isDraw,isGiveUp,team},
    /// MvpPlayer, FinalPlayers, C2=200}. <c>FinalPlayers</c> (ByteArray) layout is undecoded so it
    /// is <b>omitted</b> (never faked — see README). Winner = team with more kills; draw if equal.
    /// </summary>
    private void EnterDeathMatchEnd(MatchRoom room, string reason)
    {
        int scoreTr, scoreCt;
        bool isDraw;
        byte winnerTeam;
        byte mvpNr;
        lock (_roomGate)
        {
            if (room.Flow.Phase is MatchFlowPhase.DeathMatchEnded
                or MatchFlowPhase.DeathMatchFinalHud
                or MatchFlowPhase.MatchOver)
                return;

            scoreTr = room.Flow.ScoreTr;
            scoreCt = room.Flow.ScoreCt;
            isDraw = scoreTr == scoreCt;
            winnerTeam = isDraw
                ? (byte)MatchTeam.None
                : scoreTr > scoreCt ? (byte)MatchTeam.Tr : (byte)MatchTeam.Ct;
            mvpNr = PickDeathMatchMvp(room);

            room.Flow.Phase = MatchFlowPhase.DeathMatchEnded;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + DeathMatchFlowParams.EndBagHold;
        }

        var nowSec = ServerTimeSeconds();
        var hold = DeathMatchFlowParams.EndBagHold;
        var finalWinTeam = new List<(string Key, LobbyVariant Value)>
        {
            (MatchFinalWinTeamKeys.IsDraw, LobbyVariant.FromBool(isDraw)),
            (MatchFinalWinTeamKeys.IsGiveUp, LobbyVariant.FromBool(false)),
            (MatchFinalWinTeamKeys.Team, LobbyVariant.FromByte(winnerTeam)),
        };
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.FinalWinTeam, LobbyVariant.FromProps(finalWinTeam)),
            (MatchRoomPropKeys.MvpPlayer, LobbyVariant.FromByte(mvpNr)),
            // FinalPlayers intentionally omitted — ByteArray inner layout not reversed yet.
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.DeathMatchEnded)),
        ]);
        Console.WriteLine(
            $"[match-host] tdm-flow: End C2={MatchC2States.DeathMatchEnded} reason={reason} " +
            $"TrScore={scoreTr} CtScore={scoreCt} isDraw={isDraw} winnerTeam={winnerTeam} " +
            $"mvpPlayer={mvpNr} hold={hold.TotalSeconds:0}s src=phone-gold RX#18692→#18879 " +
            "(FinalPlayers omitted — layout unknown)");

        var winner = (MatchTeam)winnerTeam;
        var winSide = isDraw ? "DRAW" : winner == MatchTeam.Tr ? "ATTACK (T)" : "DEFENSE (CT)";
        var mvpName = mvpNr != 0 ? ResolveActorName(mvpNr) : null;
        Dashboard.DashboardHub.PostRoundResult(
            $"MATCH OVER: {winSide}  ·  T {scoreTr} : {scoreCt} CT",
            winner, mvpNr, mvpName, isFinal: true);
    }

    /// <summary>C2=201 FinalHud — phone gold RX#18879 {Time, C2=201}.</summary>
    private void EnterDeathMatchFinalHud(MatchRoom room)
    {
        var pause = DeathMatchFlowParams.FinalHudPause;
        lock (_roomGate)
        {
            room.Flow.Phase = MatchFlowPhase.DeathMatchFinalHud;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + pause;
        }

        var nowSec = ServerTimeSeconds();
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.FinalHud)),
        ]);
        Console.WriteLine(
            $"[match-host] tdm-flow: FinalHud C2={MatchC2States.FinalHud} " +
            $"dur={pause.TotalSeconds:0}s src=phone-gold RX#18879→#18889 (match WIN/LOSE)");
    }

    /// <summary>C2=255 teardown — phone gold RX#18889 {C2=255} then disconnect. Terminal.</summary>
    private void EnterDeathMatchTeardown(MatchRoom room)
    {
        lock (_roomGate)
        {
            room.Flow.Phase = MatchFlowPhase.MatchOver;
            room.Flow.PhaseEndsUtc = DateTime.MaxValue;
        }

        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.MatchTeardown)),
        ]);
        Console.WriteLine(
            $"[match-host] tdm-flow: Teardown C2={MatchC2States.MatchTeardown} (match over)");
    }

    /// <summary>
    /// TDM MVP = actor with the most <c>kills</c> across both teams (client-owned counter;
    /// fallback <c>fair_kills</c> then <c>score</c>). Tie → lowest actor nr. 0 when no fighters.
    /// </summary>
    private static byte PickDeathMatchMvp(MatchRoom room)
    {
        byte best = 0;
        var bestKills = -1;
        foreach (var ((actor, key), value) in room.ActorProps)
        {
            if (key != MatchRoomPropKeys.Team || actor == MatchHostActor.ActorNr)
                continue;
            if (value.Kind != LobbyVariantKind.Byte)
                continue;
            if ((MatchTeam)value.Byte is not (MatchTeam.Tr or MatchTeam.Ct))
                continue;

            var kills = ReadActorIntProp(room, actor, MatchRoomPropKeys.Kills);
            if (kills < 0)
                kills = ReadActorIntProp(room, actor, MatchRoomPropKeys.FairKills);
            if (kills < 0)
                kills = ReadActorIntProp(room, actor, MatchRoomPropKeys.Score2);
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
}
