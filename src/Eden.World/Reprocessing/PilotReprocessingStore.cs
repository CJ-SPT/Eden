using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Reprocessing;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Docking;
using Eden.World.Economy;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Reprocessing;

internal sealed record ReprocessingFacility(long OwnerId, double Efficiency, double Tax);

[Injectable]
internal sealed class PilotReprocessingStore(WorldDatabase database, TimeProvider? clock = null) : IPilotReprocessing
{
    private static Pilot ReadPilot(WorldAction world, long pilotId) => world.Filter<Pilot>(p => p.Id == pilotId).Single();

    private ReprocessingFacility Station(WorldAction world, CatalogDbContext catalog, Pilot pilot, long stationId)
    {
        if (pilot.ActiveShip.Position is not null || pilot.ActiveShip.DockedLocationId != stationId)
        {
            throw new InvalidDataException("Reprocessing is available only at your current station.");
        }
        if (pilot.ActiveShip.StructureId is long structureId)
        {
            var structure = Eden.World.Structures.PilotStructureStore.Require(world, structureId);
            Eden.World.Structures.PilotStructureStore.Advance(
                world,
                catalog,
                (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                structure.SystemId
            );
            var snapshot = Eden.World.Structures.PilotStructureStore.Snapshot(world, catalog, structure);
            if (
                !Eden.World.Structures.PilotStructureStore.Access(world, structure, pilot.Id)
                || !snapshot.Services.Contains(Eden.Gameplay.Structures.StructureService.Reprocessing)
            )
            {
                throw new InvalidDataException("The structure reprocessing service is offline or access is denied.");
            }
            const int OreRefiningYield = 2444;
            const int RefineryYieldBonus = 2722;
            var module = snapshot.Modules.Single(m =>
                m.Online
                && Eden.World.Structures.StructureCatalog.Service(catalog, m.TypeId)
                    .Services.Contains(Eden.Gameplay.Structures.StructureService.Reprocessing)
            );
            double efficiency =
                catalog.FittingAttributes(module.TypeId).GetValueOrDefault((DogmaAttributeId)OreRefiningYield)
                * (1 + catalog.FittingAttributes(snapshot.Profile.TypeId).GetValueOrDefault((DogmaAttributeId)RefineryYieldBonus) / 100);
            return new(structure.CorporationId, efficiency, 0);
        }
        var station = catalog.Definitions.Find(CatalogData.Sde.NpcStations, (long)stationId);
        if (
            station?.OperationID is not long operationId
            || !StationCapabilities.Has(catalog, operationId, StationService.Reprocessing)
            || station?.ReprocessingEfficiency is not > 0 or > 1
            || station.ReprocessingStationsTake is not >= 0 or > 1
        )
        {
            throw new InvalidDataException("This station does not provide reprocessing.");
        }
        return new(
            catalog.RequireStation(checked((int)stationId)).OwnerId,
            station.ReprocessingEfficiency.Value,
            station.ReprocessingStationsTake.Value
        );
    }

    public void CheckStation(long pilotId, long stationId)
    {
        using var scope = new WorldActionScope(database, new(pilots: [pilotId]));
        var world = scope.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        _ = Station(world, catalog, ReadPilot(world, pilotId), stationId);
        scope.Commit();
    }

    private static OwnedItem[] Items(WorldAction world, Pilot pilot, IReadOnlyList<long> ids, long? sourceId = null)
    {
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count)
        {
            throw new InvalidDataException("Select distinct ore stacks to reprocess.");
        }
        var ship = pilot.ActiveShip;
        var items = world
            .Filter<OwnedItem>(i =>
                ids.Contains(i.Id)
                && i.OwnerId == pilot.Id
                && (
                    (i.LocationId == ship.DockedLocationId && i.FlagId == (int)InventoryFlag.Hangar)
                    || (
                        i.LocationId == ship.Id
                        && (i.FlagId == (int)InventoryFlag.Cargo || i.FlagId == (int)InventoryFlag.GeneralMiningHold)
                    )
                )
            )
            .OrderBy(i => i.Id)
            .ToArray();
        if (items.Length != ids.Count || items.Any(i => i.Singleton || (sourceId.HasValue && i.LocationId != sourceId)))
        {
            throw new InvalidDataException("The selected stacks are unavailable in your cargo or station hangar.");
        }
        return items;
    }

