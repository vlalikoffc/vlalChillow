using System.Security.Cryptography;
using System.Text;

namespace StandChillow.LanServer.Net.Lobby;

/// <summary>LanLobby opcode enum eub.</summary>
public enum LobbyOpcode : byte
{
    OpJoinLobbyRequest = 0,
    OpChangeSelfPropertyRequest = 1,
    OpSendChatMessageRequest = 2,
    OpJoinLobbyResponse = 3,
    OpLobbyNewMemberEvent = 4,
    OpLobbyMemberLeftEvent = 5,
    OpLobbyMemberPropertyChangedEvent = 6,
    OpLobbyPropertyChangedEvent = 7,
    OpChatMessageEvent = 8,
    OpGameHostingStateChangedEvent = 9,
    OpHaveBeenKickedEvent = 10,
}

/// <summary>Join fail reasons etk.</summary>
public enum JoinFailReason : byte
{
    InvalidGameVersion = 0,
    InvalidAuthKey = 1,
    NotJoinable = 2,
    MaxMembersReached = 3,
    ProfileValidationFailed = 4,
}

public static class LobbyAuth
{
    /// <summary>
    /// Runtime LAN version string (APK 2.06 OBT F1).
    /// eth.cctor still seeds cnuv from literal cnus="1.0.0", but NetcodeSupport.Init
    /// overwrites cnuv via eth.bjqn(cui.balw()) → Build version "2.06".
    /// </summary>
    public const string GameVersion = "2.06";

    /// <summary>
    /// Runtime LAN auth key. eth.cctor seeds cnuw from cnut="wassup", but
    /// NetcodeSupport.Init overwrites cnuw via eth.bjqp("projectchillow2").
    /// </summary>
    public const string AuthKey = "projectchillow2";

    /// <summary>Wire MD5 hex (ese.bjmy uses uppercase X2), live capture match.</summary>
    public static readonly string VersionHash = Md5Hex(GameVersion); // 9C08D5D96080EDBF4720031F88424DC3
    public static readonly string AuthHash = Md5Hex(AuthKey);       // D460C75AC10D98B20D3262A73B1DCC66

    public static string Md5Hex(string s)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    public static bool HashEquals(string? wire, string expected) =>
        string.Equals(wire, expected, StringComparison.OrdinalIgnoreCase);
}

public sealed class LobbyProfile
{
    public string Name { get; init; } = "";
    public byte[]? Avatar { get; init; }
}

public sealed class LobbyMember
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public byte[]? Avatar { get; init; }
    public IReadOnlyList<(string Key, LobbyVariant Value)>? Props { get; init; }
}

public static class LobbyCodec
{
    public static JoinLobbyRequest ParseJoinRequest(LobbyReader r)
    {
        var versionHash = r.ReadString();
        var authHash = r.ReadString();
        var name = r.ReadString();
        var hasAvatar = r.ReadByte() != 0;
        byte[]? avatar = null;
        if (hasAvatar)
        {
            var len = r.ReadInt32();
            avatar = r.ReadBytes(len);
        }
        r.SkipBareProps();
        return new JoinLobbyRequest(versionHash, authHash, new LobbyProfile { Name = name, Avatar = avatar });
    }

    public static string ParseChatRequest(LobbyReader r) => r.ReadString();

