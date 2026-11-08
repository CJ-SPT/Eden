using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Defenses;

public sealed record SignatureSuppressor(
    long ItemId,
    int TypeId,
    double DurationMilliseconds,
    double CapacitorNeed,
    double ReactivationMilliseconds,
    double PassiveMultiplier,
    double ActiveMultiplier
);

public sealed record SignatureSuppressionSchedule(long StartedAt, long ExpiresAt, long ReadyAt)
{
    public bool Active(long now) => now >= StartedAt && now < ExpiresAt;

    public bool Ready(long now) => now >= ReadyAt;
}

public static class SignatureSuppressionRules
{
    public const int Group = 1154;
    public const int MaskingSkill = 4411;

    public static SignatureSuppressor Define(long item, int type, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        SignatureSuppressor module = new(
            item,
            type,
            attributes.GetValueOrDefault(DogmaAttributeId.SignatureSuppressionDuration),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
            attributes.GetValueOrDefault(DogmaAttributeId.ModuleReactivationDelay),
            attributes.GetValueOrDefault(DogmaAttributeId.SignatureSuppressionPassiveMultiplier),
            attributes.GetValueOrDefault(DogmaAttributeId.SignatureSuppressionActiveMultiplier)
        );
        Validate(module);
        return module;
    }

    private static void Validate(SignatureSuppressor module)
    {
        if (
            !double.IsFinite(module.DurationMilliseconds)
            || module.DurationMilliseconds <= 0
            || !double.IsFinite(module.CapacitorNeed)
            || module.CapacitorNeed < 0
            || !double.IsFinite(module.ReactivationMilliseconds)
            || module.ReactivationMilliseconds < 0
            || !double.IsFinite(module.PassiveMultiplier)
            || module.PassiveMultiplier is <= 0 or > 1
            || !double.IsFinite(module.ActiveMultiplier)
            || module.ActiveMultiplier is <= 0 or > 1
        )
        {
            throw new InvalidDataException("Invalid signature suppression profile.");
        }
    }

    public static SignatureSuppressionSchedule Schedule(SignatureSuppressor module, long now)
    {
        Validate(module);
        if (now < 0)
        {
            throw new InvalidDataException("Invalid signature suppression start time.");
        }
        long expires = checked(now + checked((long)Math.Ceiling(module.DurationMilliseconds * TimeSpan.TicksPerMillisecond)));
        long ready = checked(expires + checked((long)Math.Ceiling(module.ReactivationMilliseconds * TimeSpan.TicksPerMillisecond)));
        return new(now, expires, ready);
    }

    public static IReadOnlyList<DogmaModifier> Modifiers(SignatureSuppressor module, long ship, bool online, bool active)
    {
        Validate(module);
        if (!online)
        {
            return [];
        }
        List<DogmaModifier> modifiers =
        [
            new(ship, DogmaAttributeId.SignatureRadius, DogmaOperation.PostMultiply, new DogmaValue.Constant(module.PassiveMultiplier)),
        ];
        if (active)
        {
            // Publish both native multipliers into the shared stacking bucket;
            // activation neither replaces the online factor nor bypasses penalties.
            modifiers.Add(
                new(ship, DogmaAttributeId.SignatureRadius, DogmaOperation.PostMultiply, new DogmaValue.Constant(module.ActiveMultiplier))
            );
        }
        return modifiers.AsReadOnly();
    }
}