    private static double Attribute(CatalogDbContext catalog, int typeId, DogmaAttributeId attribute, double fallback = 0) =>
        catalog.Definitions.Attributes(typeId).GetValueOrDefault(attribute, fallback);

    private static double SkillMultiplier(WorldAction world, CatalogDbContext catalog, long pilotId, int skill)
    {
        int level = PilotSkillReader.Level(world, catalog, pilotId, skill);
        return 1 + level * Attribute(catalog, skill, DogmaAttributeId.RefiningYieldMutator) / 100;
    }

    private static ReprocessingQuote Quote(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        OwnedItem item,
        double stationEfficiency,
        decimal tax
    )
    {
        var type = catalog.Definitions.Items[item.TypeId];
        var definition = catalog.Definitions.Find(CatalogData.Client.Types, (long)item.TypeId);
        var materials = catalog.Definitions.Find(CatalogData.Client.Typematerials, (long)item.TypeId);
        bool alchemy =
            type.CategoryId == (int)InventoryCategory.Material
            && Eden.World.Industry.ReactionCatalog.IsMoonAlchemyOutput(catalog, item.TypeId);
        if (
            type.CategoryId != (int)InventoryCategory.Asteroid && !alchemy
            || definition?.PortionSize is not > 0
            || materials?.Materials is not { Length: > 0 }
            || materials.RandomizedMaterials is { Length: > 0 }
        )
        {
            // Let the native window mark unsupported inputs as restricted. Do not
            // silently destroy equipment or ores with randomized outputs.
            return new(item.Id, item.TypeId, 0, item.Quantity, 0, stationEfficiency, []);
        }
        var skills = new[]
        {
            (int)ReprocessingSkill.Reprocessing,
            (int)ReprocessingSkill.ReprocessingEfficiency,
            checked((int)Attribute(catalog, item.TypeId, DogmaAttributeId.ReprocessingSkillType)),
        };
        var quote = ReprocessingRules.Quote(
            new(
                item.Id,
                item.TypeId,
                item.Quantity,
                checked((int)definition.PortionSize.Value),
                alchemy ? 1 : stationEfficiency,
                alchemy
                    ? []
                    : skills.Where(skill => skill != 0).Select(skill => SkillMultiplier(world, catalog, pilot.Id, skill)).ToArray(),
                materials
                    .Materials.Value.Select(m => new ReprocessingMaterial(checked((int)(m.MaterialTypeID ?? 0)), m.Quantity ?? 0))
                    .ToArray(),
                alchemy ? 1 : world.Settings.Industry.ReprocessingEfficiency
            )
        );
        if (!EconomicStore.Enabled(world))
        {
            return quote;
        }
        return quote with
        {
            Recoverables = quote
                .Recoverables.Select(m =>
                    m with
                    {
                        CostCents = EconomicRules.ReprocessingCharge(
                            m.Quantity,
                            EconomicStore.AdjustedPrice(world, m.TypeId) is > 0 and var price
                                ? price
                                : throw new InvalidDataException($"Missing positive adjusted price for material {m.TypeId}."),
                            tax
                        ),
                    }
                )
                .ToArray(),
        };
    }

    private static decimal Tax(WorldAction world, Pilot pilot, ReprocessingFacility station)
    {
        long owner = station.OwnerId;
        double standing = world.Set<SavedNpcStanding>().Find(pilot.Id, owner)?.Value ?? 0;
        return EconomicRules.ReprocessingTax((decimal)station.Tax, standing);
    }

    public ReprocessingQuotes Quote(long pilotId, long stationId, long activeShipId, IReadOnlyList<long> itemIds)
    {
        using var scope = new WorldActionScope(database, new(pilots: [pilotId]));
        var world = scope.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = ReadPilot(world, pilotId);
        var station = Station(world, catalog, pilot, stationId);
        decimal tax = Tax(world, pilot, station);
        if (pilot.ActiveShipId != activeShipId)
        {
            throw new InvalidDataException("The active ship has changed.");
        }
        var quotes =
            itemIds.Count == 0
                ? []
                : Items(world, pilot, itemIds).Select(i => Quote(world, catalog, pilot, i, station.Efficiency, tax)).ToArray();
        scope.Commit();
        return new((double)tax, station.Efficiency, quotes);
    }

