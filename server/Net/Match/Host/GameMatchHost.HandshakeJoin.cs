using System.Buffers.Binary;
using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Diagnostics;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

public sealed partial class GameMatchHost
{
    private void HandleHandshake(NetPeer peer, LobbyReader body, byte[] raw)
    {
        if (Diag.LogVerbose)
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
            st.IdentityKey = MatchIdentity.StableKeyFromUserId(req.UserId);
            st.AppId = req.AppId;
        }

        // Live Success (phone host RX): 04 01 <i32 serverTime> 00 01
        // = HasServerTime + HandshakeResponse + fzi=Success + bool=true (fyj.SendResponse).
        var okFlag = result == HandshakeResult.Success;
        var resp = MatchCodec.BuildHandshakeResponse(result, okFlag, NextServerTime());
        Send(peer, resp);
        if (Diag.LogVerbose)
            DumpCapture("match_tx_HandshakeResponse", resp);
        Console.WriteLine(
            $"[match-host] TX HandshakeResponse result={result} ({(byte)result}) okFlag={okFlag} " +
            $"len={resp.Length} hex={Convert.ToHexString(resp)}");
    }

    private void HandleHandshake207(NetPeer peer, LobbyReader body, byte[] raw)
    {
        if (Diag.LogVerbose)
            DumpCapture("match_rx_Handshake207Request", raw);
        var req = MatchCodec.ParseHandshake207RequestBody(body);
        Console.WriteLine(
            $"[match-host] Handshake207Request appId='{req.AppId}' userId='{req.UserId}' " +
            $"proto={req.ProtocolVersion} authTokenLen={req.AuthToken.Length} " +
            $"remaining={body.Remaining}");

        if (!_peers.TryGetValue(peer, out var st))
            st = _peers[peer] = new MatchPeerState();

        HandshakeResult result;
        if (!MatchAuth.IsAcceptedAppId(req.AppId))
            result = HandshakeResult.InvalidAppId;
        else if (string.IsNullOrWhiteSpace(req.AuthToken))
            result = HandshakeResult.InvalidUserId;
        else if (req.ProtocolVersion != MatchAuth.ProtocolVersion207)
            result = HandshakeResult.InvalidProtocolVersion;
        else if (st.Handshaken)
            result = HandshakeResult.AlreadyAuthenticated;
        else
            result = HandshakeResult.Success;

        if (result == HandshakeResult.Success)
        {
            st.Handshaken = true;
            // 2.07 actor snapshots carry the auth token without its leading C
            // (CLB… in handshake → LB… in gap), matching the live phone host.
            // 2.07 gap identity omits the handshake token's leading C (CLB… → LB…).
            st.UserId = req.AuthToken.Length > 0 ? req.AuthToken[1..] : req.AuthToken;
            // Session-independent key: CLB<md5(userId)>_<guid> → LB<md5(userId)>.
            st.IdentityKey = MatchIdentity.StableKeyFrom207Token(req.AuthToken);
            st.AppId = req.AppId;
        }

        var resp = MatchCodec.BuildHandshake207Response(result, NextServerTime());
        Send(peer, resp);
        if (Diag.LogVerbose)
            DumpCapture("match_tx_Handshake207Response", resp);
        Console.WriteLine(
            $"[match-host] TX Handshake207Response result={result} ({(byte)result}) " +
            $"len={resp.Length} hex={Convert.ToHexString(resp)}");
    }

    private void HandleJoinRoom(NetPeer peer, LobbyReader body, byte[] raw)
    {
        if (Diag.LogVerbose)
            DumpCapture("match_rx_JoinRoomRequest", raw);
        if (!_peers.TryGetValue(peer, out var st) || !st.Handshaken)
        {
            Console.WriteLine("[match-host] JoinRoom before handshake — ignore");
            return;
        }

        var req = MatchCodec.ParseJoinRoomRequestBody(body);
        // Dict lookup = password only (fyi.boeh). Live password='Dedik' (chillow joke).
        // room (cwgt) = participant roster string — who should load into катка.
        // Live phone often sends self nick; dedicated may see fuller roster strings.
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
        // Issue #39: the same install returned with a fresh session token and took over its
        // old actor. Peers never saw it leave, so no ActorJoined/ActorLeft may be sent.
        var adoptedReconnect = false;

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
        else if (st.IdentityKey is { Length: > 0 } identityKey
                 && TryFindRejoinActor(room, identityKey, out var rejoinActor, out var rejoinState)
                 && rejoinState is not null)
        {
            // Issue #39: same player, new session token, old socket not timed out yet.
            // Restore the SAME actor instead of allocating a second one (which made the
            // client throw inside OnJoinRoomResponse and hang).
            AdoptReconnectingActor(room, st, rejoinActor, rejoinState);
            actor = rejoinActor;
            result = JoinRoomResult.Found;
            adoptedReconnect = true;
            Console.WriteLine(
                $"[match-host] JoinRoom Found (reconnect) identity='{identityKey}' actorNr={rejoinActor} " +
                $"roster='{st.RosterName}' — no second actor, no duplicate uid");
        }
        else if (st.UserId is { } uid && room.UserIds.Contains(uid))
        {
            // Same wire token but no live peer to take over (should not happen): keep the
            // honest rejection rather than inventing an actor.
            Console.WriteLine(
                $"[match-host] JoinRoom duplicate token='{uid}' without a live actor — " +
                "ActorWithSameUserIdExists");
            result = JoinRoomResult.ActorWithSameUserIdExists;
        }
        else
        {
            // Accept JoinOnly when password hits Dedik. Do not gate on room==LobbyId —
            // room is roster semantics, not the dict key. Multi-name encoding TBD.
            byte? freeNr;
            lock (_roomGate)
                freeNr = MatchRoomRoster.FindFreeActorNr(room.Actors);
            if (freeNr is not { } nr)
            {
                // 254 human actors + Server: no free id. Honest refusal (result=1).
                Console.WriteLine(
                    "[match-host] JoinRoom Found refused — all actor numbers 2..255 are in use");
                result = JoinRoomResult.RoomIsFull;
            }
            else
            {
                lock (_roomGate)
                {
                    if (st.UserId is { } u)
                        room.UserIds.Add(u);
                    var nick = string.IsNullOrWhiteSpace(req.Room) ? $"actor{nr}" : req.Room;
                    room.Actors.Add((nr, nick, st.UserId ?? ""));
                    st.RosterName = nick;
                }
                st.Room = room;
                st.ActorNr = nr;
                actor = nr;
                result = JoinRoomResult.Found;
                if (st.IdentityKey is { Length: > 0 } reconnectKey)
                {
                    var remembered = false;
                    var prevTeam = MatchTeam.None;
                    lock (_roomGate)
                        remembered = _reconnectTeams.TryGetValue(reconnectKey, out prevTeam);
                    if (remembered && prevTeam is MatchTeam.Tr or MatchTeam.Ct)
                    {
                        st.PendingReconnectTeam = prevTeam;
                        Console.WriteLine(
                            $"[match-host] reconnect-pending: identity='{reconnectKey}' " +
                            $"restore team={prevTeam} after INIT (new actor={nr})");
                    }
                }
                Console.WriteLine(
                    $"[match-host] JoinRoom Found passwordKey='{room.PasswordKey}' " +
                    $"roster={MatchRoomField.Describe(req.Room)} actorNr={nr} " +
                    $"(host={MatchHostActor.ActorNr}/'{MatchHostActor.Name}' Spectator) " +
                    $"userId='{st.UserId}'");
            }
        }

        MatchGapRoom? gap = null;
        if (result == JoinRoomResult.Found && room is not null && actor is { } assigned)
        {
            List<(byte Nr, string Name, string UserId)> actorsSnap;
            byte roomC2;
            string modeId;
            string levelId;
            lock (_roomGate)
            {
                actorsSnap = room.Actors.ToList();
                roomC2 = room.RoomC2;
                modeId = _matchGameModeId;
                levelId = _matchSelectedLevel;
                // Keep actor=0 room props in sync for late-join snapshots.
                room.ActorProps[(0, MatchRoomPropKeys.C0)] = LobbyVariant.FromString(modeId);
                room.ActorProps[(0, MatchRoomPropKeys.C1)] = LobbyVariant.FromString(levelId);
            }

            // Phone-host probe (20260722_003523_* / run-20260722_073504): thin Found ~148B —
            // room props + actor names only (empty gak props). Identity/avatar come post-Found.
            // Match channel = shared truth (all actors). Lobby JoinResponse now fans out
            // full roster via op4 NewMember (dedicated MaxMembers=16).
            // MaxActorsHint ≥ live roster so Found never advertises fewer slots than actors present.
            var maxHint = (byte)Math.Clamp(
                Math.Max(MatchGapDefaults.MaxActorsHint, actorsSnap.Count), 1, 255);
            // C0/C1 from lobby selection (SetMatchSelection), not hardcoded Ranked2v2/Sandstone.
            gap = new MatchGapRoom
            {
                RoomName = LanCreateRoomPasswordKey,
                Open = true,
                MaxActorsHint = maxHint,
                RoomProps = MatchGapDefaults.BuildRoomProps(modeId, levelId, c2: roomC2),
                Actors = actorsSnap.Select(a => new MatchGapActor
                {
                    ActorNr = a.Nr,
                    Name = a.Name,
                    UserId = a.UserId,
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
        if (Diag.LogVerbose)
            DumpCapture("match_tx_JoinRoomResponse", resp);
        Console.WriteLine(
            $"[match-host] TX JoinRoomResponse result={MatchCodec.JoinRoomResultName(result)} " +
            $"actor={(actor?.ToString() ?? "-")} gap={(gap is null ? "none" : $"props={gap.Value.RoomProps.Count} actors={gap.Value.Actors.Count} C2={room?.RoomC2} C0={_matchGameModeId} C1={_matchSelectedLevel}")} " +
            $"len={resp.Length}");

        if (result == JoinRoomResult.Found && room is not null && actor is { } joinerNr)
        {
            // Phone host sends a bare `00 0F` (opcode 15) right after Found — before any
            // SetProperty / scene manager / C2. The dedicated never sent it, so clients were
            // missing the first post-Found frame (see RoomInitReady in MatchCodec).
            var initReady = MatchCodec.BuildRoomInitReady();
            Send(peer, initReady);
            if (Diag.LogVerbose)
                DumpCapture("match_tx_RoomInitReady", initReady);
            Console.WriteLine(
                "[match-host] TX RoomInitReady (op15, empty body) → joiner " +
                $"{joinerNr} — same first post-Found frame as the phone host");

            // Dynamic roster: peers who already Found only knew actors-at-entry.
            // Push ActorJoinedEvent (fup) so early humans learn about this joiner.
            var joinName = st.RosterName ?? $"actor{joinerNr}";
            if (adoptedReconnect)
            {
                // Issue #39: the actor never left the room. Re-announcing it would make peers
                // allocate a second body for the same actorNr; the returning client only needs
                // INIT + its world snapshot (bootstrap below).
                Console.WriteLine(
                    $"[match-host] reconnect-takeover: actor={joinerNr} — skip ActorJoined/" +
                    "OnMatchPlayerJoined (peers still know this actor)");
            }
            else
            {
                NotifyPeersActorJoined(room, peer, joinerNr, joinName);
                try { OnMatchPlayerJoined?.Invoke(joinName); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[match-host] OnMatchPlayerJoined: {ex.Message}");
                }
            }
            BeginAwaitJoinerThenBootstrap(peer, room, joinerNr);
        }
    }

    /// <summary>
    /// Tell every other match peer about a newly Found human actor (<c>fuo=50</c>).
    /// Joiner already has the full roster in their own Found gap — exclude them.
}
