namespace StandChillow.Server.Plugins;

/// <summary>
/// Console or in-game slash command (name without leading <c>/</c>).
/// Return <c>true</c> if handled.
/// </summary>
public delegate bool PluginCommandHandler(PluginCommandContext ctx);

public sealed class PluginCommandContext
{
    public required string Command { get; init; }
    /// <summary>Remainder after the command word (may be empty).</summary>
    public required string Args { get; init; }
    /// <summary>Speaker display name; <c>console</c> for stdin/dashboard.</summary>
    public required string Speaker { get; init; }
    public bool FromConsole { get; init; }
    public required IPluginLog Log { get; init; }
    /// <summary>Reply to console notice / server whisper path (best-effort).</summary>
    public required Action<string> Reply { get; init; }
}
