using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Сам сервер: ник зарезервирован (копий быть не может), у него свой id, и этот id — не
/// ключ бана. Никаких сокетов: проверяются только правила личности сервера.
/// </summary>
public sealed class ServerIdentityTests
{
    [Theory]
    [InlineData("Server")]
    [InlineData("server")]
    [InlineData("SERVER")]
    [InlineData("  Server  ")]
    [InlineData("SeRvEr")]
    public void ServerNickIsReserved(string nick)
    {
        Assert.True(ServerIdentity.IsServerNick(nick));
    }

    [Theory]
    [InlineData("ServerX")]
    [InlineData("XServer")]
    [InlineData("Ser ver")]
    [InlineData("Сервер")]
    [InlineData("влал")]
    [InlineData("")]
    [InlineData(null)]
    public void NearbyNicksAreNotReserved(string? nick)
    {
        Assert.False(ServerIdentity.IsServerNick(nick));
    }

    [Fact]
    public void ServerNickIsTheOneFromTheLobby()
    {
        // Резерв идёт от того же ника, что и синтетический игрок лобби: переименуют в
        // коде — резерв переедет вместе с ним, дырки «под новым именем» не останется.
        Assert.Equal(LobbySession.ServerPlayerName, ServerIdentity.Nick);
    }

    [Fact]
    public void ServerHasItsOwnDeterministicId()
    {
        Assert.Equal(MatchPublicId.ForNick("Server"), ServerIdentity.Uid);
        Assert.Equal(MatchPublicId.ForNick(ServerIdentity.Nick), ServerIdentity.Uid);
        Assert.InRange(ServerIdentity.Uid, MatchPublicId.Min, MatchPublicId.Max);
        Assert.Equal(ServerIdentity.Uid.ToString(), ServerIdentity.UidText);
    }

    [Fact]
    public void ServerIdIsRecognisedAndNothingElseIs()
    {
        Assert.True(ServerIdentity.IsServerUid(ServerIdentity.Uid));
        Assert.False(ServerIdentity.IsServerUid(0));
        Assert.False(ServerIdentity.IsServerUid(ServerIdentity.Uid + 1));
    }
}
