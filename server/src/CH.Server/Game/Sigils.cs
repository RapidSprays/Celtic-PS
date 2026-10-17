namespace CH.Server.Game;

/// <summary>Sigil items and the regen effects shown for gear (docs/decisions.md).</summary>
public static class Sigils
{
    public const int Health = 8, Energy = 9;

    /// <summary>Out-of-combat regen per tick that each sigil adds. <b>Approximated</b>: the client never states it.</summary>
    public const int RegenPerSigil = 1;

    /// <summary>"Health/Energy Regen Boost Amulet" (5…200 per tick in 5s, duration −1 = permanent).</summary>
    public const int GearHealthRegenEffect = 62, GearEnergyRegenEffect = 63;

    /// <summary>The client's permanent "Attack Speed Boost" (levels 10, 19, 28, 30, 35%): shown for haste gear.</summary>
    public const int GearHasteEffect = 61;
}
