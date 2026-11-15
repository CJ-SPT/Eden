using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Exploration;

[Flags]
public enum AbyssalFieldMask
{
    None = 0,
    Filament = 1,
    Bioluminescence = 2,
    Tachyon = 4,
    ShortTracking = 8,
    MediumTracking = 16,
}

public sealed record AbyssalField(int Slot, int TypeId, SpacePosition Position, double Range, AbyssalFieldMask Mask);

public sealed record AbyssalEnvironment(AbyssalWeather Weather, int Penalty, AbyssalFieldMask Fields);

public static class AbyssalEnvironmentRules
{
    public const int FilamentCloud = 47472;
    public const int BioluminescenceCloud = 47440;
    public const int TachyonCloud = 80876;
    public const int ShortSuppressor = 47437;
    public const int MediumSuppressor = 47438;
    public const int ShortTracking = 47469;
    public const int MediumTracking = 47470;
    public const int BoundaryType = 47465;
    public const long PulseInterval = 2 * TimeSpan.TicksPerSecond;
    public const long TerminalExposure = 15 * TimeSpan.TicksPerSecond;
    private const long FirstFieldId = 2_000_000_000;
    private const int FieldsPerRun = 32;
    private const int FieldsPerRoom = 8;

    // Stable Eden layouts and strength selection; CCP's private generation probabilities are unavailable.
    public static int Penalty(int tier, int seed) => (tier < 4 ? 30 : 50) + (seed % 2) * 20;

    public static IReadOnlyList<AbyssalField> Layout(int room, int seed)
    {
        int variant = (seed % 3 + room) % 3;
        var center = AbyssalRules.Center(room);
        return
        [
            new(
                0,
                variant switch
                {
                    0 => FilamentCloud,
                    1 => BioluminescenceCloud,
                    _ => TachyonCloud,
                },
                center + new SpacePosition(20_000, 20_000, 0),
                15_000,
                variant switch
                {
                    0 => AbyssalFieldMask.Filament,
                    1 => AbyssalFieldMask.Bioluminescence,
                    _ => AbyssalFieldMask.Tachyon,
                }
            ),
            new(
                1,
                seed % 2 == 0 ? ShortTracking : MediumTracking,
                center + new SpacePosition(20_000, -20_000, 0),
                seed % 2 == 0 ? 15_000 : 40_000,
                seed % 2 == 0 ? AbyssalFieldMask.ShortTracking : AbyssalFieldMask.MediumTracking
            ),
            new(
                2,
                seed % 2 == 0 ? ShortSuppressor : MediumSuppressor,
                center + new SpacePosition(-30_000, 0, 0),
                seed % 2 == 0 ? 15_000 : 40_000,
                AbyssalFieldMask.None
            ),
            new(3, BoundaryType, center, AbyssalRules.BoundaryRadius, AbyssalFieldMask.None),
        ];
    }

    public static long FieldId(long runId, int room, int slot) =>
        checked(FirstFieldId + (runId - AbyssalRules.FirstRunId) * FieldsPerRun + room * FieldsPerRoom + slot);

    public static AbyssalFieldMask Fields(int room, int seed, SpacePosition position)
    {
        AbyssalFieldMask mask = AbyssalFieldMask.None;
        foreach (var field in Layout(room, seed))
        {
            if (position.DistanceTo(field.Position) <= field.Range)
            {
                mask |= field.Mask;
            }
        }
        return mask;
    }

    public static IEnumerable<DogmaModifier> Modifiers(
        long target,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        AbyssalEnvironment environment,
        bool turret = false,
        bool shieldBooster = false,
        bool npc = false
    )
    {
        foreach (var (attribute, multiplier) in Multipliers(environment, turret, shieldBooster, npc))
        {
            if (attributes.ContainsKey(attribute))
            {
                yield return new(target, attribute, DogmaOperation.PostMultiply, new DogmaValue.Constant(multiplier), true);
            }
        }
    }

    public static IReadOnlyDictionary<DogmaAttributeId, double> Apply(
        IReadOnlyDictionary<DogmaAttributeId, double> source,
        AbyssalEnvironment environment
    )
    {
        var result = source.ToDictionary();
        foreach (var (attribute, multiplier) in Multipliers(environment, true, false, true))
        {
            if (result.TryGetValue(attribute, out double value))
            {
                result[attribute] = value * multiplier;
            }
        }
        return result;
    }

