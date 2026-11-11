using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    // Read new memberships once per committed batch. Old memberships come from the
    // published views, so transfers and deletes invalidate both ends, including an
    // empty destination. Coordinate projection never changes another system's view.
    private void ObserveSystemContacts(IReadOnlyCollection<CombatRowChange> changes)
    {
        if (!_directionalContacts.HasEntries && !_wrecks.HasEntries && !_drones.HasEntries)
        {
            return;
        }
        Dictionary<long, int> locations = [];
        var ships = changes.Where(c => c.Table is "WorldShip" or "WorldPosition").Select(c => c.Id).Distinct().ToArray();
        var npcs = changes.Where(c => c.Table == "WorldNpc").Select(c => c.Id).ToArray();
        var wrecks = changes.Where(c => c.Table == "WorldWreck").Select(c => c.Id).ToArray();
        var launches = changes.Where(c => c.Table == "WorldPlanetaryLaunch").Select(c => c.Id).ToArray();
        var offices = changes.Where(c => c.Table == "WorldPlanetaryOffice").Select(c => c.Id).ToArray();
        var drones = changes
            .Where(c => c.Table == "WorldDrone" || c.Table == "WorldItem" && !c.Resident)
            .Select(c => c.Id)
            .Distinct()
            .ToArray();
        using WorldDbContext world = new(database.Path) { IsResidentProjection = true };
        if (ships.Length > 0)
        {
            var rows = world
                .Ships.Where(s => ships.Contains(s.Id) && s.Position != null)
                .Select(s => new
                {
                    s.Id,
                    s.Position!.SystemId,
                    s.StationId,
                })
                .ToArray();
            foreach (var row in rows)
            {
                locations[row.Id] = row.SystemId ?? database.Catalog.FindStation(row.StationId)!.SolarSystemId;
            }
        }
        if (npcs.Length > 0)
        {
            foreach (var row in world.Set<WorldNpc>().Where(n => npcs.Contains(n.Id)).Select(n => new { n.Id, n.SystemId }))
            {
                locations[row.Id] = row.SystemId;
            }
        }
        if (wrecks.Length > 0)
        {
            foreach (var row in world.Set<WorldWreck>().Where(w => wrecks.Contains(w.Id)).Select(w => new { w.Id, w.SystemId }))
            {
                locations[row.Id] = row.SystemId;
            }
        }
        if (drones.Length > 0)
        {
            foreach (var row in world.Set<WorldDrone>().Where(d => drones.Contains(d.ItemId)).Select(d => new { d.ItemId, d.SystemId }))
            {
                if (row.SystemId is int system)
                {
                    locations[row.ItemId] = system;
                }
            }
        }
        var inventoryIds = changes.Where(c => c.Table == "WorldItemMembership").Select(c => c.Id).ToArray();
        var inventoryLocations =
            _wrecks.HasEntries && inventoryIds.Length > 0
                ? changes.Any(c => c.Resident)
                    ? inventoryIds.Select(id => owner.Economy.Peek<OwnedItem>(id)?.LocationId).OfType<long>().ToHashSet()
                    : world.Set<OwnedItem>().Where(i => inventoryIds.Contains(i.Id)).Select(i => i.LocationId).ToHashSet()
                : [];
        bool Arrived(int system, CombatRowChange change) => locations.TryGetValue(change.Id, out int current) && current == system;
        if (launches.Length > 0)
        {
            foreach (var launch in world.Set<Eden.Data.World.Planetary.SavedPlanetaryLaunch>().Where(l => launches.Contains(l.Id)))
            {
                locations[launch.Id] = launch.SystemId;
            }
        }
        if (offices.Length > 0)
        {
            foreach (var office in world.Set<Eden.Data.World.Planetary.SavedPlanetaryOffice>().Where(o => offices.Contains(o.PlanetId)))
            {
                locations[office.PlanetId] = office.SystemId;
            }
        }
        _directionalContacts.ObserveValues(
            changes,
            (system, entries, change) =>
                change.Table is "WorldLogoff" or "WorldCapsuleerSuspension"
                || Arrived(system, change)
                || entries.Any(e => e.Contact.Id == change.Id)
        );
        _wrecks.ObserveValues(
            changes,
            (system, view, change) =>
                change.Table == "WorldItemMembership"
                    ? view.Items.Contains(change.Id) || view.Contacts.Any(c => inventoryLocations.Contains(c.Id))
                    : Arrived(system, change) || view.Contacts.Any(c => c.Id == change.Id || c.SourceId == change.Id)
        );
        _drones.ObserveValues(
            changes,
            (system, entries, change) =>
                Arrived(system, change)
                || entries.Any(d =>
                    change.Table switch
                    {
                        "WorldPilot" or "WorldFitRevision" => d.PilotId == change.Id,
                        "WorldShip" => d.ShipId == change.Id,
                        _ => d.Id == change.Id,
                    }
                )
        );
    }
}
