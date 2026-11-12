using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Кто написал <c>/команду</c> в катке. Ник — основной ключ (как и в админке).
/// IP — только если он однозначен: в логе пользователя несколько телефонов сидят на
/// <c>192.168.1.65</c>, и «первый попавшийся по IP» подменял автора, из-за чего
/// админ-гейт отклонял команду.
/// </summary>
public sealed class MatchChatAuthorResolverTests
{
    private static ConnectedPlayer Player(string name) => new()
    {
        Peer = null!,
        ServerMemberId = name.Length,
        Name = name,
        Joined = true,
    };

    [Fact]
    public void NickWinsOverIp()
    {
        var влал = Player("влал");
        var влал2 = Player("влал2");

        // Оба «с одного IP» — как в реальном логе (NAT/Radmin).
        var hit = MatchChatAuthorResolver.Resolve(
            "влал", new[] { влал, влал2 }, new[] { влал, влал2 }, out var reason);

        Assert.NotNull(hit);
        Assert.Same(влал, hit!.Value.Player);
        Assert.Equal("nick", hit.Value.ResolvedBy);
        Assert.Equal("", reason);
    }

    [Fact]
    public void AmbiguousIp_IsNeverGuessedSilently()
    {
        var a = Player("влал");
        var b = Player("влал2");

        var hit = MatchChatAuthorResolver.Resolve(
            "неизвестный", new[] { a, b }, new[] { a, b }, out var reason);

        // Раньше вернулся бы «первый по IP» — и команда ушла бы не тому.
        Assert.Null(hit);
        Assert.Contains("несколько", reason);
    }

    [Fact]
    public void UniqueIp_IsUsedWhenTheNickDoesNotMatch()
    {
        var a = Player("влал");
        var b = Player("влал2");

        var hit = MatchChatAuthorResolver.Resolve(
            "обрезанный ник", new[] { a, b }, new[] { b }, out _);

        Assert.NotNull(hit);
        Assert.Same(b, hit!.Value.Player);
        Assert.Equal("ip", hit.Value.ResolvedBy);
    }

    [Fact]
    public void SingleJoinedPlayer_IsUsedAsLastResort()
    {
        var solo = Player("влал");

        var hit = MatchChatAuthorResolver.Resolve("", new[] { solo }, Array.Empty<ConnectedPlayer>(), out _);

        Assert.NotNull(hit);
        Assert.Same(solo, hit!.Value.Player);
        Assert.Equal("only-player", hit.Value.ResolvedBy);
    }

    [Fact]
    public void NobodyInLobby_ReportsTheReason()
    {
        var hit = MatchChatAuthorResolver.Resolve(
            "влал", Array.Empty<ConnectedPlayer>(), Array.Empty<ConnectedPlayer>(), out var reason);

        Assert.Null(hit);
        Assert.NotEqual("", reason);
    }

    [Fact]
    public void EmptyActorNameWithSeveralPlayers_ReportsTheReason()
    {
        var hit = MatchChatAuthorResolver.Resolve(
            "", new[] { Player("a"), Player("b") }, Array.Empty<ConnectedPlayer>(), out var reason);

        Assert.Null(hit);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void VisuallyIdenticalNicks_ResolveDespiteInvisibleCharacters()
    {
        var влал = Player("влал");

        var hit = MatchChatAuthorResolver.Resolve(
            "\u200Bвлал\u00AD", new[] { влал }, Array.Empty<ConnectedPlayer>(), out _);

        Assert.NotNull(hit);
        Assert.Same(влал, hit!.Value.Player);
    }
}
