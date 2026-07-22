namespace StandChillow.LanServer.Net.Match.Modes;

/// <summary>
/// Per-<c>GameModeId</c> match driver. Modes must not call into each other —
/// only shared kernel (<c>MatchClock</c>, flow rules, room TX helpers).
/// </summary>
public interface IMatchMode
{
    /// <summary>Wire <c>C0</c> / lobby <c>GameModeId</c> string.</summary>
    string GameModeId { get; }

    /// <summary>False until this mode's host loop is implemented from decompile/gold.</summary>
    bool IsImplemented { get; }

    /// <summary>
    /// Called each host poll when this mode owns the room.
    /// Unimplemented modes must log once and refuse to drive unknown wire.
    /// </summary>
    void Tick(IMatchModeHost host);
}

/// <summary>
/// Narrow host surface modes may use. Expand only with evidenced needs —
/// never invent opcodes here.
/// </summary>
public interface IMatchModeHost
{
    string GameModeId { get; }
    string SelectedLevel { get; }
    void Log(string message);
}
