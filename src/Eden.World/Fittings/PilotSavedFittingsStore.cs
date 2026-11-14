using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Corporations;
using Eden.Data.World.Fittings;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Fittings;

[Injectable]
internal sealed class PilotSavedFittingsStore(WorldDatabase database, TimeProvider clock) : IPilotSavedFittings
{
    public long Revision(long ownerId) => database.SavedFittingStamps.GetValueOrDefault(ownerId);

    public IReadOnlyList<SavedFitting> Read(long pilotId, long? ownerId = null)
    {
        long owner = ownerId ?? pilotId;
        return WorldActionScope.Inspect(
            database,
            world =>
            {
                Authorize(world, pilotId, owner, false);
                return world
                    .Filter<SavedFittingRecord>(f => f.OwnerId == owner)
                    .OrderBy(f => f.Id)
                    .AsEnumerable()
                    .Select(f => f.Snapshot())
                    .ToArray();
            },
            consistentFileRead: true
        );
    }

    public long Save(long pilotId, FittingPlan plan, long? fittingId = null, long? ownerId = null)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        Validate(catalog, plan);
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        long owner = ownerId ?? pilotId;
        Authorize(world, pilotId, owner, true);
        SavedFittingRecord saved;
        if (fittingId is { } id)
        {
            saved = Find(world, owner, id);
        }
        else
        {
            CheckCapacity(world, pilotId, owner, 1);
            saved = world.Add(new SavedFittingRecord { OwnerId = owner }).Entity;
        }
        Apply(saved, plan);
        world.AfterCommit(() => Changed(owner));
        transaction.Commit();
        return saved.Id;
    }

    public IReadOnlyList<FittingImportResult> SaveMany(long pilotId, long ownerId, IReadOnlyList<FittingImport> plans)
    {
        SavedFittingRules.CheckBatch(plans.Select(p => p.TemporaryId).ToArray(), true);
        using CatalogDbContext catalog = new(database.CatalogPath);
        foreach (var plan in plans)
        {
            Validate(catalog, plan.Plan);
        }
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        Authorize(world, pilotId, ownerId, true);
        CheckCapacity(world, pilotId, ownerId, plans.Count);
        List<SavedFittingRecord> saved = [];
        foreach (var plan in plans)
        {
            var row = new SavedFittingRecord { OwnerId = ownerId };
            Apply(row, plan.Plan);
            world.Add(row);
            saved.Add(row);
        }
        if (saved.Count > 0)
        {
            world.AfterCommit(() => Changed(ownerId));
        }
        transaction.Commit();
        return plans.Select((plan, index) => new FittingImportResult(plan.TemporaryId, saved[index].Id)).ToArray();
    }

    public void Rename(long pilotId, long fittingId, string name, string description, long? ownerId = null)
    {
        SavedFittingRules.CheckText(name, description);
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        long owner = ownerId ?? pilotId;
        Authorize(world, pilotId, owner, true);
        var saved = Find(world, owner, fittingId);
        saved.Name = name.Trim();
        saved.Description = description;
        saved.SavedAt = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        world.AfterCommit(() => Changed(owner));
        transaction.Commit();
    }

    public void Delete(long pilotId, long fittingId, long? ownerId = null) => DeleteMany(pilotId, ownerId ?? pilotId, [fittingId]);

    public IReadOnlyList<long> DeleteMany(long pilotId, long ownerId, IReadOnlyList<long> fittingIds)
    {
        SavedFittingRules.CheckBatch(fittingIds, false);
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        Authorize(world, pilotId, ownerId, true);
        // Resolve the whole selection before deleting any entry.
        var saved = fittingIds.Select(id => Find(world, ownerId, id)).ToArray();
        world.RemoveRange(saved);
        if (saved.Length > 0)
        {
            world.AfterCommit(() => Changed(ownerId));
        }
        transaction.Commit();
        return fittingIds.ToArray();
    }

    private void Changed(long ownerId) => database.SavedFittingStamps.AddOrUpdate(ownerId, 1, (_, revision) => revision + 1);

    private void Apply(SavedFittingRecord saved, FittingPlan plan)
    {
        saved.Name = plan.Name.Trim();
        saved.Description = plan.Description;
        saved.ShipTypeId = plan.ShipTypeId;
        saved.ItemsJson = JsonSerializer.Serialize(plan.Items);
        saved.SavedAt = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
    }

    private static void Validate(CatalogDbContext catalog, FittingPlan plan)
    {
        SavedFittingRules.Check(plan);
        var category = catalog.FittingType(plan.ShipTypeId) is { } hull ? (InventoryCategory?)hull.CategoryId : null;
        if (category is not (InventoryCategory.Ship or InventoryCategory.Structure))
        {
            throw new InvalidDataException("A saved fitting must describe a catalog ship or structure.");
        }
        if (plan.Items.Select(i => i.TypeId).Distinct().Any(type => catalog.FittingType(type) is null))
        {
            throw new InvalidDataException("A fitting entry refers to an unknown catalog type.");
        }
    }

    private static void CheckCapacity(WorldAction world, long pilotId, long ownerId, int added)
    {
        int limit = ownerId == pilotId ? SavedFittingRules.MaximumFittings : SavedFittingRules.MaximumCorporationFittings;
        if (world.Filter<SavedFittingRecord>(f => f.OwnerId == ownerId).Count() + added > limit)
        {
            throw new InvalidDataException($"This owner may save at most {limit} fittings.");
        }
    }

    private static SavedFittingRecord Find(WorldAction world, long ownerId, long fittingId) =>
        world.Set<SavedFittingRecord>().Find(fittingId) is { } row && row.OwnerId == ownerId
            ? row
            : throw new InvalidDataException("This fitting does not belong to the requested owner.");

    private static void Authorize(WorldAction world, long pilotId, long ownerId, bool mutation)
    {
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown fitting owner.");
        if (ownerId == pilotId)
        {
            return;
        }
        if (pilot.CorporationId != ownerId)
        {
            throw new InvalidDataException("Read corporation fittings for your current corporation only.");
        }
        if (mutation)
        {
            var corporation = world.Set<SavedCorporation>().Find(pilot.CorporationId);
            if (corporation is null || !CorporationRoleStore.Has(world, pilotId, corporation, CorporationRole.FittingManager))
            {
                throw new InvalidDataException("Only the CEO, a Director, or a Fitting Manager can change corporation fittings.");
            }
        }
    }
}
