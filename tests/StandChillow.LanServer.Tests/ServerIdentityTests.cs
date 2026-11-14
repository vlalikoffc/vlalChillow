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
    public void ServerIdIsTheHardcodedString()
    {
        // «У того, кто сервер, id захардкожен Server»: это не число из нумерации игроков.
        Assert.Equal("Server", ServerIdentity.Uid);
        Assert.False(int.TryParse(ServerIdentity.Uid, out _));
    }

    [Fact]
    public void ServerIdIsRecognisedAndNothingElseIs()
    {
        Assert.True(ServerIdentity.IsServerUid("Server"));
        Assert.True(ServerIdentity.IsServerUid("server"));
        Assert.False(ServerIdentity.IsServerUid("Server2"));
        Assert.False(ServerIdentity.IsServerUid("39772292"));
        Assert.False(ServerIdentity.IsServerUid(""));
        Assert.False(ServerIdentity.IsServerUid(null));
    }

    [Fact]
    public void NumericHashOfTheWordServerIsAnOrdinaryBannablePlayerId()
    {
        // Число, которое даёт хеш слова «Server», — обычный id игрока: его можно банить,
        // и по нему видно, что это не сервер, а тот, кто так назвался. Сервер отличим
        // только по строке «Server», и банятся они по-разному.
        var numeric = MatchPublicId.ForNick("Server");
        Assert.InRange(numeric, MatchPublicId.Min, MatchPublicId.Max);
        Assert.False(ServerIdentity.IsServerUid(numeric.ToString()));

        Assert.True(MatchPublicId.TryWireUid("Server", out var wireUid));
        Assert.False(ServerIdentity.IsServerUid(wireUid));
        Assert.True(ServerIdentity.IsServerUid(ServerIdentity.Uid));
    }

    [Fact]
    public void PlayersNumberingStaysSeparateFromTheServerId()
    {
        // Игроки нумеруются 1…100 000 000; сервер — строка «Server», в диапазон не входит.
        var playerId = MatchPublicId.ForNick("Ktulxy");
        Assert.InRange(playerId, MatchPublicId.Min, MatchPublicId.Max);
        Assert.NotEqual(ServerIdentity.Uid, playerId.ToString());

        Assert.True(MatchPublicId.TryWireUid("Ktulxy", out var wireUid));
        Assert.False(ServerIdentity.IsServerUid(wireUid));
    }
}
