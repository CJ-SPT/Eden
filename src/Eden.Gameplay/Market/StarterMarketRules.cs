using System.Collections.Frozen;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Market;

// Build 3662168 client/types. This small Eden bootstrap cohort bridges the
// corvette to racial combat frigates and the Venture; other goods retain production unlocks.
public enum StarterMarketType
{
    EmpSmall = 185,
    IronSmall = 215,
    AntimatterSmall = 230,
    StandardSmall = 242,
    SmallShieldBooster = 399,
    Afterburner = 439,
    GatlingPulseLaser = 450,
    Miner = 483,
    GatlingAutocannon = 484,
    SmallArmorRepairer = 523,
    GatlingRailgun = 561,
    Rifter = 587,
    Incursus = 594,
    Punisher = 597,
    Merlin = 603,
    DamageControl = 2046,
    Venture = 32880,
}

public static class StarterMarketRules
{
    public static IReadOnlySet<int> Types { get; } = Enum.GetValues<StarterMarketType>().Select(type => (int)type).ToFrozenSet();

    public static bool CanSupply(InventoryCategory category, bool published, bool onMarket, int techLevel, bool faction) =>
        published
        && onMarket
        && !faction
        && techLevel == 1
        && category is InventoryCategory.Ship or InventoryCategory.Module or InventoryCategory.Charge or InventoryCategory.Skill;
}
