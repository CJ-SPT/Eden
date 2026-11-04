using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Inventory;

// Build 3662168 inventorycommon.const and inventoryFlagsCommon.ShouldAllowAdd.
public enum HoldCargoGroup
{
    Mineral = 18,
    Frigate = 25,
    Capsule = 29,
    CapacitorBoosterCharge = 87,
    Booster = 303,
    AssaultFrigate = 324,
    Refinables = 355,
    IceProduct = 423,
    MoonMaterials = 427,
    Ice = 465,
    ScannerProbe = 479,
    SurveyProbe = 492,
    FestivalCharges = 500,
    TerranArtifacts = 519,
    HarvestableCloud = 711,
    SalvagedMaterials = 754,
    RogueDroneComponents = 886,
    ElectronicAttackShip = 893,
    AncientCompressedIce = 903,
    AncientSalvage = 966,
    CommandCenters = 1027,
    FuelBlock = 1136,
    MobileDepot = 1246,
    GenericDecryptor = 1304,
    LogisticsFrigate = 1527,
    StructureAntiCapitalMissile = 1546,
    StructureAntiSubcapitalMissile = 1547,
    GuidedBomb = 1548,
    StructureECMScript = 1549,
    StructureWarpDisruptorScript = 1551,
    NamedComponents = 1676,
    ShieldCommandBurstCharges = 1769,
    MiningForemanBurstCharges = 1771,
    SkirmishCommandBurstCharges = 1772,
    InformationCommandBurstCharges = 1773,
    ArmorCommandBurstCharges = 1774,
    StructureFestivalCharges = 1976,
    TemporalResources = 2022,
    ScalableDecorativeAsteroid = 4094,
    CompressedGas = 4168,
    StructureAreaDenialAmmunition = 4186,
    NonInteractableAsteroids = 4714,
    ColonyReagents = 4729,
    StructureLightFighter = 4777,
    StructureSupportFighter = 4778,
    StructureHeavyFighter = 4779,
    ExpeditionCommandBurstCharges = 4905,
    Prismaticite = 4915,
    UnrefinedMineral = 4932,
}

public enum HoldCargoType
{
    CorpseMale = 25,
    Water = 3645,
    Oxygen = 3683,
    Quafe = 3699,
    QuafeZeroClassic = 3898,
    LargeCratesOfQuafe = 4090,
    QuafeUltra = 12865,
    QuafeUltraSpecialEdition = 12994,
    LiquidOzone = 16273,
    StrontiumClathrates = 16275,
    SpikedQuafe = 21661,
    NaniteRepairPaste = 28668,
    CorpseFemale = 29148,
    QuafeHyper = 57422,
}

public enum ShipHoldCapacityUnit
{
    CubicMeters,
    Ships,
}

public sealed record ShipHoldDefinition(
    InventoryFlag Flag,
    DogmaAttributeId CapacityAttribute,
    IReadOnlyList<InventoryCategory>? AllowedCategories = null,
    IReadOnlyList<HoldCargoGroup>? AllowedGroups = null,
    IReadOnlyList<HoldCargoGroup>? BlockedGroups = null,
    IReadOnlyList<HoldCargoType>? AllowedTypes = null,
    IReadOnlyList<HoldCargoType>? BlockedTypes = null
)
{
    // invCacheContainer.GetFrigateEscapeBayCapacity reports 0/1 occupied ships.
    public ShipHoldCapacityUnit CapacityUnit =>
        Flag == InventoryFlag.FrigateEscapeBay ? ShipHoldCapacityUnit.Ships : ShipHoldCapacityUnit.CubicMeters;

    public double RequiredCapacity(InventoryItem item, int quantity) =>
        CapacityUnit == ShipHoldCapacityUnit.Ships ? quantity : item.Volume * quantity;

    public bool RequiresAssembledShip => Flag is InventoryFlag.ShipMaintenanceBay or InventoryFlag.FrigateEscapeBay;

    public bool AllowsAssembledShip => RequiresAssembledShip || Flag == InventoryFlag.FleetHangar;

    public bool Allows(InventoryItem item)
    {
        // The client's allow categories, groups and types form successive fallbacks.
        // A group/type exclusion applies after the preceding allow has matched.
        bool useAllow = true;
        bool allowed = true;
        if (AllowedCategories is not null)
        {
            allowed = AllowedCategories.Contains((InventoryCategory)item.CategoryId);
            useAllow = !allowed;
        }
        if (useAllow && AllowedGroups is not null)
        {
            allowed = AllowedGroups.Contains((HoldCargoGroup)item.GroupId);
            useAllow = !allowed;
        }
        else if (!useAllow && BlockedGroups is not null)
        {
            allowed = !BlockedGroups.Contains((HoldCargoGroup)item.GroupId);
            useAllow = !allowed;
        }
        if (useAllow && AllowedTypes is not null)
        {
            allowed = AllowedTypes.Contains((HoldCargoType)item.TypeId);
        }
        else if (!useAllow && BlockedTypes is not null)
        {
            allowed = !BlockedTypes.Contains((HoldCargoType)item.TypeId);
        }
        return allowed
            && (!RequiresAssembledShip || item.Singleton)
            && (item.CategoryId != (int)InventoryCategory.Ship || !item.Singleton || AllowsAssembledShip);
    }
}

