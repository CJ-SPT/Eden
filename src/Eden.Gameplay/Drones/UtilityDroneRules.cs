using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;

namespace Eden.Gameplay.Drones;

public enum UtilityDroneKind
{
    ShieldRepair,
    ArmorRepair,
    HullRepair,
    Neutralizing,
    Jamming,
    Dampening,
    TrackingDisruption,
    Painting,
    Webifying,
    Salvaging,
    OreMining,
    IceMining,
    WarpDisrupting,
}

public sealed record UtilityDroneProfile(
    UtilityDroneKind Kind,
    double Range,
    double DurationMilliseconds,
    double Amount,
    ElectronicWarfareModule? ElectronicWarfare = null,
    double EffectMilliseconds = 0,
    double ResidueChance = 0,
    double ResidueMultiplier = 0,
    double Falloff = 0
)
{
    public bool Repair => Kind is UtilityDroneKind.ShieldRepair or UtilityDroneKind.ArmorRepair or UtilityDroneKind.HullRepair;
    public bool Mining => Kind is UtilityDroneKind.OreMining or UtilityDroneKind.IceMining;
    public bool Offensive => !Repair && !Mining && Kind != UtilityDroneKind.Salvaging;
}

public sealed record DroneWorkEffect(UtilityDroneKind Kind, long TargetId, long StartedAt, double DurationMilliseconds);

public sealed record DroneCargo(int TypeId, int Quantity);

// This immutable payload is checkpointed with its drone. Cargo remains on the
// physical drone until delivery, including after recall, abandon or restart.
public sealed record DroneWork(
    long TargetId = 0,
    bool Repeat = false,
    bool Automatic = false,
    long StartedAt = 0,
    long DueAt = 0,
    bool Returning = false,
    IReadOnlyList<DroneCargo>? Cargo = null,
    FighterMobilityCycle? FighterMobility = null
);

public static class UtilityDroneRules
{
    // Worker types omit physical mass/agility. Carbon rejects non-positive
    // free-ball mass; these Eden minima keep worker motion finite and responsive.
    public const double MinimumWorkerMass = 1;
    public const float MinimumWorkerAgility = 1;
    public const int MiningGroup = 101;
    public const int EnergyGroup = 544;
    public const int ElectronicWarfareGroup = 639;
    public const int RepairGroup = 640;
    public const int WebGroup = 641;
    public const int SalvageGroup = 1159;
    public const int HybridGroup = 5239;
    public const int Rorqual = 28352;
    public const int ExcavatorOre = 41030;
    public const int ExcavatorIce = 43681;
    public const int PlutoOre = 93143;
    public const int PlutoIce = 93145;
    private const double Percent = 100;
    private const double OperationBonus = .05;
    private const double SpecializationBonus = .02;
    private const double InterfacingBonus = .1;
    private const double SalvageAccessPerLevel = 2;

    public static bool Group(int group) =>
        group is MiningGroup or EnergyGroup or ElectronicWarfareGroup or RepairGroup or WebGroup or SalvageGroup or HybridGroup;

    public static DroneActivity Activity(UtilityDroneKind kind) =>
        kind switch
        {
            UtilityDroneKind.OreMining or UtilityDroneKind.IceMining => DroneActivity.Mining,
            UtilityDroneKind.Salvaging => DroneActivity.Salvaging,
            UtilityDroneKind.ShieldRepair or UtilityDroneKind.ArmorRepair or UtilityDroneKind.HullRepair => DroneActivity.Operating,
            _ => DroneActivity.Fighting,
        };

    public static bool Working(DroneActivity activity) =>
        activity is DroneActivity.Mining or DroneActivity.Operating or DroneActivity.Salvaging;

    public static bool RequiresRorqual(int type) => type is ExcavatorOre or ExcavatorIce or PlutoOre or PlutoIce;

