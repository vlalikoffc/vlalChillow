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
}
