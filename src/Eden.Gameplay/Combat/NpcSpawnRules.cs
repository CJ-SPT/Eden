namespace Eden.Gameplay.Combat;

public enum NpcHullClass
{
    Frigate,
    Destroyer,
    Cruiser,
    Battlecruiser,
    Battleship,
}

public enum NpcSecurityBand
{
    Safest,
    High,
    Low,
    Null,
}

public static class NpcSpawnRules
{
    public const int PopulationVersion = 2;
    public const double MemberSpacing = 1500;
    public const double BeltStandOff = 10000;
    private const double SafestSecurity = 0.9;
    private const double HighSecurity = 0.5;
    private const uint GenerationSalt = 0x9e3779b9;
    private const uint FirstMix = 0x7feb352d;
    private const uint SecondMix = 0x846ca68b;

    public static NpcSecurityBand SecurityBand(double security)
    {
        if (!double.IsFinite(security) || security is < -1 or > 1)
        {
            throw new InvalidDataException("Invalid system security for NPC population.");
        }
        if (security <= 0)
        {
            return NpcSecurityBand.Null;
        }
        double displayed = Math.Clamp(Math.Round(security, 1, MidpointRounding.AwayFromZero), 0.1, 1);
        return displayed >= SafestSecurity ? NpcSecurityBand.Safest
            : displayed >= HighSecurity ? NpcSecurityBand.High
            : NpcSecurityBand.Low;
    }

    // A fixed integer mixer avoids process-randomized HashCode values. Saved
    // encounter generations prevent reconnects from rerolling groups.
    public static uint Seed(int systemId, long anchorId, int generation = 0)
    {
        unchecked
        {
            uint value = (uint)systemId ^ (uint)anchorId ^ (uint)generation * GenerationSalt;
            value ^= value >> 16;
            value *= FirstMix;
            value ^= value >> 15;
            value *= SecondMix;
            return value ^ (value >> 16);
        }
    }

    public static bool Occupied(uint seed) => seed % 2 == 0;

    public static IReadOnlyList<NpcHullClass> Composition(NpcSecurityBand band, uint seed)
    {
        int count = band switch
        {
            NpcSecurityBand.Safest => 0,
            NpcSecurityBand.High => 1 + (int)(seed % 3),
            NpcSecurityBand.Low => 2 + (int)(seed % 3),
            NpcSecurityBand.Null => 3 + (int)(seed % 3),
            _ => throw new ArgumentOutOfRangeException(nameof(band)),
        };
        List<NpcHullClass> members = [];
        for (int slot = 0; slot < count; slot++)
        {
            members.Add(
                band switch
                {
                    NpcSecurityBand.High => slot == 0 && seed % 3 == 0 ? NpcHullClass.Destroyer : NpcHullClass.Frigate,
                    NpcSecurityBand.Low => slot == 0
                        ? seed % 2 == 0
                            ? NpcHullClass.Battlecruiser
                            : NpcHullClass.Cruiser
                        : NpcHullClass.Frigate,
                    NpcSecurityBand.Null => slot < 2 ? NpcHullClass.Battleship : NpcHullClass.Cruiser,
                    _ => throw new ArgumentOutOfRangeException(nameof(band)),
                }
            );
        }
        return members;
    }
}
