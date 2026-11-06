using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Combat;

public enum NpcEwarKind
{
    Jamming,
    SensorDampening,
    TrackingDisruption,
    TargetPainting,
    EnergyNeutralization,
    Webifying,
    WarpDisruption,
    GuidanceDisruption,
}

public sealed record NpcJammingStrength(double Radar, double Ladar, double Magnetometric, double Gravimetric);

public sealed record NpcEwarPower(
    double TargetRangeMultiplier = 1,
    double ScanResolutionMultiplier = 1,
    double OptimalPercent = 0,
    double FalloffPercent = 0,
    double TrackingPercent = 0,
    double SignaturePercent = 0,
    double CapacitorDrain = 0,
    double WebPercent = 0,
    double WarpStrength = 0,
    NpcJammingStrength? Jamming = null,
    double MissileSpeedPercent = 0,
    double MissileLifetimePercent = 0,
    double ExplosionRadiusPercent = 0,
    double ExplosionVelocityPercent = 0
);

public sealed record NpcEwarAbility(
    NpcEwarKind Kind,
    DogmaEffect Effect,
    double DurationMilliseconds,
    double Optimal,
    double Falloff,
    double Chance,
    NpcEwarPower Power
);

public sealed record NpcEwarContact(
    long SourceId,
    int SourceTypeId,
    long TargetShipId,
    NpcEwarAbility Ability,
    NpcEwarPower Applied,
    long StartedAt,
    long ReadyAt
);

public sealed record NpcEwarSource(
    long Id,
    int TypeId,
    int SystemId,
    SpacePosition Position,
    SpacePosition Home,
    double Radius,
    double Leash,
    long ReactionAt,
    IReadOnlyList<NpcEwarAbility> Abilities
);

public interface INpcElectronicWarfare
{
    IReadOnlyList<NpcEwarContact> Advance(long pilotId, long shipId, long now);
    IReadOnlyList<NpcEwarContact> Incoming(long shipId);
    void Cancel(long shipId);
}

public interface INpcEwarRoll
{
    double Next();
}

public static class NpcEwarRules
{
    public const double Percent = 100;

