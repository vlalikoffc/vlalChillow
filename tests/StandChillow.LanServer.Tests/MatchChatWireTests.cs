using System.Buffers.Binary;
using System.Text;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Серверное сообщение в катку. Пользователь: «пишет и показывает в лобби, а вот в катке
/// чёт не показывает, хотя игроки могут слать сообщения». Причина — сервер собирал
/// лобби-кадр OpChat, а в матче чат живёт на ChatManager (id=7) внутри
/// <c>WorldObjectRpc</c>. Здесь зафиксирована форма этого пакета по живым капчурам.
/// </summary>
/// <remarks>
/// Gold (Dedik host → client), все три — настоящий чат в матче:
/// <list type="bullet">
/// <item><c>20261112_040303_23056_match_rx65980_WorldObjectRpc_len38.bin</c> «уебище »</item>
/// <item><c>20261112_040308_23079_match_rx66560_WorldObjectRpc_len36.bin</c> «играй »</item>
/// <item><c>20261112_040310_23085_match_rx66857_WorldObjectRpc_len35.bin</c> «сучка»</item>
/// </list>
/// Общий вид: <c>04 cb &lt;i32 stime&gt; 00000000 0700 01 03 0100 &lt;f64 timeValue&gt; &lt;len&gt;&lt;utf8&gt;</c>.
/// </remarks>
public sealed class MatchChatWireTests
{
    private const int HostHeaderLen = 10; // flags + opcode + i32 serverTime + i32 reserved

    [Fact]
    public void ChatPacket_HasTheHeaderAndChatManagerIdentityFromTheCaptures()
    {
        var packet = MatchCodec.BuildChatManagerText(11221652, "уебище ");

        Assert.Equal(0x04, packet[0]); // flags = HasServerTime
        Assert.Equal((byte)MatchOpcode.WorldObjectRpc, packet[1]);
        Assert.Equal(11221652, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(2, 4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(6, 4)));

        var body = new LobbyReader(packet.AsSpan(HostHeaderLen).ToArray());
        var parsed = MatchCodec.ParseWorldObjectRpcBody(body);

        Assert.Equal(MatchSceneManagers.ChatManagerObjectId, parsed.ObjectId);
        Assert.Equal(1, parsed.RpcId);
        Assert.Equal(3, parsed.GaaTarget); // AllCachedViaServer — доходит до всех, включая отправителя
        Assert.Equal(1, parsed.Field);
        Assert.Equal(11221652 / 1000.0, parsed.TimeValue, 3);
    }

    [Fact]
    public void BuiltPacket_MatchesTheLiveCaptureFieldByField()
    {
        // Золотой пакет (captures.zip/20261112_040310_23085_…len35.bin):
        // 04cb e755ab00 00000000 0700 01 03 0100 e3a59bc450eec540 0a <«сучка» utf-8>.
        // Единственное, что не совпадает побайтово, — double времени: в капчуре
        // timeValue=11228.631 при serverTime=11228647, то есть хост сэмплирует их
        // отдельно (разница ~9 мс). Это не наша выдумка и не повод «подгонять» число.
        var golden = Convert.FromHexString(
            "04CBE755AB0000000000070001030100E3A59BC450EEC5400AD181D183D187D0BAD0B0");
        var rebuilt = MatchCodec.BuildChatManagerText(11228647, "сучка");

        Assert.Equal(golden.Length, rebuilt.Length);
        // Всё до double включительно-исключительно.
        Assert.Equal(golden.AsSpan(0, 16).ToArray(), rebuilt.AsSpan(0, 16).ToArray());
        // Хвост: length byte + текст.
        Assert.Equal(golden.AsSpan(24).ToArray(), rebuilt.AsSpan(24).ToArray());
        Assert.Equal(11228.631, BitConverter.ToDouble(golden.AsSpan(16, 8)), 3);
    }

    [Fact]
    public void ChatPayload_IsSingleLengthByteThenUtf8_ExactlyAsInTheCapture()
    {
        var packet = MatchCodec.BuildChatManagerText(11228647, "сучка");
        var payload = packet.AsSpan(HostHeaderLen + 14).ToArray();

        // В капчуре len=35: 10 (host header) + 14 (rpc body) + 11 (1 + 10 байт UTF-8).
        Assert.Equal(35, packet.Length);
        Assert.Equal(10, payload[0]);
        Assert.Equal("сучка", Encoding.UTF8.GetString(payload.AsSpan(1)));

        Assert.True(MatchCodec.TryParseChatManagerText(payload, out var text));
        Assert.Equal("сучка", text);
    }

    [Theory]
    [InlineData("уебище ", 38)]
    [InlineData("играй ", 36)]
    [InlineData("сучка", 35)]
    public void PacketLengths_MatchTheLiveCaptures(string text, int expectedLength)
    {
        // Тот же serverTime, что в капчуре, — длина зависит только от текста.
        var stime = expectedLength == 38 ? 11221652 : expectedLength == 36 ? 11226248 : 11228647;
        Assert.Equal(expectedLength, MatchCodec.BuildChatManagerText(stime, text).Length);
    }

