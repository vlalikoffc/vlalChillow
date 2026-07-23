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
    // Room prop TX helpers used by Ranked2v2 / Escalation / Allies bomb modes.
    private void BroadcastRoomProps(
        MatchRoom room,
        IReadOnlyList<(string Key, LobbyVariant Value)> props,
        string? reason = null,
        double? phaseDeadlineSec = null)
    {
        byte? c2 = null;
        lock (_roomGate)
        {
            foreach (var (k, v) in props)
            {
                if (k == MatchRoomPropKeys.C2 && v.Kind == LobbyVariantKind.Byte)
                {
                    room.RoomC2 = v.Byte;
                    c2 = v.Byte;
                }
                room.ActorProps[(0, k)] = v;
            }
        }
        var pkt = MatchCodec.BuildSetProperties(NextServerTime(), actorNr: 0, props);
        var n = BroadcastRoom(room, pkt, tag: "match_tx_SetProperties");
        var recipients = DescribeRoomPeerActors(room);
        var reasonTag = string.IsNullOrEmpty(reason) ? "" : $" reason={reason}";
        Console.WriteLine(
            $"[match-host] match-flow TX SetProperties actor=0 " +
            $"keys=[{string.Join(",", props.Select(p => $"{p.Key}={p.Value}"))}] " +
            $"→ peers={n} actors=[{recipients}]{reasonTag}");
        if (c2 is byte c2Val)
        {
            var deadlineTag = phaseDeadlineSec is double d
                ? $" deadline={d:0.###}"
                : "";
            Console.WriteLine(
                $"[match-host] match-flow TX C2={c2Val} reason={reason ?? "unspecified"}{deadlineTag}");
        }
    }

    /// <summary>
    /// Push current <c>TrScore</c>/<c>CtScore</c> to every connected match peer as single-key
    /// SetProperty (actor=0). RoundEnd gold bag only carries the winner’s score key; stay-in
    /// peers that miss or partial-apply that bag desync until rejoin snapshot. Late-join
    /// already gets both from <see cref="SendMatchStateSnapshotToPeer"/> — this mirrors that
    /// for in-match peers on every score change.
    /// </summary>
    private void BroadcastMatchScoresToAllPeers(MatchRoom room, int scoreTr, int scoreCt)
    {
        lock (_roomGate)
        {
            room.ActorProps[(0, MatchRoomPropKeys.TrScore)] = LobbyVariant.FromInt(scoreTr);
            room.ActorProps[(0, MatchRoomPropKeys.CtScore)] = LobbyVariant.FromInt(scoreCt);
        }

        var trPkt = MatchCodec.BuildSetProperty(
            NextServerTime(), actorNr: 0, MatchRoomPropKeys.TrScore, LobbyVariant.FromInt(scoreTr));
        var ctPkt = MatchCodec.BuildSetProperty(
            NextServerTime(), actorNr: 0, MatchRoomPropKeys.CtScore, LobbyVariant.FromInt(scoreCt));
        var nTr = BroadcastRoom(room, trPkt, tag: "match_tx_SetProperty");
        var nCt = BroadcastRoom(room, ctPkt, tag: "match_tx_SetProperty");
        var recipients = DescribeRoomPeerActors(room);
        Console.WriteLine(
            $"[match-host] match-flow TX score SetProperty actor=0 " +
            $"TrScore={scoreTr} → peers={nTr}; CtScore={scoreCt} → peers={nCt} " +
            $"actors=[{recipients}] (all stay-in + connected; not snapshot-only)");
    }

    /// <summary>Actor nrs currently attached to <paramref name="room"/> (for TX recipient logs).</summary>
    private string DescribeRoomPeerActors(MatchRoom room)
    {
        var parts = new List<string>();
        foreach (var (p, st) in _peers)
        {
            if (st.Room != room)
                continue;
            var name = string.IsNullOrEmpty(st.RosterName) ? "?" : st.RosterName;
            var init = st.BootstrapSent ? "init" : "pre-init";
            parts.Add($"{st.ActorNr}/{name}({init})");
        }
        return parts.Count == 0 ? "none" : string.Join(",", parts);
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
