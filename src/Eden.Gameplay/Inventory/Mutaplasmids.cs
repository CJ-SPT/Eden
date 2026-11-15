using System.Collections.Immutable;
using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Inventory;

public sealed record MutationRange(DogmaAttributeId Attribute, double Minimum, double Maximum);

public sealed record MutationRoll(DogmaAttributeId Attribute, double Fraction);

public sealed record MutatedItem(
    long ItemId,
    int TypeId,
    int SourceTypeId,
    long SourceItemId,
    int MutatorTypeId,
    long MutatorItemId,
    long CreatorId,
    long CreatedAt,
    ImmutableDictionary<DogmaAttributeId, double> Attributes,
    ImmutableDictionary<DogmaAttributeId, double> Modifiers
);

public sealed record MutationResult(MutatedItem Item, IReadOnlyList<InventoryChange> Changes);

public interface IMutaplasmids
{
    MutatedItem? Read(long itemId);
    MutationResult Create(long pilotId, long mutatorId, long sourceId);
}

public static class MutationRules
{
    public static ImmutableDictionary<DogmaAttributeId, double> Apply(
        IReadOnlyDictionary<DogmaAttributeId, double> basis,
        IReadOnlyList<MutationRange> ranges,
        IReadOnlyList<MutationRoll> rolls
    )
    {
        if (
            ranges.Count == 0
            || ranges.Select(r => r.Attribute).Distinct().Count() != ranges.Count
            || rolls.Count != ranges.Count
            || rolls.Select(r => r.Attribute).Distinct().Count() != rolls.Count
        )
        {
            throw new InvalidDataException("Invalid mutation definition or roll set.");
        }
        var result = basis.ToImmutableDictionary().ToBuilder();
        foreach (var range in ranges)
        {
            var roll = rolls.SingleOrDefault(r => r.Attribute == range.Attribute);
            if (
                roll is null
                || !double.IsFinite(roll.Fraction)
                || roll.Fraction is < 0 or > 1
                || !double.IsFinite(range.Minimum)
                || !double.IsFinite(range.Maximum)
                || range.Minimum > range.Maximum
                || !basis.TryGetValue(range.Attribute, out double value)
                || !double.IsFinite(value)
            )
            {
                throw new InvalidDataException("Invalid mutation attribute or roll.");
            }
            double mutated = value * (range.Minimum + roll.Fraction * (range.Maximum - range.Minimum));
            if (!double.IsFinite(mutated))
            {
                throw new InvalidDataException("Mutation exceeded the supported attribute range.");
            }
            result[range.Attribute] = mutated;
        }
        return result.ToImmutable();
    }
}
