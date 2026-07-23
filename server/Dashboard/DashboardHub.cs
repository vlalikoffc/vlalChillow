using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Dashboard;

/// <summary>
/// Lightweight one-way event bus from the host (lobby + match) into the CLI dashboard.
/// Display-only: the host raises facts it already tracks (chat lines, victim deaths, round
/// results) and the dashboard renders them. No wire protocol is invented here.
/// </summary>
public static class DashboardHub
{
    /// <summary>A chat line to show in the lobby/match chat area.</summary>
    public sealed record ChatEntry(string Speaker, string Text, bool FromServer, DateTime WhenUtc);

    /// <summary>A victim death fact from host combat handling (killer is best-effort, see below).</summary>
    public sealed record DeathEntry(byte VictimNr, string VictimName, MatchTeam VictimTeam, string Source, DateTime WhenUtc);

    /// <summary>A round / match result to banner (Ranked round end or DeathMatch final).</summary>
    public sealed record RoundResultEntry(
        string Headline, MatchTeam Winner, byte MvpNr, string? MvpName, bool IsFinal, DateTime WhenUtc);

    public static event Action<ChatEntry>? Chat;
    public static event Action<DeathEntry>? Death;
    public static event Action<RoundResultEntry>? RoundResult;

    public static void PostChat(string speaker, string text, bool fromServer = false) =>
        SafeInvoke(() => Chat?.Invoke(new ChatEntry(speaker, text, fromServer, DateTime.UtcNow)));

    public static void PostDeath(byte victimNr, string victimName, MatchTeam victimTeam, string source) =>
        SafeInvoke(() => Death?.Invoke(new DeathEntry(victimNr, victimName, victimTeam, source, DateTime.UtcNow)));

    public static void PostRoundResult(
        string headline, MatchTeam winner, byte mvpNr, string? mvpName, bool isFinal) =>
        SafeInvoke(() => RoundResult?.Invoke(
            new RoundResultEntry(headline, winner, mvpNr, mvpName, isFinal, DateTime.UtcNow)));

    private static void SafeInvoke(Action a)
    {
        // The hub must never take the host down — a broken/absent subscriber is non-fatal.
        try { a(); }
        catch { /* dashboard is display-only */ }
    }
}
