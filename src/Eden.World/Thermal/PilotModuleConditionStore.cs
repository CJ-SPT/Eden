using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Thermal;

[Injectable]
internal sealed class PilotModuleConditionStore(WorldDatabase database) : IPilotModuleConditions
{
    public IReadOnlyList<FittedModuleCondition> Read(long pilot, long ship)
    {
        var live = LiveWorld.Find(database.Path);
        using var ownership = live?.Enter();
        var view = live?.Reads is { } resident ? resident.ModuleConditions(pilot, ship, () => ReadSql(pilot, ship)) : ReadSql(pilot, ship);
        return live?.Heat.Conditions(ship, view) ?? view;
    }

    private IReadOnlyList<FittedModuleCondition> ReadSql(long pilot, long ship)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (!world.Ships.Any(s => s.Id == ship && s.OwnerId == pilot))
        {
            throw new InvalidDataException("Read module conditions only on an owned ship.");
        }
        var saved = world
            .Set<SavedModuleCondition>()
            .Where(c => world.Set<OwnedItem>().Any(i => i.Id == c.ModuleId && i.OwnerId == pilot && i.LocationId == ship))
            .ToDictionary(c => c.ModuleId, c => c.Damage);
        var result = Array.AsReadOnly(
            world
                .Set<OwnedItem>()
                .Where(i => i.OwnerId == pilot && i.LocationId == ship)
                .OrderBy(i => i.Id)
                .AsEnumerable()
                .Where(i =>
                    InventoryDefinitions.IsLowSlot(i.FlagId)
                    || InventoryDefinitions.IsMediumSlot(i.FlagId)
                    || InventoryDefinitions.IsHighSlot(i.FlagId)
                )
                .Where(i =>
                    saved.ContainsKey(i.Id)
                    || catalog.FittingAttributes(i.TypeId).GetValueOrDefault(DogmaAttributeId.StructureHitPoints) > 0
                )
                .Select(i => new FittedModuleCondition(
                    ModuleConditionPersistence.Read(catalog, i, saved.GetValueOrDefault(i.Id)),
                    i.Online,
                    OverloadCatalog.Definition(catalog, i.TypeId)?.EffectId ?? 0
                ))
                .ToArray()
        );
        transaction.Commit();
        return result;
    }
}
