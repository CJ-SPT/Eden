using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;

namespace Eden.Gameplay.Combat;

public enum ElectronicWarfareSkill
{
    ElectronicWarfare = 3427,
    LongDistanceJamming = 19759,
    FrequencyModulation = 19760,
    SignalDispersion = 19761,
    SensorLinking = 3433,
    SignalSuppression = 19766,
    WeaponDisruption = 3434,
    WeaponDestabilization = 19767,
    TargetPainting = 19921,
    SignatureFocusing = 19922,
    MagnetometricSensorCompensation = 32999,
    GravimetricSensorCompensation = 33000,
    LadarSensorCompensation = 33001,
    RadarSensorCompensation = 33002,
}

public sealed record ElectronicWarfareModule(
    long ItemId,
    int TypeId,
    DogmaEffect Effect,
    NpcEwarKind Kind,
    double DurationMilliseconds,
    double CapacitorNeed,
    double Optimal,
    double Falloff,
    NpcEwarPower Power
);

public sealed record ElectronicWarfareContact(
    long PilotId,
    long SourceShipId,
    ElectronicWarfareModule Module,
    TackleTarget Target,
    NpcEwarPower? Applied,
    long StartedAt,
    long ExpiresAt
);

public sealed record ElectronicWarfareActivation(
    ElectronicWarfareModule Module,
    TackleTarget? Target,
    double Capacitor,
    IReadOnlyList<ElectronicWarfareContact> Contacts
);

public interface IPilotElectronicWarfare
{
    bool IsOnline(long pilotId, long shipId, long moduleId);
    ElectronicWarfareActivation Activate(
        long pilotId,
        long moduleId,
        DogmaEffect effect,
        TackleTarget? target,
        IReadOnlySet<long> lockedTargets,
        long now
    );
    IReadOnlyList<ElectronicWarfareContact> Incoming(long targetId);
    void Sweep(long now);
    void Stop(long pilotId, long shipId, long moduleId);
    void Cancel(long pilotId, long shipId, bool incoming = false);
}

public static class PlayerEwarRules
{
    public const int EcmGroup = 201;
    public const int SensorDampenerGroup = 208;
    public const int WeaponDisruptorGroup = 291;
    public const int TargetPainterGroup = 379;
    public const int TrackingDisruptionScriptGroup = 909;
    public const int DampeningScriptGroup = 911;
    public const int GuidanceDisruptionScriptGroup = 1569;

    public static IReadOnlySet<int> Groups { get; } =
        new HashSet<int> { EcmGroup, SensorDampenerGroup, WeaponDisruptorGroup, TargetPainterGroup };

    public static IReadOnlyList<DogmaAttributeId> SourceAttributes { get; } =
        SupportModuleDefinitions
            .BonusAttributes.Concat(
                new[]
                {
                    DogmaAttributeId.SignatureRadiusBonus,
                    DogmaAttributeId.RadarJammingStrength,
                    DogmaAttributeId.LadarJammingStrength,
                    DogmaAttributeId.MagnetometricJammingStrength,
                    DogmaAttributeId.GravimetricJammingStrength,
                }
            )
            .ToArray();

    public static NpcEwarKind? Kind(DogmaEffect effect) =>
        effect switch
        {
            DogmaEffect.PlayerEcm => NpcEwarKind.Jamming,
            DogmaEffect.SensorDampener => NpcEwarKind.SensorDampening,
            DogmaEffect.TrackingDisruptor => NpcEwarKind.TrackingDisruption,
            DogmaEffect.GuidanceDisruptor => NpcEwarKind.GuidanceDisruption,
            DogmaEffect.TargetPainter => NpcEwarKind.TargetPainting,
            _ => null,
        };

    public static bool Matches(int group, DogmaEffect effect) =>
        group switch
        {
            EcmGroup => effect == DogmaEffect.PlayerEcm,
            SensorDampenerGroup => effect == DogmaEffect.SensorDampener,
            WeaponDisruptorGroup => effect is DogmaEffect.TrackingDisruptor or DogmaEffect.GuidanceDisruptor,
            TargetPainterGroup => effect == DogmaEffect.TargetPainter,
            _ => false,
        };

