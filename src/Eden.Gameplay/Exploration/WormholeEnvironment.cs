using System.Collections.Immutable;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Exploration;

public sealed record WormholeSystemModifier(
    ModifierSelector Selector,
    int? SelectionId,
    DogmaAttributeId Attribute,
    DogmaOperation Operation,
    double Value
);

public sealed record WormholeEnvironment(int TypeId, ImmutableArray<WormholeSystemModifier> Modifiers)
{
    public ImmutableArray<int> EffectIds { get; init; } = [];
}

public sealed record WormholeSystemEffect(int EffectId, long SourceItemId, int SourceTypeId);

public interface IWormholeEnvironments
{
    IReadOnlyList<WormholeSystemEffect> Effects(int systemId);
}

public sealed record WormholeEffectTarget(long Id, int GroupId, IReadOnlySet<int> RequiredSkills);

public static class WormholeEnvironmentRules
{
    public static IEnumerable<DogmaModifier> Modifiers(
        WormholeEnvironment environment,
        long shipId,
        IReadOnlyList<WormholeEffectTarget> modules,
        IReadOnlyList<WormholeEffectTarget> locations,
        IReadOnlyList<WormholeEffectTarget> ownerTargets
    )
    {
        foreach (var modifier in environment.Modifiers)
        {
            IEnumerable<long> targets = modifier.Selector switch
            {
                ModifierSelector.Item => [shipId],
                ModifierSelector.Location => modules.Select(m => m.Id),
                ModifierSelector.Group => locations.Where(m => m.GroupId == modifier.SelectionId).Select(m => m.Id),
                ModifierSelector.RequiredSkill => locations
                    .Where(m => m.RequiredSkills.Contains(modifier.SelectionId!.Value))
                    .Select(m => m.Id),
                ModifierSelector.OwnerRequiredSkill => ownerTargets
                    .Where(m => m.RequiredSkills.Contains(modifier.SelectionId!.Value))
                    .Select(m => m.Id),
                _ => throw new InvalidDataException("Unsupported wormhole effect selector."),
            };
            foreach (long target in targets)
            {
                // System effects do not enter fitted-module stacking penalties.
                yield return new(target, modifier.Attribute, modifier.Operation, new DogmaValue.Constant(modifier.Value), true);
            }
        }
    }
}
