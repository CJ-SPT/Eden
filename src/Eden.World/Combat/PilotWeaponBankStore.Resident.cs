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

internal sealed partial class PilotWeaponBankStore
{
    internal static IReadOnlyList<WeaponBank> Read(WorldAction world, long shipId) =>
        world
            .Set<SavedWeaponLink>()
            .Where(l => l.ShipId == shipId)
            .AsNoTracking()
            .AsEnumerable()
            .GroupBy(l => l.MasterId)
            .OrderBy(g => g.Key)
            .Select(g => new WeaponBank(g.Key, g.Select(l => l.SlaveId).Order().ToArray()))
            .ToArray();

    internal static long[] Members(WorldAction world, long shipId, long moduleId)
    {
        var bank = Read(world, shipId).FirstOrDefault(b => b.MasterId == moduleId || b.SlaveIds.Contains(moduleId));
        return bank is null ? [moduleId] : new[] { bank.MasterId }.Concat(bank.SlaveIds).ToArray();
    }

    internal static void Prune(WorldAction world, long shipId)
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