    public static IReadOnlyList<NpcEwarAbility> Profile(
        IReadOnlySet<DogmaEffect> effects,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes
    )
    {
        double Value(DogmaAttributeId id, double fallback = 0) => attributes.GetValueOrDefault(id, fallback);
        List<NpcEwarAbility> abilities = [];
        void Add(
            DogmaEffect effect,
            NpcEwarKind kind,
            DogmaAttributeId duration,
            DogmaAttributeId range,
            DogmaAttributeId? chance,
            NpcEwarPower power,
            DogmaAttributeId? falloff = null,
            double defaultDuration = 30000,
            double defaultRange = 0
        )
        {
            if (!effects.Contains(effect))
            {
                return;
            }
            NpcEwarAbility ability = new(
                kind,
                effect,
                Value(duration, defaultDuration),
                Value(range, defaultRange),
                falloff is { } id ? Value(id) : 0,
                chance is { } chanceAttribute ? Value(chanceAttribute) : 1,
                power
            );
            if (
                !double.IsFinite(ability.DurationMilliseconds)
                || ability.DurationMilliseconds <= 0
                || !double.IsFinite(ability.Optimal)
                || ability.Optimal <= 0
                || !double.IsFinite(ability.Falloff)
                || ability.Falloff < 0
                || !double.IsFinite(ability.Chance)
                || ability.Chance is < 0 or > 1
            )
            {
                throw new InvalidDataException("Invalid NPC electronic warfare timing, range or chance.");
            }
            Validate(power);
            abilities.Add(ability);
        }
        Add(
            DogmaEffect.NpcEcm,
            NpcEwarKind.Jamming,
            DogmaAttributeId.EntityEcmDuration,
            DogmaAttributeId.EntityEcmRange,
            DogmaAttributeId.EntityEcmChance,
            new(
                Jamming: new(
                    Value(DogmaAttributeId.RadarJammingStrength),
                    Value(DogmaAttributeId.LadarJammingStrength),
                    Value(DogmaAttributeId.MagnetometricJammingStrength),
                    Value(DogmaAttributeId.GravimetricJammingStrength)
                )
            ),
            DogmaAttributeId.EntityEcmFalloff
        );
        Add(
            DogmaEffect.NpcSensorDampening,
            NpcEwarKind.SensorDampening,
            DogmaAttributeId.EntitySensorDampenDuration,
            DogmaAttributeId.EntitySensorDampenRange,
            DogmaAttributeId.EntitySensorDampenChance,
            new(
                TargetRangeMultiplier: Value(DogmaAttributeId.TargetRangeMultiplier, 1),
                ScanResolutionMultiplier: Value(DogmaAttributeId.ScanResolutionMultiplier, 1)
            ),
            DogmaAttributeId.EntitySensorDampenFalloff
        );
        Add(
            DogmaEffect.NpcTrackingDisruption,
            NpcEwarKind.TrackingDisruption,
            DogmaAttributeId.NpcTrackingDisruptDuration,
            DogmaAttributeId.NpcTrackingDisruptRange,
            DogmaAttributeId.NpcTrackingDisruptChance,
            new(
                OptimalPercent: Value(DogmaAttributeId.OptimalRangeBonus),
                FalloffPercent: Value(DogmaAttributeId.FalloffBonus),
                TrackingPercent: Value(DogmaAttributeId.TrackingSpeedBonus)
            ),
            DogmaAttributeId.NpcTrackingDisruptFalloff,
            defaultDuration: 0
        );
        Add(
            DogmaEffect.NpcTargetPainting,
            NpcEwarKind.TargetPainting,
            DogmaAttributeId.EntityTargetPaintDuration,
            DogmaAttributeId.EntityTargetPaintRange,
            DogmaAttributeId.EntityTargetPaintChance,
            new(SignaturePercent: Value(DogmaAttributeId.SignatureRadiusBonus)),
            DogmaAttributeId.EntityTargetPaintFalloff
        );
        Add(
            DogmaEffect.NpcEnergyNeutralization,
            NpcEwarKind.EnergyNeutralization,
            DogmaAttributeId.EntityNeutralizerDuration,
            DogmaAttributeId.EnergyNeutralizerRange,
            DogmaAttributeId.EntityNeutralizerChance,
            new(CapacitorDrain: Value(DogmaAttributeId.EnergyNeutralizerAmount)),
            DogmaAttributeId.EnergyNeutralizerFalloff
        );
        Add(
            DogmaEffect.NpcWebifier,
            NpcEwarKind.Webifying,
            DogmaAttributeId.EntityWebDuration,
            DogmaAttributeId.EntityWebRange,
            DogmaAttributeId.EntityWebChance,
            new(WebPercent: Value(DogmaAttributeId.SpeedFactor)),
            defaultDuration: 5000,
            defaultRange: 20000
        );
        Add(
            DogmaEffect.NpcWarpDisruption,
            NpcEwarKind.WarpDisruption,
            DogmaAttributeId.EntityWarpScrambleDuration,
            DogmaAttributeId.WarpScrambleRange,
            DogmaAttributeId.EntityWarpScrambleChance,
            new(WarpStrength: Value(DogmaAttributeId.WarpScrambleStrength)),
            defaultDuration: 8000
        );
        Add(
            DogmaEffect.BehaviorWeb,
            NpcEwarKind.Webifying,
            DogmaAttributeId.BehaviorWebDuration,
            DogmaAttributeId.BehaviorWebRange,
            null,
            new(WebPercent: Value(DogmaAttributeId.SpeedFactor)),
            DogmaAttributeId.BehaviorWebFalloff
        );
        Add(
            DogmaEffect.BehaviorDisrupt,
            NpcEwarKind.WarpDisruption,
            DogmaAttributeId.BehaviorDisruptDuration,
            DogmaAttributeId.BehaviorDisruptRange,
            null,
            new(WarpStrength: Value(DogmaAttributeId.BehaviorDisruptStrength))
        );
        Add(
            DogmaEffect.BehaviorScramble,
            NpcEwarKind.WarpDisruption,
            DogmaAttributeId.BehaviorScrambleDuration,
            DogmaAttributeId.BehaviorScrambleRange,
            null,
            new(WarpStrength: Value(DogmaAttributeId.BehaviorScrambleStrength))
        );
        Add(
            DogmaEffect.BehaviorPaint,
            NpcEwarKind.TargetPainting,
            DogmaAttributeId.BehaviorPaintDuration,
            DogmaAttributeId.BehaviorPaintRange,
            null,
            new(SignaturePercent: Value(DogmaAttributeId.SignatureRadiusBonus)),
            DogmaAttributeId.BehaviorPaintFalloff
        );
        Add(
            DogmaEffect.BehaviorDamp,
            NpcEwarKind.SensorDampening,
            DogmaAttributeId.BehaviorDampDuration,
            DogmaAttributeId.BehaviorDampRange,
            null,
            new(
                TargetRangeMultiplier: 1 + Value(DogmaAttributeId.TargetRangeBonus) / Percent,
                ScanResolutionMultiplier: 1 + Value(DogmaAttributeId.ScanResolutionBonus) / Percent
            ),
            DogmaAttributeId.BehaviorDampFalloff
        );
        Add(
            DogmaEffect.BehaviorEcm,
            NpcEwarKind.Jamming,
            DogmaAttributeId.BehaviorEcmDuration,
            DogmaAttributeId.BehaviorEcmRange,
            null,
            new(
                Jamming: new(
                    Value(DogmaAttributeId.RadarJammingStrength),
                    Value(DogmaAttributeId.LadarJammingStrength),
                    Value(DogmaAttributeId.MagnetometricJammingStrength),
                    Value(DogmaAttributeId.GravimetricJammingStrength)
                )
            ),
            DogmaAttributeId.BehaviorEcmFalloff
        );
        Add(
            DogmaEffect.BehaviorGuidanceDisrupt,
            NpcEwarKind.GuidanceDisruption,
            DogmaAttributeId.BehaviorGuidanceDuration,
            DogmaAttributeId.BehaviorGuidanceRange,
            null,
            new(
                MissileSpeedPercent: Value(DogmaAttributeId.MissileVelocityBonus),
                MissileLifetimePercent: Value(DogmaAttributeId.MissileLifetimeBonus),
                ExplosionRadiusPercent: Value(DogmaAttributeId.ExplosionRadiusBonus),
                ExplosionVelocityPercent: Value(DogmaAttributeId.ExplosionVelocityBonus)
            ),
            DogmaAttributeId.BehaviorGuidanceFalloff
        );
        return abilities;
    }

