using System.Buffers.Binary;
using K4os.Compression.LZ4;
using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net.Match;

/// <summary>
/// Chillow.Netcode match opcodes (<c>fuo</c>) — DiffableCs enum.
/// <para>
/// The opcode is only the envelope tag; the per-op <b>parameters</b> live in the typed value codec
/// (<see cref="LobbyWriter.WriteFzp"/> / <see cref="LobbyReader.ReadFzp"/>) — the <c>fzp/fzq</c>
/// variant: <c>Bool=6, Int=1, Short=2, Double=5, Byte=255, Reference=10→{String=13, StringArray=14,
/// PropertiesRecord=10, ByteArray=4, Null=255}</c>. That is why the ops below look like "shells":
/// the keys (<c>death</c>, <c>kills</c>, <c>CtScore</c>, <c>C2</c>, <c>Time</c>, <c>FinalWinTeam</c>,
/// …) are just strings, and every value flows through the one variant codec.
/// </para>
/// <para>
/// Builder coverage (verified byte-identical to phone gold captures where noted):
/// <list type="bullet">
/// <item><c>101 SetProperty</c> / <c>100 SetProperties</c> — full. Death sequence
///   <c>death</c>/<c>kills</c>/<c>fair_kills</c>/<c>score</c>/<c>CtScore</c>/<c>TrScore</c> are all
///   <c>fzq.Int</c>; C2 bag = <c>int16 count + Time:Double(05) + C2:Byte(ff)</c> — both match gold.</item>
/// <item><c>50 ActorJoinedEvent</c> / <c>51 ActorLeftEvent</c> / <c>102 SetInternalProperty</c> (nick) — full.</item>
/// <item><c>200 CreateWorldObject</c> / <c>201 DestroyWorldObject</c> / <c>203 WorldObjectRpc</c>
///   (incl. <see cref="BuildRadarManagerDeathRefreshRpc"/>) / <c>204 WorldObjectState</c> — full for the pawn/death path.</item>
/// </list>
/// Remaining stubs (NOT death-HUD related, intentionally not faked): <c>FinalWinTeam.FinalPlayers</c>
/// inner ByteArray layout (match-end scoreboard, omitted) and <c>202 ReCreateSceneManager</c> 2B body
/// (undecoded; only seen at phase transitions, never in a kill). No <c>205 CustomEvent</c> is seen on
/// the wire, and the dedicated host logged <b>zero</b> unhandled opcodes across the death sessions.
/// </para>
/// </summary>
public enum MatchOpcode : byte
{
    HandshakeRequest = 0,
    HandshakeResponse = 1,
    FetchServerTimeRequest = 2,
    FetchServerTimeResponse = 3,
    JoinRoomRequest = 10,
    JoinRandomRoomRequest = 11,
    JoinRoomResponse = 12,
    LeaveRoomRequest = 13,
    LeaveRoomResponse = 14,
    ActorJoinedEvent = 50,
    ActorLeftEvent = 51,
    SetProperties = 100,
    SetProperty = 101,
    SetInternalProperty = 102,
    CreateBotActorRequest = 150,
    RemoveBotActorRequest = 151,
    CreateWorldObject = 200,
    DestroyWorldObject = 201,
    ReCreateSceneManager = 202,
    WorldObjectRpc = 203,
    WorldObjectState = 204,
    CustomEvent = 205,
    ServerSideError = 255,
}

/// <summary>Handshake result (<c>fzi</c>).</summary>
public enum HandshakeResult : byte
{
    Success = 0,
    InvalidAppId = 2,
    InvalidUserId = 4,
    InvalidProtocolVersion = 8,
    MaxCcuReached = 16,
    AlreadyAuthenticated = 32,
}

/// <summary>Join room mode (<c>fzm</c>) — wire byte on <c>fuy</c>.</summary>
public enum JoinRoomMode : byte
{
    JoinOnly = 0,
    CreateOnly = 1,
    JoinOrCreate = 2,
}

/// <summary>Join room result (<c>fzn</c>) — first body byte of <c>fuz</c> / JoinRoomResponse.</summary>
public enum JoinRoomResult : byte
{
    Found = 0,
    RoomIsFull = 1,
    RoomIsClosed = 2,
    CreateRoomPluginNotFound = 3,
    RoomWithSameIdAlreadyExists = 4,
    RoomPluginMismatch = 5,
    ActorWithSameUserIdExists = 6,
    RoomNotFound = 7,
    InvalidQuery = 8,
    InvalidCreateOptions = 9,
    KickedByServerPlugin = 10,
}

/// <summary>
/// Netcode match AppId values observed on the HandshakeRequest wire.
/// Live phone→dedicated host (and ConnectAsClient probe): <c>com.Chillow.StandChillow</c>
/// (<c>captures/*_match_rx_HandshakeRequest_len62.bin</c>).
/// Alternate metadata attribution for <c>cui.balx</c>: <c>gamma.Chillow.StandChillow</c>
/// (host accepts both; probe TX prefers the live package id).
/// Not lobby auth (<c>projectchillow2</c>).
/// </summary>
public static class MatchAuth
{
    /// <summary>Live phone HandshakeRequest AppId (dedicated-host RX + ConnectAsClient TX).</summary>
    public const string AppId = "com.Chillow.StandChillow";

    /// <summary>Alternate AppId from metadata balx attribution — accepted by host, not preferred TX.</summary>
    public const string AppIdGamma = "gamma.Chillow.StandChillow";

    /// <summary><c>ful.cwfh</c> / <c>fuv.cwgm</c> protocol version ushort.</summary>
    public const ushort ProtocolVersion = 1024;

    /// <summary><c>cui.balw()</c> — lobby version string (not used on match wire).</summary>
    public const string GameVersion = "2.06";

    /// <summary>True if HandshakeRequest AppId is one of the observed live values.</summary>
    public static bool IsAcceptedAppId(string? appId) =>
        string.Equals(appId, AppId, StringComparison.Ordinal)
        || string.Equals(appId, AppIdGamma, StringComparison.Ordinal);
}

/// <summary>
/// Envelope flags (<c>fun</c>) on the first byte of <c>fum</c>.
/// Host→client TX live with <c>HasServerTime</c>; client→host requests use <c>None</c>.
/// </summary>
[Flags]
public enum MatchFrameFlags : byte
{
    None = 0,
    IsEncrypted = 1,
    IsCompressed = 2,
    HasServerTime = 4,
}

