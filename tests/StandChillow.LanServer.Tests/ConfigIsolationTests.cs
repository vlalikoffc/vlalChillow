using StandChillow.LanServer.Config;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Изоляция пробы от конфига: <c>config.json</c> — настройки выделенного сервера,
/// и режим <c>--client</c> не должен читать из него ничего.
/// </summary>
/// <remarks>
/// Проверяются инварианты, а не «мы аккуратно написали if»:
/// конфиг нельзя превратить в пробу через <c>launch.args</c>; дефолты совпадают
/// с теми, на которых проба работала до появления конфига; переопределения
/// лобби пустые, пока их не поставит дедик. Глобальные статики не мутируются —
/// иначе параллельные тесты протокола ловят чужие значения.
/// </remarks>
public sealed class ConfigIsolationTests
{
    [Fact]
    public void ProbeDefaults_MatchTheValuesProbeActedOnBeforeConfig()
    {
        var tuning = MatchTuning.Resolve(new TuningConfig());
        Assert.Equal(33, tuning.TickMs);
        Assert.Equal(50, tuning.StateIntervalMs);
        Assert.Equal(256, tuning.ReliableQueueSoftLimit);
        Assert.Equal(4096, tuning.PacketPoolSize);
        Assert.Equal(10_000, tuning.DisconnectTimeoutMs);
        Assert.Equal(15, tuning.NetUpdateTimeMs);
        Assert.Equal(1000, tuning.PingIntervalMs);

        var timings = MatchHostSettings.Resolve(new TimingsConfig());
        Assert.Equal(3, timings.WarmupSeconds);
        Assert.Equal(3, timings.PreStartSeconds);
        Assert.Equal(10, timings.PrepSeconds);
        Assert.Equal(109, timings.RoundSeconds);
        Assert.Equal(6, timings.RoundEndPauseSeconds);
        Assert.Equal(40, timings.BombFuseSeconds);
        Assert.Equal(8, timings.TotalRounds);
        Assert.Equal(800, timings.RoundStartMoney);
    }

    [Fact]
    public void Resolve_TimingsCarriesEveryConfiguredValue()
    {
        var timings = MatchHostSettings.Resolve(new TimingsConfig
        {
            Warmup = 7,
            PreStart = 4,
            Prep = 12,
            RoundTime = 90,
            RoundEndPause = 5,
            BombFuse = 35,
            TotalRounds = 6,
            WinsNeeded = 5,
            RoundStartMoney = 900,
        });

        Assert.Equal(7, timings.WarmupSeconds);
        Assert.Equal(4, timings.PreStartSeconds);
        Assert.Equal(12, timings.PrepSeconds);
        Assert.Equal(90, timings.RoundSeconds);
        Assert.Equal(5, timings.RoundEndPauseSeconds);
        Assert.Equal(35, timings.BombFuseSeconds);
        Assert.Equal(6, timings.TotalRounds);
        Assert.Equal(5, timings.WinsNeeded);
        Assert.True(timings.WinsFromConfig);
        Assert.Equal(900, timings.RoundStartMoney);
    }

    [Fact]
    public void WinsNeededZero_MeansTheModePicksItsOwnTarget()
    {
        // 0 в конфиге — «не трогать дефолт режима» (Allies 8, Defuse 6).
        var timings = MatchHostSettings.Resolve(new TimingsConfig { WinsNeeded = 0 });
        Assert.False(timings.WinsFromConfig);
    }

    [Fact]
    public void Resolve_TimingsClampsGarbageIntoTheLiveRanges()
    {
        var timings = MatchHostSettings.Resolve(new TimingsConfig
        {
            Warmup = 9999,
            RoundTime = 1,
            TotalRounds = 0,
            WinsNeeded = 999,
            BombFuse = 0,
        });

        Assert.Equal(120, timings.WarmupSeconds);
        Assert.Equal(10, timings.RoundSeconds);
        Assert.Equal(1, timings.TotalRounds);
        Assert.Equal(64, timings.WinsNeeded);
        Assert.Equal(5, timings.BombFuseSeconds);
    }

    [Fact]
    public void LobbyOverrides_StartEmpty_SoProbeKeepsBuiltInWelcome()
    {
        // Переопределения ставит только дедик (ApplyRuntimeConfig).
        // Пока их никто не трогал, проба видит ровно старые константы.
        Assert.Equal(0, LobbySession.MaxMembersOverride);
        Assert.Null(LobbySession.WelcomeMessageOverride);
        Assert.Equal((byte)16, LobbySession.DedicatedMaxMembers);
        Assert.Equal("это тест ради нехуй делать", LobbySession.WelcomeMessage);
    }

    [Fact]
    public void LaunchArgs_CannotAskTheServerToBecomeAProbe()
    {
        var cfg = new ServerConfig();
        cfg.Launch.Args.Add("--client");
        cfg.Launch.Args.Add("--verbose");
        cfg.Normalize();

        // Иначе конфиг увёл бы процесс в --client, а проба конфиг не читает.
        Assert.DoesNotContain("--client", cfg.Launch.Args);
        Assert.Contains("--verbose", cfg.Launch.Args);
    }
}
