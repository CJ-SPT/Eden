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

    public sealed record Product(DogmaValue Left, DogmaValue Right) : DogmaValue;

    public sealed record Sum(DogmaValue Left, DogmaValue Right) : DogmaValue;

    public static IEnumerable<Attribute> References(DogmaValue value) =>
        value switch
        {
            Attribute attribute => [attribute],
            Product product => References(product.Left).Concat(References(product.Right)),
            Sum sum => References(sum.Left).Concat(References(sum.Right)),
            _ => [],
        };
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

    private readonly IReadOnlyDictionary<long, DogmaNode> _items;
    private readonly IReadOnlyDictionary<DogmaAttributeId, DogmaAttributeDefinition> _definitions;
    private readonly Dictionary<(long Item, DogmaAttributeId Attribute), IGrouping<DogmaOperation, DogmaModifier>[]> _incoming;
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
        // Signed operator IDs give native precedence, including PreAssignment (-1).
        // GroupBy retains source order for multiple modifiers in the same operation.
        _incoming = modifiers
            .GroupBy(m => (m.TargetId, m.Attribute))
            .ToDictionary(group => group.Key, group => group.GroupBy(m => m.Operation).OrderBy(bucket => (int)bucket.Key).ToArray());
    }

    public double Read(DogmaValue value) =>
        value switch
        {
            DogmaValue.Constant constant => constant.Value,
            DogmaValue.Attribute attribute => Read(attribute.ItemId, attribute.AttributeId),
            DogmaValue.Product product => Read(product.Left) * Read(product.Right),
            DogmaValue.Sum sum => Read(sum.Left) + Read(sum.Right),
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
            var definition = _definitions.GetValueOrDefault(attribute);
            double value = _items[itemId].Attributes.GetValueOrDefault(attribute, definition?.DefaultValue ?? 0);
            // Attribute.Update bounds the base value before applying modifiers.
            if (definition?.Maximum is { } maximum)
            {
                value = Math.Min(value, Read(itemId, maximum));
            }
            if (definition?.Minimum is { } minimum)
            {
                value = Math.Max(value, Read(itemId, minimum));
            }
            if (_incoming.TryGetValue(key, out var operations))
            {
                value = ApplyModifiers(value, operations, definition?.Stackable ?? true);
            }
            // Attribute.Update clamps both the base and the final modified value.
            if (definition?.Maximum is { } finalMaximum)
            {
                value = Math.Min(value, Read(itemId, finalMaximum));
            }
            if (definition?.Minimum is { } finalMinimum)
            {
                value = Math.Max(value, Read(itemId, finalMinimum));
            }
            if (definition?.Integer == true)
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

    private double ApplyModifiers(double value, IGrouping<DogmaOperation, DogmaModifier>[] operations, bool stackable)
    {
        foreach (var modifiers in operations)
        {
            switch (modifiers.Key)
            {
                case DogmaOperation.PreAssignment:
                case DogmaOperation.PostAssignment:
                    foreach (var modifier in modifiers)
                    {
                        value = Read(modifier.Source);
                    }
                    break;
                case DogmaOperation.Add:
                    double sum = 0;
                    foreach (var modifier in modifiers)
                    {
                        sum += Read(modifier.Source);
                    }
                    value += sum;
                    break;
                case DogmaOperation.PreMultiply:
                case DogmaOperation.PostMultiply:
                case DogmaOperation.PostPercent:
                    value = Multiply(value, modifiers, stackable);
                    break;
            }
        }
        return value;
    }

    private double Multiply(double value, IGrouping<DogmaOperation, DogmaModifier> modifiers, bool stackable)
    {
        double product = 1;
        List<double>? penalties = null;
        List<double>? bonuses = null;
        foreach (var modifier in modifiers)
        {
            double source = Read(modifier.Source);
            double factor = modifiers.Key == DogmaOperation.PostPercent ? 1 + source / Percent : source;
            if (stackable || modifier.ExemptFromStacking)
            {
                product *= factor;
            }
            else if (factor >= 1)
            {
                (bonuses ??= []).Add(factor);
            }
            else
            {
                (penalties ??= []).Add(factor);
            }
        }
        // Apply exempt factors first. Reductions and increases have separate rankings.
        return ApplyStacking(ApplyStacking(value * product, penalties), bonuses);
    }

    private static double ApplyStacking(double value, List<double>? factors)
    {
        if (factors is not null)
        {
            int index = 0;
            foreach (double factor in factors.OrderByDescending(f => Math.Abs(f - 1)).Take(MaximumPenalizedModifiers))
            {
                value *= 1 + (factor - 1) / Math.Exp(Math.Pow(index++ / StackingDivisor, 2));
            }
        }
        return value;
    }

    public Dictionary<DogmaAttributeId, double> Attributes(long itemId)
    {
        var raw = _items[itemId].Attributes;
        Dictionary<DogmaAttributeId, double> values = new(raw.Count);
        foreach (var attribute in raw.Keys)
        {
            values.Add(attribute, Read(itemId, attribute));
        }
        foreach (var target in _incoming.Keys)
        {
            if (target.Item == itemId && !raw.ContainsKey(target.Attribute))
            {
                values.Add(target.Attribute, Read(itemId, target.Attribute));
            }
        }
        return values;
    }
}
