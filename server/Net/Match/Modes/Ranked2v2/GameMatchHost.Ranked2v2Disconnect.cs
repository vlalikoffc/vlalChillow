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
    private void RemoveActorFromMatch(MatchRoom room, byte actorNr, string? userId, string reason)
    {
        if (actorNr == 0 || actorNr == MatchHostActor.ActorNr)
            return;

        List<short> pawnIds;
        lock (_roomGate)
        {
            if (userId is { } uid)
                room.UserIds.Remove(uid);
            room.Actors.RemoveAll(a => a.Nr == actorNr);
            room.SpawnedPawns.Remove(actorNr);
            room.Flow.DeadActors.Remove(actorNr);

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