public static class ShipHoldRules
{
    public static IReadOnlyList<ShipHoldDefinition> Definitions { get; } =
    [
        new(InventoryFlag.Cargo, DogmaAttributeId.CargoCapacity),
        new(InventoryFlag.DroneBay, DogmaAttributeId.DroneCapacity, AllowedCategories: [InventoryCategory.Drone]),
        new(
            InventoryFlag.ShipMaintenanceBay,
            DogmaAttributeId.ShipMaintenanceBayCapacity,
            AllowedCategories: [InventoryCategory.Ship],
            BlockedGroups: [HoldCargoGroup.Capsule]
        ),
        new(InventoryFlag.FuelBay, DogmaAttributeId.SpecialFuelBayCapacity, AllowedGroups: [HoldCargoGroup.IceProduct]),
        new(
            InventoryFlag.GeneralMiningHold,
            DogmaAttributeId.GeneralMiningHoldCapacity,
            AllowedCategories: [InventoryCategory.Asteroid],
            AllowedGroups: [HoldCargoGroup.HarvestableCloud, HoldCargoGroup.CompressedGas, HoldCargoGroup.Prismaticite],
            BlockedGroups:
            [
                HoldCargoGroup.ScalableDecorativeAsteroid,
                HoldCargoGroup.TerranArtifacts,
                HoldCargoGroup.NonInteractableAsteroids,
            ]
        ),
        new(
            InventoryFlag.GasHold,
            DogmaAttributeId.SpecialGasHoldCapacity,
            AllowedGroups: [HoldCargoGroup.HarvestableCloud, HoldCargoGroup.CompressedGas]
        ),
        new(
            InventoryFlag.MineralHold,
            DogmaAttributeId.SpecialMineralHoldCapacity,
            AllowedGroups: [HoldCargoGroup.Mineral, HoldCargoGroup.UnrefinedMineral]
        ),
        new(
            InventoryFlag.AmmoHold,
            DogmaAttributeId.SpecialAmmoHoldCapacity,
            AllowedCategories: [InventoryCategory.Charge],
            AllowedTypes: [HoldCargoType.NaniteRepairPaste]
        ),
        new(
            InventoryFlag.CommandCenterHold,
            DogmaAttributeId.SpecialCommandCenterHoldCapacity,
            AllowedGroups: [HoldCargoGroup.CommandCenters]
        ),
        new(
            InventoryFlag.PlanetaryCommoditiesHold,
            DogmaAttributeId.SpecialPlanetaryCommoditiesHoldCapacity,
            AllowedCategories: [InventoryCategory.PlanetaryCommodity, InventoryCategory.PlanetaryResource],
            AllowedTypes: [HoldCargoType.Water, HoldCargoType.Oxygen]
        ),
        new(
            InventoryFlag.QuafeBay,
            DogmaAttributeId.SpecialQuafeHoldCapacity,
            AllowedTypes:
            [
                HoldCargoType.LargeCratesOfQuafe,
                HoldCargoType.Quafe,
                HoldCargoType.QuafeUltra,
                HoldCargoType.QuafeUltraSpecialEdition,
                HoldCargoType.QuafeZeroClassic,
                HoldCargoType.SpikedQuafe,
                HoldCargoType.QuafeHyper,
            ]
        ),
        new(InventoryFlag.FleetHangar, DogmaAttributeId.FleetHangarCapacity),
        new(InventoryFlag.FighterBay, DogmaAttributeId.FighterCapacity, AllowedCategories: [InventoryCategory.Fighter]),
        new(
            InventoryFlag.CorpseBay,
            DogmaAttributeId.SpecialCorpseHoldCapacity,
            AllowedTypes: [HoldCargoType.CorpseMale, HoldCargoType.CorpseFemale]
        ),
        new(
            InventoryFlag.FrigateEscapeBay,
            DogmaAttributeId.FrigateEscapeBayCapacity,
            AllowedGroups:
            [
                HoldCargoGroup.Frigate,
                HoldCargoGroup.AssaultFrigate,
                HoldCargoGroup.LogisticsFrigate,
                HoldCargoGroup.ElectronicAttackShip,
            ],
            BlockedGroups: [HoldCargoGroup.Capsule]
        ),
        new(
            InventoryFlag.IceHold,
            DogmaAttributeId.SpecialIceHoldCapacity,
            AllowedGroups: [HoldCargoGroup.Ice, HoldCargoGroup.TemporalResources, HoldCargoGroup.AncientCompressedIce]
        ),
        new(InventoryFlag.MobileDepotHold, DogmaAttributeId.SpecialMobileDepotHoldCapacity, AllowedGroups: [HoldCargoGroup.MobileDepot]),
        new(
            InventoryFlag.InfrastructureHold,
            DogmaAttributeId.SpecialColonyResourcesHoldCapacity,
            AllowedCategories:
            [
                InventoryCategory.PlanetaryCommodity,
                InventoryCategory.PlanetaryResource,
                InventoryCategory.InfrastructureUpgrade,
                InventoryCategory.Orbitals,
                InventoryCategory.SovereigntyStructure,
                InventoryCategory.Structure,
                InventoryCategory.Deployable,
                InventoryCategory.StructureModule,
            ],
            AllowedGroups:
            [
                HoldCargoGroup.ColonyReagents,
                HoldCargoGroup.MoonMaterials,
                HoldCargoGroup.FuelBlock,
                HoldCargoGroup.IceProduct,
                HoldCargoGroup.StructureLightFighter,
                HoldCargoGroup.StructureSupportFighter,
                HoldCargoGroup.StructureHeavyFighter,
                HoldCargoGroup.GuidedBomb,
                HoldCargoGroup.StructureAntiSubcapitalMissile,
                HoldCargoGroup.StructureAntiCapitalMissile,
                HoldCargoGroup.StructureECMScript,
                HoldCargoGroup.StructureAreaDenialAmmunition,
                HoldCargoGroup.CommandCenters,
                HoldCargoGroup.StructureWarpDisruptorScript,
            ],
            AllowedTypes: [HoldCargoType.Water, HoldCargoType.Oxygen]
        ),
        new(
            InventoryFlag.ExpeditionHold,
            DogmaAttributeId.SpecialExpeditionHoldCapacity,
            AllowedCategories:
            [
                InventoryCategory.Deployable,
                InventoryCategory.Blueprint,
                InventoryCategory.Personalization,
                InventoryCategory.ShipSkin,
                InventoryCategory.Commodity,
                InventoryCategory.Apparel,
                InventoryCategory.Skill,
                InventoryCategory.SpecialEditionAssets,
                InventoryCategory.Relic,
            ],
            AllowedGroups:
            [
                HoldCargoGroup.HarvestableCloud,
                HoldCargoGroup.CompressedGas,
                HoldCargoGroup.GenericDecryptor,
                HoldCargoGroup.SalvagedMaterials,
                HoldCargoGroup.AncientSalvage,
                HoldCargoGroup.Booster,
                HoldCargoGroup.FestivalCharges,
                HoldCargoGroup.ScannerProbe,
                HoldCargoGroup.SurveyProbe,
                HoldCargoGroup.StructureFestivalCharges,
                HoldCargoGroup.ArmorCommandBurstCharges,
                HoldCargoGroup.ExpeditionCommandBurstCharges,
                HoldCargoGroup.InformationCommandBurstCharges,
                HoldCargoGroup.MiningForemanBurstCharges,
                HoldCargoGroup.ShieldCommandBurstCharges,
                HoldCargoGroup.SkirmishCommandBurstCharges,
                HoldCargoGroup.CapacitorBoosterCharge,
                HoldCargoGroup.Refinables,
                HoldCargoGroup.RogueDroneComponents,
                HoldCargoGroup.NamedComponents,
            ],
            AllowedTypes: [HoldCargoType.NaniteRepairPaste]
        ),
    ];

    public static ShipHoldDefinition? Definition(int flag) => Definitions.SingleOrDefault(hold => (int)hold.Flag == flag);

    public static double Capacity(int flag, IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        Definition(flag) is { } hold ? Math.Max(0, attributes.GetValueOrDefault(hold.CapacityAttribute)) : 0;

    public static bool IsHold(int flag) => Definition(flag) is not null;
}
