using System.Globalization;
using Eden.Protocol;
using Eden.Protocol.Docking;
using Eden.Protocol.Reprocessing;
using Eden.Protocol.Skills;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotReprocessingStore(WorldDatabase database) : IPilotReprocessing
{
    private static Pilot ReadPilot(WorldDbContext world, long pilotId) => world.Pilots
        .Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);

    private static SdeNpcStations Station(CatalogDbContext catalog, Pilot pilot, int stationId)
    {
        if (pilot.ActiveShip.Position is not null || pilot.ActiveShip.StationId != stationId)
        {
            throw new InvalidDataException("Reprocessing is available only at your current station.");
        }
        var station = Source<SdeNpcStations>(catalog, "sde/npcStations", stationId);
        var operation = station?.OperationID is long id ? Source<ClientStationoperations>(catalog, "client/stationoperations", id) : null;
        if (operation?.Services?.Contains((long)StationService.Reprocessing) != true || station?.ReprocessingEfficiency is not > 0 or > 1 ||
            station.ReprocessingStationsTake is not >= 0 or > 1)
        {
            throw new InvalidDataException("This station does not provide reprocessing.");
        }
        return station;
    }

    private static T? Source<T>(CatalogDbContext catalog, string dataset, long key) where T : class =>
        catalog.Set<SourceRecord>().Find(dataset, key.ToString(CultureInfo.InvariantCulture))?.Read<T>();

    public void CheckStation(long pilotId, int stationId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        _ = Station(catalog, ReadPilot(world, pilotId), stationId);
    }

    private static OwnedItem[] Items(WorldDbContext world, Pilot pilot, IReadOnlyList<long> ids, long? sourceId = null)
    {
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count)
        {
            throw new InvalidDataException("Select distinct ore stacks to reprocess.");
        }
        var ship = pilot.ActiveShip;
        var items = world.Set<OwnedItem>().Where(i => ids.Contains(i.Id) && i.OwnerId == pilot.Id &&
            ((i.LocationId == ship.StationId && i.FlagId == (int)InventoryFlag.Hangar) ||
             (i.LocationId == ship.Id && i.FlagId == (int)InventoryFlag.Cargo))).OrderBy(i => i.Id).ToArray();
        if (items.Length != ids.Count || items.Any(i => i.Singleton || (sourceId.HasValue && i.LocationId != sourceId)))
        {
            throw new InvalidDataException("The selected stacks are unavailable in your cargo or station hangar.");
        }
        return items;
    }

    private static double Attribute(CatalogDbContext catalog, int typeId, DogmaAttributeId attribute, double fallback = 0) =>
        catalog.Set<TypeAttribute>().Find(typeId, (int)attribute)?.Value ?? fallback;

    private static double SkillMultiplier(WorldDbContext world, CatalogDbContext catalog, long pilotId, int skill)
    {
        long points = world.Set<PilotSkill>().Find(pilotId, skill)?.Points ?? 0;
        int level = new TrainedSkill(skill, points, Attribute(catalog, skill, DogmaAttributeId.SkillTimeConstant, 1)).Level;
        return 1 + level * Attribute(catalog, skill, DogmaAttributeId.RefiningYieldMutator) / 100;
    }

    private static ReprocessingQuote Quote(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, OwnedItem item, double stationEfficiency)
    {
        var type = catalog.Set<ItemType>().Include(t => t.Group).Single(t => t.Id == item.TypeId);
        var definition = Source<ClientTypes>(catalog, "client/types", item.TypeId);
        var materials = Source<ClientTypematerials>(catalog, "client/typematerials", item.TypeId);
        if (type.Group.CategoryId != (int)InventoryCategory.Asteroid || definition?.PortionSize is not > 0 ||
            materials?.Materials is not { Length: > 0 } || materials.RandomizedMaterials is { Length: > 0 })
        {
            // Let the native window mark unsupported inputs as restricted. Do not
            // silently destroy equipment or ores with randomized outputs.
            return new(item.Id, item.TypeId, 0, item.Quantity, 0, stationEfficiency, []);
        }
        int portionSize = checked((int)definition.PortionSize.Value);
        int portions = item.Quantity / portionSize;
        int leftOvers = item.Quantity % portionSize;
        double efficiency = stationEfficiency;
        foreach (int skill in new[] { (int)ReprocessingSkill.Reprocessing, (int)ReprocessingSkill.ReprocessingEfficiency,
            checked((int)Attribute(catalog, item.TypeId, DogmaAttributeId.ReprocessingSkillType)) })
        {
            if (skill != 0) { efficiency *= SkillMultiplier(world, catalog, pilot.Id, skill); }
        }
        efficiency = Math.Clamp(efficiency, 0, 1);
        List<RecoveredMaterial> recoverables = [];
        if (portions > 0)
        {
            foreach (var material in materials.Materials)
            {
                if (material.MaterialTypeID is not > 0 || material.Quantity is not > 0)
                {
                    throw new InvalidDataException("The catalog contains an invalid reprocessing output.");
                }
                long total = checked(material.Quantity.Value * portions);
                int recovered = checked((int)Math.Floor(total * efficiency));
                recoverables.Add(new(checked((int)material.MaterialTypeID.Value), recovered, total - recovered));
            }
        }
        return new(item.Id, item.TypeId, item.Quantity - leftOvers, leftOvers, portions, efficiency, recoverables);
    }

    public ReprocessingQuotes Quote(long pilotId, int stationId, long activeShipId, IReadOnlyList<long> itemIds)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = ReadPilot(world, pilotId);
        var station = Station(catalog, pilot, stationId);
        if (pilot.ActiveShipId != activeShipId) { throw new InvalidDataException("The active ship has changed."); }
        var quotes = itemIds.Count == 0 ? [] : Items(world, pilot, itemIds)
            .Select(i => Quote(world, catalog, pilot, i, station.ReprocessingEfficiency!.Value)).ToArray();
        return new(station.ReprocessingStationsTake!.Value, station.ReprocessingEfficiency!.Value, quotes);
    }

    public ReprocessingResult Reprocess(long pilotId, int stationId, IReadOnlyList<long> itemIds, long sourceId,
        long? ownerId, long outputLocationId, int outputFlagId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = ReadPilot(world, pilotId);
        var station = Station(catalog, pilot, stationId);
        var ship = pilot.ActiveShip;
        if ((ownerId.HasValue && ownerId != pilotId) ||
            !((outputLocationId == stationId && outputFlagId == (int)InventoryFlag.Hangar) ||
              (outputLocationId == ship.Id && outputFlagId == (int)InventoryFlag.Cargo)))
        {
            throw new InvalidDataException("Reprocessing output must go to your current hangar or cargo.");
        }
        var items = Items(world, pilot, itemIds, sourceId);
        var quotes = items.Select(i => Quote(world, catalog, pilot, i, station.ReprocessingEfficiency!.Value)).ToArray();
        if (quotes.Any(q => q.Portions == 0 || q.Recoverables.Count == 0))
        {
            throw new InvalidDataException("Each ore stack must contain at least one complete reprocessing batch.");
        }
        Dictionary<int, int> output = [];
        foreach (var material in quotes.SelectMany(q => q.Recoverables).Where(m => m.Quantity > 0))
        {
            output[material.TypeId] = checked(output.GetValueOrDefault(material.TypeId) + material.Quantity);
        }
        if (output.Count == 0) { throw new InvalidDataException("These stacks yield no recoverable minerals."); }
        if (outputLocationId == ship.Id)
        {
            double capacity = catalog.Set<ItemType>().Single(t => t.Id == ship.TypeId).Capacity ?? 0;
            double used = world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && i.LocationId == ship.Id && i.FlagId == (int)InventoryFlag.Cargo)
                .AsEnumerable().Sum(i => PilotInventoryStore.Volume(catalog, i.TypeId) * i.Quantity);
            double removed = sourceId == ship.Id ? quotes.Sum(q => PilotInventoryStore.Volume(catalog, q.TypeId) * q.QuantityToProcess) : 0;
            double added = output.Sum(p => PilotInventoryStore.Volume(catalog, p.Key) * p.Value);
            if (added > capacity - used + removed + InventoryDefinitions.ResourceTolerance)
            {
                throw new CargoCapacityException(Math.Max(0, capacity - used + removed), added);
            }
        }
        List<InventoryChange> changes = [];
        long nextId = PilotInventoryStore.NextId(world);
        foreach (var (item, quote) in items.Zip(quotes))
        {
            var previous = PilotInventoryStore.Read(catalog, item);
            item.Quantity = quote.LeftOvers;
            changes.Add(new(previous with { Quantity = item.Quantity }, previous));
            if (item.Quantity == 0) { world.Remove(item); }
        }
        foreach (var (typeId, quantity) in output)
        {
            var stack = world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && i.LocationId == outputLocationId &&
                i.FlagId == outputFlagId && i.TypeId == typeId && !i.Singleton).OrderBy(i => i.Id).FirstOrDefault();
            InventoryItem? previous = stack is null ? null : PilotInventoryStore.Read(catalog, stack);
            if (stack is null)
            {
                stack = world.Add(new OwnedItem
                {
                    Id = nextId++,
                    OwnerId = pilotId,
                    TypeId = typeId,
                    LocationId = outputLocationId,
                    FlagId = outputFlagId,
                    Quantity = quantity
                }).Entity;
            }
            else { stack.Quantity = checked(stack.Quantity + quantity); }
            changes.Add(new(PilotInventoryStore.Read(catalog, stack), previous));
        }
        world.SaveChanges();
        transaction.Commit();
        return new(items.Select(i => i.Id).ToArray(), output, changes);
    }
}
