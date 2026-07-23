namespace StandChillow.Server.Plugins;

/// <summary>Read-only host facts raised to plugins. Never invents wire protocol.</summary>
public interface IPluginEvents
{
    event Action<PlayerLobbyEventArgs>? PlayerJoinedLobby;
    event Action<PlayerLobbyEventArgs>? PlayerLeftLobby;
    event Action<TeamChangeEventArgs>? TeamChanged;
    event Action<MatchLifecycleEventArgs>? MatchStarted;
    event Action<MatchLifecycleEventArgs>? MatchEnded;
    event Action<RoundEndEventArgs>? RoundEnded;
    event Action<DeathEventArgs>? Death;
    event Action<ChatEventArgs>? Chat;
    event Action<PhaseChangeEventArgs>? PhaseChanged;
}

public sealed class PlayerLobbyEventArgs
{
    public required string Name { get; init; }
    public string? Status { get; init; }
}

public sealed class TeamChangeEventArgs
{
    public required string Name { get; init; }
    public byte ActorNr { get; init; }
    public string Team { get; init; } = "";
    public string? PreviousTeam { get; init; }
}

public sealed class MatchLifecycleEventArgs
{
    public required string ModeId { get; init; }
    public required string Map { get; init; }
    public string? Reason { get; init; }
}

public sealed class RoundEndEventArgs
{
    public required string Headline { get; init; }
    public string WinnerTeam { get; init; } = "";
    public byte MvpNr { get; init; }
    public string? MvpName { get; init; }
    public bool IsFinal { get; init; }
    public int ScoreTr { get; init; }
    public int ScoreCt { get; init; }
}

public sealed class DeathEventArgs
{
    public byte VictimNr { get; init; }
    public required string VictimName { get; init; }
    public string VictimTeam { get; init; } = "";
    public string Source { get; init; } = "";
}

public sealed class ChatEventArgs
{
    public required string Speaker { get; init; }
    public required string Text { get; init; }
    public bool FromServer { get; init; }
}

public sealed class PhaseChangeEventArgs
{
    public string? PreviousPhase { get; init; }
    public required string Phase { get; init; }
    public int Round { get; init; }
}
