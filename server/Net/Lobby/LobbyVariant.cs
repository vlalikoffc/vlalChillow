namespace StandChillow.LanServer.Net.Lobby;

/// <summary>Decoded fzp/fzq variant used in lobby property bags (live capture ground truth).</summary>
public enum LobbyVariantKind : byte
{
    Null,
    Bool,
    Int,
    Byte,
    String,
    StringArray,
    Properties,
    /// <summary>fzu.ByteArray — match actor <c>avatar</c> JPEG (phone-host capture).</summary>
    ByteArray,
    /// <summary>fzq.Double=5 — room <c>Time</c> / <c>RoundStartTime</c> (<c>bbs.omr</c>/<c>omv</c>).</summary>
    Double,
}

public readonly struct LobbyVariant
{
    public LobbyVariantKind Kind { get; private init; }
    public bool Bool { get; private init; }
    public int Int { get; private init; }
    public byte Byte { get; private init; }
    public double Double { get; private init; }
    public string? String { get; private init; }
    public IReadOnlyList<string>? Strings { get; private init; }
    public IReadOnlyList<(string Key, LobbyVariant Value)>? Props { get; private init; }
    public byte[]? Bytes { get; private init; }

    public static LobbyVariant Null() => new() { Kind = LobbyVariantKind.Null };
    public static LobbyVariant FromBool(bool v) => new() { Kind = LobbyVariantKind.Bool, Bool = v };
    public static LobbyVariant FromInt(int v) => new() { Kind = LobbyVariantKind.Int, Int = v };
    public static LobbyVariant FromByte(byte v) => new() { Kind = LobbyVariantKind.Byte, Byte = v };
    public static LobbyVariant FromDouble(double v) => new() { Kind = LobbyVariantKind.Double, Double = v };
    public static LobbyVariant FromString(string v) => new() { Kind = LobbyVariantKind.String, String = v };
    public static LobbyVariant FromStrings(IReadOnlyList<string> v) =>
        new() { Kind = LobbyVariantKind.StringArray, Strings = v };
    public static LobbyVariant FromProps(IReadOnlyList<(string Key, LobbyVariant Value)> v) =>
        new() { Kind = LobbyVariantKind.Properties, Props = v };
    public static LobbyVariant FromBytes(byte[] v) => new() { Kind = LobbyVariantKind.ByteArray, Bytes = v };

    public override string ToString() => Kind switch
    {
        LobbyVariantKind.Bool => Bool ? "true" : "false",
        LobbyVariantKind.Int => Int.ToString(),
        LobbyVariantKind.Byte => $"b{Byte}",
        LobbyVariantKind.Double => Double.ToString("G"),
        LobbyVariantKind.String => $"'{String}'",
        LobbyVariantKind.StringArray => $"[{string.Join(", ", Strings ?? Array.Empty<string>())}]",
        LobbyVariantKind.Properties =>
            "{" + string.Join(", ", (Props ?? Array.Empty<(string, LobbyVariant)>()).Select(p => $"{p.Key}={p.Value}")) + "}",
        LobbyVariantKind.ByteArray => $"bytes[{Bytes?.Length ?? 0}]",
        _ => "null",
    };
}

/// <summary>etf — lobby property-change kind on OpLobbyPropertyChangedEvent (etx).</summary>
public enum LobbyPropKind : byte
{
    Name = 0,
    MaxMembers = 1,
    Game = 2,
    CustomProperty = 3,
    CustomProperties = 255,
}

/// <summary>Known lobby custom property keys (Client.bla).</summary>
public static class LobbyPropKeys
{
    public const string LobbyId = "LobbyId";
    public const string GameModeId = "GameModeId";
    public const string SelectedLevels = "SelectedLevels";
    public const string SearchingStarted = "SearchingStarted";
    public const string DefaultAvatarId = "DefaultAvatarId";

    /// <summary>LanLobbyHelper.cbtc — default mode on real phone JoinResponse.</summary>
    public const string PhoneDefaultGameModeId = "DeathMatch";

    /// <summary>Dedicated host default — live capture «союзники» / Ranked2v2.</summary>
    public const string DefaultGameModeId = "Ranked2v2";

    /// <summary>Dedicated host default SelectedLevels entry — live capture Sandstone 2x2.</summary>
    public const string DefaultSelectedLevel = "Sandstone 2x2";
}
