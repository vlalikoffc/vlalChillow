using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net.Match.Host;

// <see cref="MatchHostSettings"/> живёт в StandChillow.LanServer.Net.Match — using выше.

/// <summary>In-memory LAN match room (password-keyed). Owned by <c>GameMatchHost</c>.</summary>
internal sealed class MatchRoom
{
    public required string PasswordKey { get; init; }
    public required string RoomId { get; init; }
    public readonly HashSet<string> UserIds = new(StringComparer.Ordinal);
    public readonly List<(byte Nr, string Name, string UserId)> Actors = new();
    /// <summary>
    /// Issue #39: who owns each actor, by the install-stable identity key
    /// (<see cref="MatchIdentity.StableKeyFrom207Token"/>) instead of the per-session wire uid.
    /// </summary>
    /// <remarks>
    /// Ownership lives in the room, not in the socket: when a phone drops its connection the actor
    /// used to stay behind as an unreachable ghost — the returning player could not be matched to
    /// it (<c>MatchReconnect.FindActorNr</c> only looked at live peer states), so he was given a
    /// <b>second</b> actor while the ghost kept counting as alive in
    /// <see cref="ActorProps"/> team props. Two, then three copies of the same player in one room.
    /// </remarks>
    public readonly Dictionary<byte, string> ActorIdentities = new();
    /// <summary>
    /// When an actor lost its owning peer (Environment.TickCount64), so the reaper can decide that
    /// the session is really gone instead of a reconnect in flight.
    /// </summary>
    public readonly Dictionary<byte, long> OwnerlessSinceTick = new();
    public readonly Dictionary<(byte Actor, string Key), LobbyVariant> ActorProps = new();
    public readonly HashSet<byte> SpawnedPawns = new();
    /// <summary>Living fighting pawns keyed by net object id (joiner often allocates ≠129).</summary>
    public readonly Dictionary<short, MatchLivingPawn> LivingPawns = new();
    /// <summary>
    /// Non-scene Entity world objects created this round (weapon drops, planted-bomb entities,
    /// etc.) keyed by net id → the decoded CreateWorldObject fields this host relayed. Scene
    /// managers (bootstrap ids 1–8) are never tracked. Cleared via host <c>DestroyWorldObject</c>
    /// on RoundEnd / PreStart (phone-host clears leftovers between rounds).
    /// </summary>
    /// <remarks>
    /// Issue #28: keep every decoded field (kind/owner/fus/trailing) so a rejoining peer receives
    /// the same object the others did — no re-guessed fus tag, no lost drop payload. The packet is
    /// rebuilt on replay so its host header carries a fresh server time.
    /// </remarks>
    public readonly Dictionary<short, TrackedRoundEntity> TrackedRoundEntities = new();
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

    // ── per-room state (несколько комнат на одном 7777) ─────────────────────────

    /// <summary>
    /// Номер комнаты в терминах человека (<c>/join 2v2 #3</c>); 0 — legacy-комната <c>Dedik</c>,
    /// созданная не из панели. Номер не переиспользуется (см. <c>RoomRegistry</c>).
    /// </summary>
    public int DisplayNumber { get; init; }

    /// <summary>Название комнаты для панели (в провод не уходит).</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>
    /// Гейт старта <b>этой</b> комнаты. Заменяет глобальный
    /// <see cref="MatchHostSettings.MatchStartArmed"/> для комнат из панели: иначе одно
    /// «/start» запускало бы все комнаты сразу, а конец катка в одной снимал бы armed у другой.
    /// </summary>
    /// <remarks>
    /// <c>false</c> (значение по умолчанию) означает «комната legacy»: решение по старту
    /// принимает глобальная настройка, как было до появления комнат. Это сохраняет прежнее
    /// поведение для <c>/play</c> + <c>/set start</c> и не ломает существующие тесты.
    /// </remarks>
    public bool IsPanelRoom { get; init; }

    /// <summary>Старт разрешён именно для этой комнаты (используется только при <see cref="IsPanelRoom"/>).</summary>
    public bool StartArmed { get; set; }

    /// <summary>
    /// Сколько раундов нужно выиграть в этой комнате (first-to-N). 0 — взять глобальное
    /// значение <see cref="MatchHostSettings.WinsNeeded"/> (legacy-поведение).
    /// </summary>
    public int WinsNeededOverride { get; init; }
}

/// <summary>
/// Issue #28 — a non-pawn world object this host relayed during the round, stored as decoded
/// <see cref="MatchCodec.ParsedCreateWorldObject"/> fields so it can be re-sent to a rejoining
/// peer exactly as other peers saw it.
/// </summary>
internal readonly record struct TrackedRoundEntity(
    MatchCodec.ParsedCreateWorldObject Fields)
{
    public string TypeName => Fields.TypeName;
}
