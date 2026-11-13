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
    // Actor remove + mid-round wipe on disconnect.
    private void RemoveActorFromMatch(
        MatchRoom room, byte actorNr, string? userId, string? identityKey, string reason)
    {
        if (actorNr == 0 || actorNr == MatchHostActor.ActorNr)
            return;

        string? leftName = null;
        // Remember fighting team for mid-match reconnect restore (new actorNr after rejoin).
        MatchTeam remembered = MatchTeam.None;
        List<short> pawnIds;
        lock (_roomGate)
        {
            leftName = room.Actors.FirstOrDefault(a => a.Nr == actorNr).Name;
            if (string.IsNullOrEmpty(leftName))
                leftName = null;
            if (room.ActorProps.TryGetValue((actorNr, MatchRoomPropKeys.Team), out var teamVar)
                && teamVar.Kind == LobbyVariantKind.Byte)
            {
                remembered = (MatchTeam)teamVar.Byte;
            }

            if (userId is { } uid)
                room.UserIds.Remove(uid);
            room.Actors.RemoveAll(a => a.Nr == actorNr);
            room.SpawnedPawns.Remove(actorNr);
            room.Flow.DeadActors.Remove(actorNr);
            // #33: a departed fighter must also leave the frozen round roster, otherwise the
            // remaining side is never «wiped» and the round falls through to the 109s timeout
            // instead of ending on the kill that decided it (log: round 3, timeout after a leave).
            MatchFlowRules.RemoveFromRoundRoster(room.Flow, actorNr);

            var propKeys = room.ActorProps.Keys.Where(k => k.Actor == actorNr).ToList();
            foreach (var k in propKeys)
                room.ActorProps.Remove(k);

            pawnIds = room.LivingPawns
                .Where(kv => kv.Value.OwnerActorNr == actorNr)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in pawnIds)
                room.LivingPawns.Remove(id);
        }

        if (!string.IsNullOrEmpty(leftName))
        {
            try { OnMatchPlayerLeft?.Invoke(leftName); }
            catch (Exception ex)
            {
                Console.WriteLine($"[match-host] OnMatchPlayerLeft: {ex.Message}");
            }
        }

        // Issue #39: remember the team under the session-independent identity key — the
        // returning phone arrives with a NEW auth token, so the old uid would never match.
        var memoryKey = string.IsNullOrEmpty(identityKey) ? userId : identityKey;
        if (memoryKey is { } reconnectKey
            && remembered is MatchTeam.Tr or MatchTeam.Ct)
        {
            lock (_roomGate)
                _reconnectTeams[reconnectKey] = remembered;
            Console.WriteLine(
                $"[match-host] reconnect-remember: identity='{reconnectKey}' team={remembered} " +
                $"(actor={actorNr} left reason={reason}, wire uid='{userId}')");
        }

        foreach (var id in pawnIds)
        {
            var destroy = MatchCodec.BuildDestroyWorldObject(NextServerTime(), id);
            var nD = BroadcastInitReady(room, destroy, tag: "match_tx_DestroyWorldObject");
            Console.WriteLine(
                $"[match-host] TX DestroyWorldObject id={id} → peers={nD} " +
                $"(disconnect actor={actorNr})");
        }

        var left = MatchCodec.BuildActorLeftEvent(NextServerTime(), actorNr);
        var n = BroadcastRoom(room, left, tag: "match_tx_ActorLeftEvent");
        Console.WriteLine(
            $"[match-host] TX ActorLeftEvent actor={actorNr} → peers={n} " +
            $"(full remove reason={reason})");

        // Mid-round leave: remaining team may win — same wipe rules, proper RoundEnd path.
        bool bombPlanted;
        MatchFlowPhase phase;
        lock (_roomGate)
        {
            phase = room.Flow.Phase;
            bombPlanted = room.Flow.BombPlanted;
        }
        if (MatchFlowRules.AllowsWipeCheck(phase))
        {
            Console.WriteLine(
                $"[match-host] match-flow: disconnect actor={actorNr} mid-{phase} — wipe check");
            TryResolveWipeImmediate(room, bombPlanted);
        }
    }

}
