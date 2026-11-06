using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Propulsion;

public enum TackleTargetKind
{
    Ship,
    Npc,
}

public sealed record TackleTarget(long Id, TackleTargetKind Kind);

public sealed record TackleModule(
    long ItemId,
    int TypeId,
    DogmaEffect Effect,
    double DurationMilliseconds,
    double CapacitorNeed,
    double Range,
    double Strength,
    double WebPercent,
    double MwdBlock,
    double ReactivationMilliseconds
);

public sealed record TackleEffect(
    long PilotId,
    long SourceShipId,
    TackleModule Module,
    TackleTarget Target,
    long StartedAt,
    long ExpiresAt
);

public sealed record TackleActivation(TackleModule Module, TackleTarget? Target, double Capacitor, long ReadyAt);

public interface IPilotTackle
{
    TackleActivation Activate(
        long pilotId,
        long moduleId,
        DogmaEffect effect,
        TackleTarget? target,
        IReadOnlySet<long> lockedTargets,
        long now
    );
    void Sweep(long now);
    IReadOnlyList<TackleEffect> Incoming(long shipId);
    IReadOnlyDictionary<DogmaAttributeId, double> ShipAttributes(long pilotId);
    void Stop(long pilotId, long shipId, long moduleId);
    void Cancel(long pilotId, long shipId);
    void Disconnect(long pilotId, long shipId);
}

public static class TackleDefinitions
{
    public const int WebifierGroup = 65;
    public const int WarpDisruptionGroup = 52;
    public const int WarpCoreStabilizerGroup = 315;
    public const int PropulsionJammingSkill = 3435;
    public const double MaximumDistance = 250000;

    public static bool Targeted(DogmaEffect effect) =>
        effect is DogmaEffect.StasisWebifier or DogmaEffect.WarpDisruptor or DogmaEffect.WarpScrambler;

    public static TackleModule Define(long itemId, int typeId, DogmaEffect effect, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        bool web = effect == DogmaEffect.StasisWebifier;
        bool core = effect == DogmaEffect.WarpCoreStabilizer;
        if (!core && !Targeted(effect))
        {
            throw new InvalidDataException("Unsupported tackle effect.");
        }
        TackleModule module = new(
            itemId,
            typeId,
            effect,
            attributes.GetValueOrDefault(DogmaAttributeId.Duration),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
            core ? 0 : attributes.GetValueOrDefault(DogmaAttributeId.MaxRange),
            web ? 0 : attributes.GetValueOrDefault(DogmaAttributeId.WarpScrambleStrength),
            web ? attributes.GetValueOrDefault(DogmaAttributeId.SpeedFactor) : 0,
            effect == DogmaEffect.WarpScrambler ? attributes.GetValueOrDefault(DogmaAttributeId.ActivationBlockStrength) : 0,
            core ? attributes.GetValueOrDefault(DogmaAttributeId.ModuleReactivationDelay) : 0
        );
        if (
            !double.IsFinite(module.DurationMilliseconds)
            || module.DurationMilliseconds <= 0
            || !double.IsFinite(module.CapacitorNeed)
            || module.CapacitorNeed < 0
            || !double.IsFinite(module.Range)
            || (!core && module.Range <= 0)
            || !double.IsFinite(module.Strength)
            || (core ? module.Strength >= 0 : module.Strength < 0)
            || !double.IsFinite(module.WebPercent)
            || (web && (module.WebPercent >= 0 || module.WebPercent <= -100))
            || !double.IsFinite(module.MwdBlock)
            || module.MwdBlock < 0
            || !double.IsFinite(module.ReactivationMilliseconds)
            || module.ReactivationMilliseconds < 0
        )
        {
            throw new InvalidDataException("Invalid tackle module attributes.");
        }
        return module;
    }

    public static IEnumerable<DogmaModifier> Modifiers(long target, IEnumerable<TackleModule> modules)
    {
        foreach (var module in modules)
        {
            if (module.Effect == DogmaEffect.StasisWebifier)
            {
                yield return new(
                    target,
                    DogmaAttributeId.MaxVelocity,
                    DogmaOperation.PostPercent,
                    new DogmaValue.Product(
                        new DogmaValue.Constant(module.WebPercent),
                        new DogmaValue.Attribute(target, DogmaAttributeId.StasisWebifierResistance)
                    )
                );
            }
            else
            {
                yield return new(
                    target,
                    DogmaAttributeId.WarpScrambleStatus,
                    DogmaOperation.Add,
                    new DogmaValue.Constant(module.Strength),
                    true
                );
            }
        }
    }

    public static IReadOnlyDictionary<DogmaAttributeId, double> NpcAttributes(
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        IReadOnlyList<TackleModule> modules
    )
    {
        if (!modules.Any(m => m.Effect == DogmaEffect.StasisWebifier))
        {
            return attributes;
        }
        const long NpcNode = 1;
        DogmaGraph graph = new(
            [new(NpcNode, attributes)],
            Modifiers(NpcNode, modules).ToArray(),
            new Dictionary<DogmaAttributeId, DogmaAttributeDefinition>
            {
                [DogmaAttributeId.MaxVelocity] = new(DogmaAttributeId.MaxVelocity, 0, false),
                [DogmaAttributeId.StasisWebifierResistance] = new(DogmaAttributeId.StasisWebifierResistance, 1),
            }
        );
        var result = graph.Attributes(NpcNode);
        double maximum = attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity);
        if (maximum <= 0)
        {
            throw new InvalidDataException("Invalid NPC maximum speed.");
        }
        result[DogmaAttributeId.EntityCruiseSpeed] =
            attributes.GetValueOrDefault(DogmaAttributeId.EntityCruiseSpeed, maximum) * result[DogmaAttributeId.MaxVelocity] / maximum;
        return result;
    }
}
