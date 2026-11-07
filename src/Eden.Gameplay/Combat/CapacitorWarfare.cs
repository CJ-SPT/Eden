using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Combat;

public enum CapacitorModuleKind
{
    Transfer,
    Neutralizer,
    Nosferatu,
}

public enum CapacitorEmissionSkill
{
    CapacitorEmissionSystems = 3423,
    CapitalCapacitorEmissionSystems = 24572,
}

public sealed record CapacitorModule(
    long ItemId,
    int TypeId,
    DogmaEffect Effect,
    CapacitorModuleKind Kind,
    double DurationMilliseconds,
    double CapacitorNeed,
    double Amount,
    double Optimal,
    double Falloff,
    double SignatureResolution,
    bool NosOverride = false
);

public sealed record CapacitorPool(double Charge, double Capacity);

public sealed record CapacitorApplication(double Source, double Target, double Amount);

public sealed record CapacitorCycle(CapacitorModule Module, long SourceShipId, long TargetId, long StartedAt, long ExpiresAt);

public sealed record CapacitorActivation(CapacitorCycle Cycle, double Capacitor, double TargetCapacitor, double Applied);

public sealed record CapacitorChanged(long PilotId, long ShipId);

public interface ICapacitorEvents
{
    void Publish(CapacitorChanged change);
}

public interface IPilotCapacitorWarfare
{
    CapacitorActivation Activate(long pilotId, long moduleId, DogmaEffect effect, long targetId, IReadOnlySet<long> locks, long now);
    bool IsOnline(long pilotId, long shipId, long moduleId);
    void Stop(long pilotId, long shipId, long moduleId);
}

public static class CapacitorWarfareRules
{
    public const int TransferGroup = 67;
    public const int NosferatuGroup = 68;
    public const int NeutralizerGroup = 71;

    public static DogmaEffect? Effect(int group) =>
        group switch
        {
            TransferGroup => DogmaEffect.RemoteCapacitorTransfer,
            NosferatuGroup => DogmaEffect.EnergyNosferatu,
            NeutralizerGroup => DogmaEffect.EnergyNeutralizer,
            _ => null,
        };

    public static CapacitorModule Define(long item, int type, DogmaEffect effect, IReadOnlyDictionary<DogmaAttributeId, double> values)
    {
        var kind = effect switch
        {
            DogmaEffect.RemoteCapacitorTransfer => CapacitorModuleKind.Transfer,
            DogmaEffect.EnergyNeutralizer => CapacitorModuleKind.Neutralizer,
            DogmaEffect.EnergyNosferatu => CapacitorModuleKind.Nosferatu,
            _ => throw new InvalidDataException("Unsupported capacitor module effect."),
        };
        CapacitorModule module = new(
            item,
            type,
            effect,
            kind,
            values.GetValueOrDefault(DogmaAttributeId.Duration),
            values.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
            values.GetValueOrDefault(
                kind == CapacitorModuleKind.Neutralizer ? DogmaAttributeId.EnergyNeutralizerAmount : DogmaAttributeId.PowerTransferAmount
            ),
            values.GetValueOrDefault(DogmaAttributeId.MaxRange),
            values.GetValueOrDefault(DogmaAttributeId.FalloffEffectiveness),
            values.GetValueOrDefault(DogmaAttributeId.EnergyNeutralizerSignatureResolution),
            values.GetValueOrDefault(DogmaAttributeId.NosOverride) != 0
        );
        Validate(module);
        return module;
    }

    private static void Validate(CapacitorModule module)
    {
        if (
            !Enum.IsDefined(module.Kind)
            || !double.IsFinite(module.DurationMilliseconds)
            || module.DurationMilliseconds <= 0
            || !double.IsFinite(module.Amount)
            || module.Amount <= 0
            || new[] { module.CapacitorNeed, module.Optimal, module.Falloff, module.SignatureResolution }.Any(v => !Valid(v))
        )
        {
            throw new InvalidDataException("Invalid capacitor module profile.");
        }
    }

    public static CapacitorApplication Apply(
        CapacitorModule module,
        CapacitorPool source,
        CapacitorPool target,
        double distance,
        double signature,
        double resistance
    )
    {
        Validate(module);
        if (!Pool(source) || !Pool(target) || !Valid(distance) || !Valid(signature) || !Valid(resistance))
        {
            throw new InvalidDataException("Invalid capacitor application inputs.");
        }
        if (source.Charge + InventoryDefinitions.ResourceTolerance < module.CapacitorNeed)
        {
            throw new InvalidDataException("Insufficient capacitor to activate the module.");
        }
        double paid = Math.Max(0, source.Charge - module.CapacitorNeed);
        double range =
            distance <= module.Optimal ? 1
            : module.Falloff == 0 ? 0
            : Math.Pow(.5, Math.Pow((distance - module.Optimal) / module.Falloff, 2));
        double factor = module.SignatureResolution == 0 ? 1 : Math.Min(1, signature / module.SignatureResolution);
        double amount = module.Amount * range * resistance * factor;
        if (!double.IsFinite(amount))
        {
            throw new InvalidDataException("Invalid capacitor application amount.");
        }
        if (module.Kind == CapacitorModuleKind.Transfer)
        {
            double added = Math.Min(amount, target.Capacity - target.Charge);
            return new(paid, target.Charge + added, added);
        }
        // Odyssey 1.1 compares absolute remaining GJ, not capacitor percentages.
        if (module.Kind == CapacitorModuleKind.Nosferatu && !module.NosOverride && target.Charge <= paid)
        {
            amount = 0;
        }
        double drained = Math.Min(target.Charge, amount);
        return new(
            module.Kind == CapacitorModuleKind.Nosferatu ? Math.Min(source.Capacity, paid + drained) : paid,
            target.Charge - drained,
            drained
        );
    }

    private static bool Valid(double value) => double.IsFinite(value) && value >= 0;

    private static bool Pool(CapacitorPool pool) => Valid(pool.Capacity) && Valid(pool.Charge) && pool.Charge <= pool.Capacity;
}
