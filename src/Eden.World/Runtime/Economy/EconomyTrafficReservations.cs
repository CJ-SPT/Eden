using System.Runtime.CompilerServices;
using Eden.Data.Catalog.Capsuleers;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.World.Capsuleers;

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

    // Pools belong to a bounded catalog cache. Evicting a pool also releases its exhaustion marker.
    internal ConditionalWeakTable<TrafficSnapshot.TrafficCandidatePools, object> FullyReservedTrafficPools { get; } = new();

    private void TrafficIdentityChanged(EconomySavedTrafficIdentity? before, EconomySavedTrafficIdentity? after)
    {
        if (before is not null && before.CharacterId != after?.CharacterId)
        {
            FullyReservedTrafficPools.Clear();
        }
    }
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

    internal TrafficProfile? SelectTrafficProfile(TrafficSnapshot.TrafficCandidatePools pool, long seed, int system, int slot)
    {
        // A private assignment or release must never consume or publish a shared negative result.
        bool committed = !HasPrivateRows<SavedTrafficIdentity>();
        if (committed && store.FullyReservedTrafficPools.TryGetValue(pool, out _))
        {
            return null;
        }
        bool allCharactersTaken = true;
        foreach (var profile in pool.Candidates(seed, system, slot))
        {
            if (TrafficCharacterTaken(profile.CharacterId))
            {
                continue;
            }
            allCharactersTaken = false;
            if (!HasPilotName(profile.Name))
            {
                return profile;
            }
        }
        // Names can be changed independently. Only occupied character IDs prove the entire pool unavailable.
        if (committed && allCharactersTaken)
        {
            store.FullyReservedTrafficPools.GetOrCreateValue(pool);
        }
        return null;
    }
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
