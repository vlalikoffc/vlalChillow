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
    private void HandleHandshake(NetPeer peer, LobbyReader body, byte[] raw)
    {
        DumpCapture("match_rx_HandshakeRequest", raw);
        var req = MatchCodec.ParseHandshakeRequestBody(body);
        Console.WriteLine(
            $"[match-host] HandshakeRequest appId='{req.AppId}' userId='{req.UserId}' proto={req.ProtocolVersion}");

        if (!_peers.TryGetValue(peer, out var st))
            st = _peers[peer] = new MatchPeerState();

        HandshakeResult result;
        // Live phone sends com.Chillow.StandChillow; rejecting it → InvalidAppId →
        // HandshakeFailedException ("Connection to matchmaking server failed").
        if (!MatchAuth.IsAcceptedAppId(req.AppId))
            result = HandshakeResult.InvalidAppId;
        else if (string.IsNullOrWhiteSpace(req.UserId))
            result = HandshakeResult.InvalidUserId;
        else if (req.ProtocolVersion != MatchAuth.ProtocolVersion)
            result = HandshakeResult.InvalidProtocolVersion;
        else if (st.Handshaken)
            result = HandshakeResult.AlreadyAuthenticated;
        else
            result = HandshakeResult.Success;

        if (result == HandshakeResult.Success)
        {
            st.Handshaken = true;
            st.UserId = req.UserId;
            st.AppId = req.AppId;
        }

        // Live Success (phone host RX): 04 01 <i32 serverTime> 00 01
        // = HasServerTime + HandshakeResponse + fzi=Success + bool=true (fyj.SendResponse).
        var okFlag = result == HandshakeResult.Success;
        var resp = MatchCodec.BuildHandshakeResponse(result, okFlag, NextServerTime());
        Send(peer, resp);
        DumpCapture("match_tx_HandshakeResponse", resp);
        Console.WriteLine(
            $"[match-host] TX HandshakeResponse result={result} ({(byte)result}) okFlag={okFlag} " +
            $"len={resp.Length} hex={Convert.ToHexString(resp)}");
    }

    private void HandleJoinRoom(NetPeer peer, LobbyReader body, byte[] raw)
    {
        DumpCapture("match_rx_JoinRoomRequest", raw);
        if (!_peers.TryGetValue(peer, out var st) || !st.Handshaken)
        {
            Console.WriteLine("[match-host] JoinRoom before handshake — ignore");
            return;
        }

        var req = MatchCodec.ParseJoinRoomRequestBody(body);
        // Dict lookup = password only (fyi.boeh). Live password='Dedik' (chillow joke).
        // room (cwgt) = participant roster string — who should load into катка.
        // Live 'влал' alone is consistent with per-client lobby illusion (Server+self).
        Console.WriteLine(
            $"[match-host] JoinRoomRequest room={MatchRoomField.Describe(req.Room)} " +
            $"mode={req.Mode} " +
            $"password={(req.Password.Length == 0 ? "(empty)" : $"'{req.Password}'")} " +
            $"hasCreateOptions={req.HasCreateOptions} " +
            $"(lookupKey=password; expect '{LanCreateRoomPasswordKey}')");

        // CreateOnly / JoinOrCreate may register under the password they send.
        // JoinOnly: ensure Dedik LAN room (Play may race slightly before EnsureLanRoom).
        if (req.Mode is JoinRoomMode.CreateOnly or JoinRoomMode.JoinOrCreate)
            EnsureRoom(req.Password, string.IsNullOrEmpty(req.Room) ? _lobbyId : req.Room);
        else
            EnsureLanRoom();

        MatchRoom? room;
        lock (_roomGate)
            _roomsByPassword.TryGetValue(req.Password, out room);

        JoinRoomResult result;
        byte? actor = null;

        if (room is null)
        {
            // JoinOnly miss → RoomNotFound (fyk SendJoinRoomResponse result=7).
            var known = string.Join(", ",
                _roomsByPassword.Keys.Select(k => k.Length == 0 ? "\"\"" : $"'{k}'"));
            Console.WriteLine(
                $"[match-host] JoinRoom dict miss password='{req.Password}' " +
                $"(registered keys: [{known}]) — RoomNotFound");
            result = JoinRoomResult.RoomNotFound;
        }
        else if (st.UserId is { } uid && room.UserIds.Contains(uid))
        {
            result = JoinRoomResult.ActorWithSameUserIdExists;
        }
        else
        {
            // Accept JoinOnly when password hits Dedik. Do not gate on room==LobbyId —
            // room is roster semantics, not the dict key. Multi-name encoding TBD;
            // when lobby illusion is dropped, expect fuller roster strings here.
            byte nr;
            lock (_roomGate)
            {
                nr = (byte)Math.Clamp(room.NextActorNr++, 1, 255);
                if (st.UserId is { } u)
                    room.UserIds.Add(u);
                var nick = string.IsNullOrWhiteSpace(req.Room) ? $"actor{nr}" : req.Room;
                room.Actors.Add((nr, nick));
                st.RosterName = nick;
            }
            st.Room = room;
            st.ActorNr = nr;
            actor = nr;
            result = JoinRoomResult.Found;
            Console.WriteLine(
                $"[match-host] JoinRoom Found passwordKey='{room.PasswordKey}' " +
                $"roster={MatchRoomField.Describe(req.Room)} actorNr={nr} " +
                $"(host={MatchHostActor.ActorNr}/'{MatchHostActor.Name}' Spectator) " +
                $"userId='{st.UserId}'");
        }

        MatchGapRoom? gap = null;
        if (result == JoinRoomResult.Found && room is not null && actor is { } assigned)
        {
            List<(byte Nr, string Name)> actorsSnap;
            byte roomC2;
            lock (_roomGate)
            {
                actorsSnap = room.Actors.ToList();
                roomC2 = room.RoomC2;
            }

            // Phone-host probe (20260722_003523_* / run-20260722_073504): thin Found ~148B —
            // room props + actor names only (empty gak props). Identity/avatar come post-Found.
            // Match channel = shared truth (all actors). Lobby stays Server+self illusion for N>4.
            // MaxActorsHint ≥ live roster so Found never advertises fewer slots than actors present.
            var maxHint = (byte)Math.Clamp(
                Math.Max(MatchGapDefaults.MaxActorsHint, actorsSnap.Count), 1, 255);
            gap = new MatchGapRoom
            {
                RoomName = LanCreateRoomPasswordKey,
                Open = true,
                MaxActorsHint = maxHint,
                RoomProps = MatchGapDefaults.BuildRoomProps(
                    LobbyPropKeys.DefaultGameModeId,
                    LobbyPropKeys.DefaultSelectedLevel,
                    c2: roomC2),
                Actors = actorsSnap.Select(a => new MatchGapActor
                {
                    ActorNr = a.Nr,
                    Name = a.Name,
                    Props = null,
                    Flag = false,
                }).ToList(),
                TrailingByte = MatchGapDefaults.TrailingByte,
            };
        }

        var resp = MatchCodec.BuildJoinRoomResponse(
            result,
            NextServerTime(),
            actor,
            debugMessage: result == JoinRoomResult.Found ? LanCreateRoomPasswordKey : null,
            gap: gap);
        Send(peer, resp);
        DumpCapture("match_tx_JoinRoomResponse", resp);
        Console.WriteLine(
            $"[match-host] TX JoinRoomResponse result={MatchCodec.JoinRoomResultName(result)} " +
            $"actor={(actor?.ToString() ?? "-")} gap={(gap is null ? "none" : $"props={gap.Value.RoomProps.Count} actors={gap.Value.Actors.Count} C2={room?.RoomC2}")} " +
            $"len={resp.Length}");

        if (result == JoinRoomResult.Found && room is not null && actor is { } joinerNr)
        {
            // Dynamic roster: peers who already Found only knew actors-at-entry.
            // Push ActorJoinedEvent (fup) so early humans learn about this joiner.
            NotifyPeersActorJoined(room, peer, joinerNr, st.RosterName ?? $"actor{joinerNr}");
            BeginAwaitJoinerThenBootstrap(peer, room, joinerNr);
        }
    }

    /// <summary>
    /// Tell every other match peer about a newly Found human actor (<c>fuo=50</c>).
    /// Joiner already has the full roster in their own Found gap — exclude them.
}
