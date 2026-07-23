using System.Buffers.Binary;

namespace StandChillow.LanServer.Net.Match;

/// <summary>
/// Defuse / Allies / Ranked money — amounts from user CS-style table + decompile
/// <c>gpy</c> weapon ids (<c>Axlebolt.Standoff.Shared/gpy.cs</c>) and
/// <c>DefuseEconomicsSettings</c> fields. Cap after every grant.
/// </summary>
public static class MatchEconomy
{
    public const int MaxMoney = 10_000;

    public const int TeamWin = 3000;
    public const int ObjectiveWin = 3200; // bomb explode or defuse — replaces TeamWin
    public const int LossFirst = 1900;
    public const int LossSecond = 2400;
    public const int LossThirdPlus = 2900;

    public const int PlantTeam = 300;
    public const int PlantPlanter = 600; // planter total plant bonus (not stacked on PlantTeam)
    public const int DefuseActor = 300;

    public const int KillDefault = 300;
    public const int KillKnife = 1500;
    public const int KillSmg = 800;
    public const int KillP90 = 300;
    public const int KillShotgun = 600;
    public const int KillAwm = 100;
    public const int KillOtherSniper = 300;

    /// <summary>Clamp balance into <c>[0, MaxMoney]</c>.</summary>
    public static int Clamp(int money) => Math.Clamp(money, 0, MaxMoney);

    /// <summary>
    /// Loss streak payout from <c>CoLosses</c> after the round's increment
    /// (1→1900, 2→2400, 3+→2900).
    /// </summary>
    public static int LossPayout(int coLossesAfterRound) => coLossesAfterRound switch
    {
        <= 0 => 0,
        1 => LossFirst,
        2 => LossSecond,
        _ => LossThirdPlus,
    };

    /// <summary>
    /// Kill reward from decompile <c>gpy</c> weapon id. Unknown / 0 → <see cref="KillDefault"/>.
    /// AWM (51) → 100; other snipers → 300 (no capture override found).
    /// </summary>
    public static int KillRewardForWeapon(byte weaponId)
    {
        if (weaponId == 0)
            return KillDefault;

        // Knives (gpy 70–88 except Hands=89)
        if (weaponId is >= 70 and <= 88 && weaponId != 89)
            return KillKnife;

        // SMG / P90
        if (weaponId == 35) // P90
            return KillP90;
        if (weaponId is 32 or 33 or 34 or 36 or 37) // UMP45, AkimboUzi, MP7, MP5, MAC10
            return KillSmg;

        // Shotguns
        if (weaponId is 62 or 63 or 65) // SM1014, FabM, SPAS
            return KillShotgun;

        // Snipers
        if (weaponId == 51) // AWM
            return KillAwm;
        if (weaponId is 52 or 53 or 54) // M40, M110, Mallard
            return KillOtherSniper;

        // Pistols / rifles / heavy / unknown mapped ids → default
        return KillDefault;
    }

    /// <summary>
    /// Best-effort weapon id from pawn damage Rpc payload. Decompile <c>bzw.mur</c>:
    /// two Vector3 (12B each) then <c>gpy</c> as int32 (<c>fzr.bojg</c>). Rpc is
    /// <c>oqk(dur, bzw, dwa)</c> — leading actor framing may add 0–4 bytes; try those skips.
    /// Returns 0 when not confidently a known <c>gpy</c> weapon.
    /// </summary>
    public static byte TryPeekDamageWeaponId(ReadOnlySpan<byte> payload)
    {
        Span<int> skips = stackalloc int[4];
        skips[0] = 0;
        skips[1] = 1;
        skips[2] = 2;
        skips[3] = 4;
        foreach (var skip in skips)
        {
            if (payload.Length < skip + 28)
                continue;
            var id = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(skip + 24, 4));
            if (id is > 0 and <= 255 && IsKnownWeaponId((byte)id))
                return (byte)id;
        }
        return 0;
    }

    /// <summary>Known combat <c>gpy</c> ids from decompile enum (no invent).</summary>
    public static bool IsKnownWeaponId(byte id) => id switch
    {
        // Pistols
        11 or 12 or 13 or 15 or 16 or 17 or 18 => true,
        // SMG
        32 or 33 or 34 or 35 or 36 or 37 => true,
        // Rifles
        42 or 43 or 44 or 45 or 46 or 47 or 48 or 49 => true,
        // Snipers
        51 or 52 or 53 or 54 => true,
        // Shotgun / heavy
        62 or 63 or 64 or 65 => true,
        // Knives
        70 or 71 or 72 or 73 or 75 or 77 or 78 or 79
            or 80 or 81 or 82 or 83 or 85 or 86 or 88 => true,
        // Hands / grenades — still valid ids but kill reward defaults
        89 or 91 or 92 or 93 or 94 or 95 or 99 => true,
        _ => false,
    };
}
