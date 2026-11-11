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
    internal IReadOnlyCollection<EconomyKey> PilotNames(string name) =>
        ((EconomyTable<Pilot, EconomyPilot>)_tables[typeof(Pilot)]).Indexed("Name", name);

    internal IReadOnlyCollection<EconomyKey> TrafficCharacters(long character) =>
        ((EconomyTable<SavedTrafficIdentity, EconomySavedTrafficIdentity>)_tables[typeof(SavedTrafficIdentity)]).Indexed(
            "CharacterId",
            character
        );

    internal int SavedPilotCount => ((EconomyTable<Pilot, EconomyPilot>)_tables[typeof(Pilot)]).Keys.Count();
}

internal sealed partial class EconomyTransaction
{
    internal override int PilotCount =>
        store.SavedPilotCount
        + Set<Pilot>().Local.Count(p => !store.IsPilot(p.Id))
        - Set<Pilot>().Local.Count(p => store.IsPilot(p.Id) && Entry(p).State == Microsoft.EntityFrameworkCore.EntityState.Deleted);

    internal override bool HasPilotName(string name) => PilotNameTaken(name, 0);

    private bool PilotNameTaken(string name, long excluded)
    {
        var local = Set<Pilot>().Local;
        return local.Any(p =>
                p.Id != excluded
                && Entry(p).State != Microsoft.EntityFrameworkCore.EntityState.Deleted
                && StringComparer.OrdinalIgnoreCase.Equals(p.Name, name)
            ) || store.PilotNames(name).Any(key => key.Id != excluded && !local.Any(p => p.Id == key.Id));
    }

    internal override bool TrafficCharacterTaken(long character)
    {
        var local = Set<SavedTrafficIdentity>().Local;
        return local.Any(p => Entry(p).State != Microsoft.EntityFrameworkCore.EntityState.Deleted && p.CharacterId == character)
            || store.TrafficCharacters(character).Any(key => !local.Any(p => p.PilotId == key.Id));
    }
}
