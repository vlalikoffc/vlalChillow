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

    /// <summary>
    /// В какую комнату пускать этого игрока. Провод неизменен: клиент присылает ник
    /// (<c>JoinRoomRequest.room</c>) и всегда <c>'Dedik'</c>. Панель записала, кто какую
    /// комнату создал, — по этой записи и выбираем.
    /// </summary>
    /// <remarks>
    /// Возврат всегда рабочий ключ. Если брони нет или комната уже удалена — уходим в
    /// legacy <c>Dedik</c> и громко пишем в лог: «тихо пустить не туда» хуже, чем сыграть
    /// в общей комнате.
    /// </remarks>
    /// <summary>
    /// Ключ, которого не бывает: <c>ResolveJoinRoomKey</c> возвращает его, когда
    /// комната нужна игроку, но брони нет. Дальше вызывающий код видит
    /// <c>room is null</c> и отвечает <c>RoomNotFound</c> — без выдуманных комнат.
    /// </summary>
    private const string NoSuchRoomKey = "\u0000нет-такой-комнаты";

    private string ResolveJoinRoomKey(JoinRoomRequestBody req, NetPeer peer)
    {
        // CreateOnly/JoinOrCreate регистрируют комнату под присланным паролем — уважаем это
        // и не перебиваем: там пароль осмысленный, а не 'Dedik'.
        if (req.Mode is JoinRoomMode.CreateOnly or JoinRoomMode.JoinOrCreate
            && req.Password is { Length: > 0 })
            return req.Password;

        string identity;
        lock (_roomGate)
            identity = _peers.TryGetValue(peer, out var st) ? st.IdentityKey ?? "" : "";

        var address = peer.Address?.ToString() ?? "";

        // Панельные комнаты существуют: молча кидать в legacy 'Dedik' нельзя —
        // именно так игроки без брони оказывались «в параллельном измерении» на
        // чужой карте. Если брони нет — подставляем ключ, которого заведомо нет:
        // вызывающий код вернёт RoomNotFound, и клиент увидит честный отказ.
        if (!PendingRooms.TryResolve(req.Room, identity, address, out var key))
        {
            if (HasPanelRooms())
            {
                Console.WriteLine(
                    $"[match-host] у '{req.Room}' нет брони, а панельные комнаты есть — " +
                    "RoomNotFound (нужен /join в лобби), в legacy 'Dedik' не пускаем");
                return NoSuchRoomKey;
            }

            return LanCreateRoomPasswordKey;
        }

        MatchRoom? room;
        lock (_roomGate)
            _roomsByPassword.TryGetValue(key, out room);

        if (room is null)
        {
            Console.WriteLine(
                $"[match-host] маршрут '{req.Room}' → '{key}' протух: комнаты нет, " +
                $"иду в '{LanCreateRoomPasswordKey}'");
            PendingRooms.Release(key);
            return LanCreateRoomPasswordKey;
        }

        var roomMode = ReadRoomModeId(room);
        Console.WriteLine(
            $"[match-host] маршрут '{req.Room}' → '{key}' " +
            $"(режим {roomMode ?? "legacy"}) — комната найдена");
        return key;
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

        // Ник сервера зарезервирован: второго «Server» в катке быть не может. Отклоняем до
        // комнаты и актора — у копии не должно быть ни номера, ни слота в росписи.
        if (ServerIdentity.IsServerNick(req.Room))
        {
            Console.WriteLine(
                $"[match-host] JoinRoom отклонён: ник '{req.Room}' зарезервирован за сервером " +
                $"({peer.Address})");
            try { peer.Disconnect(); }
            catch { /* уже мёртв */ }
            return;
        }

        // Админский ник без кода: мимо лобби на 7777 не пройти — код обязателен везде.
        if (IsUnauthorizedAdmin?.Invoke(req.Room, peer.Address) == true)
        {
            Console.WriteLine(
                $"[auth] JoinRoom отклонён: '{req.Room}' {peer.Address} — админский ник " +
                "без кода, сначала авторизуйся в лобби");
            try { peer.Disconnect(); }
            catch { /* уже мёртв */ }
            return;
        }

        // Гейт входа и реконнекта — до EnsureRoom и до TryFindRejoinActor: иначе
        // забаненный «усыновлял» старый актор и возвращался в катку после смены сети.
        var banDecision = InspectBanJoin(req.Room, peer.Address);
        if (banDecision.Banned)
        {
            Console.WriteLine(
                $"[ban] JoinRoom отклонён: '{req.Room}' {peer.Address} — совпало по " +
                banDecision.MatchedBy + (banDecision.LearnedAlias ? ", алиас записан" : ""));
            try { peer.Disconnect(); }
            catch { /* уже мёртв */ }
            return;
        }
        if (banDecision.AdminExempt)
            Console.WriteLine($"[ban] JoinRoom '{req.Room}' — админ или сервер, бан не применяется");

        // CreateOnly / JoinOrCreate may register under the password they send.
        // JoinOnly: ensure Dedik LAN room (Play may race slightly before EnsureLanRoom).
        if (req.Mode is JoinRoomMode.CreateOnly or JoinRoomMode.JoinOrCreate)
            EnsureRoom(req.Password, string.IsNullOrEmpty(req.Room) ? _lobbyId : req.Room);
        else
            EnsureLanRoom();

        // PR 3 плана: комната выбирается по нику, а не по паролю — клиент всегда шлёт
        // 'Dedik', и без этого все игроки попадали бы в одну комнату. Найденная комната
        // должна существовать и режим в ней должен совпадать, иначе — legacy 'Dedik'.
        var roomKey = ResolveJoinRoomKey(req, peer);
        MatchRoom? room;
        lock (_roomGate)
            _roomsByPassword.TryGetValue(roomKey, out room);

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
                 && TryFindRejoinActor(room, identityKey, out var rejoinActor, out var rejoinState))
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
                $"roster='{st.RosterName}' — no second actor, no duplicate uid" +
                (rejoinState is null
                    ? " (ghost actor: его сокет уже пропал, номер возвращён владельцу)"
                    : ""));
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
            var roomIsFull = false;
            lock (_roomGate)
            {
                // Комната знает свой размер: «союзники 1 на 1» — два места, а не
                // шестнадцать. Без этой проверки в неё набивались лишние люди и
                // сидели зрителями, не понимая, почему не играют.
                roomIsFull = room.MaxPlayers > 0
                             && MatchRoomRoster.CountHumans(room.Actors) >= room.MaxPlayers;
                freeNr = roomIsFull ? null : MatchRoomRoster.FindFreeActorNr(room.Actors);
            }

            if (freeNr is not { } nr)
            {
                // Две разные причины отказа, и в лог они идут разными словами:
                // «мест по правилам комнаты нет» и «кончились номера актёров».
                Console.WriteLine(roomIsFull
                    ? $"[match-host] JoinRoom в '{room.PasswordKey}' отклонён: комнату " +
                      $"заполнили ({room.MaxPlayers} мест, команда по {room.PerTeam})"
                    : "[match-host] JoinRoom Found refused — all actor numbers 2..255 are in use");
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
                    // Issue #39: владелец актёра запоминается в комнате, чтобы следующий заход
                    // этого же игрока занял тот же номер, даже если сокет уже пропал.
                    if (st.IdentityKey is { Length: > 0 } ownerKey)
                        room.ActorIdentities[nr] = ownerKey;
                    room.OwnerlessSinceTick.Remove(nr);
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

                // Бронь продлеваем на входе: игрок может стоять в комнате дольше TTL
                // (создал, ждёт друзей), и тогда его переподключение ушло бы в legacy 'Dedik'.
                if (room.IsPanelRoom && st.RosterName is { Length: > 0 } joinedNick)
                    PendingRooms.Reserve(
                        room.PasswordKey, joinedNick, st.IdentityKey, peer.Address?.ToString());
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
                // C0/C1 комнаты — из её собственных props (комната из панели несёт свой режим
                // и карту); глобальный выбор — только fallback для legacy Dedik-комнаты.
                // Так две комнаты на одном 7777 не перетирают режим друг друга.
                modeId = ReadRoomModeId(room) ?? _matchGameModeId;
                levelId = ReadRoomLevelId(room) ?? _matchSelectedLevel;
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
