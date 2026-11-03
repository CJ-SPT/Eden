namespace Eden.Gameplay.Dogma;

// Build 3662168 dogma.const operator IDs. Only the operators needed by the
// supported passive profiles are admitted at the catalog boundary.
public enum DogmaOperation
{
    PreAssignment = -1,
    PreMultiply = 0,
    Add = 2,
    PostMultiply = 4,
    PostPercent = 6,
    PostAssignment = 7,
}

public sealed record DogmaAttributeDefinition(
    DogmaAttributeId Id,
    double DefaultValue,
    bool Stackable = true,
    DogmaAttributeId? Maximum = null,
    DogmaAttributeId? Minimum = null,
    bool Integer = false
);

public sealed record DogmaNode(long Id, IReadOnlyDictionary<DogmaAttributeId, double> Attributes);

public abstract record DogmaValue
{
    public sealed record Constant(double Value) : DogmaValue;

    public sealed record Attribute(long ItemId, DogmaAttributeId AttributeId) : DogmaValue;
}

public sealed record DogmaModifier(
    long TargetId,
    DogmaAttributeId Attribute,
    DogmaOperation Operation,
    DogmaValue Source,
    bool ExemptFromStacking = false
);

public sealed class DogmaGraph
{
    private const double Percent = 100;
    private const int MaximumPenalizedModifiers = 8;

    // stackingNurfedAttribute.pyj: exp((index / 2.67) ** 2).
    private const double StackingDivisor = 2.67;

    // Native precedence puts -1 first. Enum.GetValues sorts negative IDs as
    // unsigned values, so it cannot supply the operator order.
    private static readonly DogmaOperation[] s_operationOrder =
    [
        DogmaOperation.PreAssignment,
        DogmaOperation.PreMultiply,
        DogmaOperation.Add,
        DogmaOperation.PostMultiply,
        DogmaOperation.PostPercent,
        DogmaOperation.PostAssignment,
    ];
    private readonly IReadOnlyDictionary<long, DogmaNode> _items;
    private readonly IReadOnlyDictionary<DogmaAttributeId, DogmaAttributeDefinition> _definitions;
    private readonly ILookup<(long Item, DogmaAttributeId Attribute), DogmaModifier> _incoming;
    private readonly Dictionary<(long Item, DogmaAttributeId Attribute), double> _values = [];
    private readonly HashSet<(long Item, DogmaAttributeId Attribute)> _evaluating = [];

    public DogmaGraph(
        IReadOnlyList<DogmaNode> items,
        IReadOnlyList<DogmaModifier> modifiers,
        IReadOnlyDictionary<DogmaAttributeId, DogmaAttributeDefinition> definitions
    )
    {
        _items = items.ToDictionary(i => i.Id);
        _definitions = definitions;
        _incoming = modifiers.ToLookup(m => (m.TargetId, m.Attribute));
    }

    public double Read(DogmaValue value) =>
        value switch
        {
            DogmaValue.Constant constant => constant.Value,
            DogmaValue.Attribute attribute => Read(attribute.ItemId, attribute.AttributeId),
            _ => throw new InvalidDataException("Unsupported dogma value."),
        };

    public double Read(long itemId, DogmaAttributeId attribute)
    {
        var key = (itemId, attribute);
        if (_values.TryGetValue(key, out double cached))
        {
            return cached;
        }
        if (!_evaluating.Add(key))
        {
            throw new InvalidDataException("Cyclic fitting modifiers.");
        }
        try
        {
            var definition = _definitions.GetValueOrDefault(attribute) ?? new(attribute, 0);
            double value = _items[itemId].Attributes.GetValueOrDefault(attribute, definition.DefaultValue);
            // Attribute.Update bounds the base value before applying modifiers.
            if (definition.Maximum is { } maximum)
            {
                value = Math.Min(value, Read(itemId, maximum));
            }
            if (definition.Minimum is { } minimum)
            {
                value = Math.Max(value, Read(itemId, minimum));
            }
            foreach (var operation in s_operationOrder)
            {
                var modifiers = _incoming[key].Where(m => m.Operation == operation).ToArray();
                if (operation is DogmaOperation.PreAssignment or DogmaOperation.PostAssignment)
                {
                    foreach (var modifier in modifiers)
                    {
                        value = Read(modifier.Source);
                    }
                    continue;
                }
                if (operation == DogmaOperation.Add)
                {
                    value += modifiers.Sum(m => Read(m.Source));
                    continue;
                }
                var factors = modifiers
                    .Select(m =>
                        (
                            Factor: operation == DogmaOperation.PostPercent ? 1 + Read(m.Source) / Percent : Read(m.Source),
                            Exempt: definition.Stackable || m.ExemptFromStacking || operation == DogmaOperation.PreMultiply
                        )
                    )
                    .ToArray();
                value *= factors.Where(f => f.Exempt).Aggregate(1d, (v, f) => v * f.Factor);
                // Bonuses and penalties occupy separate strongest-first sequences.
                foreach (bool increasing in new[] { false, true })
                {
                    var penalized = factors
                        .Where(f => !f.Exempt && (f.Factor >= 1) == increasing)
                        .Select(f => f.Factor)
                        .OrderByDescending(f => Math.Abs(f - 1))
                        .Take(MaximumPenalizedModifiers);
                    int index = 0;
                    foreach (double factor in penalized)
                    {
                        value *= 1 + (factor - 1) / Math.Exp(Math.Pow(index++ / StackingDivisor, 2));
                    }
                }
            }
            // Attribute.Update clamps both the base and the final modified value.
            if (definition.Maximum is { } finalMaximum)
            {
                value = Math.Min(value, Read(itemId, finalMaximum));
            }
            if (definition.Minimum is { } finalMinimum)
            {
                value = Math.Max(value, Read(itemId, finalMinimum));
            }
            if (definition.Integer)
            {
                value = Math.Truncate(value);
            }
            if (attribute is DogmaAttributeId.Cpu or DogmaAttributeId.Power or DogmaAttributeId.CpuOutput or DogmaAttributeId.PowerOutput)
            {
                value = Math.Round(value, 2);
            }
            if (!double.IsFinite(value))
            {
                throw new InvalidDataException("Non-finite fitting attribute.");
            }
            _values.Add(key, value);
            return value;
        }
        finally
        {
            _evaluating.Remove(key);
        }
    }

    public Dictionary<DogmaAttributeId, double> Attributes(long itemId) =>
        _items[itemId]
            .Attributes.Keys.Concat(_incoming.Where(group => group.Key.Item == itemId).Select(group => group.Key.Attribute))
            .Distinct()
            .ToDictionary(id => id, id => Read(itemId, id));
}
