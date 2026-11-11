using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using Xunit;

namespace StandChillow.LanServer.Tests;

public sealed class ClientVersionTests
{
    [Fact]
    public void Lobby_UsesOnlyThe207VersionTagAndHash()
    {
        Assert.Equal("2.07", LobbyAuth.GameVersion);
        Assert.Equal("2.07", MatchAuth.GameVersion);
        Assert.Equal("7F8A3CB21BF6C31961C634036D0CFAB6", LobbyAuth.VersionHash);
        Assert.False(LobbyAuth.HashEquals(
            "9C08D5D96080EDBF4720031F88424DC3", // 2.06 must be rejected.
            LobbyAuth.VersionHash));
    }

    [Fact]
    public void Lobby_UsesTheObserved207AuthHashAndRejectsGuessedKeys()
    {
        Assert.Equal("4EF9253D5750014EAF9865C31CB13EBA", LobbyAuth.AuthHash);
        Assert.False(LobbyAuth.HashEquals(
            "F2638DBFF86C757AFA70670C11254A67", // Incorrect wassup guess.
            LobbyAuth.AuthHash));
        Assert.False(LobbyAuth.HashEquals(
            "D460C75AC10D98B20D3262A73B1DCC66", // Incorrect projectchillow2 guess.
            LobbyAuth.AuthHash));
    }

    [Fact]
    public void JoinRequest_RoundTripsThe207VersionHash()
    {
        var packet = LobbyCodec.BuildJoinRequest("207-probe");
        Assert.Equal((byte)LobbyOpcode.OpJoinLobbyRequest, packet[0]);

        var request = LobbyCodec.ParseJoinRequest(new LobbyReader(packet.AsSpan(1)));

        Assert.Equal(LobbyAuth.VersionHash, request.VersionHash);
        Assert.Equal(LobbyAuth.AuthHash, request.AuthHash);
        Assert.Equal("207-probe", request.Profile.Name);
    }
}
