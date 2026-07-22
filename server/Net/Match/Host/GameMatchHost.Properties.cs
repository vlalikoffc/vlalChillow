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
    private void HandleSetProperty(NetPeer peer, MatchFrameFlags flags, byte[] raw)
    {
        DumpCapture("match_rx_SetProperty", raw);
        if ((flags & MatchFrameFlags.IsEncrypted) != 0)
        {
            Console.WriteLine($"[match-host] SetProperty flags={flags} encrypted — dump only");
            return;
        }

        try
        {
            if (!MatchCodec.TryOpenBody(raw, out _, out _, out _, out var bodyBytes))
            {
                Console.WriteLine(
                    $"[match-host] SetProperty flags={flags} — could not open body (len={raw.Length})");
                return;
            }

            var body = new LobbyReader(bodyBytes);
            var (actorNr, key, value) = MatchCodec.ParseSetPropertyBody(body);
            var compressed = (flags & MatchFrameFlags.IsCompressed) != 0;

            if (!_peers.TryGetValue(peer, out var st) || st.Room is null)
                return;
            var room = st.Room;

            // Ping: do not echo client estimate (often ≈0/1/2). Phone host path measures
            // LiteNetLib latency (grp.OnNetworkLatencyUpdate); we write RoundTripTime ms.
            var pingFromRtt = false;
            if (key == MatchRoomPropKeys.Ping)
            {
                var rtt = Math.Max(0, peer.RoundTripTime);
                value = LobbyVariant.FromInt(rtt);
                pingFromRtt = true;
            }

            Console.WriteLine(
                $"[match-host] SetProperty actor={actorNr} key='{key}' value={value}" +
                (compressed ? " (LZ4→echo uncompressed HasServerTime)" : "") +
                (pingFromRtt ? $" (server RTT={peer.RoundTripTime}ms, not client echo)" : ""));

            lock (_roomGate)
                room.ActorProps[(actorNr, key)] = value;

            // Rebroadcast uncompressed with HasServerTime (phone-host avatar path).
            var outPkt = MatchCodec.BuildSetProperty(NextServerTime(), actorNr, key, value);
            BroadcastRoom(room, outPkt, tag: "match_tx_SetProperty");

            // Joiner-ready gate: identity batch before host managers+C2.
            NoteJoinerIdentityProp(peer, actorNr, key);

            // Fighting pawn: phone joiner allocates CreateWorldObject (often id≠129) after
            // team pick — do not host-spawn id=129 here (that capture was host-self).
            if (key == MatchRoomPropKeys.Team
                && value.Kind == LobbyVariantKind.Byte
                && actorNr != MatchHostActor.ActorNr)
            {
                var team = (MatchTeam)value.Byte;
                if (team is MatchTeam.Tr or MatchTeam.Ct)
                {
                    Console.WriteLine(
                        $"[match-host] team={team} actor={actorNr} — await joiner CreateWorldObject " +
                        $"(echo+rpc1+state; wire name Tr_Tr/Ct_Ct + fusTag=101 looks like *e in ASCII)");
                    TryBeginMatchFlowIfBothTeams(room);
                    TryAssignBomberOnTrJoin(room, actorNr);
                }
            }

            if (key == MatchRoomPropKeys.Death)
                NoteActorDeath(room, actorNr, value, source: "SetProperty");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-host] SetProperty parse failed: {FormatException(ex)}");
        }
    }

    private void HandleSetProperties(NetPeer peer, MatchFrameFlags flags, byte[] raw)
    {
        DumpCapture("match_rx_SetProperties", raw);
        if ((flags & MatchFrameFlags.IsEncrypted) != 0)
        {
            Console.WriteLine($"[match-host] SetProperties flags={flags} encrypted — dump only");
            return;
        }

        try
        {
            if (!MatchCodec.TryOpenBody(raw, out _, out _, out _, out var bodyBytes))
            {
                Console.WriteLine(
                    $"[match-host] SetProperties flags={flags} — could not open body (len={raw.Length})");
                return;
            }

            var body = new LobbyReader(bodyBytes);
            var (actorNr, props) = MatchCodec.ParseSetPropertiesBody(body);
            Console.WriteLine(
                $"[match-host] SetProperties actor={actorNr} count={props.Count} " +
                $"keys=[{string.Join(",", props.Select(p => p.Key))}]");

            if (!_peers.TryGetValue(peer, out var st) || st.Room is null)
                return;
            var room = st.Room;
            lock (_roomGate)
            {
                foreach (var (k, v) in props)
                {
                    if (v.Kind == LobbyVariantKind.Null)
                        room.ActorProps.Remove((actorNr, k));
                    else
                        room.ActorProps[(actorNr, k)] = v;
                    if (actorNr == 0 && k == MatchRoomPropKeys.C2 && v.Kind == LobbyVariantKind.Byte)
                        room.RoomC2 = v.Byte;
                }
            }

            var outPkt = MatchCodec.BuildSetProperties(NextServerTime(), actorNr, props);
            BroadcastRoom(room, outPkt, tag: "match_tx_SetProperties");

            foreach (var (k, v) in props)
            {
                if (k == MatchRoomPropKeys.Team
                    && v.Kind == LobbyVariantKind.Byte
                    && actorNr != MatchHostActor.ActorNr
                    && (MatchTeam)v.Byte is MatchTeam.Tr or MatchTeam.Ct)
                {
                    Console.WriteLine(
                        $"[match-host] team bag actor={actorNr} — await joiner CreateWorldObject");
                    TryBeginMatchFlowIfBothTeams(room);
                    TryAssignBomberOnTrJoin(room, actorNr);
                }

                if (k == MatchRoomPropKeys.Death)
                    NoteActorDeath(room, actorNr, v, source: "SetProperties");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[match-host] SetProperties parse failed: {ex.Message}");
        }
    }
}
