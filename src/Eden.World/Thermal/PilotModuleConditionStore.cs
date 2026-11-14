using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Thermal;

[Injectable]
internal sealed class PilotModuleConditionStore(WorldDatabase database) : IPilotModuleConditions
{
    public IReadOnlyList<FittedModuleCondition> Read(long pilot, long ship)
    {
        return LiveWorld.ExecuteForShip(database.Path, ship, () => ReadOwned(pilot, ship), nameof(Read));
    }

    private IReadOnlyList<FittedModuleCondition> ReadOwned(long pilot, long ship)
    {
        var live = LiveWorld.Find(database.Path);
        using var ownership = live?.Enter();
        var view = live?.Reads is { } resident
            ? resident.ModuleConditions(pilot, ship, () => ReadBaseline(pilot, ship))
            : ReadBaseline(pilot, ship);
        return live?.Heat.Conditions(ship, view) ?? view;
    }

    private IReadOnlyList<FittedModuleCondition> ReadBaseline(long pilot, long ship)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (LiveWorld.Find(database.Path) is { } live)
        {
            return live.Economy.Inspect(world => ReadConditions(world, catalog, pilot, ship), new(pilots: [pilot], ships: [ship]));
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var result = ReadConditions(new SqlWorldAction(world), catalog, pilot, ship);
        transaction.Commit();
        return result;
    }

    private static IReadOnlyList<FittedModuleCondition> ReadConditions(WorldAction world, CatalogDbContext catalog, long pilot, long ship)
    {
        if (!world.Filter<Ship>(s => s.Id == ship && s.OwnerId == pilot).Any())
        {
            throw new InvalidDataException("Read module conditions only on an owned ship.");
        }
        var modules = world
            .Filter<OwnedItem>(i => i.OwnerId == pilot && i.LocationId == ship)
            .OrderBy(i => i.Id)
            .AsEnumerable()
            .Where(i =>
                InventoryDefinitions.IsLowSlot(i.FlagId)
                || InventoryDefinitions.IsMediumSlot(i.FlagId)
                || InventoryDefinitions.IsHighSlot(i.FlagId)
            )
            .ToArray();
        long[] ids = modules.Select(module => module.Id).ToArray();
        var saved = world
            .Filter<SavedModuleCondition>(c => Enumerable.Contains(ids, c.ModuleId))
            .ToDictionary(c => c.ModuleId, c => c.Damage);
        return Array.AsReadOnly(
            modules
                .Where(module =>
                    saved.ContainsKey(module.Id)
                    || catalog.FittingAttributes(module.TypeId).GetValueOrDefault(DogmaAttributeId.StructureHitPoints) > 0
                )
                .Select(module => new FittedModuleCondition(
                    ModuleConditionPersistence.Read(catalog, module, saved.GetValueOrDefault(module.Id)),
                    module.Online,
                    OverloadCatalog.Definition(catalog, module.TypeId)?.EffectId ?? 0
                ))
                .ToArray()
        );
    }
}
