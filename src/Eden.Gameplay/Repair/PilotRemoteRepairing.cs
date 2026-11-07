using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Repair;

public enum RemoteRepairKind
{
    Shield,
    Armor,
}

public enum RemoteRepairSkill
{
    ShieldEmissionSystems = 3422,
    RemoteArmorRepairSystems = 16069,
    CapitalRemoteArmorRepairSystems = 24568,
    CapitalShieldEmissionSystems = 24571,
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
    double Falloff
);

public sealed record RemoteRepairCycle(RemoteRepairModule Module, long ShipId, long TargetId, long StartedAt, long CompletesAt);

public sealed record RemoteRepairActivation(RemoteRepairCycle Cycle, double Capacitor);

public sealed record RemoteRepairApplied(long PilotId, ShipHealth Health);

public interface IRemoteRepairEvents
{
    void Publish(RemoteRepairApplied repair);
}

public interface IPilotRemoteRepairing
{
    RemoteRepairActivation Start(long pilotId, long moduleId, DogmaEffect effect, long targetId, IReadOnlySet<long> locks, long now);
    void Complete(long pilotId, long moduleId, long now);
    void Stop(long pilotId, long moduleId);
    bool IsOnline(long pilotId, long shipId, long moduleId);
}

public static class RemoteRepairRules
{
    public const int ShieldGroup = 41;
    public const int ArmorGroup = 325;

    public static DogmaEffect? Effect(int group) =>
        group switch
        {
            ShieldGroup => DogmaEffect.RemoteShieldBoost,
            ArmorGroup => DogmaEffect.RemoteArmorRepair,
            _ => null,
        };

    public static RemoteRepairModule Define(long item, int type, DogmaEffect effect, IReadOnlyDictionary<DogmaAttributeId, double> values)
    {
        var kind = effect switch
        {
            DogmaEffect.RemoteShieldBoost => RemoteRepairKind.Shield,
            DogmaEffect.RemoteArmorRepair => RemoteRepairKind.Armor,
            _ => throw new InvalidDataException("Unsupported remote repair effect."),
        };
        double duration = values.GetValueOrDefault(DogmaAttributeId.Duration);
        double cost = values.GetValueOrDefault(DogmaAttributeId.CapacitorNeed);
        double amount = values.GetValueOrDefault(
            kind == RemoteRepairKind.Shield ? DogmaAttributeId.ShieldBonus : DogmaAttributeId.ArmorRepairAmount
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
        return new(item, type, kind, effect, duration, cost, amount, optimal, falloff);
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
            Remaining =
                kind == RemoteRepairKind.Shield
                    ? health.Remaining with
                    {
                        Shield = Math.Min(health.Defense.Shield, health.Remaining.Shield + amount),
                    }
                    : health.Remaining with
                    {
                        Armor = Math.Min(health.Defense.Armor, health.Remaining.Armor + amount),
                    },
        };
    }
}
