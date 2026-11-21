using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Flight;

public sealed record CapitalModeCycle(
    long PilotId,
    long ShipId,
    long ModuleId,
    int TypeId,
    DogmaEffect Effect,
    long StartedAt,
    long EndsAt
);

public sealed record CapitalModeActivation(
    CapitalModeCycle Cycle,
    IReadOnlyList<InventoryChange> FuelChanges,
    IReadOnlyDictionary<DogmaAttributeId, double> ShipAttributes
);

public static class CapitalModeRules
{
    // Build 3662168 typedogma effects 6581 and 6582, and their published group-515 modules.
    public const int Group = 515;
    public const int SiegeOne = 20280;
    public const int SiegeTwo = 4292;
    public const int TriageOne = 27951;
    public const int TriageTwo = 4294;
    public const int IndustrialCoreOne = 28583;
    public const int IndustrialCoreTwo = 42890;
    public const int CapitalIndustrialReconfiguration = 28585;
    public const int CapitalIndustrialShips = 28374;
    public const int CycleMilliseconds = 300_000;
    public const int Sarathiel = 87381;
    public const int SiegeSkill = 22043;
    public const int TriageSkill = 27906;

    public static ReadOnlySpan<int> Types => [SiegeOne, SiegeTwo, TriageOne, TriageTwo, IndustrialCoreOne, IndustrialCoreTwo];

    public static DogmaEffect? Effect(int type) =>
        type switch
        {
            SiegeOne or SiegeTwo => DogmaEffect.CapitalSiege,
            TriageOne or TriageTwo => DogmaEffect.CapitalTriage,
            IndustrialCoreOne or IndustrialCoreTwo => DogmaEffect.CapitalIndustrialCore,
            _ => null,
        };

    public static bool CanActivate(DogmaEffect effect, int group) =>
        group == (int)RequiredHull(effect) || (effect == DogmaEffect.CapitalSiege && group == (int)HullClass.LancerDreadnought);

    private static HullClass RequiredHull(DogmaEffect effect) =>
        effect switch
        {
            DogmaEffect.CapitalSiege => HullClass.Dreadnought,
            DogmaEffect.CapitalTriage => HullClass.ForceAuxiliary,
            DogmaEffect.CapitalIndustrialCore => HullClass.CapitalIndustrial,
            _ => HullClass.Unclassified,
        };

    public static int RequiredSkill(DogmaEffect effect) =>
        effect switch
        {
            DogmaEffect.CapitalSiege => SiegeSkill,
            DogmaEffect.CapitalTriage => TriageSkill,
            DogmaEffect.CapitalIndustrialCore => CapitalIndustrialReconfiguration,
            _ => throw new InvalidDataException("Unknown capital mode."),
        };
}