    private static IEnumerable<KeyValuePair<DogmaAttributeId, double>> Multipliers(
        AbyssalEnvironment state,
        bool turret,
        bool shieldBooster,
        bool npc
    )
    {
        double penalty = state.Penalty / 100.0;
        switch (state.Weather)
        {
            case AbyssalWeather.Dark:
                yield return new(DogmaAttributeId.MaxVelocity, 1.5);
                if (turret)
                {
                    yield return new(DogmaAttributeId.MaxRange, 1 - penalty);
                    yield return new(DogmaAttributeId.AccuracyFalloff, 1 - penalty);
                }
                break;
            case AbyssalWeather.Electrical:
                yield return new(DogmaAttributeId.CapacitorRechargeTime, 0.5);
                break;
            case AbyssalWeather.Exotic:
                yield return new(DogmaAttributeId.ScanResolution, 1.5);
                break;
            case AbyssalWeather.Firestorm:
                yield return new(DogmaAttributeId.ArmorHitPoints, 1.5);
                break;
            case AbyssalWeather.Gamma:
                yield return new(DogmaAttributeId.ShieldCapacity, 1.5);
                break;
        }
        DogmaAttributeId[] resistances = state.Weather switch
        {
            AbyssalWeather.Electrical =>
            [
                DogmaAttributeId.ShieldEmResonance,
                DogmaAttributeId.ArmorEmResonance,
                DogmaAttributeId.StructureEmResonance,
            ],
            AbyssalWeather.Exotic =>
            [
                DogmaAttributeId.ShieldKineticResonance,
                DogmaAttributeId.ArmorKineticResonance,
                DogmaAttributeId.StructureKineticResonance,
            ],
            AbyssalWeather.Firestorm =>
            [
                DogmaAttributeId.ShieldThermalResonance,
                DogmaAttributeId.ArmorThermalResonance,
                DogmaAttributeId.StructureThermalResonance,
            ],
            AbyssalWeather.Gamma =>
            [
                DogmaAttributeId.ShieldExplosiveResonance,
                DogmaAttributeId.ArmorExplosiveResonance,
                DogmaAttributeId.StructureExplosiveResonance,
            ],
            _ => [],
        };
        foreach (var resistance in resistances)
        {
            yield return new(resistance, 1 + penalty);
        }
        if (state.Fields.HasFlag(AbyssalFieldMask.Bioluminescence))
        {
            yield return new(DogmaAttributeId.SignatureRadius, 4);
        }
        if (state.Fields.HasFlag(AbyssalFieldMask.Tachyon))
        {
            yield return new(DogmaAttributeId.MaxVelocity, 4);
            yield return new(DogmaAttributeId.Agility, 0.5);
        }
        if (turret && (state.Fields & (AbyssalFieldMask.ShortTracking | AbyssalFieldMask.MediumTracking)) != 0)
        {
            yield return new(DogmaAttributeId.TrackingSpeed, state.Fields.HasFlag(AbyssalFieldMask.ShortTracking) ? 1.8 : 1.6);
        }
        if (state.Fields.HasFlag(AbyssalFieldMask.Filament))
        {
            if (shieldBooster)
            {
                yield return new(DogmaAttributeId.ShieldBonus, 0.6);
                yield return new(DogmaAttributeId.Duration, 0.6);
            }
            if (npc)
            {
                yield return new(DogmaAttributeId.EntityShieldBoostAmount, 0.6);
                yield return new(DogmaAttributeId.EntityShieldBoostDuration, 0.6);
            }
        }
    }

    public static bool SuppressorTarget(int typeId, int groupId, int categoryId) =>
        typeId is not (48262 or 48263 or 48264 or 48265) && (categoryId is 8 or 18 or 87 || groupId == 1997);

    public static DamageVector SuppressorDamage(int typeId) => typeId == ShortSuppressor ? new(0, 30, 0, 30) : new(0, 10, 0, 10);

    // Eden's conservative depth curve follows the observed one-second cadence; terminal exposure is explicit policy.
    public static double BoundaryDamage(double distance) =>
        distance <= AbyssalRules.BoundaryRadius ? 0 : 10.55 + (distance - AbyssalRules.BoundaryRadius) / 1000 * 10.24;
}