/// <summary>Decoded <c>fuz</c> JoinRoomResponse body (after envelope).</summary>
public readonly struct JoinRoomResponseBody
{
    public JoinRoomResult Result { get; init; }
    public string? Message { get; init; }
    public string? DebugMessage { get; init; }
    public byte? ActorNr { get; init; }
    public bool HasRoomProperties { get; init; }
    public bool IsSuccess => Result == JoinRoomResult.Found;
}

/// <summary>Inputs for writing <c>gap</c> on Found JoinRoomResponse (<c>gap.boow</c>).</summary>
public readonly struct MatchGapRoom
{
    /// <summary><c>gap.book</c> — live = password key <c>Dedik</c>.</summary>
    public string RoomName { get; init; }
    /// <summary><c>gap.boom</c> — live true.</summary>
    public bool Open { get; init; }
    /// <summary><c>gap.booo</c> — live 4.</summary>
    public byte MaxActorsHint { get; init; }
    public IReadOnlyList<(string Key, LobbyVariant Value)> RoomProps { get; init; }
    public IReadOnlyList<MatchGapActor> Actors { get; init; }
    /// <summary><c>gap.boou</c> — live 1.</summary>
    public byte TrailingByte { get; init; }
}

/// <summary>Decoded <c>fuv</c> HandshakeRequest body (after envelope).</summary>
public readonly struct HandshakeRequestBody
{
    public ushort ProtocolVersion { get; init; }
    public string AppId { get; init; }
    public string UserId { get; init; }
}

/// <summary>Decoded <c>fuy</c> JoinRoomRequest body (after envelope).</summary>
public readonly struct JoinRoomRequestBody
{
    /// <summary>
    /// <c>fuy.cwgt</c> — single wire string: who should be in the match (lobby humans
    /// that load into катка). Not a separate protobuf list. With the dedicated lobby's
    /// per-client illusion the phone only knows itself → live <c>влал</c>. Full multi-player
    /// lobbies may encode multiple names in this same string (separator TBD from decompile).
    /// </summary>
    public string Room { get; init; }
    public JoinRoomMode Mode { get; init; }
    public string Password { get; init; }
    public bool HasCreateOptions { get; init; }
}

/// <summary>
/// Helpers for <c>fuy.cwgt</c> (JoinRoom room field) — match participant roster semantics.
/// </summary>
public static class MatchRoomField
{
    /// <summary>
    /// Describe the room string for logs. Wire is one length-prefixed string; we do
    /// <b>not</b> invent a multi-name separator (none recovered in decompile yet).
    /// </summary>
    public static string Describe(string roomRaw)
    {
        if (string.IsNullOrEmpty(roomRaw))
            return "(empty)";
        // No proven delimiter — treat as opaque roster blob (may be 1 name or N encoded).
        return $"opaque='{roomRaw}' len={roomRaw.Length} " +
               "(single string; multi-member separator not yet recovered — " +
               "illusion lobbies send one human name)";
    }
}

/// <summary>
/// Chillow.Netcode match frames: plain <c>fum</c> (flags + opcode [+ optional serverTime]) + body via lobby <c>fzr</c>.
/// </summary>
public static class MatchCodec
{
    /// <summary>
    /// Plain HandshakeRequest (<c>fuo=0</c> / <c>fuv</c>):
    /// flags=0, opcode=0, u16 LE 1024, AppId string, UserId string.
    /// </summary>
    public static byte[] BuildHandshakeRequest(string appId, string userId)
    {
        var w = new LobbyWriter();
        w.WriteByte(0); // fun.None
        w.WriteByte((byte)MatchOpcode.HandshakeRequest);
        Span<byte> u16 = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(u16, MatchAuth.ProtocolVersion);
        w.WriteBytes(u16);
        w.WriteString(appId);
        w.WriteString(userId);
        return w.ToArray();
    }