    public static ElectronicWarfareModule Define(
        long itemId,
        int typeId,
        DogmaEffect effect,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes
    )
    {
        double Value(DogmaAttributeId id) => attributes.GetValueOrDefault(id);
        var kind = Kind(effect) ?? throw new InvalidDataException("Unsupported electronic warfare effect.");
        NpcEwarPower power = kind switch
        {
            NpcEwarKind.Jamming => new(
                Jamming: new(
                    Value(DogmaAttributeId.RadarJammingStrength),
                    Value(DogmaAttributeId.LadarJammingStrength),
                    Value(DogmaAttributeId.MagnetometricJammingStrength),
                    Value(DogmaAttributeId.GravimetricJammingStrength)
                )
            ),
            NpcEwarKind.SensorDampening => new(
                TargetRangeMultiplier: 1 + Value(DogmaAttributeId.TargetRangeBonus) / 100,
                ScanResolutionMultiplier: 1 + Value(DogmaAttributeId.ScanResolutionBonus) / 100
            ),
            NpcEwarKind.TrackingDisruption => new(
                OptimalPercent: Value(DogmaAttributeId.OptimalRangeBonus),
                FalloffPercent: Value(DogmaAttributeId.FalloffBonus),
                TrackingPercent: Value(DogmaAttributeId.TrackingSpeedBonus)
            ),
            NpcEwarKind.GuidanceDisruption => new(
                MissileSpeedPercent: Value(DogmaAttributeId.MissileVelocityBonus),
                MissileLifetimePercent: Value(DogmaAttributeId.MissileLifetimeBonus),
                ExplosionRadiusPercent: Value(DogmaAttributeId.ExplosionRadiusBonus),
                ExplosionVelocityPercent: Value(DogmaAttributeId.ExplosionVelocityBonus)
            ),
            _ => new(SignaturePercent: Value(DogmaAttributeId.SignatureRadiusBonus)),
        };
        ElectronicWarfareModule module = new(
            itemId,
            typeId,
            effect,
            kind,
            Value(DogmaAttributeId.Duration),
            Value(DogmaAttributeId.CapacitorNeed),
            Value(DogmaAttributeId.MaxRange),
            Value(DogmaAttributeId.FalloffEffectiveness),
            power
        );
        if (
            attributes.Values.Any(v => !double.IsFinite(v))
            || module.DurationMilliseconds <= 0
            || module.CapacitorNeed < 0
            || module.Optimal <= 0
            || module.Falloff < 0
            || power.TargetRangeMultiplier is < 0 or > 1
            || power.ScanResolutionMultiplier is < 0 or > 1
            || power.OptimalPercent is < -100 or > 0
            || power.FalloffPercent is < -100 or > 0
            || power.TrackingPercent is < -100 or > 0
            || power.SignaturePercent < 0
            || power.MissileSpeedPercent is <= -100 or > 0
            || power.MissileLifetimePercent is <= -100 or > 0
            || power.ExplosionRadiusPercent < 0
            || power.ExplosionVelocityPercent is <= -100 or > 0
            || power.Jamming is { } jam && new[] { jam.Radar, jam.Ladar, jam.Magnetometric, jam.Gravimetric }.Any(v => v < 0)
        )
        {
            throw new InvalidDataException("Invalid electronic warfare module attributes.");
        }
        return module;
    }

    public static NpcEwarPower? Attempt(
        ElectronicWarfareModule module,
        double distance,
        IReadOnlyDictionary<DogmaAttributeId, double> target,
        double roll
    )
    {
        var ability = new NpcEwarAbility(
            module.Kind,
            module.Effect,
            module.DurationMilliseconds,
            module.Optimal,
            module.Falloff,
            1,
            module.Power
        );
        if (!double.IsFinite(distance) || distance < 0 || !double.IsFinite(roll) || roll is < 0 or >= 1)
        {
            throw new InvalidDataException("Invalid electronic warfare attempt.");
        }
        if (module.Kind == NpcEwarKind.Jamming)
        {
            return NpcEwarRules.Attempt(ability, distance, target, roll);
        }
        // Player falloff effects reduce strength continuously; they do not roll
        // the legacy NPC chance-to-apply policy used by NpcEwarRules.Attempt.
        double factor = NpcEwarRules.RangeFactor(ability, distance);
        var p = module.Power;
        return p with
        {
            TargetRangeMultiplier = 1 + (p.TargetRangeMultiplier - 1) * factor,
            ScanResolutionMultiplier = 1 + (p.ScanResolutionMultiplier - 1) * factor,
            OptimalPercent = p.OptimalPercent * factor,
            FalloffPercent = p.FalloffPercent * factor,
            TrackingPercent = p.TrackingPercent * factor,
            SignaturePercent = p.SignaturePercent * factor,
            MissileSpeedPercent = p.MissileSpeedPercent * factor,
            MissileLifetimePercent = p.MissileLifetimePercent * factor,
            ExplosionRadiusPercent = p.ExplosionRadiusPercent * factor,
            ExplosionVelocityPercent = p.ExplosionVelocityPercent * factor,
        };
    }

    public static bool CanTarget(IEnumerable<ElectronicWarfareContact> incoming, long target) =>
        !incoming.Any(c => c.Module.Effect == DogmaEffect.PlayerEcm && c.Applied is not null)
        || incoming.Any(c => c.Module.Effect == DogmaEffect.PlayerEcm && c.Applied is not null && c.SourceShipId == target);

