using System.Linq.Expressions;
using Eden.Data.World.Characters;
using Eden.Data.World.Contracts;
using Eden.Data.World.Inventory;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    internal bool VisibleScalar<TRow>(EconomyKey key)
        where TRow : class
    {
        if (typeof(TRow) == typeof(Pilot))
        {
            var pilots = (EconomyTable<Pilot, EconomyPilot>)_tables[typeof(Pilot)];
            var ships = (EconomyTable<Ship, EconomyShip>)_tables[typeof(Ship)];
            return pilots.Snapshot(key) is { } pilot
                && ships.Snapshot(new(pilot.ActiveShipId)) is { } ship
                && ship.OwnerId == pilot.Id
                && !_tables[typeof(SavedContractEscrow)].Contains(new(ship.Id));
        }
        return typeof(TRow) != typeof(Ship) || !_tables[typeof(SavedContractEscrow)].Contains(key);
    }
}

internal sealed partial class EconomySet<TRow, TState>
{
    internal override IEnumerable<TResult> Project<TResult>(
        Expression<Func<TRow, TResult>> selector,
        Expression<Func<TRow, bool>>? predicate
    )
    {
        // Item balances and coordinates may have a newer movement/combat overlay.
        // Keep those reads on the linked view until that overlay has been applied.
        if (typeof(TRow) == typeof(OwnedItem) || typeof(TRow) == typeof(Eden.Data.World.Docking.FlightPosition))
        {
            return base.Project(selector, predicate);
        }
        Func<TState, TResult> select;
        Func<TState, bool>? match = null;
        try
        {
            new ArrayProjectionGuard().Visit(selector.Body);
            var state = Expression.Parameter(typeof(TState), "saved");
            var rewrite = new EconomyPredicate(selector.Parameters[0], state).Visit(selector.Body);
            select = Expression.Lambda<Func<TState, TResult>>(rewrite, state).Compile(preferInterpretation: true);
            if (predicate is not null)
            {
                var filter = new EconomyPredicate(predicate.Parameters[0], state).Visit(predicate.Body);
                match = Expression.Lambda<Func<TState, bool>>(filter, state).Compile(preferInterpretation: true);
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return base.Project(selector, predicate);
        }
        var localSelect = selector.Compile(preferInterpretation: true);
        var localMatch = predicate?.Compile(preferInterpretation: true);
        var keys = (predicate is null ? table.Keys : table.Matching(predicate)).Concat(_local.Keys).Distinct().ToArray();
        return Read();

        IEnumerable<TResult> Read()
        {
            foreach (var key in keys)
            {
                if (_removed.Contains(key))
                {
                    continue;
                }
                if (_local.TryGetValue(key, out var row))
                {
                    if (transaction.Visible(row) && (localMatch is null || localMatch(row)))
                    {
                        yield return localSelect(row);
                    }
                }
                else if (table.Snapshot(key) is { } saved && transaction.VisibleScalar<TRow>(key) && (match is null || match(saved)))
                {
                    yield return select(saved);
                }
            }
        }
    }

    private sealed class ArrayProjectionGuard : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Type.IsArray)
            {
                throw new InvalidOperationException("Array projections require a private copy.");
            }
            return base.VisitMember(node);
        }
    }
}

internal sealed partial class EconomyTransaction
{
    internal bool VisibleScalar<TRow>(EconomyKey key)
        where TRow : class => store.VisibleScalar<TRow>(key);
}