    private static void Validate(NpcEwarPower power)
    {
        double[] values =
        [
            power.TargetRangeMultiplier,
            power.ScanResolutionMultiplier,
            power.OptimalPercent,
            power.FalloffPercent,
            power.TrackingPercent,
            power.SignaturePercent,
            power.CapacitorDrain,
            power.WebPercent,
            power.WarpStrength,
            power.MissileSpeedPercent,
            power.MissileLifetimePercent,
            power.ExplosionRadiusPercent,
            power.ExplosionVelocityPercent,
        ];
        if (
            values.Any(v => !double.IsFinite(v))
            || power.TargetRangeMultiplier is < 0 or > 1
            || power.ScanResolutionMultiplier is < 0 or > 1
            || power.OptimalPercent is < -100 or > 0
            || power.FalloffPercent is < -100 or > 0
            || power.TrackingPercent is < -100 or > 0
            || power.WebPercent is <= -100 or > 0
            || power.SignaturePercent < 0
            || power.CapacitorDrain < 0
            || power.WarpStrength < 0
            || power.MissileSpeedPercent is <= -100 or > 0
            || power.MissileLifetimePercent is <= -100 or > 0
            || power.ExplosionRadiusPercent < 0
            || power.ExplosionVelocityPercent is <= -100 or > 0
        )
        {
            throw new InvalidDataException("Invalid NPC electronic warfare strength.");
        }
        if (
            power.Jamming is { } jam
            && new[] { jam.Radar, jam.Ladar, jam.Magnetometric, jam.Gravimetric }.Any(v => !double.IsFinite(v) || v < 0)
        )
        {
            throw new InvalidDataException("Invalid NPC racial jamming strength.");
        }
    }

    public static double RangeFactor(NpcEwarAbility ability, double distance)
    {
        if (distance <= ability.Optimal)
        {
            return 1;
        }
        return ability.Falloff > 0 ? Math.Pow(0.5, Math.Pow((distance - ability.Optimal) / ability.Falloff, 2)) : 0;
    }

    public static NpcEwarPower? Attempt(
        NpcEwarAbility ability,
        double distance,
        IReadOnlyDictionary<DogmaAttributeId, double> target,
        double roll
    )
    {
        if (!double.IsFinite(distance) || distance < 0 || !double.IsFinite(roll) || roll is < 0 or >= 1)
        {
            throw new InvalidDataException("Invalid NPC electronic warfare attempt.");
        }
        double range = distance <= NpcEncounterDefinitions.VisibilityRange ? RangeFactor(ability, distance) : 0;
        double chance = ability.Chance;
        if (ability.Kind == NpcEwarKind.Jamming && ability.Power.Jamming is { } jam)
        {
            double Ratio(DogmaAttributeId sensor, double strength) =>
                target.GetValueOrDefault(sensor) is > 0 and var defense ? strength / defense : 0;
            chance *= Math.Clamp(
                new[]
                {
                    Ratio(DogmaAttributeId.RadarStrength, jam.Radar),
                    Ratio(DogmaAttributeId.LadarStrength, jam.Ladar),
                    Ratio(DogmaAttributeId.MagnetometricStrength, jam.Magnetometric),
                    Ratio(DogmaAttributeId.GravimetricStrength, jam.Gravimetric),
                }.Max() * target.GetValueOrDefault(DogmaAttributeId.EcmResistance, 1),
                0,
                1
            );
        }
        if (ability.Kind != NpcEwarKind.EnergyNeutralization)
        {
            chance *= range;
        }
        if (range <= 0 || roll >= chance)
        {
            return null;
        }
        return ability.Kind == NpcEwarKind.EnergyNeutralization
            ? ability.Power with
            {
                CapacitorDrain =
                    ability.Power.CapacitorDrain * range * target.GetValueOrDefault(DogmaAttributeId.EnergyWarfareResistance, 1),
            }
            : ability.Power;
    }