    public static IEnumerable<DogmaModifier> Modifiers(
        long ship,
        IReadOnlyList<long> turrets,
        IReadOnlyList<long> missiles,
        IEnumerable<ElectronicWarfareContact> incoming
    )
    {
        foreach (var contact in incoming.Where(c => c.Applied is not null))
        {
            var p = contact.Applied!;
            if (contact.Module.Kind == NpcEwarKind.SensorDampening)
            {
                yield return Damp(DogmaAttributeId.MaxTargetRange, p.TargetRangeMultiplier);
                yield return Damp(DogmaAttributeId.ScanResolution, p.ScanResolutionMultiplier);
                DogmaModifier Damp(DogmaAttributeId attribute, double multiplier) =>
                    new(
                        ship,
                        attribute,
                        DogmaOperation.PostPercent,
                        new DogmaValue.Product(
                            new DogmaValue.Constant((multiplier - 1) * 100),
                            new DogmaValue.Attribute(ship, DogmaAttributeId.SensorDampenerResistance)
                        )
                    );
                continue;
            }
            NpcEwarContact adapter = new(
                contact.SourceShipId,
                contact.Module.TypeId,
                contact.Target.Id,
                new(contact.Module.Kind, contact.Module.Effect, contact.Module.DurationMilliseconds, contact.Module.Optimal, 0, 1, p),
                p,
                contact.StartedAt,
                contact.ExpiresAt
            );
            foreach (var modifier in NpcEwarRules.Modifiers(ship, turrets, [adapter], missiles))
            {
                yield return modifier;
            }
        }
    }

    public static IReadOnlyDictionary<DogmaAttributeId, double> NpcAttributes(
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        IReadOnlyList<ElectronicWarfareContact> incoming,
        double missileRange = 0
    )
    {
        if (incoming.Count == 0)
        {
            return attributes;
        }
        // NPCs use Eden's existing detection envelope when their catalog omits
        // maxTargetRange. Only dampening installs that temporary lock ceiling.
        if (
            !attributes.ContainsKey(DogmaAttributeId.MaxTargetRange)
            && incoming.Any(e => e.Module.Kind == NpcEwarKind.SensorDampening && e.Applied is not null)
        )
        {
            var baseline = attributes.ToDictionary();
            baseline[DogmaAttributeId.MaxTargetRange] = NpcPopulationRules.DetectionRange(attributes, missileRange);
            attributes = baseline;
        }
        const long Npc = 1;
        var modifiers = Modifiers(Npc, [Npc], [], incoming).ToList();
        // NPC launchers use hull multipliers rather than fitted charge nodes.
        // Apply guidance disruption to those multipliers, leaving movement alone.
        foreach (var contact in incoming.Where(c => c.Module.Kind == NpcEwarKind.GuidanceDisruption && c.Applied is not null))
        {
            var power = contact.Applied!;
            AddMissile(DogmaAttributeId.EntityMissileVelocityMultiplier, power.MissileSpeedPercent);
            AddMissile(DogmaAttributeId.EntityMissileFlightTimeMultiplier, power.MissileLifetimePercent);
            AddMissile(DogmaAttributeId.EntityMissileExplosionRadiusMultiplier, power.ExplosionRadiusPercent);
            AddMissile(DogmaAttributeId.EntityMissileExplosionVelocityMultiplier, power.ExplosionVelocityPercent);
        }
        Dictionary<DogmaAttributeId, DogmaAttributeDefinition> definitions = [];
        foreach (var attribute in modifiers.Select(m => m.Attribute))
        {
            definitions[attribute] = new(attribute, 0, false);
        }
        foreach (
            var attribute in new[]
            {
                DogmaAttributeId.SensorDampenerResistance,
                DogmaAttributeId.WeaponDisruptionResistance,
                DogmaAttributeId.TargetPainterResistance,
            }
        )
        {
            definitions[attribute] = new(attribute, 1);
        }
        foreach (
            var attribute in new[]
            {
                DogmaAttributeId.EntityMissileVelocityMultiplier,
                DogmaAttributeId.EntityMissileFlightTimeMultiplier,
                DogmaAttributeId.EntityMissileExplosionRadiusMultiplier,
                DogmaAttributeId.EntityMissileExplosionVelocityMultiplier,
            }
        )
        {
            definitions[attribute] = new(attribute, 1, false);
        }
        return new DogmaGraph([new(Npc, attributes)], modifiers.ToArray(), definitions).Attributes(Npc);

        void AddMissile(DogmaAttributeId attribute, double percent) =>
            modifiers.Add(
                new(
                    Npc,
                    attribute,
                    DogmaOperation.PostPercent,
                    new DogmaValue.Product(
                        new DogmaValue.Constant(percent),
                        new DogmaValue.Attribute(Npc, DogmaAttributeId.WeaponDisruptionResistance)
                    )
                )
            );
    }
}
