using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

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
    public const int CycleMilliseconds = 300_000;
    public const int SiegeSkill = 22043;
    public const int TriageSkill = 27906;

    public static ReadOnlySpan<int> Types => [SiegeOne, SiegeTwo, TriageOne, TriageTwo];

    public static DogmaEffect? Effect(int type) =>
        type switch
        {
            SiegeOne or SiegeTwo => DogmaEffect.CapitalSiege,
            TriageOne or TriageTwo => DogmaEffect.CapitalTriage,
            _ => null,
        };
}
