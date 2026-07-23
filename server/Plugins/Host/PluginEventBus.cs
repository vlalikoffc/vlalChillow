using StandChillow.Server.Plugins;

namespace StandChillow.LanServer.Plugins;

/// <summary>Event bus for plugins — never throws into the host.</summary>
internal sealed class PluginEventBus : IPluginEvents
{
    public event Action<PlayerLobbyEventArgs>? PlayerJoinedLobby;
    public event Action<PlayerLobbyEventArgs>? PlayerLeftLobby;
    public event Action<TeamChangeEventArgs>? TeamChanged;
    public event Action<MatchLifecycleEventArgs>? MatchStarted;
    public event Action<MatchLifecycleEventArgs>? MatchEnded;
    public event Action<RoundEndEventArgs>? RoundEnded;
    public event Action<DeathEventArgs>? Death;
    public event Action<ChatEventArgs>? Chat;
    public event Action<PhaseChangeEventArgs>? PhaseChanged;

    public void RaisePlayerJoined(PlayerLobbyEventArgs e) => Safe(PlayerJoinedLobby, e);
    public void RaisePlayerLeft(PlayerLobbyEventArgs e) => Safe(PlayerLeftLobby, e);
    public void RaiseTeamChanged(TeamChangeEventArgs e) => Safe(TeamChanged, e);
    public void RaiseMatchStarted(MatchLifecycleEventArgs e) => Safe(MatchStarted, e);
    public void RaiseMatchEnded(MatchLifecycleEventArgs e) => Safe(MatchEnded, e);
    public void RaiseRoundEnded(RoundEndEventArgs e) => Safe(RoundEnded, e);
    public void RaiseDeath(DeathEventArgs e) => Safe(Death, e);
    public void RaiseChat(ChatEventArgs e) => Safe(Chat, e);
    public void RaisePhaseChanged(PhaseChangeEventArgs e) => Safe(PhaseChanged, e);

    private static void Safe<T>(Action<T>? handlers, T args)
    {
        if (handlers is null) return;
        foreach (Action<T> h in handlers.GetInvocationList())
        {
            try { h(args); }
            catch (Exception ex)
            {
                Console.WriteLine($"[plugins] event handler faulted: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
