using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Inventory;

public sealed record SupportModule(long ItemId, int TypeId, DogmaEffect Effect, double DurationMilliseconds, double CapacitorNeed);

public sealed record SupportActivation(SupportModule Module, double Capacitor);

public sealed record ScriptModifier(DogmaAttributeId Attribute, DogmaAttributeId SourceAttribute, DogmaOperation Operation);

public interface IPilotSupportModules
{
    SupportActivation Activate(long pilotId, long moduleId, DogmaEffect effect, long now);
    void Stop(long pilotId, long shipId, long moduleId);
}

public static class SupportModuleDefinitions
{
    public const int TrackingComputerGroup = 213;
    public const int SensorBoosterGroup = 212;
    public const int GuidanceComputerGroup = 1396;
    public const int OmnidirectionalTrackingLinkGroup = 646;
    public const int TrackingScriptGroup = 907;
    public const int SensorScriptGroup = 910;
    public const int GuidanceScriptGroup = 1400;
    public const int ScriptChargeSize = 1;

    public static DogmaEffect? Effect(int group) =>
        group switch
        {
            TrackingComputerGroup => DogmaEffect.TrackingComputer,
            SensorBoosterGroup => DogmaEffect.SensorBooster,
            GuidanceComputerGroup => DogmaEffect.MissileGuidanceComputer,
            OmnidirectionalTrackingLinkGroup => DogmaEffect.OmnidirectionalTrackingLink,
            _ => null,
        };

    public static int ScriptGroup(int group) =>
        group switch
        {
            TrackingComputerGroup => TrackingScriptGroup,
            OmnidirectionalTrackingLinkGroup => TrackingScriptGroup,
            SensorBoosterGroup => SensorScriptGroup,
            GuidanceComputerGroup => GuidanceScriptGroup,
            PlayerEwarRules.SensorDampenerGroup => PlayerEwarRules.DampeningScriptGroup,
            _ => 0,
        };

    public static bool IsScript(int group) =>
        group
            is TrackingScriptGroup
                or SensorScriptGroup
                or GuidanceScriptGroup
                or PlayerEwarRules.TrackingDisruptionScriptGroup
                or PlayerEwarRules.DampeningScriptGroup
                or PlayerEwarRules.GuidanceDisruptionScriptGroup;

    public static bool CompatibleScript(
        int group,
        int chargeGroup,
        IReadOnlyDictionary<DogmaAttributeId, double> module,
        IReadOnlyDictionary<DogmaAttributeId, double> script
    ) =>
        (
            ScriptGroup(group) == chargeGroup
            || group == PlayerEwarRules.WeaponDisruptorGroup
                && chargeGroup is PlayerEwarRules.TrackingDisruptionScriptGroup or PlayerEwarRules.GuidanceDisruptionScriptGroup
        )
        && IsScript(chargeGroup)
        && module.GetValueOrDefault(DogmaAttributeId.ChargeGroup1) == chargeGroup
        && module.GetValueOrDefault(DogmaAttributeId.ChargeSize) == script.GetValueOrDefault(DogmaAttributeId.ChargeSize)
        && (
            script.GetValueOrDefault(DogmaAttributeId.LauncherGroup) == group
            || script.GetValueOrDefault(DogmaAttributeId.LauncherGroup2) == group
            || script.GetValueOrDefault(DogmaAttributeId.LauncherGroup3) == group
            || group == PlayerEwarRules.WeaponDisruptorGroup
                && chargeGroup == PlayerEwarRules.GuidanceDisruptionScriptGroup
                && script.GetValueOrDefault(DogmaAttributeId.LauncherGroup) == GuidanceComputerGroup
        );

    public static SupportModule Define(long itemId, int typeId, int group, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        var effect = Effect(group) ?? throw new InvalidDataException("Unsupported support module family.");
        double duration = attributes.GetValueOrDefault(DogmaAttributeId.Duration);
        double cost = attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed);
        if (!double.IsFinite(duration) || duration <= 0 || !double.IsFinite(cost) || cost < 0)
        {
            throw new InvalidDataException("Invalid support module cycle attributes.");
        }
        return new(itemId, typeId, effect, duration, cost);
    }

    public static IReadOnlyList<DogmaAttributeId> BonusAttributes { get; } =
    [
        DogmaAttributeId.OptimalRangeBonus,
        DogmaAttributeId.FalloffBonus,
        DogmaAttributeId.TrackingSpeedBonus,
        DogmaAttributeId.MissileVelocityBonus,
        DogmaAttributeId.MissileLifetimeBonus,
        DogmaAttributeId.ExplosionVelocityBonus,
        DogmaAttributeId.ExplosionRadiusBonus,
        DogmaAttributeId.TargetRangeBonus,
        DogmaAttributeId.ScanResolutionBonus,
        DogmaAttributeId.GravimetricStrengthBonus,
        DogmaAttributeId.MagnetometricStrengthBonus,
        DogmaAttributeId.LadarStrengthBonus,
        DogmaAttributeId.RadarStrengthBonus,
    ];
}
