using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Чат катки — структурный трафик, а не косметика. Отчёт: «сервер видит команды, но не пишет
/// ничего в ответ в чат матча … свои сообщения вовсе не шлёт». Единственный канал чата в матче —
/// <c>WorldObjectRpc</c> на ChatManager (в <c>captures/chat.zip</c> на id=7 нет ни State, ни
/// SetProperty), а этот опкод хост дропал отстающему пиру: сообщение терялось молча.
/// </summary>
public sealed class MatchChatDeliveryTests
{
    [Fact]
    public void ChatManagerRpc_IsStructural_AndNeverDroppable()
    {
        Assert.True(MatchChatDelivery.IsChatRpc(MatchSceneManagers.ChatManagerObjectId));
        Assert.True(MatchChatDelivery.IsStructuralRpc(MatchSceneManagers.ChatManagerObjectId));
    }

    /// <summary>#33: plant/defuse/explode тем же опкодом — уже были на недропаемом пути.</summary>
    [Fact]
    public void BombManagerRpc_StaysStructural()
    {
        Assert.False(MatchChatDelivery.IsChatRpc(MatchFlowTestParams.BombManagerObjectId));
        Assert.True(MatchChatDelivery.IsStructuralRpc(MatchFlowTestParams.BombManagerObjectId));
    }

    [Theory]
    [InlineData(MatchSceneManagers.RadarManagerObjectId)]
    [InlineData(4)]   // WeaponDropManager — поток дропов
    [InlineData(5)]   // GrenadeManager
    [InlineData(129)] // пешка: позы устаревают на следующем кадре
    public void EverythingElse_StaysDroppableCosmetic(int objectId)
    {
        Assert.False(MatchChatDelivery.IsChatRpc((short)objectId));
        Assert.False(MatchChatDelivery.IsStructuralRpc((short)objectId));
    }

    /// <summary>
    /// «Никто не получил» должно называть причину: иначе peers=0 не отличить от «сервер
    /// ничего не отправил», а это ровно та неопределённость, из которой родился отчёт.
    /// </summary>
    [Fact]
    public void NoTarget_ReportsWhichOfTheThreeReasonsItIs()
    {
        Assert.Contains("нет пиров", MatchChatDelivery.DescribeNoTarget(0, 0, 0));
        Assert.Contains("world-gate", MatchChatDelivery.DescribeNoTarget(2, 0, 2));
        Assert.Contains("bootstrap", MatchChatDelivery.DescribeNoTarget(2, 0, 0));
        Assert.Contains("не совпал", MatchChatDelivery.DescribeNoTarget(2, 2, 0));
    }

    /// <summary>
    /// Серверный ответ и релей чужого чата обязаны идти недропаемым путём, а релей —
    /// сохранять байт автора (иначе сообщение придёт подписанным чужим актором).
    /// </summary>
    [Fact]
    public void ServerChatAndRelay_AreWiredToTheCriticalPath()
    {
        var root = ServerSourceRoot();
        var host = File.ReadAllText(Path.Combine(root, "Net", "GameMatchHost.cs"));
        var worldObjects = File.ReadAllText(Path.Combine(
            root, "Net", "Match", "Host", "GameMatchHost.WorldObjects.cs"));

        // Широкая рассылка (ник не задан) — критичным бордкастом, иначе её дропает бэкпрешер.
        Assert.Contains("BroadcastInitReadyCritical(", host, StringComparison.Ordinal);
        Assert.Contains("CountChatTargets(room)", host, StringComparison.Ordinal);
        // Адресный ответ и last-chance — тоже мимо дропа.
        Assert.Contains("SendCritical(p, payload)", host, StringComparison.Ordinal);
        Assert.Contains("SendCritical(final[0].Peer, payload)", host, StringComparison.Ordinal);
        // Релей чужого чата: решение о критичности — из той же политики, автор байтом как пришёл.
        Assert.Contains("MatchChatDelivery.IsStructuralRpc(parsed.ObjectId)", worldObjects, StringComparison.Ordinal);
        Assert.Contains("parsed.RpcId,", worldObjects, StringComparison.Ordinal);
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
