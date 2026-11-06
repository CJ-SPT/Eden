using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Protocol;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotInventoryStackStore(WorldDatabase database) : IPilotInventoryStacks
{
    private sealed record StackIdentity(int TypeId, double? VolumeOverride, double CrystalDamage);

    public IReadOnlyList<InventoryChange> StackAll(long pilotId, long locationId, int flagId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var ship = pilot.ActiveShip;
        if (
            !(locationId == ship.StationId && flagId == (int)InventoryFlag.Hangar && ship.Position is null)
            && !(locationId == ship.Id && ShipHoldRules.IsHold(flagId))
        )
        {
            throw new InvalidDataException("Stack items in your current station hangar or active ship hold.");
        }
        var stacks = world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == pilotId && i.LocationId == locationId && i.FlagId == flagId && !i.Singleton && !i.Online)
            .OrderBy(i => i.Id)
            .ToArray();
        var ids = stacks.Select(i => i.Id).ToArray();
        var containers = world.Set<OwnedItem>().Where(i => ids.Contains(i.LocationId)).Select(i => i.LocationId).ToHashSet();
        var installed = world.Set<IndustryJob>().Where(j => j.Status == IndustryJobStatus.Installed).Select(j => j.BlueprintId).ToHashSet();
        List<InventoryChange> changes = [];
        foreach (
            var group in stacks
                .Where(i => !containers.Contains(i.Id) && !installed.Contains(i.Id))
                .GroupBy(i => new StackIdentity(i.TypeId, i.VolumeOverride, i.CrystalDamage))
                .Where(g => g.Count() > 1)
        )
        {
            var retained = group.First();
            var previous = PilotInventoryStore.Read(catalog, retained);
            retained.Quantity = InventoryStackRules.CombinedQuantity(group.Select(i => i.Quantity));
            changes.Add(new(PilotInventoryStore.Read(catalog, retained), previous));
            foreach (var removed in group.Skip(1))
            {
                var before = PilotInventoryStore.Read(catalog, removed);
                changes.Add(new(before with { Quantity = 0 }, before));
                world.Remove(removed);
            }
        }
        world.SaveChanges();
        transaction.Commit();
        return changes;
    }
}
