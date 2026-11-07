using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.Fittings;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Fittings;

[Injectable]
internal sealed class PilotSavedFittingsStore(WorldDatabase database, TimeProvider clock) : IPilotSavedFittings
{
    public IReadOnlyList<SavedFitting> Read(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        CheckPilot(world, pilotId);
        return world
            .Set<SavedFittingRecord>()
            .AsNoTracking()
            .Where(f => f.OwnerId == pilotId)
            .OrderBy(f => f.Id)
            .ToArray()
            .Select(f => f.Snapshot())
            .ToArray();
    }

    public long Save(long pilotId, FittingPlan plan, long? fittingId = null)
    {
        SavedFittingRules.Check(plan);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var category = catalog
            .Set<ItemType>()
            .Where(t => t.Id == plan.ShipTypeId)
            .Select(t => (InventoryCategory?)t.Group.CategoryId)
            .SingleOrDefault();
        if (category is not (InventoryCategory.Ship or InventoryCategory.Structure))
        {
            throw new InvalidDataException("A saved fitting must describe a catalog ship or structure.");
        }
        var types = plan.Items.Select(i => i.TypeId).Distinct().ToArray();
        if (catalog.Set<ItemType>().Count(t => types.Contains(t.Id)) != types.Length)
        {
            throw new InvalidDataException("A fitting entry refers to an unknown catalog type.");
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        CheckPilot(world, pilotId);
        SavedFittingRecord saved;
        if (fittingId is { } id)
        {
            saved = Find(world, pilotId, id);
        }
        else
        {
            if (world.Set<SavedFittingRecord>().Count(f => f.OwnerId == pilotId) >= SavedFittingRules.MaximumFittings)
            {
                throw new InvalidDataException("This pilot already has 500 saved fittings.");
            }
            saved = new() { OwnerId = pilotId };
            world.Add(saved);
        }
        saved.Name = plan.Name.Trim();
        saved.Description = plan.Description;
        saved.ShipTypeId = plan.ShipTypeId;
        saved.ItemsJson = JsonSerializer.Serialize(plan.Items);
        saved.SavedAt = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        world.SaveChanges();
        transaction.Commit();
        return saved.Id;
    }

    public void Rename(long pilotId, long fittingId, string name, string description)
    {
        SavedFittingRules.CheckText(name, description);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var saved = Find(world, pilotId, fittingId);
        saved.Name = name.Trim();
        saved.Description = description;
        saved.SavedAt = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        world.SaveChanges();
        transaction.Commit();
    }

    public void Delete(long pilotId, long fittingId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        world.Remove(Find(world, pilotId, fittingId));
        world.SaveChanges();
        transaction.Commit();
    }

    private static SavedFittingRecord Find(WorldDbContext world, long pilotId, long fittingId) =>
        world.Set<SavedFittingRecord>().SingleOrDefault(f => f.Id == fittingId && f.OwnerId == pilotId)
        ?? throw new InvalidDataException("This fitting does not belong to the selected pilot.");

    private static void CheckPilot(WorldDbContext world, long pilotId)
    {
        if (!world.Pilots.Any(p => p.Id == pilotId))
        {
            throw new InvalidDataException("Unknown fitting owner.");
        }
    }
}
