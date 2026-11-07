using Eden.Gameplay.Content;

namespace Eden.Gameplay.Combat;

// Ordinary published build-3662168 ammunition, repair and weapon-support types.
public enum NpcLootType
{
    AntimatterSmall = 222,
    AntimatterMedium = 230,
    AntimatterLarge = 238,
    EmpSmall = 185,
    EmpMedium = 193,
    EmpLarge = 201,
    MultifrequencySmall = 246,
    MultifrequencyMedium = 254,
    MultifrequencyLarge = 262,
    ScourgeLight = 210,
    ScourgeHeavy = 209,
    ScourgeCruise = 203,
    SmallShieldBooster = 399,
    MediumShieldBooster = 10836,
    LargeShieldBooster = 10838,
    SmallArmorRepairer = 523,
    MediumArmorRepairer = 3528,
    LargeArmorRepairer = 3538,
    Gyrostabilizer = 520,
    MagneticFieldStabilizer = 9944,
    HeatSink = 2363,
    BallisticControl = 12274,
    CapRecharger = 1195,
    MetalScraps = 15331,
}

public sealed record NpcLootDrop(int TypeId, int Quantity);

public static class NpcLootRules
{
    public const int RollOutcomes = 100;
    public const int AmmunitionChance = 60;
    public const int ModuleChance = 35;
    public const int ModuleChoices = 3;
    public const int SizeCount = 3;

    public static IReadOnlyList<NpcLootDrop> Roll(
        NpcPirateFaction faction,
        NpcHullClass hull,
        int ammunitionRoll,
        int moduleRoll,
        int moduleChoice
    )
    {
        if (ammunitionRoll is < 0 or >= RollOutcomes || moduleRoll is < 0 or >= RollOutcomes || moduleChoice is < 0 or >= ModuleChoices)
        {
            throw new ArgumentOutOfRangeException(nameof(ammunitionRoll));
        }
        int size = hull switch
        {
            NpcHullClass.Frigate or NpcHullClass.Destroyer => 0,
            NpcHullClass.Cruiser or NpcHullClass.Battlecruiser => 1,
            NpcHullClass.Battleship => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(hull)),
        };
        var policy = BuildContentPolicy.Current.Npcs;
        var content = policy.Loot.GetValueOrDefault(faction) ?? throw new ArgumentOutOfRangeException(nameof(faction));
        List<NpcLootDrop> result = [];
        if (ammunitionRoll < AmmunitionChance)
        {
            int quantity = content.SingleAmmunition ? 1 : content.AmmunitionUnitsPerSize * (size + 1);
            result.Add(new(content.Ammunition[size], quantity));
        }
        if (moduleRoll < ModuleChance)
        {
            var type = moduleChoice switch
            {
                0 => content.Repair[size],
                1 => content.Support,
                _ => policy.FallbackLootModule,
            };
            result.Add(new(type, 1));
        }
        return result;
    }
}
