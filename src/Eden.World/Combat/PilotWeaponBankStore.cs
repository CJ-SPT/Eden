using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat.Weapons;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

[Injectable]
internal sealed partial class PilotWeaponBankStore(WorldDatabase database, TimeProvider clock) : IPilotWeaponBanks
{
    internal static IReadOnlyList<WeaponBank> Read(WorldDbContext world, long shipId) =>
        world
            .Set<SavedWeaponLink>()
            .Where(l => l.ShipId == shipId)
            .AsNoTracking()
            .AsEnumerable()
            .GroupBy(l => l.MasterId)
            .OrderBy(g => g.Key)
            .Select(g => new WeaponBank(g.Key, g.Select(l => l.SlaveId).Order().ToArray()))
            .ToArray();

    internal static long[] Members(WorldDbContext world, long shipId, long moduleId)
    {
        var bank = Read(world, shipId).FirstOrDefault(b => b.MasterId == moduleId || b.SlaveIds.Contains(moduleId));
        return bank is null ? [moduleId] : new[] { bank.MasterId }.Concat(bank.SlaveIds).ToArray();
    }

    public IReadOnlyList<WeaponBank> Read(long pilotId, long shipId) =>
        WorldActionScope.Inspect(
            database,
            world =>
            {
                CheckShip(world, pilotId, shipId);
                return Read(world, shipId);
            }
        );

    private static void CheckShip(WorldAction world, long pilotId, long shipId)
    {
        if (world.Ships.Find(shipId)?.OwnerId != pilotId)
        {
            throw new InvalidDataException("The ship is unavailable.");
        }
    }

    public WeaponBankChange Change(long pilotId, long shipId, WeaponBankOperation operation, long masterId = 0, long sourceId = 0)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId], ships: [shipId]));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        CheckShip(world, pilotId, shipId);
        if (world.Pilots.Find(pilotId)?.ActiveShipId != shipId)
        {
            throw new InvalidDataException("Group weapons on your active ship.");
        }
        var modules = world
            .Filter<OwnedItem>(i => i.OwnerId == pilotId && i.LocationId == shipId && i.Singleton)
            .OrderBy(i => i.FlagId)
            .ToArray();
        var weapons = modules
            .Where(i =>
                i.Online
                && InventoryDefinitions.IsHighSlot(i.FlagId)
                && catalog.FittingType(i.TypeId)?.GroupId != (int)MissileLauncherGroup.BreacherPod
                && WeaponCatalog.Supported(catalog, i.TypeId, catalog.FittingType(i.TypeId)!.GroupId)
            )
            .Select(i => new GroupableWeapon(i.Id, i.TypeId))
            .ToArray();
        var before = Read(world, shipId);
        var result = WeaponGrouping.Change(before, weapons, operation, masterId, sourceId);
        var changed = before
            .Where(b => !result.Banks.Any(a => a.MasterId == b.MasterId && a.SlaveIds.SequenceEqual(b.SlaveIds)))
            .Concat(result.Banks.Where(b => !before.Any(a => a.MasterId == b.MasterId && a.SlaveIds.SequenceEqual(b.SlaveIds))))
            .SelectMany(b => new[] { b.MasterId }.Concat(b.SlaveIds))
            .ToHashSet();
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (changed.Any(id => world.Set<ModuleCooldown>().Find(id)?.ReadyAt > now))
        {
            throw new InvalidDataException("Finish weapon cycles and reloads before changing groups.");
        }
        var desired = result
            .Banks.SelectMany(bank => bank.SlaveIds.Select(slave => (Slave: slave, Master: bank.MasterId)))
            .ToDictionary(link => link.Slave, link => link.Master);
        foreach (var link in world.Filter<SavedWeaponLink>(l => l.ShipId == shipId).ToArray())
        {
            if (desired.Remove(link.SlaveId, out long master))
            {
                link.MasterId = master;
            }
            else
            {
                world.Remove(link);
            }
        }
        foreach (var (slave, master) in desired)
        {
            world.Add(
                new SavedWeaponLink
                {
                    ShipId = shipId,
                    MasterId = master,
                    SlaveId = slave,
                }
            );
        }
        transaction.Commit();
        return result;
    }

    internal static void Prune(WorldDbContext world, long shipId)
    {
        var modules = world
            .Set<OwnedItem>()
            .Where(i => i.LocationId == shipId && i.Online && i.Singleton)
            .AsEnumerable()
            .Where(i => InventoryDefinitions.IsHighSlot(i.FlagId))
            .ToDictionary(i => i.Id);
        foreach (var link in world.Set<SavedWeaponLink>().Where(l => l.ShipId == shipId))
        {
            if (
                !modules.TryGetValue(link.MasterId, out var master)
                || !modules.TryGetValue(link.SlaveId, out var slave)
                || master.TypeId != slave.TypeId
            )
            {
                world.Remove(link);
            }
        }
    }
}
