using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Repair;

public sealed record RemoteAssistanceBonuses(
    double Optimal = 0,
    double Falloff = 0,
    double Tracking = 0,
    double TargetRange = 0,
    double ScanResolution = 0,
    double Gravimetric = 0,
    double Ladar = 0,
    double Magnetometric = 0,
    double Radar = 0
);

public sealed record RemoteAssistanceModule(SupportModule Cycle, double Optimal, double Falloff, RemoteAssistanceBonuses Bonuses);

public static class RemoteAssistanceRules
{
    public const int TrackingGroup = 209;
    public const int SensorGroup = 290;

    public static DogmaEffect? Effect(int group) =>
        group switch
        {
            TrackingGroup => DogmaEffect.RemoteTrackingComputer,
            SensorGroup => DogmaEffect.RemoteSensorBooster,
            _ => null,
        };

    public static int ScriptGroup(int group) =>
        group switch
        {
            TrackingGroup => SupportModuleDefinitions.TrackingScriptGroup,
            SensorGroup => SupportModuleDefinitions.SensorScriptGroup,
            _ => 0,
        };

    public static RemoteAssistanceModule Define(
        long itemId,
        int typeId,
        DogmaEffect effect,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes
    )
    {
        if (effect is not (DogmaEffect.RemoteTrackingComputer or DogmaEffect.RemoteSensorBooster))
        {
            throw new InvalidDataException("Unsupported remote assistance effect.");
        }
        double Value(DogmaAttributeId id) => attributes.GetValueOrDefault(id);
        RemoteAssistanceBonuses bonuses =
            effect == DogmaEffect.RemoteTrackingComputer
                ? new(
                    Optimal: Value(DogmaAttributeId.OptimalRangeBonus),
                    Falloff: Value(DogmaAttributeId.FalloffBonus),
                    Tracking: Value(DogmaAttributeId.TrackingSpeedBonus)
                )
                : new(
                    TargetRange: Value(DogmaAttributeId.TargetRangeBonus),
                    ScanResolution: Value(DogmaAttributeId.ScanResolutionBonus),
                    Gravimetric: Value(DogmaAttributeId.GravimetricStrengthBonus),
                    Ladar: Value(DogmaAttributeId.LadarStrengthBonus),
                    Magnetometric: Value(DogmaAttributeId.MagnetometricStrengthBonus),
                    Radar: Value(DogmaAttributeId.RadarStrengthBonus)
                );
        RemoteAssistanceModule module = new(
            new(itemId, typeId, effect, Value(DogmaAttributeId.Duration), Value(DogmaAttributeId.CapacitorNeed)),
            Value(DogmaAttributeId.MaxRange),
            Value(DogmaAttributeId.FalloffEffectiveness),
            bonuses
        );
        double[] values =
        [
            bonuses.Optimal,
            bonuses.Falloff,
            bonuses.Tracking,
            bonuses.TargetRange,
            bonuses.ScanResolution,
            bonuses.Gravimetric,
            bonuses.Ladar,
            bonuses.Magnetometric,
            bonuses.Radar,
        ];
        if (
            !double.IsFinite(module.Cycle.DurationMilliseconds)
            || module.Cycle.DurationMilliseconds <= 0
            || !double.IsFinite(module.Optimal)
            || module.Optimal <= 0
            || new[] { module.Cycle.CapacitorNeed, module.Falloff }.Concat(values).Any(v => !double.IsFinite(v) || v < 0)
            || values.All(v => v == 0)
        )
        {
            throw new InvalidDataException("Invalid remote assistance profile.");
        }
        return module;
    }

    public static double Strength(RemoteAssistanceModule module, double edgeDistance, double impedance = 1)
    {
        if (!double.IsFinite(edgeDistance) || edgeDistance < 0 || !double.IsFinite(impedance) || impedance < 0)
        {
            throw new InvalidDataException("Invalid remote assistance application inputs.");
        }
        if (edgeDistance <= module.Optimal)
        {
            return impedance;
        }
        return module.Falloff == 0 ? 0 : impedance * Math.Pow(0.5, Math.Pow((edgeDistance - module.Optimal) / module.Falloff, 2));
    }

    public static IReadOnlyList<DogmaModifier> Modifiers(
        long targetShip,
        IReadOnlyList<long> gunneryModules,
        RemoteAssistanceModule module,
        double edgeDistance
    )
    {
        double strength = Strength(module, edgeDistance);
        var bonuses = module.Bonuses;
        List<DogmaModifier> modifiers = [];
        if (module.Cycle.Effect == DogmaEffect.RemoteTrackingComputer)
        {
            // The pinned BaseRemoteTrackingComputer selects modules requiring
            // Gunnery on the recipient's location, rather than changing its hull.
            foreach (long turret in gunneryModules)
            {
                Add(turret, DogmaAttributeId.MaxRange, bonuses.Optimal);
                Add(turret, DogmaAttributeId.AccuracyFalloff, bonuses.Falloff);
                Add(turret, DogmaAttributeId.TrackingSpeed, bonuses.Tracking);
            }
        }
        else if (module.Cycle.Effect == DogmaEffect.RemoteSensorBooster)
        {
            Add(targetShip, DogmaAttributeId.MaxTargetRange, bonuses.TargetRange);
            Add(targetShip, DogmaAttributeId.ScanResolution, bonuses.ScanResolution);
            Add(targetShip, DogmaAttributeId.GravimetricStrength, bonuses.Gravimetric);
            Add(targetShip, DogmaAttributeId.LadarStrength, bonuses.Ladar);
            Add(targetShip, DogmaAttributeId.MagnetometricStrength, bonuses.Magnetometric);
            Add(targetShip, DogmaAttributeId.RadarStrength, bonuses.Radar);
        }
        else
        {
            throw new InvalidDataException("Unsupported remote assistance effect.");
        }
        return modifiers;

        void Add(long target, DogmaAttributeId attribute, double bonus)
        {
            if (bonus == 0 || strength == 0)
            {
                return;
            }
            modifiers.Add(
                new(
                    target,
                    attribute,
                    DogmaOperation.PostPercent,
                    new DogmaValue.Product(
                        new DogmaValue.Constant(bonus * strength),
                        new DogmaValue.Attribute(targetShip, DogmaAttributeId.RemoteAssistanceImpedance)
                    )
                )
            );
        }
    }
}
