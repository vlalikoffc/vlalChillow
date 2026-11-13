using StandChillow.LanServer.Diagnostics;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Issue #35: <c>[health]</c> must answer «сервер тормозит или железо» — tick cost,
/// RX rate/opcode mix, GC pressure. The counters are process-wide, so the test class
/// owns them exclusively via <see cref="MatchPerf.ResetForTests"/>.
/// </summary>
[Collection("match-perf")]
public sealed class MatchPerfTests
{
    [Fact]
    public void Window_reports_tick_cost_and_rx_mix()
    {
        MatchPerf.ResetForTests();
        MatchPerf.SlowTickMs = 66;

        for (var i = 0; i < 10; i++)
            MatchPerf.RecordTick(totalMs: 2.0, pollMs: 1.0, flowMs: 0.5);
        MatchPerf.RecordRx(opcode: 204, bytes: 60);
        MatchPerf.RecordRx(opcode: 204, bytes: 60);
        MatchPerf.RecordRx(opcode: 203, bytes: 40);

        var line = MatchPerf.DescribeWindow(op => $"op{op}");

        Assert.Contains("tick avg=2.0ms", line);
        Assert.Contains("max=2.0ms", line);
        Assert.Contains("poll=1.0ms", line);
        Assert.Contains("flow=0.5ms", line);
        Assert.Contains("rx=3", line);
        Assert.Contains("op204:2", line);
        Assert.Contains("op203:1", line);
        Assert.Contains("gc0=", line);
        Assert.Contains("alloc=", line);
    }

    [Fact]
    public void Window_is_a_delta_since_the_previous_snapshot()
    {
        MatchPerf.ResetForTests();
        MatchPerf.RecordTick(totalMs: 3.0, pollMs: 1.0, flowMs: 1.0);
        MatchPerf.RecordRx(opcode: 204, bytes: 10);

        var first = MatchPerf.DescribeWindow(op => $"op{op}");
        Assert.Contains("rx=1", first);
        Assert.Contains("tick avg=3.0ms", first);

        var second = MatchPerf.DescribeWindow(op => $"op{op}");
        Assert.Contains("rx=0", second);
        Assert.Contains("tick avg=0.0ms", second);
        Assert.Contains("op=none", second);
    }

    [Fact]
    public void Slow_tick_is_flagged_against_the_budget()
    {
        MatchPerf.ResetForTests();
        MatchPerf.SlowTickMs = 66;

        Assert.False(MatchPerf.IsSlowTick(33.0));
        Assert.True(MatchPerf.IsSlowTick(67.0));

        MatchPerf.RecordTick(totalMs: 120.0, pollMs: 100.0, flowMs: 5.0);
        Assert.Equal(1L, MatchPerf.SlowTicks);
    }
}
