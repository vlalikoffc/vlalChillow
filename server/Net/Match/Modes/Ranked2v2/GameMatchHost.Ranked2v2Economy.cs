using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Match money grants — round-end / plant / defuse / kill. Additive, capped at
/// <see cref="MatchEconomy.MaxMoney"/>. Allies + Ranked bomb modes only (not TDM).
/// </summary>
public sealed partial class GameMatchHost
{
    private void GrantActorMoney(MatchRoom room, byte actorNr, int delta, string reason)
    {
        if (actorNr == 0 || actorNr == MatchHostActor.ActorNr || delta == 0)
            return;

        int before, after;
        lock (_roomGate)
        {
            before = ReadActorIntProp(room, actorNr, MatchRoomPropKeys.Money);
            if (before < 0)
                before = 0;
            after = MatchEconomy.Clamp(before + delta);
            room.ActorProps[(actorNr, MatchRoomPropKeys.Money)] = LobbyVariant.FromInt(after);
        }

        var pkt = MatchCodec.BuildSetProperty(
            NextServerTime(), actorNr, MatchRoomPropKeys.Money, LobbyVariant.FromInt(after));
        BroadcastRoom(room, pkt, tag: "match_tx_SetProperty");
        Console.WriteLine(
            $"[match-host] economy: money actor={actorNr} {before}→{after} " +
            $"(Δ={delta:+0;-#} reason={reason} cap={MatchEconomy.MaxMoney})");
    }

    private void GrantTeamMoney(MatchRoom room, MatchTeam team, int amount, string reason)
    {
        if (amount <= 0 || team is not (MatchTeam.Tr or MatchTeam.Ct))
            return;
        List<byte> actors;
        lock (_roomGate)
        {
            actors = room.Actors
                .Select(a => a.Nr)
                .Where(nr => nr != MatchHostActor.ActorNr && GetActorTeam(room, nr) == team)
                .ToList();
        }
        foreach (var nr in actors)
            GrantActorMoney(room, nr, amount, reason);
    }

    /// <summary>
    /// Round-end payouts after CoLosses/score bump. Objective (explode/defuse) → $3200 win;
    /// else $3000. Losers get loss-streak table. Timeout + alive T → $0 (no loss bonus).
    /// </summary>
    private void ApplyRoundEndEconomy(MatchRoom room, MatchTeam winner, string reason)
    {
        if (IsDeathMatchRoom(room) || IsEscalationRoom(room) || IsDuelRoom(room))
            return;

        int coLossesTr, coLossesCt;
        HashSet<byte> dead;
        lock (_roomGate)
        {
            coLossesTr = room.Flow.CoLossesTr;
            coLossesCt = room.Flow.CoLossesCt;
            dead = new HashSet<byte>(room.Flow.DeadActors);
            foreach (var a in room.Actors)
            {
                if (a.Nr == MatchHostActor.ActorNr)
                    continue;
                if (MatchFlowRules.IsActorDead(room.Flow, room.ActorProps, a.Nr))
                    dead.Add(a.Nr);
            }
        }

        var objective = reason is "bomb-explode" or "bomb-explode-rpc" or "bomb-defuse";
        var winAmount = objective ? MatchEconomy.ObjectiveWin : MatchEconomy.TeamWin;
        var loseTeam = winner == MatchTeam.Tr ? MatchTeam.Ct : MatchTeam.Tr;
        var loseStreak = loseTeam == MatchTeam.Tr ? coLossesTr : coLossesCt;
        var lossAmount = MatchEconomy.LossPayout(loseStreak);

        GrantTeamMoney(room, winner, winAmount,
            objective ? $"win-objective/{reason}" : $"win-team/{reason}");

        // Losing team — per-actor so timeout survivor T can be zeroed.
        List<byte> losers;
        lock (_roomGate)
        {
            losers = room.Actors
                .Select(a => a.Nr)
                .Where(nr => nr != MatchHostActor.ActorNr && GetActorTeam(room, nr) == loseTeam)
                .ToList();
        }

        var timeoutCtWin = reason == "timeout" && winner == MatchTeam.Ct;
        foreach (var nr in losers)
        {
            if (timeoutCtWin && loseTeam == MatchTeam.Tr && !dead.Contains(nr))
            {
                Console.WriteLine(
                    $"[match-host] economy: timeout survivor T actor={nr} → $0 (no loss bonus)");
                continue;
            }
            GrantActorMoney(room, nr, lossAmount, $"loss-streak={loseStreak}/{reason}");
        }
    }

    /// <summary>
    /// Plant bonus at plant accept: whole T +$300; planter gets +$600 instead of +$300.
    /// </summary>
    private void ApplyPlantEconomy(MatchRoom room, byte planterActorNr)
    {
        if (IsDeathMatchRoom(room) || IsEscalationRoom(room) || IsDuelRoom(room))
            return;

        List<byte> terrorists;
        lock (_roomGate)
        {
            terrorists = room.Actors
                .Select(a => a.Nr)
                .Where(nr => nr != MatchHostActor.ActorNr && GetActorTeam(room, nr) == MatchTeam.Tr)
                .ToList();
        }

        foreach (var nr in terrorists)
        {
            var amount = nr == planterActorNr ? MatchEconomy.PlantPlanter : MatchEconomy.PlantTeam;
            GrantActorMoney(room, nr, amount,
                nr == planterActorNr ? "plant-planter" : "plant-team");
        }
    }

    private void ApplyDefuseEconomy(MatchRoom room, byte defuserActorNr)
    {
        if (IsDeathMatchRoom(room) || IsEscalationRoom(room) || IsDuelRoom(room))
            return;
        if (defuserActorNr == 0 || defuserActorNr == MatchHostActor.ActorNr)
            return;
        if (GetActorTeam(room, defuserActorNr) != MatchTeam.Ct)
            return;
        GrantActorMoney(room, defuserActorNr, MatchEconomy.DefuseActor, "defuse");
    }

    private void ApplyKillEconomy(MatchRoom room, byte killerActorNr, byte weaponId)
    {
        if (IsDeathMatchRoom(room) || IsEscalationRoom(room) || IsDuelRoom(room))
            return;
        if (killerActorNr == 0 || killerActorNr == MatchHostActor.ActorNr)
            return;
        var team = GetActorTeam(room, killerActorNr);
        if (team is not (MatchTeam.Tr or MatchTeam.Ct))
            return;
        var amount = MatchEconomy.KillRewardForWeapon(weaponId);
        GrantActorMoney(room, killerActorNr, amount,
            weaponId == 0 ? "kill-default" : $"kill-weapon={weaponId}");
    }
}
