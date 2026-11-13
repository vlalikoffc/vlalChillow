using StandChillow.LanServer.Net.Lobby;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Issue #35 hot path: match packets are read straight out of LiteNetLib's fresh payload
/// array via <see cref="LobbyReader(byte[], int, int)"/> instead of copying the body.
/// The reader must never see bytes outside the requested window.
/// </summary>
public sealed class LobbyReaderWindowTests
{
    [Fact]
    public void Offset_reader_reads_only_its_window()
    {
        // [0xAA] [len=3 "abc"] [0xBB] — window starts after the first byte.
        var data = new byte[] { 0xAA, 0x03, (byte)'a', (byte)'b', (byte)'c', 0xBB };
        var r = new LobbyReader(data, offset: 1, count: 4);

        Assert.Equal(4, r.Remaining);
        Assert.Equal("abc", r.ReadString());
        Assert.Equal(0, r.Remaining);
        Assert.Throws<InvalidDataException>(() => r.ReadByte());
    }

    [Fact]
    public void Offset_reader_rejects_a_window_outside_the_buffer()
    {
        var data = new byte[4];
        Assert.Throws<ArgumentOutOfRangeException>(() => new LobbyReader(data, 3, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LobbyReader(data, -1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LobbyReader(data, 0, -1));
    }

    [Fact]
    public void Whole_buffer_reader_still_starts_at_zero()
    {
        var r = new LobbyReader(new byte[] { 1, 2, 3 });
        Assert.Equal(3, r.Remaining);
        Assert.Equal(1, r.ReadByte());
        Assert.Equal(2, r.Remaining);
    }
}