    /// <summary>Build OpJoinLobbyRequest from known auth/profile (never from captured bytes).</summary>
    public static byte[] BuildJoinRequest(string profileName, byte[]? avatar = null)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)LobbyOpcode.OpJoinLobbyRequest);
        w.WriteString(LobbyAuth.VersionHash);
        w.WriteString(LobbyAuth.AuthHash);
        w.WriteString(profileName);
        if (avatar is { Length: > 0 })
        {
            w.WriteByte(1);
            w.WriteInt32(avatar.Length);
            w.WriteBytes(avatar);
        }
        else
        {
            w.WriteByte(0);
        }
        w.WriteBareEmptyProps();
        return w.ToArray();
    }

    public static JoinLobbyResponse ParseJoinResponse(LobbyReader r)
    {
        var success = r.ReadBool();
        if (!success)
            return JoinLobbyResponse.Fail((JoinFailReason)r.ReadByte());

        var selfId = r.ReadInt32();
        var snap = ParseLobbySnapshot(r);
        return JoinLobbyResponse.Ok(selfId, snap);
    }

    public static LobbySnapshot ParseLobbySnapshot(LobbyReader r)
    {
        var lobbyName = r.ReadString();
        var count = r.ReadInt32();
        if (count < 0 || count > 64)
            throw new InvalidDataException($"Bad member count {count}");
        var members = new List<LobbyMember>(count);
        for (var i = 0; i < count; i++)
            members.Add(ParseMember(r));
        var maxMembers = r.ReadByte();
        var hasHosting = r.ReadByte() != 0;
        string? hostIp = null;
        short hostPort = 0;
        if (hasHosting)
        {
            hostIp = r.ReadString();
            hostPort = r.ReadInt16();
        }
        var props = r.ReadTypedProps();
        var cnvl = r.ReadInt32();
        return new LobbySnapshot(lobbyName, members, maxMembers, hasHosting, hostIp, hostPort, props, cnvl);
    }

    public static LobbyMember ParseMember(LobbyReader r)
    {
        var id = r.ReadInt32();
        var name = r.ReadString();
        byte[]? avatar = null;
        if (r.ReadByte() != 0)
        {
            var len = r.ReadInt32();
            avatar = r.ReadBytes(len);
        }
        var props = r.ReadTypedProps();
        return new LobbyMember { Id = id, Name = name, Avatar = avatar, Props = props };
    }

    public static (int SenderId, string Message) ParseChatEvent(LobbyReader r)
    {
        var id = r.ReadInt32();
        var msg = r.ReadString();
        return (id, msg);
    }

    public static int ParseMemberLeft(LobbyReader r) => r.ReadInt32();

    /// <summary>
    /// OpLobbyPropertyChangedEvent (etx : etj&lt;etf&gt;).
    /// Wire: u8 kind (etf), u8 hasKey, [string key], fzp value.
    /// </summary>
    public static LobbyPropertyChanged ParsePropertyChanged(LobbyReader r)
    {
        var kind = (LobbyPropKind)r.ReadByte();
        var hasKey = r.ReadByte() != 0;
        string? key = null;
        if (hasKey)
            key = r.ReadString();
        var value = r.ReadFzp();
        return new LobbyPropertyChanged(kind, key, value);
    }

    /// <summary>
    /// OpGameHostingStateChangedEvent (etr → esz).
    /// Wire: u8 hasHosting; if set: string ip, i16 port (LE). Live: port 7777.
    /// </summary>
    public static GameHostingState ParseGameHostingState(LobbyReader r)
    {
        var has = r.ReadByte() != 0;
        if (!has)
            return GameHostingState.None;
        var ip = r.ReadString();
        var port = r.ReadInt16();
        return new GameHostingState(true, ip, unchecked((ushort)port));
    }

    public static byte[] BuildJoinSuccess(
        int selfMemberId,
        string lobbyName,
        byte maxMembers,
        IReadOnlyList<LobbyMember> members,
        IReadOnlyList<(string Key, LobbyVariant Value)>? lobbyProps = null,
        string? hostingIp = null,
        ushort hostingPort = 0)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)LobbyOpcode.OpJoinLobbyResponse);
        w.WriteBool(true);
        w.WriteInt32(selfMemberId);
        WriteLobbySnapshot(w, lobbyName, maxMembers, members, lobbyProps, hostingIp, hostingPort);
        return w.ToArray();
    }

    public static byte[] BuildJoinFail(JoinFailReason reason)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)LobbyOpcode.OpJoinLobbyResponse);
        w.WriteBool(false);
        w.WriteByte((byte)reason);
        return w.ToArray();
    }

    public static byte[] BuildChatEvent(int senderMemberId, string message)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)LobbyOpcode.OpChatMessageEvent);
        w.WriteInt32(senderMemberId);
        w.WriteString(message);
        return w.ToArray();
    }

    public static byte[] BuildMemberLeft(int memberId)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)LobbyOpcode.OpLobbyMemberLeftEvent);
        w.WriteInt32(memberId);
        return w.ToArray();
    }

    public static byte[] BuildNewMember(LobbyMember member)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)LobbyOpcode.OpLobbyNewMemberEvent);
        WriteMember(w, member);
        return w.ToArray();
    }

    public static byte[] BuildPropertyChanged(LobbyPropKind kind, string? key, LobbyVariant value)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)LobbyOpcode.OpLobbyPropertyChangedEvent);
        w.WriteByte((byte)kind);
        var hasKey = !string.IsNullOrWhiteSpace(key);
        w.WriteByte(hasKey ? (byte)1 : (byte)0);
        if (hasKey)
            w.WriteString(key);
        w.WriteFzp(value);
        return w.ToArray();
    }

    public static byte[] BuildCustomProperty(string key, LobbyVariant value) =>
        BuildPropertyChanged(LobbyPropKind.CustomProperty, key, value);

    public static byte[] BuildCustomProperties(IReadOnlyList<(string Key, LobbyVariant Value)> props) =>
        BuildPropertyChanged(LobbyPropKind.CustomProperties, key: null, LobbyVariant.FromProps(props));

    public static byte[] BuildGameHostingState(string? ip, ushort port)
    {
        var w = new LobbyWriter();
        w.WriteByte((byte)LobbyOpcode.OpGameHostingStateChangedEvent);
        if (string.IsNullOrWhiteSpace(ip))
        {
            w.WriteByte(0);
        }
        else
        {
            w.WriteByte(1);
            w.WriteString(ip);
            w.WriteInt16(unchecked((short)port));
        }
        return w.ToArray();
    }

    private static void WriteLobbySnapshot(
        LobbyWriter w,
        string lobbyName,
        byte maxMembers,
        IReadOnlyList<LobbyMember> members,
        IReadOnlyList<(string Key, LobbyVariant Value)>? lobbyProps,
        string? hostingIp = null,
        ushort hostingPort = 0)
    {
        w.WriteString(lobbyName);
        w.WriteInt32(members.Count);
        foreach (var m in members)
            WriteMember(w, m);
        w.WriteByte(maxMembers);
        // etm.hasHosting + esz (ip, i16 port). Live pre-match JoinResponse = 0.
        // Mid-match late join: set true so joiner sees embedded match endpoint (same shape as op9).
        if (!string.IsNullOrWhiteSpace(hostingIp) && hostingPort != 0)
        {
            w.WriteByte(1);
            w.WriteString(hostingIp);
            w.WriteInt16(unchecked((short)hostingPort));
        }
        else
            w.WriteByte(0);
        if (lobbyProps is { Count: > 0 })
            w.WriteTypedProps(lobbyProps);
        else
            w.WriteTypedEmptyProps();
        w.WriteInt32(0); // etm.cnvl
    }

    private static void WriteMember(LobbyWriter w, LobbyMember m)
    {
        w.WriteInt32(m.Id);
        w.WriteString(m.Name);
        if (m.Avatar is { Length: > 0 } av)
        {
            w.WriteByte(1);
            w.WriteInt32(av.Length);
            w.WriteBytes(av);
        }
        else
        {
            w.WriteByte(0);
        }
        if (m.Props is { Count: > 0 } props)
            w.WriteTypedProps(props);
        else
            w.WriteTypedEmptyProps();
    }
}

