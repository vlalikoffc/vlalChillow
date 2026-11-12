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
}
