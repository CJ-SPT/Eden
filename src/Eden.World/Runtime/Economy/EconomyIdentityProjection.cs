using Eden.Data.World.Characters;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;

namespace Eden.World.Runtime.Economy;

internal sealed partial class EconomyTable<TRow, TState>
{
    internal bool HasIdPrimaryColumn => primaryColumn == "Id";
}

internal sealed partial class EconomySet<TRow, TState>
{
    internal override bool TryMaximumIdentity(long minimum, out long? maximum)
    {
        maximum = null;
        if (
            !table.HasIdPrimaryColumn
            || typeof(TRow) == typeof(OwnedItem)
            || typeof(TRow) == typeof(FlightPosition)
            || typeof(TRow) == typeof(Pilot)
            || typeof(TRow) == typeof(Ship)
        )
        {
            return false;
        }
        // Identity-only maxima need neither row clones nor candidate arrays. Local
        // additions, removals and visibility still belong to this private action.
        foreach (var key in table.Keys)
        {
            if (
                key.Id >= minimum
                && (maximum is null || key.Id > maximum)
                && !_removed.Contains(key)
                && !_local.ContainsKey(key)
                && transaction.VisibleScalar<TRow>(key)
            )
            {
                maximum = key.Id;
            }
        }
        foreach (var (key, row) in _local)
        {
            long id = table.Key(row).Id;
            if (id >= minimum && (maximum is null || id > maximum) && !_removed.Contains(key) && transaction.Visible(row))
            {
                maximum = id;
            }
        }
        return true;
    }
}
