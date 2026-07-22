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
    // Room prop TX helpers used by Ranked2v2 flow.
    private void BroadcastRoomProps(
        MatchRoom room,
        IReadOnlyList<(string Key, LobbyVariant Value)> props)
    {
        lock (_roomGate)
        {
            foreach (var (k, v) in props)
            {
                if (k == MatchRoomPropKeys.C2 && v.Kind == LobbyVariantKind.Byte)
                    room.RoomC2 = v.Byte;
                room.ActorProps[(0, k)] = v;
            }
        }
        var pkt = MatchCodec.BuildSetProperties(NextServerTime(), actorNr: 0, props);
        BroadcastRoom(room, pkt, tag: "match_tx_SetProperties");
        Console.WriteLine(
            $"[match-host] match-flow TX SetProperties actor=0 " +
            $"keys=[{string.Join(",", props.Select(p => $"{p.Key}={p.Value}"))}]");
    }

    private void SetAllFightersMoney(MatchRoom room, int money)
    {
        List<byte> actors;
        lock (_roomGate)
        {
            actors = room.Actors
                .Select(a => a.Nr)
                .Where(nr => nr != MatchHostActor.ActorNr)
                .ToList();
            foreach (var nr in actors)
                room.ActorProps[(nr, MatchRoomPropKeys.Money)] = LobbyVariant.FromInt(money);
        }
        foreach (var nr in actors)
        {
            var pkt = MatchCodec.BuildSetProperty(
                NextServerTime(), nr, MatchRoomPropKeys.Money, LobbyVariant.FromInt(money));
            BroadcastRoom(room, pkt, tag: "match_tx_SetProperty");
        }
        Console.WriteLine(
            $"[match-host] match-flow: money={money} → actors=[{string.Join(",", actors)}]");
    }

}