public readonly record struct JoinLobbyRequest(string VersionHash, string AuthHash, LobbyProfile Profile);

public sealed record LobbySnapshot(
    string LobbyName,
    IReadOnlyList<LobbyMember> Members,
    byte MaxMembers,
    bool HasHosting,
    string? HostIp,
    short HostPort,
    IReadOnlyList<(string Key, LobbyVariant Value)> Props,
    int Cnvl);

public readonly record struct LobbyPropertyChanged(LobbyPropKind Kind, string? Key, LobbyVariant Value);

public readonly record struct GameHostingState(bool HasHosting, string? Ip, ushort Port)
{
    public static GameHostingState None => new(false, null, 0);

    public override string ToString() =>
        HasHosting ? $"{Ip}:{Port}" : "(none)";
}

public readonly struct JoinLobbyResponse
{
    public bool Success { get; init; }
    public JoinFailReason? FailReason { get; init; }
    public int SelfMemberId { get; init; }
    public LobbySnapshot? Snapshot { get; init; }

    public static JoinLobbyResponse Ok(int selfId, LobbySnapshot snap) => new()
    {
        Success = true,
        SelfMemberId = selfId,
        Snapshot = snap,
    };

    public static JoinLobbyResponse Fail(JoinFailReason reason) => new()
    {
        Success = false,
        FailReason = reason,
    };
}
