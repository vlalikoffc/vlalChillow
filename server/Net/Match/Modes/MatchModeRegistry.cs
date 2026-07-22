namespace StandChillow.LanServer.Net.Match.Modes;

/// <summary>Factory for the nine LAN <c>GameModeId</c> drivers (probe run-20260722_213859).</summary>
public static class MatchModeRegistry
{
    private static readonly Dictionary<string, Func<IMatchMode>> Factories =
        new(StringComparer.Ordinal)
        {
            ["Ranked2v2"] = () => new Ranked2v2.Ranked2v2Mode(),
            ["Ranked2v2Alt"] = () => new Ranked2v2Alt.Ranked2v2AltMode(),
            ["RankedDefuse"] = () => new RankedDefuse.RankedDefuseMode(),
            ["Defuse"] = () => new Defuse.DefuseMode(),
            ["Escalation"] = () => new Escalation.EscalationMode(),
            ["DeathMatch"] = () => new DeathMatch.DeathMatchMode(),
            ["ArmsRace"] = () => new ArmsRace.ArmsRaceMode(),
            ["FreeForAll"] = () => new FreeForAll.FreeForAllMode(),
            ["Duel"] = () => new Duel.DuelMode(),
        };

    public static IReadOnlyCollection<string> KnownModeIds => Factories.Keys;

    public static IMatchMode Create(string gameModeId)
    {
        if (Factories.TryGetValue(gameModeId, out var factory))
            return factory();
        return new UnknownMatchMode(gameModeId);
    }
}

/// <summary>Fallback when lobby/room <c>C0</c> is not one of the nine known ids.</summary>
internal sealed class UnknownMatchMode : IMatchMode
{
    private bool _logged;

    public UnknownMatchMode(string gameModeId) => GameModeId = gameModeId;

    public string GameModeId { get; }
    public bool IsImplemented => false;

    public void Tick(IMatchModeHost host)
    {
        if (_logged) return;
        _logged = true;
        host.Log($"[mode] unknown GameModeId='{GameModeId}' — refuse to drive wire");
    }
}