    public static UtilityDroneProfile Define(DogmaEffect effect, IReadOnlyDictionary<DogmaAttributeId, double> a)
    {
        double Value(DogmaAttributeId id) => a.GetValueOrDefault(id);
        var kind = effect switch
        {
            DogmaEffect.DroneShieldRepair => UtilityDroneKind.ShieldRepair,
            DogmaEffect.DroneArmorRepair => UtilityDroneKind.ArmorRepair,
            DogmaEffect.DroneHullRepair => UtilityDroneKind.HullRepair,
            DogmaEffect.NpcEnergyNeutralization => UtilityDroneKind.Neutralizing,
            DogmaEffect.NpcEcm => UtilityDroneKind.Jamming,
            DogmaEffect.DroneSensorDampening => UtilityDroneKind.Dampening,
            DogmaEffect.DroneTrackingDisruption => UtilityDroneKind.TrackingDisruption,
            DogmaEffect.DroneTargetPainting => UtilityDroneKind.Painting,
            DogmaEffect.DroneWeb => UtilityDroneKind.Webifying,
            DogmaEffect.DroneSalvaging => UtilityDroneKind.Salvaging,
            DogmaEffect.DroneMining => InventoryDefinitions.RequiredSkills.Any(p =>
                Value(p.Skill) == (int)DroneSkill.IceHarvestingDroneOperation
            )
                ? UtilityDroneKind.IceMining
                : UtilityDroneKind.OreMining,
            _ => throw new InvalidDataException("Unsupported utility drone effect."),
        };
        var amountAttribute = kind switch
        {
            UtilityDroneKind.ShieldRepair => DogmaAttributeId.ShieldBonus,
            UtilityDroneKind.ArmorRepair => DogmaAttributeId.ArmorRepairAmount,
            UtilityDroneKind.HullRepair => DogmaAttributeId.HullRepairAmount,
            UtilityDroneKind.Neutralizing => DogmaAttributeId.EnergyNeutralizerAmount,
            UtilityDroneKind.Salvaging => DogmaAttributeId.AccessDifficultyBonus,
            UtilityDroneKind.Webifying => DogmaAttributeId.SpeedFactor,
            _ => DogmaAttributeId.MiningAmount,
        };
        var rangeAttribute = kind switch
        {
            UtilityDroneKind.Neutralizing => DogmaAttributeId.EnergyNeutralizerRange,
            UtilityDroneKind.Jamming => DogmaAttributeId.EntityEcmRange,
            _ => DogmaAttributeId.MaxRange,
        };
        var durationAttribute = kind switch
        {
            UtilityDroneKind.Neutralizing => DogmaAttributeId.EntityNeutralizerDuration,
            UtilityDroneKind.Jamming => DogmaAttributeId.EntityEcmDuration,
            _ => DogmaAttributeId.Duration,
        };
        var moduleEffect = kind switch
        {
            UtilityDroneKind.Jamming => DogmaEffect.PlayerEcm,
            UtilityDroneKind.Dampening => DogmaEffect.SensorDampener,
            UtilityDroneKind.TrackingDisruption => DogmaEffect.TrackingDisruptor,
            UtilityDroneKind.Painting => DogmaEffect.TargetPainter,
            _ => (DogmaEffect?)null,
        };
        ElectronicWarfareModule? module = null;
        if (moduleEffect is not null)
        {
            var attributes = a.ToDictionary();
            attributes[DogmaAttributeId.Duration] = Value(durationAttribute);
            attributes[DogmaAttributeId.MaxRange] = Value(rangeAttribute);
            attributes[DogmaAttributeId.CapacitorNeed] = 0;
            module = PlayerEwarRules.Define(0, 0, moduleEffect.Value, attributes);
        }
        UtilityDroneProfile profile = new(
            kind,
            Value(rangeAttribute),
            Value(durationAttribute),
            Value(amountAttribute),
            module,
            kind == UtilityDroneKind.Jamming ? Value(DogmaAttributeId.EcmJamDuration) : Value(durationAttribute),
            Value(DogmaAttributeId.MiningResidueChance) / Percent,
            Value(DogmaAttributeId.MiningResidueMultiplier)
        );
        if (
            a.Values.Any(v => !double.IsFinite(v))
            || profile.Range <= 0
            || profile.DurationMilliseconds <= 0
            || profile.EffectMilliseconds <= 0
            || profile.ResidueChance is < 0 or > 1
            || profile.ResidueMultiplier < 0
            || (profile.Repair || profile.Mining || kind is UtilityDroneKind.Neutralizing or UtilityDroneKind.Salvaging)
                && profile.Amount <= 0
            || kind == UtilityDroneKind.Webifying && profile.Amount is < -100 or >= 0
        )
        {
            throw new InvalidDataException("Invalid utility drone profile.");
        }
        return profile;
    }

    public static DroneProfile ApplyGathering(DroneProfile profile, ResourcePolicy policy)
    {
        if (profile.Utility is not { Mining: true } utility)
        {
            return profile;
        }
        var family = utility.Kind == UtilityDroneKind.IceMining ? MiningFamily.Ice : MiningFamily.Ore;
        return profile with { Utility = utility with { Amount = utility.Amount * (double)WorldTuningRules.Yield(policy, family) } };
    }

    public static DroneProfile ApplySkills(DroneProfile profile, IReadOnlyDictionary<DroneSkill, int> skills, IReadOnlySet<int> required)
    {
        var utility = profile.Utility!;
        double Level(DroneSkill skill) => skills.GetValueOrDefault(skill);
        double speed = 1;
        if (utility.Repair)
        {
            utility = utility with { Amount = utility.Amount * (1 + OperationBonus * Level(DroneSkill.RepairDroneOperation)) };
        }
        if (utility.Kind == UtilityDroneKind.OreMining)
        {
            utility = utility with
            {
                Amount =
                    utility.Amount
                    * (1 + OperationBonus * Level(DroneSkill.MiningDroneOperation))
                    * (1 + InterfacingBonus * Level(DroneSkill.DroneInterfacing)),
            };
        }
        if (utility.Kind == UtilityDroneKind.IceMining)
        {
            utility = utility with
            {
                DurationMilliseconds = utility.DurationMilliseconds * (1 - OperationBonus * Level(DroneSkill.IceHarvestingDroneOperation)),
            };
        }
        foreach (
            var skill in new[]
            {
                DroneSkill.MiningDroneSpecialization,
                DroneSkill.IceHarvestingDroneSpecialization,
                DroneSkill.SalvageDroneSpecialization,
            }
        )
        {
            if (!required.Contains((int)skill))
            {
                continue;
            }
            speed *= 1 + SpecializationBonus * Level(skill);
            utility = skill switch
            {
                DroneSkill.MiningDroneSpecialization => utility with { Amount = utility.Amount * speed },
                DroneSkill.IceHarvestingDroneSpecialization => utility with
                {
                    DurationMilliseconds = utility.DurationMilliseconds * (1 - SpecializationBonus * Level(skill)),
                },
                _ => utility with { Amount = utility.Amount + SalvageAccessPerLevel * Level(skill) },
            };
        }
        if (utility.Kind == UtilityDroneKind.Salvaging)
        {
            utility = utility with { Amount = utility.Amount + SalvageAccessPerLevel * Level(DroneSkill.SalvageDroneOperation) };
        }
        return profile with { Utility = utility, MaximumSpeed = profile.MaximumSpeed * (float)speed };
    }
}
