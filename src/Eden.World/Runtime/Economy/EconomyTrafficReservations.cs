using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;

namespace Eden.World.Runtime.Economy;

internal sealed class EconomyNameComparer : IEqualityComparer<object>
{
    public new bool Equals(object? x, object? y) =>
        x is string left && y is string right ? StringComparer.OrdinalIgnoreCase.Equals(left, right) : object.Equals(x, y);

    public int GetHashCode(object value) => value is string name ? StringComparer.OrdinalIgnoreCase.GetHashCode(name) : value.GetHashCode();
}

internal sealed partial class ResidentEconomy
{
    internal int SavedPilotCount => ((EconomyTable<Pilot, EconomyPilot>)_tables[typeof(Pilot)]).RowCount;
}

internal sealed partial class EconomyTransaction
{
    internal override int PilotCount =>
        store.SavedPilotCount
        + Set<Pilot>().Local.Count(p => !store.IsPilot(p.Id))
        - Set<Pilot>().Local.Count(p => store.IsPilot(p.Id) && Entry(p).State == Microsoft.EntityFrameworkCore.EntityState.Deleted);

    internal override bool HasPilotName(string name) => PilotNameTaken(name, null);

    private bool PilotNameTaken(string name, long? excluded) =>
        ((EconomySet<Pilot, EconomyPilot>)Pilots).HasReservation(
            nameof(Pilot.Name),
            name,
            static (pilot, candidate) => StringComparer.OrdinalIgnoreCase.Equals(pilot.Name, candidate),
            excluded
        );

    internal override bool TrafficCharacterTaken(long character) =>
        ((EconomySet<SavedTrafficIdentity, EconomySavedTrafficIdentity>)Set<SavedTrafficIdentity>()).HasReservation(
            nameof(SavedTrafficIdentity.CharacterId),
            character,
            static (identity, candidate) => identity.CharacterId == candidate
        );
}

internal sealed partial class EconomySet<TRow, TState>
{
    internal bool HasReservation<TValue>(string column, TValue value, Func<TRow, TValue, bool> matches, long? excluded = null)
        where TValue : notnull
    {
        var committed = table.Indexed(column, value);
        if (_local.Count == 0 && excluded is null)
        {
            return committed.Count != 0;
        }
        foreach (var (key, row) in _local)
        {
            if (key.Id != excluded && matches(row, value) && !_removed.Contains(key))
            {
                return true;
            }
        }
        // A privately changed or removed row shadows its committed index membership.
        foreach (var key in committed)
        {
            if (key.Id != excluded && !_local.ContainsKey(key))
            {
                return true;
            }
        }
        return false;
    }
}
