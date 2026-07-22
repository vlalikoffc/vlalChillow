using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net.Match.Host;

/// <summary>In-memory LAN match room (password-keyed). Owned by <c>GameMatchHost</c>.</summary>
internal sealed class MatchRoom
{
    public required string PasswordKey { get; init; }
    public required string RoomId { get; init; }
    /// <summary>Next joiner nr; starts at 2 because actor 1 is always synthetic Server.</summary>
    public int NextActorNr { get; set; } = MatchHostActor.ActorNr + 1;
    public readonly HashSet<string> UserIds = new(StringComparer.Ordinal);
    public readonly List<(byte Nr, string Name)> Actors = new();
    public readonly Dictionary<(byte Actor, string Key), LobbyVariant> ActorProps = new();
    public readonly HashSet<byte> SpawnedPawns = new();
    /// <summary>Living fighting pawns keyed by net object id (joiner often allocates ≠129).</summary>
    public readonly Dictionary<short, MatchLivingPawn> LivingPawns = new();
    public byte RoomC2 { get; set; }
    public MatchFlowState Flow { get; } = new();
}
