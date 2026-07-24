namespace StandChillow.LanServer.Net.Match.Modes.Duel;

/// <summary>
/// Duel forced loadouts + round modifiers from <c>DuelConfig</c> asset dump
/// (<c>decompiled/DUEL_MODIFIERS_LOADOUTS.md</c> / <c>duel_modifiers_loadouts.json</c>).
/// Wire bags: <c>current_loadout</c> / <c>current_round_modifier_id</c> /
/// <c>used_round_modifier_ids</c> — gold <c>MATCH_DUEL_PROBE.md</c>.
/// </summary>
public static class DuelLoadouts
{
    /// <summary>
    /// Modifier chance per scored round: Bernoulli p = 1/3.
    /// Matches DuelConfig <c>_roundsBetweenModifiers=3</c> on average (~one modifier every
    /// three rounds) without a fixed every-3rd schedule (probe rounds 3/6/9 are consistent
    /// with that mean, not proof of a deterministic stride).
    /// </summary>
    public const double ModifierChancePerRound = 1.0 / 3.0;

    /// <summary>Nested <c>current_loadout</c> PropertiesRecord keys (probe gold).</summary>
    public static class LoadoutKeys
    {
        public const string PrimaryWeapon = "PrimaryWeapon";
        public const string SecondaryWeapon = "SecondaryWeapon";
        public const string OtherSlots = "OtherSlots";
    }

    public readonly record struct Loadout(
        string Name,
        byte PrimaryWeapon,
        byte SecondaryWeapon,
        byte[] OtherSlots);

    /// <summary>
    /// Round modifier definition. <see cref="Enabled"/> gates RNG only —
    /// disabled modifiers stay fully defined (loadout + semantics) for later flip-on.
    /// </summary>
    public sealed class ModifierDefinition
    {
        public required string WireId { get; init; }
        public required Loadout Loadout { get; init; }
        /// <summary>When false, excluded from random pick (OnlyGrenades until host grenades work).</summary>
        public required bool Enabled { get; init; }
        /// <summary>
        /// OnlyGrenades: infinite HE — on throw return instantly / do not despawn the thrown
        /// HE; WeaponDrop of a grenade still removes it. Host grenade throw path not wired yet;
        /// <see cref="MatchFlowState.DuelInfiniteHeGrenades"/> marks the round when this fires.
        /// </summary>
        public bool InfiniteHeGrenades { get; init; }
        /// <summary>
        /// Knife rounds: loadout has no Knife* gpy — client always grants knife by default;
        /// host TX is Flash/HE/Molotov/Smoke OtherSlots only (via modifier id; gold omits
        /// <c>current_loadout</c> on modifier C2=22 bags).
        /// </summary>
        public bool ClientDefaultKnife { get; init; }
    }

    public readonly record struct RoundPick(
        bool HasModifier,
        ModifierDefinition? Modifier,
        Loadout ActiveLoadout);

    /// <summary>
    /// 23 <c>DuelConfig._loadouts</c> presets (gpy ids). OtherSlots matches dump;
    /// probe wire sends a single <c>0</c> when the SO has OtherSlots=None.
    /// </summary>
    public static readonly Loadout[] Presets =
    [
        new("Loadout1", 44, 15, [91]),           // AKR / Deagle / HE
        new("Loadout2", 15, 0, [94]),            // Deagle / Molotov
        new("Loadout3", 42, 12, [0]),            // VAL / USP
        new("Loadout5", 49, 12, [92]),           // FnFal / USP / Smoke
        new("Loadout6", 35, 11, [95]),           // P90 / G22 / Incendiary
        new("Loadout7", 51, 11, [92]),           // AWM / G22 / Smoke
        new("Loadout8", 18, 0, [94]),            // Berettas / Molotov
        new("Loadout9", 46, 12, [95]),           // M4 / USP / Incendiary
        new("Loadout10", 45, 11, [0]),           // AKR12 / G22
        new("Loadout11", 63, 11, [0]),           // FabM / G22
        new("Loadout12", 12, 0, [91, 92, 94]),   // USP / HE+Smoke+Molotov
        new("Loadout13", 34, 0, [95]),           // MP7 / Incendiary
        new("Loadout14", 47, 12, [0]),           // M16 / USP
        new("Loadout16", 43, 12, [95]),          // M4A1 / USP / Incendiary
        new("Loadout17", 36, 13, [91, 94]),      // MP5 / P350 / HE+Molotov
        new("Loadout18", 48, 12, [95, 91]),      // FAMAS / USP / Incendiary+HE
        new("Loadout19", 42, 12, [95, 91]),      // VAL / USP / Incendiary+HE
        new("Loadout20", 52, 12, [93, 91]),      // M40 / USP / Flash+HE
        new("Loadout21", 65, 11, [92]),          // SPAS / G22 / Smoke
        new("Loadout22", 64, 11, [92, 94]),      // M60 / G22 / Smoke+Molotov
        new("Loadout23", 11, 0, [91]),           // G22 / HE
        new("Loadout24", 33, 0, [91]),           // AkimboUzi / HE
        new("Loadout25", 54, 0, [92]),           // Mallard / Smoke
    ];

    /// <summary>
    /// Four <c>DuelConfig._roundModifiers</c> in asset order. OnlyGrenades is fully defined
    /// but <see cref="ModifierDefinition.Enabled"/> = false until dedicated grenades work.
    /// </summary>
    public static readonly ModifierDefinition[] Modifiers =
    [
        new()
        {
            WireId = "OnlyGrenadesModifier",
            Loadout = new("OnlyGrenadesLoadout", 0, 0, [91]), // HE only
            Enabled = false, // gated off — flip true when GrenadeManager throw-return exists
            InfiniteHeGrenades = true,
        },
        new()
        {
            WireId = "OnlyHeadshotsModifier",
            Loadout = new("OnlyHeadShotsLoadout", 44, 11, [0]), // AKR / G22; hit filter is client-side
            Enabled = true,
        },
        new()
        {
            WireId = "OnlyKnifesModifier",
            Loadout = new("OnlyKnivesLoadout", 0, 0, [93, 91, 94, 92]), // Flash/HE/Molotov/Smoke
            Enabled = true,
            ClientDefaultKnife = true,
        },
        new()
        {
            WireId = "NoScopeModifier",
            Loadout = new("NoScopeLoadout", 51, 0, [0]), // AWM; no-scope is client-side
            Enabled = true,
        },
    ];

    public static IReadOnlyList<ModifierDefinition> EnabledModifiers { get; } =
        Modifiers.Where(m => m.Enabled).ToArray();

    public static Loadout PickRandomPreset(Random rng)
    {
        if (Presets.Length == 0)
            throw new InvalidOperationException("Duel presets empty");
        return Presets[rng.Next(Presets.Length)];
    }

    /// <summary>
    /// Roll modifier (~1/3) then uniform among <see cref="EnabledModifiers"/>;
    /// otherwise random preset from the 23. Modifier rounds use the modifier's own loadout
    /// (probe omits <c>current_loadout</c> TX — client applies definition via wire id).
    /// </summary>
    public static RoundPick RollRound(Random rng)
    {
        if (EnabledModifiers.Count > 0 && rng.NextDouble() < ModifierChancePerRound)
        {
            var mod = EnabledModifiers[rng.Next(EnabledModifiers.Count)];
            return new RoundPick(HasModifier: true, Modifier: mod, ActiveLoadout: mod.Loadout);
        }

        var preset = PickRandomPreset(rng);
        return new RoundPick(HasModifier: false, Modifier: null, ActiveLoadout: preset);
    }
}
