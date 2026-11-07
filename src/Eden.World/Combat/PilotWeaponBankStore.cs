using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat.Weapons;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

[Injectable]
internal sealed class PilotWeaponBankStore(WorldDatabase database, TimeProvider clock) : IPilotWeaponBanks
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

    public IReadOnlyList<WeaponBank> Read(long pilotId, long shipId)
    {
        using WorldDbContext world = new(database.Path);
        CheckShip(world, pilotId, shipId);
        return Read(world, shipId);
    }

    private static void CheckShip(WorldDbContext world, long pilotId, long shipId)
    {
        if (!world.Ships.Any(s => s.Id == shipId && s.OwnerId == pilotId))
        {
            throw new InvalidDataException("The ship is unavailable.");
        }
    }

    public WeaponBankChange Change(long pilotId, long shipId, WeaponBankOperation operation, long masterId = 0, long sourceId = 0)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        CheckShip(world, pilotId, shipId);
        if (!world.Pilots.Any(p => p.Id == pilotId && p.ActiveShipId == shipId))
        {
            throw new InvalidDataException("Group weapons on your active ship.");
        }
        var modules = world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == pilotId && i.LocationId == shipId && i.Singleton)
            .OrderBy(i => i.FlagId)
            .ToArray();
        var weapons = modules
            .Where(i =>
                i.Online
                && InventoryDefinitions.IsHighSlot(i.FlagId)
                && WeaponCatalog.Supported(catalog, i.TypeId, catalog.Set<ItemType>().Find(i.TypeId)!.GroupId)
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
        if (world.Set<ModuleCooldown>().Any(c => changed.Contains(c.ModuleId) && c.ReadyAt > now))
        {
            throw new InvalidDataException("Finish weapon cycles and reloads before changing groups.");
        }
        world.Set<SavedWeaponLink>().Where(l => l.ShipId == shipId).ExecuteDelete();
        foreach (var bank in result.Banks)
        {
            foreach (long slave in bank.SlaveIds)
            {
                world.Add(
                    new SavedWeaponLink
                    {
                        ShipId = shipId,
                        MasterId = bank.MasterId,
                        SlaveId = slave,
                    }
                );
            }
        }
        world.SaveChanges();
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
