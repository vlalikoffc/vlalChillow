namespace StandChillow.LanServer.Net.Match;

/// <summary>
/// Mutable dedicated-host knobs — lobby/console <c>/set</c> (and console <c>set</c>).
/// Allies / Ranked2v2: first to <see cref="WinsNeeded"/> round wins (default 8); half-time
/// after round 7. Escalation MR-N: <see cref="IsMatchSeriesOver"/> still caps at
/// <see cref="TotalRounds"/> with wins = N/2+1 when configured via <c>/set round</c>.
/// </summary>
public static class MatchHostSettings
{
    private static readonly object Gate = new();

    private static int _totalRounds = 8;
    private static int _winsNeeded = 8;
    private static int _roundStartMoney = MatchFlowTestParams.DefaultRoundStartMoney;
    private static int _warmupSeconds = 3;
    // C2=22 / _roundStartingTime ≈3s freeze (MATCH_PHASES_TIMERS) — not Prep's 10s.
    private static int _preStartSeconds = 3;
    private static int _prepSeconds = 10;
    private static int _roundSeconds = 90;
    private static int _roundEndPauseSeconds = 6;
    private static int _bombFuseSeconds = 40;
    private static bool _matchStartArmed;
    private static bool _debugMatchChat;

    /// <summary>
    /// When true, dedicated host posts short Server-nick chat lines on decisive match
    /// reactions (plant/defuse/explode, kills, wipe, round end, WarmUp/Live). Default off;
    /// enable via <c>--debug-chat</c> / env <c>DEBUG_MATCH_CHAT</c>.
    /// </summary>
    public static bool DebugMatchChat
    {
        get { lock (Gate) return _debugMatchChat; }
        set { lock (Gate) _debugMatchChat = value; }
    }

    /// <summary>Max rounds in the series (MR-N). Default 8.</summary>
    public static int TotalRounds
    {
        get { lock (Gate) return _totalRounds; }
        set
        {
            if (value < 1) value = 1;
            if (value > 64) value = 64;
            lock (Gate) _totalRounds = value;
        }
    }

    /// <summary>Rounds a team must win (Allies default 8). Overridable via <c>/set wins</c>.</summary>
    public static int WinsNeeded
    {
        get { lock (Gate) return _winsNeeded; }
        set
        {
            if (value < 1) value = 1;
            if (value > 64) value = 64;
            lock (Gate) _winsNeeded = value;
        }
    }

    public static int RoundStartMoney
    {
        get { lock (Gate) return _roundStartMoney; }
        set { lock (Gate) _roundStartMoney = Math.Clamp(value, 0, 16000); }
    }

    public static int WarmupSeconds
    {
        get { lock (Gate) return _warmupSeconds; }
        set { lock (Gate) _warmupSeconds = Math.Clamp(value, 0, 120); }
    }

    public static int PreStartSeconds
    {
        get { lock (Gate) return _preStartSeconds; }
        set { lock (Gate) _preStartSeconds = Math.Clamp(value, 1, 120); }
    }

    public static int PrepSeconds
    {
        get { lock (Gate) return _prepSeconds; }
        set { lock (Gate) _prepSeconds = Math.Clamp(value, 1, 120); }
    }

    public static int RoundSeconds
    {
        get { lock (Gate) return _roundSeconds; }
        set { lock (Gate) _roundSeconds = Math.Clamp(value, 10, 600); }
    }

    public static int RoundEndPauseSeconds
    {
        get { lock (Gate) return _roundEndPauseSeconds; }
        set { lock (Gate) _roundEndPauseSeconds = Math.Clamp(value, 1, 30); }
    }

    public static int BombFuseSeconds
    {
        get { lock (Gate) return _bombFuseSeconds; }
        set { lock (Gate) _bombFuseSeconds = Math.Clamp(value, 5, 120); }
    }

    public static TimeSpan Warmup => TimeSpan.FromSeconds(WarmupSeconds);
    public static TimeSpan PreStart => TimeSpan.FromSeconds(PreStartSeconds);
    public static TimeSpan Prep => TimeSpan.FromSeconds(PrepSeconds);
    public static TimeSpan RoundDuration => TimeSpan.FromSeconds(RoundSeconds);
    public static TimeSpan RoundEndPause => TimeSpan.FromSeconds(RoundEndPauseSeconds);
    public static TimeSpan BombFuse => TimeSpan.FromSeconds(BombFuseSeconds);

    /// <summary>
    /// Manual gate: both teams ≥1 stays in WaitingPlayers / C2=10 until armed via
    /// <c>/set start</c> (or console <c>set start</c> / <c>startmatch</c>). Then
    /// <c>TryBeginMatchFlowIfBothTeams</c> / tick may enter WarmUp.
    /// </summary>
    public static bool MatchStartArmed
    {
        get { lock (Gate) return _matchStartArmed; }
        set { lock (Gate) _matchStartArmed = value; }
    }

    /// <summary>
    /// Allies / Ranked2v2: match over when either side reaches <see cref="WinsNeeded"/>.
    /// No round-count cap — play continues past round 7 half-time until first-to-N.
    /// </summary>
    public static bool IsAlliesMatchOver(int scoreTr, int scoreCt)
    {
        var wins = WinsNeeded;
        return scoreTr >= wins || scoreCt >= wins;
    }

    /// <summary>
    /// Escalation MR-N: match over if a side hit <c>TotalRounds/2+1</c>,
    /// or <paramref name="roundIndex"/> already reached <see cref="TotalRounds"/> (full series /
    /// possible draw).
    /// </summary>
    public static bool IsMatchSeriesOver(int roundIndex, int scoreTr, int scoreCt)
    {
        var wins = TotalRounds / 2 + 1;
        if (scoreTr >= wins || scoreCt >= wins)
            return true;
        return roundIndex >= TotalRounds;
    }

    public static string FormatStatusLine()
    {
        lock (Gate)
        {
            var start = _matchStartArmed
                ? "start=armed"
                : "start=waiting for /set start";
            var debugChat = _debugMatchChat ? "debug-chat=on" : "debug-chat=off";
            return
                $"rounds={_totalRounds} (Escalation MR cap) wins={_winsNeeded} (Allies first-to) " +
                $"money={_roundStartMoney} warmup={_warmupSeconds}s prestart={_preStartSeconds}s " +
                $"prep={_prepSeconds}s round={_roundSeconds}s pause={_roundEndPauseSeconds}s " +
                $"fuse={_bombFuseSeconds}s {start} {debugChat}";
        }
    }

    public static string FormatSetHelp() =>
        """
        /set — параметры дедика:
          /set start         разрешить WarmUp когда обе команды ≥1 (иначе C2=10 ждёт)
                             (то же: console start|startmatch; НЕ /play — /play = rematch teardown)
          /set round <N>     Escalation MR-N max rounds (default 8)
          /set wins <N>      Allies first-to-N wins (default 8)
          /set team <t>      ct|tr|t|spectator — себе в матче (SetProperty team)
          /set money <N>     деньги на старт раунда (0–16000)
          /set fuse <сек>    таймер бомбы
          /set prep <сек>    Allies buy C2=22 / Ranked Prep C2=31
          /set prestart <сек> Ranked PreStart C2=22 (Allies buy uses /set prep)
          /set warmup <сек>  WarmUp C2=21 (первый раунд)
          /set roundtime <сек> длительность live
          /set pause <сек>   пауза после RoundEnd
          /set status        текущие значения
        """.Trim();
}