    public static bool CanTarget(IReadOnlyList<NpcEwarContact> incoming, long target) =>
        !incoming.Any(e => e.Ability.Kind == NpcEwarKind.Jamming)
        || incoming.Any(e => e.Ability.Kind == NpcEwarKind.Jamming && e.SourceId == target);

    public static IEnumerable<DogmaModifier> Modifiers(
        long ship,
        IReadOnlyList<long> turrets,
        IEnumerable<NpcEwarContact> incoming,
        IReadOnlyList<long>? missileCharges = null
    )
    {
        DogmaModifier PercentModifier(long item, DogmaAttributeId attribute, double percent, DogmaAttributeId resistance) =>
            new(
                item,
                attribute,
                DogmaOperation.PostPercent,
                new DogmaValue.Product(new DogmaValue.Constant(percent), new DogmaValue.Attribute(ship, resistance))
            );
        DogmaModifier Multiply(DogmaAttributeId attribute, double multiplier, DogmaOperation operation) =>
            new(
                ship,
                attribute,
                operation,
                new DogmaValue.Sum(
                    new DogmaValue.Constant(1),
                    new DogmaValue.Product(
                        new DogmaValue.Constant(multiplier - 1),
                        new DogmaValue.Attribute(ship, DogmaAttributeId.SensorDampenerResistance)
                    )
                )
            );
        foreach (var effect in incoming)
        {
            var power = effect.Applied;
            switch (effect.Ability.Kind)
            {
                case NpcEwarKind.GuidanceDisruption:
                    foreach (long charge in missileCharges ?? [])
                    {
                        yield return PercentModifier(
                            charge,
                            DogmaAttributeId.MaxVelocity,
                            power.MissileSpeedPercent,
                            DogmaAttributeId.WeaponDisruptionResistance
                        );
                        yield return PercentModifier(
                            charge,
                            DogmaAttributeId.MissileFlightTime,
                            power.MissileLifetimePercent,
                            DogmaAttributeId.WeaponDisruptionResistance
                        );
                        yield return PercentModifier(
                            charge,
                            DogmaAttributeId.ExplosionRadius,
                            power.ExplosionRadiusPercent,
                            DogmaAttributeId.WeaponDisruptionResistance
                        );
                        yield return PercentModifier(
                            charge,
                            DogmaAttributeId.ExplosionVelocity,
                            power.ExplosionVelocityPercent,
                            DogmaAttributeId.WeaponDisruptionResistance
                        );
                    }
                    break;
                case NpcEwarKind.SensorDampening:
                    yield return Multiply(DogmaAttributeId.MaxTargetRange, power.TargetRangeMultiplier, DogmaOperation.PostMultiply);
                    yield return Multiply(DogmaAttributeId.ScanResolution, power.ScanResolutionMultiplier, DogmaOperation.PreMultiply);
                    break;
                case NpcEwarKind.TrackingDisruption:
                    foreach (long turret in turrets)
                    {
                        yield return PercentModifier(
                            turret,
                            DogmaAttributeId.MaxRange,
                            power.OptimalPercent,
                            DogmaAttributeId.WeaponDisruptionResistance
                        );
                        yield return PercentModifier(
                            turret,
                            DogmaAttributeId.AccuracyFalloff,
                            power.FalloffPercent,
                            DogmaAttributeId.WeaponDisruptionResistance
                        );
                        yield return PercentModifier(
                            turret,
                            DogmaAttributeId.TrackingSpeed,
                            power.TrackingPercent,
                            DogmaAttributeId.WeaponDisruptionResistance
                        );
                    }
                    break;
                case NpcEwarKind.TargetPainting:
                    yield return PercentModifier(
                        ship,
                        DogmaAttributeId.SignatureRadius,
                        power.SignaturePercent,
                        DogmaAttributeId.TargetPainterResistance
                    );
                    break;
                case NpcEwarKind.Webifying:
                    yield return PercentModifier(
                        ship,
                        DogmaAttributeId.MaxVelocity,
                        power.WebPercent,
                        DogmaAttributeId.StasisWebifierResistance
                    );
                    break;
                case NpcEwarKind.WarpDisruption:
                    yield return new(
                        ship,
                        DogmaAttributeId.WarpScrambleStatus,
                        DogmaOperation.Add,
                        new DogmaValue.Constant(power.WarpStrength),
                        true
                    );
                    break;
            }
        }
    }
}
