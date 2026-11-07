using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Docking;
using Eden.World.Economy;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Industry;

[Injectable]
internal sealed class PilotIndustryStore(WorldDatabase database, TimeProvider clock) : IPilotIndustry
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static Pilot Pilot(WorldDbContext world, long id) =>
        world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == id);

    private static IndustryFacility ReadFacility(Station station) =>
        new(station.Id, station.TypeId, station.OwnerId, station.SolarSystemId);

    private IndustryFacility PricedFacility(Station station)
    {
        using WorldDbContext world = new(database.Path);
        bool enabled = EconomicStore.Enabled(world);
        return new(
            station.Id,
            station.TypeId,
            station.OwnerId,
            station.SolarSystemId,
            enabled ? (double)EconomicStore.Index(world, station.SolarSystemId) : 1,
            enabled ? (double)EconomicRules.NpcFacilityTax : 0
        );
    }

    private static bool HasService(CatalogDbContext catalog, Station station, StationService service = StationService.Manufacturing) =>
        StationCapabilities.Has(catalog, station.OperationId, service);

    private static Station Station(CatalogDbContext catalog, int id, StationService? service = StationService.Manufacturing)
    {
        var station = catalog.Set<Station>().SingleOrDefault(s => s.Id == id);
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

    private static Station CurrentStation(
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
            throw new InvalidDataException("Use manufacturing from your current station while docked.");
        }
        return Station(catalog, stationId, service);
    }

    public void CheckStation(long pilotId, int stationId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        _ = CurrentStation(world, catalog, pilotId, stationId);
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
        return PricedFacility(Station(catalog, stationId));
    }

    public IReadOnlyList<IndustryFacility> Facilities(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        int systemId =
            pilot.ActiveShip.Position?.SystemId ?? catalog.Set<Station>().Single(s => s.Id == pilot.ActiveShip.StationId).SolarSystemId;
        int regionId = catalog.Set<SolarSystem>().Where(s => s.Id == systemId).Select(s => s.Constellation.RegionId).Single();
        return catalog
            .Set<Station>()
            .Where(s => s.SolarSystem.Constellation.RegionId == regionId)
            .OrderBy(s => s.Id)
            .AsEnumerable()
            .Where(s => HasService(catalog, s))
            .Select(PricedFacility)
            .ToArray();
    }

    private static Dictionary<DogmaAttributeId, double> Attributes(CatalogDbContext catalog, int typeId) =>
        catalog.Set<TypeAttribute>().Where(a => a.ItemTypeId == typeId).ToDictionary(a => (DogmaAttributeId)a.AttributeId, a => a.Value);

    private static bool SupportedProduct(CatalogDbContext catalog, ItemType type)
    {
        if (StarterProduction.IsHull(type.Id))
        {
            return HullCatalog.CanAssemble(catalog, type, (int)InventoryCategory.Ship);
        }
        var attributes = Attributes(catalog, type.Id);
        if (attributes.GetValueOrDefault(DogmaAttributeId.TechLevel) != AmmunitionDefinitions.TechOne)
        {
            return false;
        }
        // Manufacturing expansion is separate from fitting/firing new weapon sizes.
        if (
            type.GroupId == CombatDefinitions.HybridWeaponGroup
            && attributes.GetValueOrDefault(DogmaAttributeId.ChargeSize) != AmmunitionDefinitions.SmallChargeSize
        )
        {
            return false;
        }
        return StarterProduction.IsUtility(type.Id) && FittingCatalog.Definition(catalog, type.Id) is not null
            || type.GroupId == AmmunitionDefinitions.HybridChargeGroup
                && attributes.GetValueOrDefault(DogmaAttributeId.ChargeSize) == AmmunitionDefinitions.SmallChargeSize
            || (
                (
                    type.GroupId
                    is InventoryDefinitions.MiningLaserGroup
                        or ShieldBoostDefinitions.Group
                        or PropulsionDefinitions.AfterburnerGroup
                        or CombatDefinitions.HybridWeaponGroup
                )
                && FittingCatalog.Definition(catalog, type.Id) is not null
                && !FittingCatalog.Effects(catalog, type.Id).Contains(DogmaEffect.Microwarpdrive)
            );
    }

    public IReadOnlyList<int> ProductTypes()
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return catalog
            .Set<ItemType>()
            .Where(t =>
                t.Id == (int)StarterProductionType.Merlin
                || t.Id == (int)StarterProductionType.Venture
                || t.Id == (int)StarterProductionType.Badger
                || t.Id == (int)StarterProductionType.ExpandedCargohold
                || t.Id == (int)StarterProductionType.DamageControl
                || t.GroupId == AmmunitionDefinitions.HybridChargeGroup
                || t.GroupId == CombatDefinitions.HybridWeaponGroup
                || t.GroupId == InventoryDefinitions.MiningLaserGroup
                || t.GroupId == ShieldBoostDefinitions.Group
                || t.GroupId == PropulsionDefinitions.AfterburnerGroup
            )
            .OrderBy(t => t.Id)
            .AsEnumerable()
            .Where(t => SupportedProduct(catalog, t))
            .Select(t => t.Id)
            .ToArray();
    }

    private static bool Active(IndustryJob job) => job.Status == IndustryJobStatus.Installed;

    internal static bool IsInstalled(WorldDbContext world, long itemId) =>
        world.Set<IndustryJob>().Any(j => j.BlueprintId == itemId && j.Status == IndustryJobStatus.Installed);

    private static OwnedBlueprint ReadBlueprint(WorldDbContext world, CatalogDbContext catalog, OwnedItem item)
    {
        var inventory = PilotInventoryStore.Read(catalog, item);
        if (
            inventory.CategoryId != (int)InventoryCategory.Blueprint
            || item.FlagId != (int)InventoryFlag.Hangar
            || item.LocationId > int.MaxValue
        )
        {
            throw new InvalidDataException("Use an owned original blueprint in a station Item Hangar.");
        }
        var station =
            catalog.Set<Station>().SingleOrDefault(s => s.Id == item.LocationId)
            ?? throw new InvalidDataException("The blueprint is not in a station hangar.");
        var facility = ReadFacility(station);
        long? job = world
            .Set<IndustryJob>()
            .Where(j => j.BlueprintId == item.Id && j.Status == IndustryJobStatus.Installed)
            .Select(j => (long?)j.Id)
            .SingleOrDefault();
        return new(inventory, facility, job);
    }

    public IReadOnlyList<OwnedBlueprint> Blueprints(long pilotId, int? stationId = null)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var types = catalog.Set<ItemType>().Where(t => t.Group.CategoryId == (int)InventoryCategory.Blueprint).Select(t => t.Id).ToArray();
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
            .Where(i => i.LocationId <= int.MaxValue && catalog.Set<Station>().Any(s => s.Id == i.LocationId))
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
        _ = CurrentStation(world, catalog, pilotId, stationId);
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

    private ManufacturingJob ReadJob(CatalogDbContext catalog, IndustryJob job) =>
        new(
            job.Id,
            job.PilotId,
            job.BlueprintId,
            job.BlueprintTypeId,
            ReadFacility(Station(catalog, job.StationId)),
            job.ProductTypeId,
            job.Runs,
            job.OutputQuantity,
            job.StartTime,
            job.EndTime,
            job.DurationSeconds,
            Active(job) && Now >= job.EndTime ? IndustryJobStatus.Ready : job.Status,
            job.CompletedAt,
            job.CostCents
        );

    public IReadOnlyList<ManufacturingJob> Jobs(long pilotId, bool includeCompleted)
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

    public ManufacturingJob Job(long pilotId, long jobId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return ReadJob(catalog, OwnedJob(world, pilotId, jobId));
    }

    private static IndustryJob OwnedJob(WorldDbContext world, long pilotId, long jobId) =>
        world.Set<IndustryJob>().SingleOrDefault(j => j.Id == jobId && j.PilotId == pilotId)
        ?? throw new InvalidDataException("The manufacturing job is unavailable or belongs to another pilot.");

    public IndustryResult Install(long pilotId, ManufacturingRequest request)
    {
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
        // This cache's keys are strings, unlike the SDE's numeric blueprint keys.
        var recipe =
            catalog
                .Set<SourceRecord>()
                .Find("client/blueprints", JsonSerializer.Serialize(item.TypeId.ToString(CultureInfo.InvariantCulture)))
                ?.Read<ClientBlueprints>()
                .Activities?.Manufacturing
            ?? throw new InvalidDataException("This blueprint has no manufacturing recipe.");
        if (
            recipe.Products is not [var product]
            || product.TypeID is not > 0 or > int.MaxValue
            || request.ProductTypeId.HasValue && product.TypeID != request.ProductTypeId
            || product.Quantity is not > 0
            || recipe.Time is not > 0
            || !SupportedProduct(catalog, catalog.Set<ItemType>().Single(t => t.Id == (int)product.TypeID.Value))
        )
        {
            throw new InvalidDataException("This blueprint product is outside the supported starter production and module cohorts.");
        }
        foreach (var skill in recipe.Skills ?? [])
        {
            if (
                skill.TypeID is not > 0 or > int.MaxValue
                || skill.Level is not >= 1 or > 5
                || Level(world, catalog, pilotId, (int)skill.TypeID.Value) < skill.Level
            )
            {
                throw new InvalidDataException("Train the blueprint's required manufacturing skills first.");
            }
        }
        var calculation = ManufacturingRules.Plan(
            request,
            new(
                recipe.Time.Value,
                world.Set<IndustryJob>().Count(j => j.PilotId == pilotId && j.Status == IndustryJobStatus.Installed),
                Bonuses(world, catalog, pilotId),
                (recipe.Skills ?? [])
                    .Select(skill => new ManufacturingSkillTimeBonus(
                        Attributes(catalog, checked((int)skill.TypeID!.Value)).GetValueOrDefault(DogmaAttributeId.ManufactureTimePerLevel),
                        Level(world, catalog, pilotId, (int)skill.TypeID.Value)
                    ))
                    .ToArray(),
                (recipe.Materials ?? []).Select(material => new ManufacturingMaterial(material.TypeID, material.Quantity)).ToArray()
            )
        );
        long duration = calculation.DurationSeconds;
        var materials = calculation.Materials;
        List<InventoryChange> changes = [];
        foreach (var material in materials)
        {
            var stacks = world
                .Set<OwnedItem>()
                .Where(i =>
                    i.OwnerId == pilotId
                    && i.LocationId == request.FacilityId
                    && i.FlagId == (int)InventoryFlag.Hangar
                    && i.TypeId == material.Key
                    && !i.Singleton
                )
                .OrderBy(i => i.Id)
                .ToArray();
            if (stacks.Sum(i => (long)i.Quantity) < material.Value)
            {
                throw new InvalidDataException("There are not enough manufacturing materials in Item Hangar.");
            }
            int remaining = material.Value;
            foreach (var stack in stacks)
            {
                if (remaining == 0)
                {
                    break;
                }
                var before = PilotInventoryStore.Read(catalog, stack);
                int taken = Math.Min(stack.Quantity, remaining);
                remaining -= taken;
                stack.Quantity -= taken;
                changes.Add(new(before with { Quantity = stack.Quantity }, before));
                if (stack.Quantity == 0)
                {
                    world.Remove(stack);
                }
            }
        }
        InventoryItem? original = PilotInventoryStore.Read(catalog, item);
        if (item.Quantity > 1)
        {
            item.Quantity--;
            changes.Add(new(PilotInventoryStore.Read(catalog, item), original));
            item = world
                .Set<OwnedItem>()
                .Add(
                    new()
                    {
                        Id = InventoryPersistence.NextId(world),
                        OwnerId = pilotId,
                        TypeId = item.TypeId,
                        LocationId = item.LocationId,
                        FlagId = item.FlagId,
                        Quantity = 1,
                    }
                )
                .Entity;
            original = null;
        }
        item.Singleton = true;
        changes.Add(new(PilotInventoryStore.Read(catalog, item), original));
        long now = Now;
        var job = world
            .Set<IndustryJob>()
            .Add(
                new()
                {
                    PilotId = pilotId,
                    BlueprintId = item.Id,
                    BlueprintTypeId = item.TypeId,
                    StationId = request.FacilityId,
                    ProductTypeId = checked((int)product.TypeID.Value),
                    Runs = request.Runs,
                    OutputQuantity = checked((int)(product.Quantity.Value * request.Runs)),
                    StartTime = now,
                    EndTime = checked(now + duration * TimeSpan.TicksPerSecond),
                    DurationSeconds = duration,
                    Status = IndustryJobStatus.Installed,
                    MaterialsJson = JsonSerializer.Serialize(materials),
                }
            )
            .Entity;
        double? security = catalog
            .FindSource("client/systems", station.SolarSystemId.ToString(CultureInfo.InvariantCulture))
            ?.Read<ClientSystems>()
            .SecurityStatus;
        if (EconomicStore.Enabled(world) && security is null)
        {
            throw new InvalidDataException("Missing catalog industry security classification.");
        }
        var charge = EconomicStore.Manufacturing(world, pilotId, station.SolarSystemId, materials, security ?? 0);
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
        var station = CurrentStation(world, catalog, pilotId, job.StationId);
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
            throw new InvalidDataException("This manufacturing job cannot be completed yet.");
        }
        List<InventoryChange> changes = [];
        if (!cancel)
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
        // Cancellation releases the blueprint and slot, but consumes the paid
        // materials. A completed job delivers once, even after reconnect/retry.
        job.Status = status;
        job.CompletedAt = Now;
        if (!cancel)
        {
            PilotTaskTrackingStore.Record(world, catalog, pilotId, job.CompletedAt.Value, TaskEvents.IndustryDelivered);
        }
        world.SaveChanges();
        transaction.Commit();
        return new(ReadJob(catalog, job), changes);
    }
}
