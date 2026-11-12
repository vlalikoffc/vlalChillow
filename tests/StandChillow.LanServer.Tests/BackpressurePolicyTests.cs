using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Match;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Обратное давление при рассылке: отстающему пиру можно дропать только косметику.
/// Дроп структурного пакета — это десинк мира и «object reference» на клиенте,
/// поэтому это правило зафиксировано тестом.
/// </summary>
public sealed class BackpressurePolicyTests
{
    [Theory]
    [InlineData(MatchOpcode.WorldObjectState)]
    [InlineData(MatchOpcode.WorldObjectRpc)]
    public void CosmeticTraffic_MayBeDroppedForASlowPeer(MatchOpcode op)
    {
        Assert.True(GameMatchHost.IsDroppableWhenBacklogged(op));
    }

    [Theory]
    [InlineData(MatchOpcode.CreateWorldObject)]
    [InlineData(MatchOpcode.DestroyWorldObject)]
    [InlineData(MatchOpcode.SetProperty)]
    [InlineData(MatchOpcode.SetProperties)]
    [InlineData(MatchOpcode.SetInternalProperty)]
    [InlineData(MatchOpcode.ReCreateSceneManager)]
    [InlineData(MatchOpcode.ActorJoinedEvent)]
    [InlineData(MatchOpcode.ActorLeftEvent)]
    [InlineData(MatchOpcode.RoomInitReady)]
    [InlineData(MatchOpcode.CustomEvent)]
    [InlineData(MatchOpcode.HandshakeRequest)]
    [InlineData(MatchOpcode.HandshakeResponse)]
    [InlineData(MatchOpcode.JoinRoomRequest)]
    [InlineData(MatchOpcode.JoinRoomResponse)]
    public void StructuralTraffic_MustNeverBeDropped(MatchOpcode op)
    {
        // Пропуск Destroy/CWO/SetProperties/C2 = клиент навсегда разъезжается по миру.
        Assert.False(GameMatchHost.IsDroppableWhenBacklogged(op));
    }

    /// <summary>
    /// #33: плант/дефьюз/взрыв идут опкодом WorldObjectRpc. Дроп такого пакета отстающему пиру
    /// оставляет его в живом раунде, который у всех уже закончился, — это десинк, а не косметика.
    /// Поэтому у решающих BombManager Rpc есть отдельный, недропаемый путь.
    /// </summary>
    [Fact]
    public void CriticalRpcPath_IsNotSubjectToBackpressure()
    {
        var source = ServerSourceRoot();
        var broadcast = File.ReadAllText(
            Path.Combine(source, "Net", "Match", "Host", "GameMatchHost.Broadcast.cs"));
        var worldObjects = File.ReadAllText(
            Path.Combine(source, "Net", "Match", "Host", "GameMatchHost.WorldObjects.cs"));

        // SendCritical bypasses the droppable check by writing straight through SendCore.
        Assert.Contains("SendCritical(NetPeer peer, byte[] payload) => SendCore", broadcast,
            StringComparison.Ordinal);
        // Plant fan-out and the BombManager relay must use the critical path.
        Assert.Contains("BroadcastInitReadyCritical", broadcast, StringComparison.Ordinal);
        Assert.Contains("isBombManagerRpc", worldObjects, StringComparison.Ordinal);
    }

    private static string ServerSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "server")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "server");
    }
}
