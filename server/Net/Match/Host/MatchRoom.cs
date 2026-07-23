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
    /// <summary>
    /// Non-scene Entity world objects created this round (weapon drops, planted-bomb entities,
    /// etc.) keyed by net id → type name. Scene managers (bootstrap ids 1–8) are never tracked.
    /// Cleared via host <c>DestroyWorldObject</c> on RoundEnd / PreStart (phone-host clears
    /// leftovers between rounds).
    /// </summary>
    public readonly Dictionary<short, string> TrackedRoundEntities = new();
    /// <summary>
    /// DeathMatch combat attribution — last enemy the attacker damaged via a pawn damage
    /// WorldObjectRpc (<c>field=5</c>), keyed by attacker actor nr → (victim actor nr, time).
    /// The dedicated host is the room master: this game's victim client never self-destroys /
    /// self-sets <c>death</c> on the wire (gold + <c>latest.log</c>: victim freezes at 0 HP with
    /// only WorldObjectState until the master authors the death), so the master resolves the
    /// victim from here when the killer confirms the kill (<c>kills</c>++). Damage is per-hit and
    /// non-lethal, so this is only bookkeeping — never a kill on its own.
    /// </summary>
    /// <summary>
    /// Attacker → last damage attribution. <c>WeaponId</c> is decompile <c>gpy</c> when
    /// peeked from the damage payload (0 = unknown → kill reward default).
    /// </summary>
    public readonly Dictionary<byte, (byte Victim, DateTime When, byte WeaponId)> LastCombatDamage = new();
    /// <summary>
    /// DeathMatch de-dup — last time the host master-authored a death for a victim actor nr.
    /// Guards the two combat-death signals (killer <c>kills</c>++ and the Live no-respawn Destroy
    /// fallback) from double-counting the same elimination within a short window.
    /// </summary>
    public readonly Dictionary<byte, DateTime> LastAuthoredDeath = new();
    public byte RoomC2 { get; set; }
    public MatchFlowState Flow { get; } = new();
}
