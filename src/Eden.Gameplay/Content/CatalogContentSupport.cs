using System.Collections.Immutable;
using Eden.Gameplay.Combat;

namespace Eden.Gameplay.Content;

public sealed record ManufacturingContentSupport(ImmutableHashSet<int> HullTypes, ImmutableHashSet<int> UtilityTypes);

public sealed record InsuranceContentSupport(ImmutableHashSet<int> MaterialValuedHullTypes);

public sealed record StartingEquipmentContent(int MinerType, int WeaponType);

public sealed record HullContentSupport(
    ImmutableHashSet<int> SpecialTypes,
    ImmutableHashSet<int> SmallClasses,
    ImmutableHashSet<int> CombatClasses,
    ImmutableHashSet<int> UtilityClasses
);

public sealed record NpcLootContent(
    ImmutableArray<int> Ammunition,
    ImmutableArray<int> Repair,
    int Support,
    int AmmunitionUnitsPerSize,
    bool SingleAmmunition
);

public sealed record NpcContentSupport(
    ImmutableHashSet<int> ExcludedTypes,
    ImmutableDictionary<int, NpcGuardRoster> Guards,
    ImmutableDictionary<int, NpcPirateFaction> PirateRegions,
    int FallbackLootModule,
    ImmutableDictionary<NpcPirateFaction, NpcLootContent> Loot
);

public sealed record CatalogContentSupport(
    int Build,
    ManufacturingContentSupport Manufacturing,
    InsuranceContentSupport Insurance,
    HullContentSupport Hulls,
    ImmutableHashSet<int> DroneHullTypes,
    NpcContentSupport Npcs,
    StartingEquipmentContent StartingEquipment
);