    /// <summary>
    /// Host HandshakeResponse (<c>fuo=1</c> / <c>fuw</c>).
    /// Live: flags=<c>HasServerTime</c>, body = <c>fzi</c> + bool (Success path uses bool=true).
    /// </summary>
    public static byte[] BuildHandshakeResponse(
        HandshakeResult result,
        bool flag,
        int serverTime)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)MatchFrameFlags.HasServerTime);
        w.WriteByte((byte)MatchOpcode.HandshakeResponse);
        w.WriteInt32(serverTime);
        w.WriteByte((byte)result);
        w.WriteBool(flag);
        return w.ToArray();
    }

    /// <summary>
    /// Host FetchServerTimeResponse (<c>fuo=3</c>). Dedicated <c>fyf.bodw</c>:
    /// <c>fum(HasServerTime=4, opcode=3, Environment.TickCount)</c> — empty body.
    /// Wire: <c>04 03 &lt;i32 LE TickCount ms&gt;</c>. Client request is <c>00 02</c> (op 2).
    /// Client <c>dws.bfwi</c> converts synced TickCount → seconds via <c>/1000</c> for
    /// <c>NetManager.bfqt</c>; room <c>Time</c>/<c>RoundStartTime</c> doubles use that
    /// seconds clock — do not put seconds into this i32.
    /// </summary>
    public static byte[] BuildFetchServerTimeResponse(int serverTime)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)MatchFrameFlags.HasServerTime);
        w.WriteByte((byte)MatchOpcode.FetchServerTimeResponse);
        w.WriteInt32(serverTime);
        return w.ToArray();
    }

    /// <summary>
    /// Plain JoinRoomRequest (<c>fuo=10</c> / <c>fuy</c>).
    /// Wire: room string + <c>fzm</c> + password string + hasCreateOptions.
    /// Host <c>fyk.OnJoinRoomRequest</c> looks up the room dict by the <b>password</b> field
    /// (<c>fuy+0x10</c> / <c>cwgv</c>), not the room field — see <c>fyi.boeh(password)</c>.
    /// Live dedicated-host RX (phone joiner): room=<c>влал</c>, mode=JoinOnly,
    /// password=<c>Dedik</c> (chillow joke literal). Room is a <b>single</b> string on the
    /// wire — not a packed lobby-member list; Handshake userId remains a UUID.
    /// </summary>
    public static byte[] BuildJoinRoomRequest(
        string roomId,
        JoinRoomMode mode = JoinRoomMode.JoinOnly,
        string? password = null)
    {
        password ??= GameMatchHost.LanCreateRoomPasswordKey;
        var w = new LobbyWriter();
        w.WriteByte(0); // fun.None
        w.WriteByte((byte)MatchOpcode.JoinRoomRequest);
        w.WriteString(roomId);
        w.WriteByte((byte)mode);
        w.WriteString(password);
        w.WriteBool(false); // no fzf createOptions
        return w.ToArray();
    }

    /// <summary>
    /// Host JoinRoomResponse (<c>fuo=12</c> / <c>fuz</c>).
    /// Live failure: HasServerTime + result + four false optionals.
    /// Success (phone host capture len=145): Found + debug=<c>Dedik</c> + actorNr + <c>gap</c> room props/actors.
    /// </summary>
    public static byte[] BuildJoinRoomResponse(
        JoinRoomResult result,
        int serverTime,
        byte? actorNr = null,
        string? message = null,
        string? debugMessage = null,
        MatchGapRoom? gap = null)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)MatchFrameFlags.HasServerTime);
        w.WriteByte((byte)MatchOpcode.JoinRoomResponse);
        w.WriteInt32(serverTime);
        w.WriteByte((byte)result);
        if (message is not null)
        {
            w.WriteBool(true);
            w.WriteString(message);
        }
        else
            w.WriteBool(false);
        if (debugMessage is not null)
        {
            w.WriteBool(true);
            w.WriteString(debugMessage);
        }
        else
            w.WriteBool(false);
        if (actorNr is { } a)
        {
            w.WriteBool(true);
            w.WriteByte(a);
        }
        else
            w.WriteBool(false);
        if (gap is { } g)
        {
            w.WriteBool(true);
            WriteGap(w, g);
        }
        else
            w.WriteBool(false);
        return w.ToArray();
    }

    /// <summary>
    /// <c>gap.boow</c>: roomName, open bool, maxActors byte, hasProps + bare fzs, actorCount byte + <c>gak</c>[], trailing byte.
    /// </summary>
    public static void WriteGap(LobbyWriter w, MatchGapRoom gap)
    {
        w.WriteString(gap.RoomName);
        w.WriteBool(gap.Open);
        w.WriteByte(gap.MaxActorsHint);
        if (gap.RoomProps is { Count: > 0 } props)
        {
            w.WriteBool(true);
            w.WriteBareProps(props);
        }
        else
            w.WriteBool(false);

        var actors = gap.Actors ?? Array.Empty<MatchGapActor>();
        w.WriteByte(checked((byte)actors.Count));
        foreach (var a in actors)
            WriteGak(w, a);
        w.WriteByte(gap.TrailingByte);
    }

    /// <summary>
    /// <c>gak.booe</c>: nr, name, hasTypedProps, [typed props], flag.
    /// Same layout as gap actor entries (thin Found uses empty props + flag=false).
    /// </summary>
    public static void WriteGak(LobbyWriter w, MatchGapActor a)
    {
        w.WriteByte(a.ActorNr);
        w.WriteString(a.Name);
        w.WriteBool(true);
        if (a.Props is { Count: > 0 } actorProps)
            w.WriteTypedProps(actorProps);
        else
            w.WriteTypedEmptyProps();
        w.WriteBool(a.Flag);
    }

    /// <summary>
    /// Host ActorJoinedEvent (<c>fuo=50</c> / <c>fup</c>): HasServerTime + <c>gak</c> + optional Guid.
    /// ISIL <c>fup.bnkb</c>: write gak.booe, then bool hasGuid [, Guid string].
    /// Used so peers who already Found learn about a late human joiner (dynamic roster).
    /// </summary>
    public static byte[] BuildActorJoinedEvent(
        int serverTime,
        MatchGapActor actor,
        string? userGuid = null)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)MatchFrameFlags.HasServerTime);
        w.WriteByte((byte)MatchOpcode.ActorJoinedEvent);
        w.WriteInt32(serverTime);
        WriteGak(w, actor);
        if (userGuid is { Length: > 0 })
        {
            w.WriteBool(true);
            w.WriteString(userGuid);
        }
        else
            w.WriteBool(false);
        return w.ToArray();
    }

    /// <summary>
    /// Host ActorLeftEvent (<c>fuo=51</c> / <c>fuq</c>): HasServerTime + actorNr +
    /// nullable-bool + nullable-byte. ISIL <c>fuq.bnkb</c>: write <c>cwfv</c> byte, then
    /// presence+value for <c>Nullable&lt;bool&gt;</c>, then presence+value for
    /// <c>Nullable&lt;byte&gt;</c>. Minimal leave: both nullables absent (presence=false).
    /// </summary>
    public static byte[] BuildActorLeftEvent(
        int serverTime,
        byte actorNr,
        bool? optionalBool = null,
        byte? optionalByte = null)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)MatchFrameFlags.HasServerTime);
        w.WriteByte((byte)MatchOpcode.ActorLeftEvent);
        w.WriteInt32(serverTime);
        w.WriteByte(actorNr);
        if (optionalBool is { } b)
        {
            w.WriteBool(true);
            w.WriteBool(b);
        }
        else
            w.WriteBool(false);
        if (optionalByte is { } by)
        {
            w.WriteBool(true);
            w.WriteByte(by);
        }
        else
            w.WriteBool(false);
        return w.ToArray();
    }

    /// <summary>
    /// Host SetInternalProperty (<c>fuo=102</c>): HasServerTime + actorNr + code + fzp.
    /// Phone-host nick: code <c>255</c> + string (capture <c>*_SetInternalProperty_len19.bin</c>).
    /// </summary>
    public static byte[] BuildSetInternalProperty(
        int serverTime,
        byte actorNr,
        byte code,
        LobbyVariant value)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)MatchFrameFlags.HasServerTime);
        w.WriteByte((byte)MatchOpcode.SetInternalProperty);
        w.WriteInt32(serverTime);
        w.WriteByte(actorNr);
        w.WriteByte(code);
        w.WriteFzp(value);
        return w.ToArray();
    }

    /// <summary>Internal prop code for actor display nick (phone-host wire <c>FF</c>).</summary>
    public const byte InternalPropNick = 255;

    /// <summary>
    /// Host SetProperty (<c>fuo=101</c> / <c>fvg</c>): HasServerTime + actorNr + key + fzp.
    /// Live host→client team example: <c>… 04 team FF 02</c> (Ct); Spectator uses <c>FF 03</c>.
    /// </summary>
    public static byte[] BuildSetProperty(
        int serverTime,
        byte actorNr,
        string key,
        LobbyVariant value)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)MatchFrameFlags.HasServerTime);
        w.WriteByte((byte)MatchOpcode.SetProperty);
        w.WriteInt32(serverTime);
        w.WriteByte(actorNr);
        w.WriteString(key);
        w.WriteFzp(value);
        return w.ToArray();
    }

    /// <summary>
    /// Client→host SetProperty: flags=<c>None</c> (same as Handshake/JoinRoom TX), body <c>fvg</c>
    /// actorNr + key + fzp. Team shape matches capture encoding: key <c>team</c>, fzq.Byte <c>FF</c>,
    /// value <c>cux</c> (Tr=1 / Ct=2 / Spectator=3).
    /// </summary>
    public static byte[] BuildClientSetProperty(byte actorNr, string key, LobbyVariant value)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)MatchFrameFlags.None);
        w.WriteByte((byte)MatchOpcode.SetProperty);
        w.WriteByte(actorNr);
        w.WriteString(key);
        w.WriteFzp(value);
        return w.ToArray();
    }

    /// <summary>
    /// SetProperties (<c>fuo=100</c> / <c>fvf</c>): actorNr + bare fzs bag.
    /// Actor 0 = room props in capture (C2 update).
    /// </summary>
    public static byte[] BuildSetProperties(
        int serverTime,
        byte actorNr,
        IReadOnlyList<(string Key, LobbyVariant Value)> props)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)MatchFrameFlags.HasServerTime);
        w.WriteByte((byte)MatchOpcode.SetProperties);
        w.WriteInt32(serverTime);
        w.WriteByte(actorNr);
        w.WriteBareProps(props);
        return w.ToArray();
    }

    /// <summary>
    /// CreateWorldObject (<c>fuo=200</c> / <c>fus</c>):
    /// kind (<c>gac</c>), optional owner, id, type name, fus tag byte, optional trailing payload
    /// (e.g. WeaponDropManager int32 count + id bytes).
    /// </summary>
    public static byte[] BuildCreateWorldObject(
        int serverTime,
        WorldObjectKind kind,
        short objectId,
        string typeName,
        byte fusTag,
        byte? ownerActorNr = null,
        byte[]? trailingPayload = null)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)MatchFrameFlags.HasServerTime);
        w.WriteByte((byte)MatchOpcode.CreateWorldObject);
        w.WriteInt32(serverTime);
        WriteCreateWorldObjectBody(w, kind, objectId, typeName, fusTag, ownerActorNr, trailingPayload);
        return w.ToArray();
    }

    /// <summary>
    /// Client→host CreateWorldObject: flags=<c>None</c>, no server time
    /// (live joiner <c>*_CreateWorldObject_len59.bin</c>).
    /// </summary>
    public static byte[] BuildClientCreateWorldObject(
        WorldObjectKind kind,
        short objectId,
        string typeName,
        byte fusTag,
        byte? ownerActorNr = null,
        byte[]? trailingPayload = null)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)MatchFrameFlags.None);
        w.WriteByte((byte)MatchOpcode.CreateWorldObject);
        WriteCreateWorldObjectBody(w, kind, objectId, typeName, fusTag, ownerActorNr, trailingPayload);
        return w.ToArray();
    }

    private static void WriteCreateWorldObjectBody(
        LobbyWriter w,
        WorldObjectKind kind,
        short objectId,
        string typeName,
        byte fusTag,
        byte? ownerActorNr,
        byte[]? trailingPayload)
    {
        w.WriteByte((byte)kind);
        if (ownerActorNr is { } owner)
        {
            w.WriteBool(true);
            w.WriteByte(owner);
        }
        else
            w.WriteBool(false);
        w.WriteInt16(objectId);
        w.WriteString(typeName);
        w.WriteByte(fusTag);
        if (trailingPayload is { Length: > 0 } payload)
            w.WriteBytes(payload);
    }

    /// <summary>Build WeaponDropManager / GrenadeManager trailing catalog: int32 count + id bytes.</summary>
    public static byte[] BuildDropCatalogPayload(byte[] catalogIds)
    {
        var w = new LobbyWriter();
        w.WriteInt32(catalogIds.Length);
        w.WriteBytes(catalogIds);
        return w.ToArray();
    }

    /// <summary>
    /// Pawn CreateWorldObject trailing after fus tag — fields decoded from Sandstone capture len=63.
    /// </summary>
    public static byte[] BuildPawnSpawnPayload(
        float posX, float posY, float posZ,
        float quatX, float quatY, float quatZ, float quatW,
        byte flags = MatchSceneManagers.PawnSpawn.Flags,
        byte rotFlags = MatchSceneManagers.PawnSpawn.RotFlags,
        string loadoutTag = "")
    {
        var w = new LobbyWriter();
        w.WriteByte(flags);
        w.WriteFloat(posX);
        w.WriteFloat(posY);
        w.WriteFloat(posZ);
        w.WriteByte(rotFlags);
        w.WriteFloat(quatX);
        w.WriteFloat(quatY);
        w.WriteFloat(quatZ);
        w.WriteFloat(quatW);
        w.WriteByte(MatchSceneManagers.PawnSpawn.TrailingU8A);
        w.WriteBytes(new byte[8]); // eight zero bytes observed after u8=100
        w.WriteByte(MatchSceneManagers.PawnSpawn.TrailingU8B);
        w.WriteFloat(MatchSceneManagers.PawnSpawn.TrailingFloat);
        // Final field = length-prefixed loadout tag = COSMETICS ONLY (the agent/skins the player
        // has equipped in their locker — a look). It is NOT a spawn/mesh-activation signal and NOT
        // the TDM buy-menu weapon pick: gold player_1547 spawns fully visible + killable with an
        // EMPTY tag, so visibility/radar/damage never depend on it. Empty ("") writes a single 0x00
        // (varint 0) — byte-identical to the old default 45B trailing (TrailingU8C=0). A gold tag
        // (AgentCTLincoln / AgentTMarco) only changes the equipped look.
        w.WriteString(loadoutTag);
        return w.ToArray();
    }

    /// <summary>
    /// Sandstone spawn trailing from phone-host capture (codec-built).
    /// Uses Tr or Ct live spawn pose; defaults to Tr for back-compat. Optional
    /// <paramref name="loadoutTag"/> is the agent/cosmetic display tag (see
    /// <see cref="BuildPawnSpawnPayload"/>): empty = default character; a gold tag gives a
    /// visible, collidable model.
    /// </summary>
    public static byte[] BuildSandstonePawnSpawnPayload(MatchTeam team = MatchTeam.Tr, string loadoutTag = "")
    {
        var p = team == MatchTeam.Ct
            ? MatchSceneManagers.PawnSpawn.SandstonePosCt
            : MatchSceneManagers.PawnSpawn.SandstonePosTr;
        var q = team == MatchTeam.Ct
            ? MatchSceneManagers.PawnSpawn.SandstoneQuatCt
            : MatchSceneManagers.PawnSpawn.SandstoneQuatTr;
        return BuildPawnSpawnPayload(p.X, p.Y, p.Z, q.X, q.Y, q.Z, q.W, loadoutTag: loadoutTag);
    }

    /// <summary>Wire prefab name for a fighting team (Tr_Tr / Ct_Ct).</summary>
    public static string PawnTypeNameForTeam(MatchTeam team) => team switch
    {
        MatchTeam.Ct => MatchSceneManagers.PlayerPawnNameCt,
        MatchTeam.Tr => MatchSceneManagers.PlayerPawnNameTr,
        _ => throw new ArgumentOutOfRangeException(nameof(team), team, "No fighting pawn for team"),
    };

    /// <summary>
    /// Gold <b>cosmetics-only</b> tag for a fighting team — the length-prefixed string appended
    /// after the pawn spawn pose. It is just the agent/skins equipped in the player's locker (a
    /// look); it is <b>NOT</b> a spawn/visibility signal and <b>NOT</b> the TDM buy-menu weapon
    /// pick. An empty tag still spawns a fully visible, killable pawn (gold <c>player_1547</c>), so
    /// this is optional. Values are gold-observed (Ct <c>AgentCTLincoln</c>, Tr <c>AgentTMarco</c>)
    /// — re-built via the codec, not a capture replay.
    /// </summary>
    public static string PawnAgentTagForTeam(MatchTeam team) => team switch
    {
        MatchTeam.Ct => MatchSceneManagers.PawnAgentTagCt,
        MatchTeam.Tr => MatchSceneManagers.PawnAgentTagTr,
        _ => "",
    };

    /// <summary>
    /// WorldObjectRpc (<c>fuo=203</c> / <c>fvh</c>): objectId, rpc byte, <c>gaa</c> target,
    /// short, double, then method payload. Live pawn rpc: id=129, rpc=1,
    /// gaa=AllCachedViaServer(4), short=2, double≈serverTime/1000.
    /// </summary>
    public static byte[] BuildWorldObjectRpc(
        int serverTime,
        short objectId,
        byte rpcId,
        byte gaaTarget,
        short field,
        double timeValue,
        byte[]? payload = null)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)MatchFrameFlags.HasServerTime);
        w.WriteByte((byte)MatchOpcode.WorldObjectRpc);
        w.WriteInt32(serverTime);
        WriteWorldObjectRpcBody(w, objectId, rpcId, gaaTarget, field, timeValue, payload);
        return w.ToArray();
    }

    /// <summary>
    /// Client→host WorldObjectRpc: flags=<c>None</c>, no server time
    /// (live <c>*_WorldObjectRpc_len30.bin</c>).
    /// </summary>
    public static byte[] BuildClientWorldObjectRpc(
        short objectId,
        byte rpcId,
        byte gaaTarget,
        short field,
        double timeValue,
        byte[]? payload = null)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)MatchFrameFlags.None);
        w.WriteByte((byte)MatchOpcode.WorldObjectRpc);
        WriteWorldObjectRpcBody(w, objectId, rpcId, gaaTarget, field, timeValue, payload);
        return w.ToArray();
    }

    private static void WriteWorldObjectRpcBody(
        LobbyWriter w,
        short objectId,
        byte rpcId,
        byte gaaTarget,
        short field,
        double timeValue,
        byte[]? payload)
    {
        w.WriteInt16(objectId);
        w.WriteByte(rpcId);
        w.WriteByte(gaaTarget);
        w.WriteInt16(field);
        w.WriteDouble(timeValue);
        if (payload is { Length: > 0 } p)
            w.WriteBytes(p);
    }

    /// <summary>
    /// Master combat-death <b>RadarManager</b> refresh — <c>WorldObjectRpc</c> on the RadarManager
    /// scene object, <c>Rpc(7)</c> = <c>oet(dwa)</c> (0-payload full radar/occlusion rebuild).
    /// Phone gold <c>/tmp/tdm-probe.log</c>: every kill brackets the victim <c>Destroy</c>+<c>death</c>
    /// with two of these (RX#3786/3793, RX#4266/4269 — <c>gaa=3</c>, <c>field=7</c>, no payload).
    /// The master authors this (scene object is <c>owner=no-owner</c>); the dedicated host omitting it
    /// left the victim's pawn destroyed but the death/respawn view never engaging. The <c>rpc</c> byte
    /// is a per-call value the client tolerates (gold used 1/2, our relayed client RPCs use 3); we send
    /// the host's <c>rpc=1</c> convention. Same wire layout as any WorldObjectRpc — not a capture replay.
    /// </summary>
    public static byte[] BuildRadarManagerDeathRefreshRpc(int serverTime) =>
        BuildWorldObjectRpc(
            serverTime,
            objectId: MatchSceneManagers.RadarManagerObjectId,
            rpcId: 1,
            gaaTarget: 3,
            field: 7,
            timeValue: serverTime / 1000.0,
            payload: null);

    /// <summary>
    /// BombManager plant pose payload — evidenced from dedicated-host RX
    /// <c>20260723_023555_189</c> (id=8 rpc=2 field=1 payloadLen=28):
    /// <c>i16</c> planter pawn object id + two typed Vector3 (<c>0x11</c> + 3×float LE each) =
    /// position then up (0,0,1). Rebuilt via codec — not a capture replay.
    /// BombManager <c>nyo</c>/<c>nyu</c> Rpc(1)/Rpc(2) args are <c>(short, Vector3, Vector3)</c>.
    /// </summary>
    public const byte BombPlantVector3TypeCode = 0x11;

    public static byte[] BuildBombManagerPlantPosePayload(
        short planterPawnObjectId,
        float posX, float posY, float posZ,
        float upX = 0f, float upY = 0f, float upZ = 1f)
    {
        var w = new LobbyWriter();
        w.WriteInt16(planterPawnObjectId);
        w.WriteByte(BombPlantVector3TypeCode);
        w.WriteFloat(posX);
        w.WriteFloat(posY);
        w.WriteFloat(posZ);
        w.WriteByte(BombPlantVector3TypeCode);
        w.WriteFloat(upX);
        w.WriteFloat(upY);
        w.WriteFloat(upZ);
        return w.ToArray();
    }

    /// <summary>
    /// Host-authored BombManager plant rpc-token=2 / <c>field=1</c> — same method clients use
    /// for plant pose (Escalation <c>cme</c> → <c>BombManager.nyy</c> → CallRpc). Wire method
    /// id is <c>field</c> (= DiffableCs <c>[Rpc(1)] nyo</c>); <c>rpc</c> byte is a sender token.
    /// <c>gaa=2</c> (Others) matches live plant captures; host broadcasts to all INIT-ready peers.
    /// </summary>
    public static byte[] BuildBombManagerPlantPoseRpc(
        int serverTime,
        short planterPawnObjectId,
        float posX, float posY, float posZ) =>
        BuildWorldObjectRpc(
            serverTime,
            objectId: MatchFlowTestParams.BombManagerObjectId,
            rpcId: 2,
            gaaTarget: 2,
            field: 1,
            timeValue: serverTime / 1000.0,
            payload: BuildBombManagerPlantPosePayload(planterPawnObjectId, posX, posY, posZ));

    /// <summary>
    /// Escalation host auto-plant — <c>BombManager</c> field=3 (gold
    /// <c>MATCH_ESCALATION_PROBE.md</c>): <c>i32(-2)</c> planter sentinel + typed Vector3 +
    /// float 0. Rebuilt via codec — not a capture replay. <c>rpc=1 gaa=2</c> matches gold RX.
    /// </summary>
    public static byte[] BuildBombManagerEscalationAutoPlantPayload(
        float posX, float posY, float posZ)
    {
        var w = new LobbyWriter();
        w.WriteInt32(EscalationFlowParams.AutoPlantPlanterSentinel);
        w.WriteByte(BombPlantVector3TypeCode);
        w.WriteFloat(posX);
        w.WriteFloat(posY);
        w.WriteFloat(posZ);
        w.WriteFloat(0f);
        return w.ToArray();
    }

    public static byte[] BuildBombManagerEscalationAutoPlantRpc(
        int serverTime,
        float posX, float posY, float posZ) =>
        BuildWorldObjectRpc(
            serverTime,
            objectId: MatchFlowTestParams.BombManagerObjectId,
            rpcId: 1,
            gaaTarget: 2,
            field: MatchFlowTestParams.BombManagerFieldEscalationAutoPlant,
            timeValue: serverTime / 1000.0,
            payload: BuildBombManagerEscalationAutoPlantPayload(posX, posY, posZ));

    /// <summary>
    /// ReCreateSceneManager (<c>fuo=202</c>) — flags + opcode + stime + ushort objectId.
    /// Gold Escalation PreStart: ids 4/5/6/8 (<c>MATCH_ESCALATION_PROBE.md</c>).
    /// </summary>
    public static byte[] BuildReCreateSceneManager(int serverTime, short objectId)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)MatchFrameFlags.HasServerTime);
        w.WriteByte((byte)MatchOpcode.ReCreateSceneManager);
        w.WriteInt32(serverTime);
        w.WriteInt16(objectId);
        return w.ToArray();
    }

    /// <summary>
    /// Spawn-tail bytes after quat in pawn CreateWorldObject trailing — same 14B payload
    /// phone host puts on WorldObjectRpc rpc=1 (<c>20260722_003536_026_*</c>).
    /// </summary>
    public static byte[] BuildPawnRpcSpawnTailPayload()
    {
        var w = new LobbyWriter();
        w.WriteByte(MatchSceneManagers.PawnSpawn.TrailingU8A);
        w.WriteBytes(new byte[8]);
        w.WriteByte(MatchSceneManagers.PawnSpawn.TrailingU8B);
        w.WriteFloat(MatchSceneManagers.PawnSpawn.TrailingFloat);
        w.WriteByte(MatchSceneManagers.PawnSpawn.TrailingU8C);
        return w.ToArray();
    }

    /// <summary>
    /// WorldObjectState (<c>fuo=204</c> / <c>fvi</c>): flags=None (Unreliable), objectId,
    /// standing pose + equal-run extension prefix from phone ticks. Full mid-body avatar
    /// fields still opaque — pose/seq/time/marker are enough to start the stream.
    /// </summary>
    public static byte[] BuildWorldObjectStateStanding(
        short objectId,
        uint seq,
        float tickTime,
        float posX, float posY, float posZ)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)MatchFrameFlags.None);
        w.WriteByte((byte)MatchOpcode.WorldObjectState);
        w.WriteInt16(objectId);
        w.WriteInt32(0);
        w.WriteInt32(unchecked((int)seq));
        w.WriteFloat(tickTime);
        w.WriteInt32(0);
        w.WriteFloat(posX);
        w.WriteFloat(posY);
        w.WriteFloat(posZ);
        w.WriteFloat(0f);
        w.WriteFloat(0f);
        w.WriteFloat(0f);
        w.WriteFloat(0f);
        w.WriteFloat(0f);
        w.WriteInt32(0);
        // Equal-run extension prefix (Tr/Ct shared) ending at marker+clock+BaseTime.
        w.WriteByte(0x03);
        w.WriteFloat(MatchSceneManagers.PawnState.BaseTime);
        w.WriteInt16(0x0003);
        w.WriteInt32(0x0003E80A);
        w.WriteBytes(new byte[20]);
        w.WriteInt32(unchecked((int)MatchSceneManagers.PawnState.Marker));
        w.WriteInt16(unchecked((short)(3 + seq * MatchSceneManagers.PawnState.MarkerClockStep)));
        w.WriteFloat(MatchSceneManagers.PawnState.BaseTime);
        return w.ToArray();
    }

    /// <summary>Parse CreateWorldObject body after envelope (<c>fus</c> + optional trailing).</summary>
    public static ParsedCreateWorldObject ParseCreateWorldObjectBody(LobbyReader r)
    {
        var kind = (WorldObjectKind)r.ReadByte();
        byte? owner = null;
        if (r.ReadBool())
            owner = r.ReadByte();
        var objectId = r.ReadInt16();
        var typeName = r.ReadString();
        var fusTag = r.ReadByte();
        var trailing = r.Remaining > 0 ? r.ReadBytes(r.Remaining) : [];
        return new ParsedCreateWorldObject(kind, owner, objectId, typeName, fusTag, trailing);
    }

    /// <summary>Parse WorldObjectRpc body after envelope (<c>fvh</c> + optional method payload).</summary>
    public static ParsedWorldObjectRpc ParseWorldObjectRpcBody(LobbyReader r)
    {
        var objectId = r.ReadInt16();
        var rpcId = r.ReadByte();
        var gaa = r.ReadByte();
        var field = r.ReadInt16();
        var timeValue = r.ReadDouble();
        var payload = r.Remaining > 0 ? r.ReadBytes(r.Remaining) : [];
        return new ParsedWorldObjectRpc(objectId, rpcId, gaa, field, timeValue, payload);
    }

    /// <summary>
    /// DestroyWorldObject (<c>fuo=201</c> / <c>fuu</c>): body is only <c>i16</c> object id.
    /// Client TX capture <c>20260722_011733_155_match_op201_len4.bin</c>:
    /// <c>00 C9 81 01</c> = flags=None + opcode + id=385. Host rebuilds with HasServerTime.
    /// </summary>
    public static byte[] BuildDestroyWorldObject(int serverTime, short objectId)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)MatchFrameFlags.HasServerTime);
        w.WriteByte((byte)MatchOpcode.DestroyWorldObject);
        w.WriteInt32(serverTime);
        w.WriteInt16(objectId);
        return w.ToArray();
    }

    /// <summary>Parse DestroyWorldObject body after envelope — single <c>i16</c> id (<c>fuu.cwgl</c>).</summary>
    public static short ParseDestroyWorldObjectBody(LobbyReader r) => r.ReadInt16();

    /// <summary>
    /// Client→host DestroyWorldObject: flags=<c>None</c>, no server time
    /// (live <c>20260722_011733_155_match_op201_len4.bin</c>: <c>00 C9 81 01</c> = id=385).
    /// </summary>
    public static byte[] BuildClientDestroyWorldObject(short objectId)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)MatchFrameFlags.None);
        w.WriteByte((byte)MatchOpcode.DestroyWorldObject);
        w.WriteInt16(objectId);
        return w.ToArray();
    }

    /// <summary>
    /// Decode pawn CreateWorldObject trailing (45B) into pose — same layout as
    /// <see cref="BuildPawnSpawnPayload"/>.
    /// </summary>
    public static bool TryParsePawnSpawnTrailing(
        ReadOnlySpan<byte> trailing,
        out float posX, out float posY, out float posZ,
        out float quatX, out float quatY, out float quatZ, out float quatW)
    {
        posX = posY = posZ = quatX = quatY = quatZ = quatW = 0;
        if (trailing.Length < 45)
            return false;
        var r = new LobbyReader(trailing);
        _ = r.ReadByte(); // flags
        posX = r.ReadFloat();
        posY = r.ReadFloat();
        posZ = r.ReadFloat();
        _ = r.ReadByte(); // rotFlags
        quatX = r.ReadFloat();
        quatY = r.ReadFloat();
        quatZ = r.ReadFloat();
        quatW = r.ReadFloat();
        return true;
    }

    /// <summary>
    /// Read the client-authored loadout/character tag appended after the 44B pawn spawn pose
    /// block (length-prefixed string; empty on a default 45B trailing, e.g. <c>"AgentCTLincoln"</c>
    /// on a 59B Ct trailing). Diagnostics only — the trailing is relayed <b>verbatim</b>, never
    /// rebuilt from this. Returns false if the trailing is too short or the tag can't be read.
    /// </summary>
    public static bool TryReadPawnLoadoutTag(ReadOnlySpan<byte> trailing, out string tag)
    {
        tag = "";
        // Pose block is 44B; the loadout string's length prefix sits at offset 44 (the last byte
        // of the canonical 45B block is the 0-length prefix of an empty tag).
        const int poseLen = 44;
        if (trailing.Length < poseLen + 1)
            return false;
        try
        {
            var r = new LobbyReader(trailing.Slice(poseLen).ToArray());
            tag = r.ReadString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public readonly record struct ParsedCreateWorldObject(
        WorldObjectKind Kind,
        byte? OwnerActorNr,
        short ObjectId,
        string TypeName,
        byte FusTag,
        byte[] Trailing);

    public readonly record struct ParsedWorldObjectRpc(
        short ObjectId,
        byte RpcId,
        byte GaaTarget,
        short Field,
        double TimeValue,
        byte[] Payload);

    /// <summary>Parse SetProperty body (<c>fvg</c>): actorNr + key + fzp.</summary>
    public static (byte ActorNr, string Key, LobbyVariant Value) ParseSetPropertyBody(LobbyReader r)
    {
        var actor = r.ReadByte();
        var key = r.ReadString();
        var value = r.ReadFzp();
        return (actor, key, value);
    }

    /// <summary>Parse SetProperties body (<c>fvf</c>): actorNr + bare fzs.</summary>
    public static (byte ActorNr, List<(string Key, LobbyVariant Value)> Props) ParseSetPropertiesBody(
        LobbyReader r)
    {
        var actor = r.ReadByte();
        var props = r.ReadBareProps();
        return (actor, props);
    }

    public static bool TryParseEnvelope(
        ReadOnlySpan<byte> payload,
        out MatchFrameFlags flags,
        out MatchOpcode opcode,
        out int? serverTime,
        out int bodyOffset)
    {
        flags = default;
        opcode = default;
        serverTime = null;
        bodyOffset = 0;
        if (payload.Length < 2)
            return false;

        flags = (MatchFrameFlags)payload[0];
        opcode = (MatchOpcode)payload[1];
        bodyOffset = 2;
        if ((flags & MatchFrameFlags.HasServerTime) != 0)
        {
            if (payload.Length < bodyOffset + 4)
                return false;
            serverTime = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(bodyOffset, 4));
            bodyOffset += 4;
        }

        return true;
    }

    /// <summary>
    /// Open match payload body after envelope. When <see cref="MatchFrameFlags.IsCompressed"/>,
    /// body is LZ4 block after uncompressed-length i32 (phone avatar SetProperty capture).
    /// Host→client rebroadcasts use uncompressed <see cref="MatchFrameFlags.HasServerTime"/>.
    /// </summary>
    public static bool TryOpenBody(
        ReadOnlySpan<byte> payload,
        out MatchFrameFlags flags,
        out MatchOpcode opcode,
        out int? serverTime,
        out byte[] body)
    {
        body = Array.Empty<byte>();
        if (!TryParseEnvelope(payload, out flags, out opcode, out serverTime, out var bodyOffset))
            return false;

        if ((flags & MatchFrameFlags.IsEncrypted) != 0)
            return false;

        if ((flags & MatchFrameFlags.IsCompressed) != 0)
        {
            // Compressed layout (no HasServerTime on client TX): uncLen i32 + LZ4 block.
            // bodyOffset is 2 when only IsCompressed; if HasServerTime were also set it would be 6.
            if (payload.Length < bodyOffset + 4)
                return false;
            var uncLen = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(bodyOffset, 4));
            if (uncLen < 0 || uncLen > 16 * 1024 * 1024)
                return false;
            var comp = payload.Slice(bodyOffset + 4);
            body = new byte[uncLen];
            var decoded = LZ4Codec.Decode(comp, body);
            if (decoded != uncLen)
                return false;
            return true;
        }

        body = payload.Slice(bodyOffset).ToArray();
        return true;
    }

    /// <summary>Legacy helper — prefers <see cref="TryParseEnvelope"/>.</summary>
    public static bool TryParseEnvelope(ReadOnlySpan<byte> payload, out MatchOpcode opcode, out int bodyOffset)
        => TryParseEnvelope(payload, out _, out opcode, out _, out bodyOffset);

    public static HandshakeRequestBody ParseHandshakeRequestBody(LobbyReader r)
    {
        var u16 = r.ReadBytes(2);
        var proto = BinaryPrimitives.ReadUInt16LittleEndian(u16);
        var appId = r.ReadString();
        var userId = r.ReadString();
        return new HandshakeRequestBody
        {
            ProtocolVersion = proto,
            AppId = appId,
            UserId = userId,
        };
    }

    public static HandshakeResult ParseHandshakeResponseBody(LobbyReader r)
    {
        // fuw: fzi byte + bool
        var result = (HandshakeResult)r.ReadByte();
        _ = r.ReadBool();
        return result;
    }

    public static JoinRoomRequestBody ParseJoinRoomRequestBody(LobbyReader r)
    {
        var room = r.ReadString();
        var mode = (JoinRoomMode)r.ReadByte();
        var password = r.ReadString();
        var hasCreate = r.ReadBool();
        // fzf createOptions left unread when present — LAN JoinOnly has none.
        return new JoinRoomRequestBody
        {
            Room = room,
            Mode = mode,
            Password = password,
            HasCreateOptions = hasCreate,
        };
    }

    /// <summary>
    /// <c>fuz.bnli</c>: result (<c>fzn</c>), optional msg, optional debug, optional actor byte, optional <c>gap</c> props.
    /// </summary>
    public static JoinRoomResponseBody ParseJoinRoomResponseBody(LobbyReader r)
    {
        var result = (JoinRoomResult)r.ReadByte();
        string? msg = null;
        if (r.ReadBool())
            msg = r.ReadString();
        string? debug = null;
        if (r.ReadBool())
            debug = r.ReadString();
        byte? actor = null;
        if (r.ReadBool())
            actor = r.ReadByte();
        var hasProps = r.ReadBool();
        // gap.boox — leave unread for capture analysis when present; failure path has none.
        return new JoinRoomResponseBody
        {
            Result = result,
            Message = msg,
            DebugMessage = debug,
            ActorNr = actor,
            HasRoomProperties = hasProps,
        };
    }

    public static string OpcodeName(MatchOpcode op) => op switch
    {
        MatchOpcode.HandshakeRequest => "HandshakeRequest",
        MatchOpcode.HandshakeResponse => "HandshakeResponse",
        MatchOpcode.FetchServerTimeRequest => "FetchServerTimeRequest",
        MatchOpcode.FetchServerTimeResponse => "FetchServerTimeResponse",
        MatchOpcode.JoinRoomRequest => "JoinRoomRequest",
        MatchOpcode.JoinRandomRoomRequest => "JoinRandomRoomRequest",
        MatchOpcode.JoinRoomResponse => "JoinRoomResponse",
        MatchOpcode.LeaveRoomRequest => "LeaveRoomRequest",
        MatchOpcode.LeaveRoomResponse => "LeaveRoomResponse",
        MatchOpcode.ActorJoinedEvent => "ActorJoinedEvent",
        MatchOpcode.ActorLeftEvent => "ActorLeftEvent",
        MatchOpcode.SetProperties => "SetProperties",
        MatchOpcode.SetProperty => "SetProperty",
        MatchOpcode.SetInternalProperty => "SetInternalProperty",
        MatchOpcode.CreateBotActorRequest => "CreateBotActorRequest",
        MatchOpcode.RemoveBotActorRequest => "RemoveBotActorRequest",
        MatchOpcode.CreateWorldObject => "CreateWorldObject",
        MatchOpcode.DestroyWorldObject => "DestroyWorldObject",
        MatchOpcode.ReCreateSceneManager => "ReCreateSceneManager",
        MatchOpcode.WorldObjectRpc => "WorldObjectRpc",
        MatchOpcode.WorldObjectState => "WorldObjectState",
        MatchOpcode.CustomEvent => "CustomEvent",
        MatchOpcode.ServerSideError => "ServerSideError",
        _ => $"op{(byte)op}",
    };

    /// <summary>
    /// True for opcodes past JoinRoom that matter for world/load learning.
    /// </summary>
    public static bool IsPostJoinTraffic(MatchOpcode op) =>
        op is MatchOpcode.ActorJoinedEvent
            or MatchOpcode.ActorLeftEvent
            or MatchOpcode.SetProperties
            or MatchOpcode.SetProperty
            or MatchOpcode.SetInternalProperty
            or MatchOpcode.CreateBotActorRequest
            or MatchOpcode.RemoveBotActorRequest
            or MatchOpcode.CreateWorldObject
            or MatchOpcode.DestroyWorldObject
            or MatchOpcode.ReCreateSceneManager
            or MatchOpcode.WorldObjectRpc
            or MatchOpcode.WorldObjectState
            or MatchOpcode.CustomEvent
            or MatchOpcode.FetchServerTimeRequest
            or MatchOpcode.FetchServerTimeResponse
            or MatchOpcode.LeaveRoomRequest
            or MatchOpcode.LeaveRoomResponse
            or MatchOpcode.ServerSideError;

    public static string JoinRoomResultName(JoinRoomResult r) => r switch
    {
        JoinRoomResult.Found => "Found",
        JoinRoomResult.RoomIsFull => "RoomIsFull",
        JoinRoomResult.RoomIsClosed => "RoomIsClosed",
        JoinRoomResult.CreateRoomPluginNotFound => "CreateRoomPluginNotFound",
        JoinRoomResult.RoomWithSameIdAlreadyExists => "RoomWithSameIdAlreadyExists",
        JoinRoomResult.RoomPluginMismatch => "RoomPluginMismatch",
        JoinRoomResult.ActorWithSameUserIdExists => "ActorWithSameUserIdExists",
        JoinRoomResult.RoomNotFound => "RoomNotFound",
        JoinRoomResult.InvalidQuery => "InvalidQuery",
        JoinRoomResult.InvalidCreateOptions => "InvalidCreateOptions",
        JoinRoomResult.KickedByServerPlugin => "KickedByServerPlugin",
        _ => $"fzn={(byte)r}",
    };
}
