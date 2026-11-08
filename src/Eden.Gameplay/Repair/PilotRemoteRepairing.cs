using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Repair;

public enum RemoteRepairKind
{
    Shield,
    Armor,
    Hull,
}

public enum RemoteRepairSkill
{
    ShieldEmissionSystems = 3422,
    RemoteArmorRepairSystems = 16069,
    CapitalRemoteArmorRepairSystems = 24568,
    CapitalShieldEmissionSystems = 24571,
    RemoteHullRepairSystems = 27902,
    CapitalRemoteHullRepairSystems = 27936,
}

public sealed record RemoteRepairModule(
    long ItemId,
    int TypeId,
    RemoteRepairKind Kind,
    DogmaEffect Effect,
    double DurationMilliseconds,
    double CapacitorNeed,
    double Amount,
    double Optimal,
    double Falloff,
    double RampPerCycle = 0,
    double RampMaximum = 0
);

public sealed record RemoteRepairCycle(
    RemoteRepairModule Module,
    long ShipId,
    long TargetId,
    long StartedAt,
    long CompletesAt,
    int RampCycles = 0,
    bool Settled = false
);

public sealed record RemoteRepairActivation(RemoteRepairCycle Cycle, double Capacitor, Magazine? Magazine = null);

public sealed record RemoteRepairApplied(long PilotId, ShipHealth Health);

public interface IRemoteRepairEvents
{
    void Publish(RemoteRepairApplied repair);
}

public interface IPilotRemoteRepairing
{
    RemoteRepairActivation Start(
        long pilotId,
        long moduleId,
        DogmaEffect effect,
        long targetId,
        IReadOnlySet<long> locks,
        long now,
        bool continuation = false
    );
    void Complete(long pilotId, long moduleId, long now);
    void Stop(long pilotId, long moduleId);
    bool IsOnline(long pilotId, long shipId, long moduleId);
}

public static class RemoteRepairRules
{
    public const int ShieldGroup = 41;
    public const int ArmorGroup = 325;
    public const int HullGroup = 585;
    public const int AncillaryShieldGroup = 1697;
    public const int AncillaryArmorGroup = 1698;
    public const int MutadaptiveArmorGroup = 2018;

    public static AncillaryRepairKind? AncillaryKind(int group) =>
        group switch
        {
            AncillaryShieldGroup => AncillaryRepairKind.Shield,
            AncillaryArmorGroup => AncillaryRepairKind.Armor,
            _ => null,
        };

    public static DogmaEffect? Effect(int group) =>
        group switch
        {
            ShieldGroup => DogmaEffect.RemoteShieldBoost,
            ArmorGroup => DogmaEffect.RemoteArmorRepair,
            HullGroup => DogmaEffect.RemoteHullRepair,
            AncillaryShieldGroup => DogmaEffect.AncillaryRemoteShieldBoost,
            AncillaryArmorGroup => DogmaEffect.AncillaryRemoteArmorRepair,
            MutadaptiveArmorGroup => DogmaEffect.MutadaptiveRemoteArmorRepair,
            _ => null,
        };

    public static RemoteRepairModule Define(long item, int type, DogmaEffect effect, IReadOnlyDictionary<DogmaAttributeId, double> values)
    {
        var kind = effect switch
        {
            DogmaEffect.RemoteShieldBoost or DogmaEffect.AncillaryRemoteShieldBoost => RemoteRepairKind.Shield,
            DogmaEffect.RemoteArmorRepair or DogmaEffect.AncillaryRemoteArmorRepair or DogmaEffect.MutadaptiveRemoteArmorRepair =>
                RemoteRepairKind.Armor,
            DogmaEffect.RemoteHullRepair => RemoteRepairKind.Hull,
            _ => throw new InvalidDataException("Unsupported remote repair effect."),
        };
        double duration = values.GetValueOrDefault(DogmaAttributeId.Duration);
        double cost = values.GetValueOrDefault(DogmaAttributeId.CapacitorNeed);
        double amount = values.GetValueOrDefault(
            kind switch
            {
                RemoteRepairKind.Shield => DogmaAttributeId.ShieldBonus,
                RemoteRepairKind.Armor => DogmaAttributeId.ArmorRepairAmount,
                _ => DogmaAttributeId.HullRepairAmount,
            }
        );
        double optimal = values.GetValueOrDefault(DogmaAttributeId.MaxRange);
        double falloff = values.GetValueOrDefault(DogmaAttributeId.FalloffEffectiveness);
        if (
            !double.IsFinite(duration)
            || duration <= 0
            || !double.IsFinite(amount)
            || amount <= 0
            || new[] { cost, optimal, falloff }.Any(v => !double.IsFinite(v) || v < 0)
        )
        {
            throw new InvalidDataException("Invalid remote repair profile.");
        }
        double perCycle = 0;
        double maximum = 0;
        if (effect == DogmaEffect.MutadaptiveRemoteArmorRepair)
        {
            perCycle = values.GetValueOrDefault(DogmaAttributeId.RepairMultiplierBonusPerCycle);
            maximum = values.GetValueOrDefault(DogmaAttributeId.RepairMultiplierBonusMax);
            if (!double.IsFinite(perCycle) || perCycle <= 0 || !double.IsFinite(maximum) || maximum <= 0 || falloff != 0)
            {
                throw new InvalidDataException("Invalid mutadaptive repair ramp.");
            }
        }
        return new(item, type, kind, effect, duration, cost, amount, optimal, falloff, perCycle, maximum);
    }

    public static RemoteRepairModule Ramp(RemoteRepairModule module, int completedCycles)
    {
        if (completedCycles < 0 || module.Effect != DogmaEffect.MutadaptiveRemoteArmorRepair)
        {
            throw new InvalidDataException("Only mutadaptive repairs have a repair ramp.");
        }
        // CCP's December 2018 rules: the first paid cycle is unboosted; each
        // additional cycle on the same target adds a fraction of the base repair.
        double amount = module.Amount * (1 + Math.Min(module.RampMaximum, completedCycles * module.RampPerCycle));
        if (!double.IsFinite(amount) || amount <= 0)
        {
            throw new InvalidDataException("Invalid ramped repair amount.");
        }
        return module with { Amount = amount };
    }

    public static double Amount(RemoteRepairModule module, double distance, double impedance)
    {
        if (!double.IsFinite(distance) || distance < 0 || !double.IsFinite(impedance) || impedance < 0)
        {
            throw new InvalidDataException("Invalid remote repair application inputs.");
        }
        if (distance <= module.Optimal)
        {
            return module.Amount * impedance;
        }
        return module.Falloff == 0
            ? 0
            : module.Amount * impedance * Math.Pow(0.5, Math.Pow((distance - module.Optimal) / module.Falloff, 2));
    }

    public static ShipHealth Apply(ShipHealth health, RemoteRepairKind kind, double amount)
    {
        if (!double.IsFinite(amount) || amount < 0)
        {
            throw new InvalidDataException("Invalid remote repair amount.");
        }
        if (health.Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot receive repairs.");
        }
        return health with
        {
            Remaining = kind switch
            {
                RemoteRepairKind.Shield => health.Remaining with
                {
                    Shield = Math.Min(health.Defense.Shield, health.Remaining.Shield + amount),
                },
                RemoteRepairKind.Armor => health.Remaining with { Armor = Math.Min(health.Defense.Armor, health.Remaining.Armor + amount) },
                RemoteRepairKind.Hull => health.Remaining with
                {
                    Structure = Math.Min(health.Defense.Structure, health.Remaining.Structure + amount),
                },
                _ => throw new InvalidDataException("Unsupported remote repair kind."),
            },
        };
    }
}