    public ReprocessingResult Reprocess(
        long pilotId,
        long stationId,
        IReadOnlyList<long> itemIds,
        long sourceId,
        long? ownerId,
        long outputLocationId,
        int outputFlagId
    )
    {
        using var scope = new WorldActionScope(database, new(pilots: [pilotId]));
        var world = scope.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = ReadPilot(world, pilotId);
        var station = Station(world, catalog, pilot, stationId);
        var ship = pilot.ActiveShip;
        if (
            (ownerId.HasValue && ownerId != pilotId)
            || !(
                (outputLocationId == stationId && outputFlagId == (int)InventoryFlag.Hangar)
                || (outputLocationId == ship.Id && outputFlagId == (int)InventoryFlag.Cargo)
            )
        )
        {
            throw new InvalidDataException("Reprocessing output must go to your current hangar or cargo.");
        }
        var items = Items(world, pilot, itemIds, sourceId);
        decimal tax = Tax(world, pilot, station);
        var quotes = items.Select(i => Quote(world, catalog, pilot, i, station.Efficiency, tax)).ToArray();
        if (quotes.Any(q => q.Portions == 0 || q.Recoverables.Count == 0))
        {
            throw new InvalidDataException("Each ore stack must contain at least one complete reprocessing batch.");
        }
        Dictionary<int, int> output = [];
        foreach (var material in quotes.SelectMany(q => q.Recoverables).Where(m => m.Quantity > 0))
        {
            output[material.TypeId] = checked(output.GetValueOrDefault(material.TypeId) + material.Quantity);
        }
        if (output.Count == 0)
        {
            throw new InvalidDataException("These stacks yield no recoverable minerals.");
        }
        if (outputLocationId == ship.Id)
        {
            double capacity = PilotInventoryStore.HoldCapacity(world, catalog, pilotId, ship.Id, (int)InventoryFlag.Cargo);
            double used = InventoryPersistence.UsedVolume(world, catalog, pilotId, ship.Id, (int)InventoryFlag.Cargo);
            double removed = items
                .Zip(quotes)
                .Where(pair => pair.First.LocationId == ship.Id && pair.First.FlagId == (int)InventoryFlag.Cargo)
                .Sum(pair => PilotInventoryStore.Volume(catalog, pair.Second.TypeId) * pair.Second.QuantityToProcess);
            double added = output.Sum(p => PilotInventoryStore.Volume(catalog, p.Key) * p.Value);
            if (added > capacity - used + removed + InventoryDefinitions.ResourceTolerance)
            {
                throw new CargoCapacityException(Math.Max(0, capacity - used + removed), added);
            }
        }
        long cost = quotes.Sum(q => q.CostCents);
        long now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc();
        long? balance =
            cost > 0 ? EconomicStore.Pay(world, pilotId, station.OwnerId, stationId, WalletEntryType.ReprocessingTax, cost, now) : null;
        List<InventoryChange> changes = [];
        foreach (var (item, quote) in items.Zip(quotes))
        {
            var previous = PilotInventoryStore.Read(catalog, item);
            item.Quantity = quote.LeftOvers;
            changes.Add(new(previous with { Quantity = item.Quantity }, previous));
            if (item.Quantity == 0)
            {
                world.Remove(item);
            }
        }
        foreach (var (typeId, quantity) in output)
        {
            changes.Add(InventoryPersistence.MergeOutput(world, catalog, pilotId, outputLocationId, outputFlagId, typeId, quantity));
        }
        PilotTaskTrackingStore.Record(
            world,
            catalog,
            pilotId,
            (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc(),
            TaskEvents.Reprocessed
        );
        scope.Commit();
        return new(items.Select(i => i.Id).ToArray(), output, changes, balance);
    }
}
