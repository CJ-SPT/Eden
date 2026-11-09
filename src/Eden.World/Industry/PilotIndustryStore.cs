using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Docking;
using Eden.World.Economy;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Industry;

[Injectable]
internal sealed partial class PilotIndustryStore(WorldDatabase database, TimeProvider clock, IIndustryRandom random) : IPilotIndustry
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static Pilot Pilot(WorldDbContext world, long id) =>
        world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == id);

    private static IndustryFacility ReadFacility(CatalogDbContext catalog, CatalogStationDefinition station) =>
        new(
            station.Id,
            station.TypeId,
            station.OwnerId,
            station.SolarSystemId,
            Manufacturing: HasService(catalog, station),
            Laboratory: HasService(catalog, station, StationService.Laboratory)
        );

    private IndustryFacility PricedFacility(CatalogDbContext catalog, CatalogStationDefinition station)
    {
        using WorldDbContext world = new(database.Path);
        bool enabled = EconomicStore.Enabled(world);
        return new(
            station.Id,
            station.TypeId,
            station.OwnerId,
            station.SolarSystemId,
            enabled ? (double)EconomicStore.Index(world, station.SolarSystemId) : 1,
            enabled ? (double)EconomicRules.NpcFacilityTax : 0,
            HasService(catalog, station),
            HasService(catalog, station, StationService.Laboratory)
        );
    }

    private static bool HasService(
        CatalogDbContext catalog,
        CatalogStationDefinition station,
        StationService service = StationService.Manufacturing
    ) => StationCapabilities.Has(catalog, station.OperationId, service);

    private static CatalogStationDefinition Station(
        CatalogDbContext catalog,
        int id,
        StationService? service = StationService.Manufacturing
    )
    {
        var station = catalog.Definitions.FindStation(id);
        if (
            station is null
            || (
                service.HasValue
                    ? !HasService(catalog, station, service.Value)
                    : !HasService(catalog, station) && !HasService(catalog, station, StationService.Laboratory)
            )
        )
        {
            throw new InvalidDataException("This station does not provide the requested industry service.");
        }
        return station;
    }

    private static CatalogStationDefinition CurrentStation(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        int stationId,
        StationService? service = StationService.Manufacturing
    )
    {
        var pilot = Pilot(world, pilotId);
        if (pilot.ActiveShip.Position is not null || pilot.ActiveShip.StationId != stationId)
        {
            throw new InvalidDataException("Use industry from your current station while docked.");
        }
        return Station(catalog, stationId, service);
    }

    public void CheckStation(long pilotId, int stationId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        _ = CurrentStation(world, catalog, pilotId, stationId, null);
    }

    public void CheckAccess(long pilotId, int stationId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        // LobbyWnd checks both service IDs even at factory-only stations.
        // Public NPC stations have no Eden standing restriction; availability
        // of manufacturing itself is checked independently on every job.
        _ = CurrentStation(world, catalog, pilotId, stationId, null);
    }

    public IndustryFacility Facility(long pilotId, int stationId)
    {
        using WorldDbContext world = new(database.Path);
        _ = Pilot(world, pilotId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return PricedFacility(catalog, Station(catalog, stationId, null));
    }

    public IReadOnlyList<IndustryFacility> Facilities(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        int systemId = pilot.ActiveShip.Position?.SystemId ?? catalog.Definitions.Stations[pilot.ActiveShip.StationId].SolarSystemId;
        int regionId = catalog.Definitions.Systems[systemId].RegionId;
        return catalog
            .Definitions.Stations.Values.Where(s => catalog.Definitions.Systems[s.SolarSystemId].RegionId == regionId)
            .OrderBy(s => s.Id)
            .AsEnumerable()
            .Where(s => HasService(catalog, s) || HasService(catalog, s, StationService.Laboratory))
            .Select(s => PricedFacility(catalog, s))
            .ToArray();
    }

    private static IReadOnlyDictionary<DogmaAttributeId, double> Attributes(CatalogDbContext catalog, int typeId) =>
        catalog.Definitions.Attributes(typeId);

    public IReadOnlyList<int> ScienceProductCategories()
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return catalog.Definitions.Items.Values.Select(t => t.CategoryId).Distinct().Order().ToArray();
    }

    public IReadOnlyList<int> ProductTypes(int? stationId = null)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (stationId is not int id)
        {
            return ProductTypes(catalog);
        }
        var station = Station(catalog, id, null);
        if (!HasService(catalog, station))
        {
            return [];
        }
        double security = ManufacturingCatalog.Security(catalog, station.SolarSystemId);
        return ProductTypes(catalog).Where(t => ManufacturingCatalog.Allowed(catalog, t, security)).ToArray();
    }

    internal static IReadOnlyList<int> ProductTypes(CatalogDbContext catalog) =>
        ManufacturingCatalog.Recipes(catalog).Values.Select(r => r.ProductTypeId).Distinct().Order().ToArray();

    private static bool Active(IndustryJob job) => job.Status == IndustryJobStatus.Installed;

    internal static bool IsInstalled(WorldDbContext world, long itemId) =>
        world.Set<IndustryJob>().Any(j => j.BlueprintId == itemId && j.Status == IndustryJobStatus.Installed);

    private static OwnedBlueprint ReadBlueprint(WorldDbContext world, CatalogDbContext catalog, OwnedItem item)
    {
        var inventory = PilotInventoryStore.Read(catalog, item);
        if (
            inventory.CategoryId is not ((int)InventoryCategory.Blueprint or (int)InventoryCategory.Relic)
            || item.FlagId != (int)InventoryFlag.Hangar
            || item.LocationId > int.MaxValue
        )
        {
            throw new InvalidDataException("Use an owned blueprint or ancient relic in a station Item Hangar.");
        }
        var station =
            catalog.Definitions.FindStation(item.LocationId) ?? throw new InvalidDataException("The blueprint is not in a station hangar.");
        var facility = ReadFacility(catalog, station);
        long? job = world
            .Set<IndustryJob>()
            .Where(j => j.BlueprintId == item.Id && j.Status == IndustryJobStatus.Installed)
            .Select(j => (long?)j.Id)
            .SingleOrDefault();
        return inventory.CategoryId == (int)InventoryCategory.Relic
            ? new(inventory, facility, job, RunsRemaining: item.Quantity)
            : new(inventory, facility, job, item.MaterialEfficiency, item.TimeEfficiency, item.BlueprintRuns);
    }

    public IReadOnlyList<OwnedBlueprint> Blueprints(long pilotId, int? stationId = null)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var types = catalog
            .Definitions.Items.Values.Where(t => t.CategoryId is (int)InventoryCategory.Blueprint or (int)InventoryCategory.Relic)
            .Select(t => t.Id)
            .ToArray();
        return world
            .Set<OwnedItem>()
            .AsNoTracking()
            .Where(i =>
                i.OwnerId == pilotId
                && i.FlagId == (int)InventoryFlag.Hangar
                && (!stationId.HasValue || i.LocationId == stationId)
                && types.Contains(i.TypeId)
            )
            .OrderBy(i => i.Id)
            .AsEnumerable()
            .Where(i => i.LocationId <= int.MaxValue && catalog.Definitions.FindStation(i.LocationId) is not null)
            .Select(i => ReadBlueprint(world, catalog, i))
            .ToArray();
    }

    public OwnedBlueprint Blueprint(long pilotId, long itemId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var item =
            world.Set<OwnedItem>().SingleOrDefault(i => i.Id == itemId && i.OwnerId == pilotId)
            ?? throw new InvalidDataException("The blueprint is unavailable or belongs to another pilot.");
        return ReadBlueprint(world, catalog, item);
    }

    public IReadOnlyDictionary<int, long> Materials(long pilotId, int stationId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        _ = CurrentStation(world, catalog, pilotId, stationId, null);
        return world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == pilotId && i.LocationId == stationId && i.FlagId == (int)InventoryFlag.Hangar && !i.Singleton)
            .AsEnumerable()
            .GroupBy(i => i.TypeId)
            .ToDictionary(g => g.Key, g => g.Sum(i => (long)i.Quantity));
    }

    private static int Level(WorldDbContext world, CatalogDbContext catalog, long pilotId, int typeId) =>
        PilotSkillReader.Level(world, catalog, pilotId, typeId);

    private static IndustryCharacterBonus[] Bonuses(WorldDbContext world, CatalogDbContext catalog, long pilotId)
    {
        List<IndustryCharacterBonus> result = [];
        foreach (
            var (skill, source, target, additive) in new[]
            {
                (IndustrySkill.Industry, DogmaAttributeId.ManufacturingTimeBonus, DogmaAttributeId.ManufactureTimeMultiplier, false),
                (IndustrySkill.Science, DogmaAttributeId.CopySpeedBonus, DogmaAttributeId.CopySpeedPercent, false),
                (
                    IndustrySkill.Research,
                    DogmaAttributeId.BlueprintManufactureTimeBonus,
                    DogmaAttributeId.ManufacturingTimeResearchSpeed,
                    false
                ),
                (IndustrySkill.Metallurgy, DogmaAttributeId.MineralNeedResearchBonus, DogmaAttributeId.MineralNeedResearchSpeed, false),
                (IndustrySkill.LaboratoryOperation, DogmaAttributeId.LaboratorySlotsBonus, DogmaAttributeId.MaximumLaboratorySlots, true),
                (
                    IndustrySkill.AdvancedLaboratoryOperation,
                    DogmaAttributeId.LaboratorySlotsBonus,
                    DogmaAttributeId.MaximumLaboratorySlots,
                    true
                ),
                (IndustrySkill.AdvancedIndustry, DogmaAttributeId.AdvancedIndustryTimeBonus, DogmaAttributeId.CopySpeedPercent, false),
                (
                    IndustrySkill.AdvancedIndustry,
                    DogmaAttributeId.AdvancedIndustryTimeBonus,
                    DogmaAttributeId.ManufacturingTimeResearchSpeed,
                    false
                ),
                (
                    IndustrySkill.AdvancedIndustry,
                    DogmaAttributeId.AdvancedIndustryTimeBonus,
                    DogmaAttributeId.MineralNeedResearchSpeed,
                    false
                ),
                (
                    IndustrySkill.AdvancedIndustry,
                    DogmaAttributeId.AdvancedIndustryTimeBonus,
                    DogmaAttributeId.InventionResearchSpeed,
                    false
                ),
                (
                    IndustrySkill.AdvancedIndustry,
                    DogmaAttributeId.AdvancedIndustryTimeBonus,
                    DogmaAttributeId.ManufactureTimeMultiplier,
                    false
                ),
                (IndustrySkill.MassProduction, DogmaAttributeId.ManufacturingSlotBonus, DogmaAttributeId.ManufactureSlotLimit, true),
                (
                    IndustrySkill.AdvancedMassProduction,
                    DogmaAttributeId.ManufacturingSlotBonus,
                    DogmaAttributeId.ManufactureSlotLimit,
                    true
                ),
            }
        )
        {
            int level = Level(world, catalog, pilotId, (int)skill);
            double value = Attributes(catalog, (int)skill).GetValueOrDefault(source) * level;
            if (!double.IsFinite(value))
            {
                throw new InvalidDataException("Invalid industry skill bonus.");
            }
            if (value != 0)
            {
                result.Add(new((int)skill, target, value, additive));
            }
        }
        return result.ToArray();
    }

    public IReadOnlyList<IndustryCharacterBonus> CharacterBonuses(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return Bonuses(world, catalog, pilotId);
    }

    private IndustryJobDetails ReadJob(CatalogDbContext catalog, IndustryJob job) =>
        new(
            job.Id,
            job.PilotId,
            job.BlueprintId,
            job.BlueprintTypeId,
            ReadFacility(catalog, Station(catalog, job.StationId, null)),
            job.ProductTypeId,
            job.Runs,
            job.OutputQuantity,
            job.StartTime,
            job.EndTime,
            job.DurationSeconds,
            Active(job) && Now >= job.EndTime ? IndustryJobStatus.Ready : job.Status,
            job.CompletedAt,
            job.CostCents,
            job.Activity,
            job.LicensedRuns,
            job.BlueprintCopy,
            job.Probability,
            job.Status == IndustryJobStatus.Delivered ? job.SuccessfulRuns ?? job.Runs : null,
            job.OptionalTypeId
        );

    public IReadOnlyList<IndustryJobDetails> Jobs(long pilotId, bool includeCompleted)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return world
            .Set<IndustryJob>()
            .AsNoTracking()
            .Where(j => j.PilotId == pilotId && (includeCompleted || j.Status == IndustryJobStatus.Installed))
            .OrderByDescending(j => j.Id)
            .AsEnumerable()
            .Select(j => ReadJob(catalog, j))
            .ToArray();
    }

    public IndustryJobDetails Job(long pilotId, long jobId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return ReadJob(catalog, OwnedJob(world, pilotId, jobId));
    }

    private static IndustryJob OwnedJob(WorldDbContext world, long pilotId, long jobId) =>
        world.Set<IndustryJob>().SingleOrDefault(j => j.Id == jobId && j.PilotId == pilotId)
        ?? throw new InvalidDataException("The industry job is unavailable or belongs to another pilot.");

    public IndustryResult Install(long pilotId, IndustryRequest request)
    {
        if (request.Activity != IndustryActivity.Manufacturing)
        {
            return InstallScience(pilotId, request);
        }
        if (request.LicensedRuns != 0)
        {
            throw new InvalidDataException("Manufacturing does not accept licensed copy runs.");
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var station = CurrentStation(world, catalog, pilotId, request.FacilityId);
        var item =
            world.Set<OwnedItem>().SingleOrDefault(i => i.Id == request.BlueprintId && i.OwnerId == pilotId)
            ?? throw new InvalidDataException("The blueprint is unavailable or belongs to another pilot.");
        var blueprint = ReadBlueprint(world, catalog, item);
        if (
            blueprint.JobId.HasValue
            || blueprint.Facility.Id != request.FacilityId
            || item.TypeId != request.BlueprintTypeId
            || request.Runs <= 0
        )
        {
            throw new InvalidDataException("Choose an available blueprint and positive run count at this station.");
        }
        var recipe =
            ManufacturingCatalog.Recipes(catalog).GetValueOrDefault(item.TypeId)
            ?? throw new InvalidDataException("This blueprint has no valid published manufacturing recipe.");
        if (
            request.ProductTypeId.HasValue && request.ProductTypeId != recipe.ProductTypeId
            || !ManufacturingCatalog.Allowed(catalog, recipe.ProductTypeId, ManufacturingCatalog.Security(catalog, station.SolarSystemId))
        )
        {
            throw new InvalidDataException("This product cannot be manufactured at this station.");
        }
        foreach (var skill in recipe.Skills)
        {
            if (Level(world, catalog, pilotId, skill.TypeId) < skill.Level)
            {
                throw new InvalidDataException("Train the blueprint's required manufacturing skills first.");
            }
        }
        var calculation = ManufacturingRules.Plan(
            request,
            new(
                recipe.Seconds,
                world
                    .Set<IndustryJob>()
                    .Count(j =>
                        j.PilotId == pilotId && j.Status == IndustryJobStatus.Installed && j.Activity == IndustryActivity.Manufacturing
                    ),
                Bonuses(world, catalog, pilotId),
                recipe
                    .Skills.Select(skill => new ManufacturingSkillTimeBonus(
                        Attributes(catalog, skill.TypeId).GetValueOrDefault(DogmaAttributeId.ManufactureTimePerLevel),
                        Level(world, catalog, pilotId, skill.TypeId)
                    ))
                    .ToArray(),
                recipe.Materials,
                new(item.MaterialEfficiency, item.TimeEfficiency, item.BlueprintRuns)
            )
        );
        long duration = calculation.DurationSeconds;
        var materials = calculation.Materials;
        List<InventoryChange> changes = [];
        ConsumeMaterials(world, catalog, pilotId, request.FacilityId, materials, changes);
        item = ReserveBlueprint(world, catalog, item, changes);
        long now = Now;
        var job = world
            .Set<IndustryJob>()
            .Add(
                new()
                {
                    PilotId = pilotId,
                    BlueprintCopy = blueprint.RunsRemaining >= 0,
                    BlueprintId = item.Id,
                    BlueprintTypeId = item.TypeId,
                    StationId = request.FacilityId,
                    ProductTypeId = recipe.ProductTypeId,
                    Runs = request.Runs,
                    OutputQuantity = checked(recipe.OutputQuantity * request.Runs),
                    StartTime = now,
                    EndTime = checked(now + duration * TimeSpan.TicksPerSecond),
                    DurationSeconds = duration,
                    Status = IndustryJobStatus.Installed,
                    MaterialsJson = JsonSerializer.Serialize(materials),
                }
            )
            .Entity;
        double? security = catalog.Definitions.Find(CatalogData.Client.Systems, (long)station.SolarSystemId)?.SecurityStatus;
        if (EconomicStore.Enabled(world) && security is null)
        {
            throw new InvalidDataException("Missing catalog industry security classification.");
        }
        var baseMaterials = IndustryMaterials.Calculate(recipe.Materials, request.Runs);
        var charge = EconomicStore.Manufacturing(world, pilotId, station.SolarSystemId, baseMaterials, security ?? 0);
        if (request.QuotedCharge is { } quoted && (quoted.CostCents != charge.CostCents || quoted.TaxCents != charge.TaxCents))
        {
            throw new InvalidDataException("Manufacturing price changed; reopen the blueprint to refresh the quote.");
        }
        job.CostCents = charge.TotalCents;
        world.SaveChanges();
        long? balance =
            charge.TotalCents > 0
                ? EconomicStore.Pay(world, pilotId, station.OwnerId, job.Id, WalletEntryType.Manufacturing, charge.TotalCents, now)
                : null;
        PilotTaskTrackingStore.Record(world, catalog, pilotId, now, TaskEvents.IndustryStarted);
        world.SaveChanges();
        transaction.Commit();
        return new(ReadJob(catalog, job), changes, balance);
    }

    public IndustryResult Deliver(long pilotId, long jobId, int systemId) => Finish(pilotId, jobId, systemId, cancel: false);

    public IndustryResult Cancel(long pilotId, long jobId, int systemId) => Finish(pilotId, jobId, systemId, cancel: true);

    private IndustryResult Finish(long pilotId, long jobId, int systemId, bool cancel)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var job = OwnedJob(world, pilotId, jobId);
        var station = CurrentStation(world, catalog, pilotId, job.StationId, null);
        if (station.SolarSystemId != systemId)
        {
            throw new InvalidDataException("The job belongs to a different solar system.");
        }
        var status = cancel ? IndustryJobStatus.Cancelled : IndustryJobStatus.Delivered;
        if (job.Status == status)
        {
            return new(ReadJob(catalog, job), []);
        }
        if (!Active(job) || !cancel && Now < job.EndTime)
        {
            throw new InvalidDataException("This industry job cannot be completed yet.");
        }
        List<InventoryChange> changes = [];
        if (!cancel && job.Activity != IndustryActivity.Manufacturing)
        {
            DeliverScience(world, catalog, job, changes);
        }
        else if (!cancel)
        {
            changes.Add(
                InventoryPersistence.MergeOutput(
                    world,
                    catalog,
                    pilotId,
                    job.StationId,
                    (int)InventoryFlag.Hangar,
                    job.ProductTypeId,
                    job.OutputQuantity
                )
            );
        }
        var source = world.Set<OwnedItem>().Find(job.BlueprintId);
        bool relic = catalog.Definitions.Items[job.BlueprintTypeId].CategoryId == (int)InventoryCategory.Relic;
        if (relic)
        {
            if (source is null || source.Quantity < job.Runs)
            {
                throw new InvalidDataException("The installed relic stack no longer has its reserved units.");
            }
            var before = PilotInventoryStore.Read(catalog, source);
            source.Quantity -= job.Runs;
            changes.Add(new(before with { Quantity = source.Quantity }, before));
            if (source.Quantity == 0)
            {
                world.Remove(source);
            }
        }
        else if (job.BlueprintCopy)
        {
            if (source is null || source.BlueprintRuns < job.Runs)
            {
                throw new InvalidDataException("The installed copy no longer has its reserved runs.");
            }
            source.BlueprintRuns -= job.Runs;
            if (source.BlueprintRuns == 0)
            {
                var before = PilotInventoryStore.Read(catalog, source);
                changes.Add(new(before with { Quantity = 0 }, before));
                world.Remove(source);
            }
        }
        // Cancellation releases the blueprint and slot, but consumes the paid
        // materials. A completed job delivers once, even after reconnect/retry.
        job.Status = status;
        job.CompletedAt = Now;
        if (!cancel && job.Activity == IndustryActivity.Manufacturing)
        {
            MarketProgressionStore.Claim(world, catalog, job);
            PilotTaskTrackingStore.Record(world, catalog, pilotId, job.CompletedAt.Value, TaskEvents.IndustryDelivered);
        }
        world.SaveChanges();
        transaction.Commit();
        return new(ReadJob(catalog, job), changes);
    }
}
