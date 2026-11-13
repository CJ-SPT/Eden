using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Combat;

public enum MissileSkill
{
    MissileLauncherOperation = 3319,
    Rockets = 3320,
    LightMissiles = 3321,
    AutoTargetingMissiles = 3322,
    HeavyMissiles = 3324,
    Torpedoes = 3325,
    CruiseMissiles = 3326,
    MissileBombardment = 12441,
    MissileProjection = 12442,
    RocketSpecialization = 20209,
    LightMissileSpecialization = 20210,
    HeavyMissileSpecialization = 20211,
    CruiseMissileSpecialization = 20212,
    TorpedoSpecialization = 20213,
    GuidedMissilePrecision = 20312,
    TargetNavigationPrediction = 20314,
    WarheadUpgrades = 20315,
    RapidLaunch = 21071,
    XlTorpedoes = 21668,
    HeavyAssaultMissileSpecialization = 25718,
    HeavyAssaultMissiles = 25719,
    XlCruiseMissiles = 32435,
    XlTorpedoSpecialization = 41409,
    XlCruiseMissileSpecialization = 41410,
}

public enum MissileLauncherGroup
{
    Cruise = 506,
    Rocket = 507,
    Torpedo = 508,
    Light = 509,
    Heavy = 510,
    RapidLight = 511,
    XlTorpedo = 524,
    HeavyAssault = 771,
    RapidHeavy = 1245,
    XlCruise = 1674,
    RapidTorpedo = 1673,
    BreacherPod = 4807,
}

public enum MissileChargeGroup
{
    Torpedo = 89,
    Light = 384,
    Heavy = 385,
    Cruise = 386,
    Rocket = 387,
    AutoTargetingLight = 394,
    AutoTargetingHeavy = 395,
    AutoTargetingCruise = 396,
    XlTorpedo = 476,
    AdvancedRocket = 648,
    AdvancedLight = 653,
    AdvancedHeavyAssault = 654,
    AdvancedHeavy = 655,
    AdvancedCruise = 656,
    AdvancedTorpedo = 657,
    HeavyAssault = 772,
    CompactXlTorpedo = 1010,
    XlCruise = 1019,
    AdvancedXlTorpedo = 1677,
    AdvancedXlCruise = 1678,
    BreacherPod = 4808,
}

public static class MissileDefinitions
{
    public const long FirstProjectileId = 6_000_000_000_000;
    public const int CivilianLauncher = 32461;
    public static IReadOnlySet<int> LauncherGroups { get; } = Enum.GetValues<MissileLauncherGroup>().Select(g => (int)g).ToHashSet();
    public static IReadOnlySet<int> ChargeGroups { get; } = Enum.GetValues<MissileChargeGroup>().Select(g => (int)g).ToHashSet();
    public static IReadOnlySet<int> Skills { get; } = Enum.GetValues<MissileSkill>().Select(s => (int)s).ToHashSet();

    // Owner modifiers are enabled for implemented missile skills only. Drone
    // modifiers on mixed upgrades still require their separate implementation.
    public static bool SupportsOwnerSkill(int skill) =>
        skill
            is (int)MissileSkill.MissileLauncherOperation
                or (int)MissileSkill.Rockets
                or (int)MissileSkill.LightMissiles
                or (int)MissileSkill.AutoTargetingMissiles
                or (int)MissileSkill.HeavyMissiles
                or (int)MissileSkill.Torpedoes
                or (int)MissileSkill.CruiseMissiles
                or (int)MissileSkill.XlTorpedoes
                or (int)MissileSkill.HeavyAssaultMissiles
                or (int)MissileSkill.XlCruiseMissiles;

    public static bool SupportedLauncher(int group, IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        LauncherGroups.Contains(group)
        && attributes.GetValueOrDefault(DogmaAttributeId.TechLevel) is 1 or 2
        && (
            group is (int)MissileLauncherGroup.RapidTorpedo or (int)MissileLauncherGroup.BreacherPod
            || !InventoryDefinitions.RequiresSpecificShip(attributes)
        );

    public static bool CompatibleCharge(
        IReadOnlyDictionary<DogmaAttributeId, double> launcher,
        int group,
        IReadOnlyDictionary<DogmaAttributeId, double>? charge = null
    ) =>
        ChargeGroups.Contains(group)
        && ChargeGroupAttributes.Any(a => launcher.GetValueOrDefault(a) == group)
        && (
            group != (int)MissileChargeGroup.BreacherPod
            || charge is not null
                && launcher.GetValueOrDefault(DogmaAttributeId.ChargeSize) == charge.GetValueOrDefault(DogmaAttributeId.ChargeSize)
        );

    public static IReadOnlyList<DogmaAttributeId> ChargeGroupAttributes { get; } =
    [
        DogmaAttributeId.ChargeGroup1,
        DogmaAttributeId.ChargeGroup2,
        DogmaAttributeId.ChargeGroup3,
        DogmaAttributeId.ChargeGroup4,
        DogmaAttributeId.ChargeGroup5,
        DogmaAttributeId.ChargeGroup6,
    ];
}