    [Fact]
    public void LongText_IsTruncatedToTheSingleLengthByte()
    {
        var packet = MatchCodec.BuildChatManagerText(1000, new string('я', 400));
        var payload = packet.AsSpan(HostHeaderLen + 14).ToArray();

        // 255 байт влезают в один байт длины; «я» = 2 байта UTF-8, значит 127 символов.
        Assert.Equal(254, payload[0]);
        Assert.Equal(255, payload.Length);

        // Обрезка не рвёт символ: текст остаётся валидным UTF-8 и читается обратно.
        Assert.True(MatchCodec.TryParseChatManagerText(payload, out var text));
        Assert.All(text, ch => Assert.Equal('я', ch));
    }

    /// <summary>
    /// <c>captures/chat.zip</c> (проба-клиент у телефон-хоста, 2026-11-14): три настоящих
    /// сообщения чата катки. Байт перед <c>gaa</c> — <b>номер актора автора</b>, а не id метода:
    /// «я хост» подписан актором 1, «я клиент» — 2, «ya client» — 3, <c>field</c> у всех 1.
    /// Тот же вывод по gold-набору: у RadarManager <c>field=7</c> с пустым payload ходит со
    /// значениями 1..4 (по 56 кадров), а у пешек байт совпадает с <c>owner</c>.
    /// </summary>
    [Theory]
    [InlineData(
        "04CBFF2B410000000000070001030100F0A7C64B17AFB0400BD18F20D185D0BED181D182",
        1, "я хост")]
    [InlineData(
        "04CBAF404100000000000700020301000000000060B4B0400FD18F20D0BAD0BBD0B8D0B5D0BDD182",
        2, "я клиент")]
    [InlineData(
        "04CBA65F41000000000007000303010079E9263148BCB04009796120636C69656E74",
        3, "ya client")]
    public void CapturedMatchChat_DecodesWithItsAuthorActor(string hex, byte author, string expected)
    {
        var raw = Convert.FromHexString(hex);
        Assert.True(MatchCodec.TryOpenBody(raw, out _, out var op, out _, out var body));
        Assert.Equal(MatchOpcode.WorldObjectRpc, op);

        var parsed = MatchCodec.ParseWorldObjectRpcBody(new LobbyReader(body));
        Assert.Equal(MatchSceneManagers.ChatManagerObjectId, parsed.ObjectId);
        Assert.Equal(author, parsed.RpcId);
        Assert.Equal(3, parsed.GaaTarget); // AllCachedViaServer — эхо уходит и автору
        Assert.Equal(1, parsed.Field);
        Assert.True(MatchCodec.TryParseChatManagerText(parsed.Payload, out var text));
        Assert.Equal(expected, text);
    }

    /// <summary>
    /// Релей чужого чата не переподписывает сообщение: тело эха совпадает с принятым кадром
    /// побайтово (меняется только serverTime в заголовке хоста).
    /// </summary>
    [Fact]
    public void RelayEcho_KeepsTheCapturedBodyByteForByte()
    {
        var raw = Convert.FromHexString(
            "04CBAF404100000000000700020301000000000060B4B0400FD18F20D0BAD0BBD0B8D0B5D0BDD182");
        Assert.True(MatchCodec.TryOpenBody(raw, out _, out _, out _, out var body));
        var parsed = MatchCodec.ParseWorldObjectRpcBody(new LobbyReader(body));

        var echo = MatchCodec.BuildWorldObjectRpc(
            4276999,
            parsed.ObjectId,
            parsed.RpcId,
            parsed.GaaTarget,
            parsed.Field,
            parsed.TimeValue,
            parsed.Payload);

        Assert.Equal(body, echo.AsSpan(HostHeaderLen).ToArray());
    }

    /// <summary>
    /// Серверное сообщение подписано актором хоста: клиент берёт имя автора из этого байта,
    /// поэтому «Server» в ростере комнаты (актор 1) и есть подпись серверного пузыря.
    /// </summary>
    [Fact]
    public void ServerAuthoredChat_IsSignedByTheHostActor()
    {
        var packet = MatchCodec.BuildChatManagerText(4276399, "я клиент");

        // Байт автора стоит сразу за i16 id объекта: 10 (заголовок) + 2 (id) = 12.
        Assert.Equal(MatchHostActor.ActorNr, packet[HostHeaderLen + 2]);
        Assert.Equal(MatchHostActor.ActorNr, MatchCodec.ChatRpcId);
    }

    /// <summary>
    /// Номер актора — ровно один байт: два автора при том же тексте и времени дают кадры,
    /// которые отличаются только им. Ничего другого подпись за собой не тянет.
    /// </summary>
    [Fact]
    public void AuthorByte_IsTheOnlyDifferenceBetweenTwoAuthors()
    {
        var one = MatchCodec.BuildChatManagerText(1000, "привет", authorActorNr: 2);
        var other = MatchCodec.BuildChatManagerText(1000, "привет", authorActorNr: 3);

        Assert.Equal(one.Length, other.Length);
        var diff = Enumerable.Range(0, one.Length).Where(i => one[i] != other[i]).ToArray();
        Assert.Equal(new[] { HostHeaderLen + 2 }, diff);
    }
}
