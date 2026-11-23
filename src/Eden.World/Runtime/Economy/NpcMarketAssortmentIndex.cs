using Eden.Gameplay.Market;

namespace Eden.World.Runtime.Economy;

internal sealed class NpcMarketAssortmentIndex()
    : EconomyIndex<EconomyNpcMarketOrder>(nameof(NpcMarketAssortment), static row => Assortment(row))
{
    // A snapshot has one row per station and source order. Keep each identity and
    // its row ID inline. Preserve collisions from older saves for normal validation.
    private readonly Dictionary<(int Station, long SourceOrder), long> _standardOrders = [];
    private readonly Dictionary<(int Station, long SourceOrder), HashSet<long>> _duplicates = [];

    private static NpcMarketAssortment Assortment(EconomyNpcMarketOrder row) =>
        NpcMarketAssortment.For(row.StationId, row.TypeId, row.Bid, row.Kind, row.SourceOrderId);

    internal override IReadOnlyCollection<EconomyKey> Find(object? key)
    {
        if (key is not NpcMarketAssortment { Kind: NpcMarketKind.Standard } assortment)
        {
            return base.Find(key);
        }
        if (assortment is { TypeId: not 0 } or { Bid: true })
        {
            return [];
        }
        var identity = (assortment.StationId, assortment.SourceOrderId);
        if (_duplicates.TryGetValue(identity, out var duplicates))
        {
            return duplicates.Select(static id => new EconomyKey(id)).ToArray();
        }
        return _standardOrders.TryGetValue(identity, out long orderId) ? [new(orderId)] : [];
    }

    internal override void Clear()
    {
        base.Clear();
        _standardOrders.Clear();
        _duplicates.Clear();
    }

    internal override void Add(EconomyKey id, EconomyNpcMarketOrder row)
    {
        if (row.Kind != NpcMarketKind.Standard)
        {
            base.Add(id, row);
            return;
        }
        var identity = (row.StationId, row.SourceOrderId);
        if (_standardOrders.TryAdd(identity, id.Id) || _standardOrders[identity] == id.Id)
        {
            return;
        }
        if (_duplicates.TryGetValue(identity, out var duplicates))
        {
            duplicates.Add(id.Id);
        }
        else
        {
            _duplicates.Add(identity, [_standardOrders[identity], id.Id]);
        }
    }

    internal override void Remove(EconomyKey id, EconomyNpcMarketOrder row)
    {
        if (row.Kind != NpcMarketKind.Standard)
        {
            base.Remove(id, row);
            return;
        }
        var identity = (row.StationId, row.SourceOrderId);
        if (_duplicates.TryGetValue(identity, out var duplicates))
        {
            duplicates.Remove(id.Id);
            if (duplicates.Count == 1)
            {
                _standardOrders[identity] = duplicates.Single();
                _duplicates.Remove(identity);
            }
        }
        else if (_standardOrders.TryGetValue(identity, out long orderId) && orderId == id.Id)
        {
            _standardOrders.Remove(identity);
        }
    }

    internal override void Replace(EconomyKey id, EconomyNpcMarketOrder before, EconomyNpcMarketOrder after)
    {
        if (Assortment(before) != Assortment(after))
        {
            Remove(id, before);
            Add(id, after);
        }
    }
}
