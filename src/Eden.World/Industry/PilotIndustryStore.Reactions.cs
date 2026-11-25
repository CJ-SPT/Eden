using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Economy;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Structures;

namespace Eden.World.Industry;

internal sealed partial class PilotIndustryStore
{
    private IndustryResult InstallReaction(long pilotId, IndustryRequest request)
    {
        using var scope = new WorldActionScope(
            database,
            select: world =>
                new(pilots: [pilotId], systems: world.Structure(request.FacilityId) is { } structure ? [structure.SystemId] : [])
        );
        var world = scope.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var result = InstallReaction(world, catalog, pilotId, request);
        scope.Commit();
        return result;
    }

    private static IndustryStation ReactionStation(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long stationId,
        bool requireOnlineReactor
    )
    {
        if (world is not EconomyTransaction || stationId < StructureRules.FirstStructureId)
        {
            return CurrentStation(world, catalog, pilotId, stationId, null);
        }
        var structure = PilotStructureStore.Require(world, stationId);
        if (!PilotStructureStore.ServiceAccess(world, catalog, structure, pilotId))
        {
            throw new InvalidDataException("Industry access to this structure is denied.");
        }
        var station = IndustryStation.From(PilotStructureStore.Snapshot(world, catalog, structure));
        var pilot = Pilot(world, pilotId);
        var ship = pilot.ActiveShip;
        if (ship.Position is null && ship.DockedLocationId == stationId)
        {
            return station;
        }
        if (
            requireOnlineReactor
            && station.StructureServices?.Any(service =>
                service is StructureService.CompositeReactions or StructureService.BiochemicalReactions or StructureService.HybridReactions
            ) != true
        )
        {
            throw new InvalidDataException("Choose a refinery with an online reactor for remote reactions.");
        }
        int system = ShipSystem(world, catalog, ship);
        if (system != station.SolarSystemId)
        {
            // Build 3662168 omits Remote Reactions from all four Alpha skill lists.
            int level =
                world.Set<Account>().Find(pilot.AccountId)?.CloneGrade == AccountCloneGrade.Omega
                    ? Level(world, catalog, pilotId, (int)IndustrySkill.RemoteReactions)
                    : 0;
            if (!WithinJobRange(catalog, system, station.SolarSystemId, level))
            {
                throw new InvalidDataException("Your Remote Reactions skill does not reach this refinery.");
            }
        }
        return station;
    }

    internal static ManufacturingCharge ReactionCharge(WorldAction world, long pilot, int system, int formula, int runs, double security) =>
        EconomicStore.IndustryCharge(
            world,
            pilot,
            system,
            (double)EconomicStore.AdjustedPrice(world, formula) * runs,
            IndustryActivity.Reaction,
            security
        );

    internal IndustryResult InstallReaction(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        IndustryRequest request,
        int? supplyCorporation = null
    )
    {
        long now = Now;
        AdvanceFacility(world, catalog, request.FacilityId, now);
        var station = supplyCorporation.HasValue
            ? CurrentStation(world, catalog, pilotId, request.FacilityId, null)
            : ReactionStation(world, catalog, pilotId, request.FacilityId, requireOnlineReactor: true);
        double security = ManufacturingCatalog.Security(catalog, station.SolarSystemId);
        var item = world.Set<OwnedItem>().Find(request.BlueprintId);
        var recipe = ReactionCatalog.Recipes(catalog).GetValueOrDefault(request.BlueprintTypeId);
        if (
            item is null
            || item.OwnerId != pilotId
            || BlueprintStation(world, catalog, item) != station.Id
            || item.TypeId != request.BlueprintTypeId
            || item.BlueprintRuns >= 0
            || IsInstalled(world, item.Id)
            || request.LicensedRuns != 0
            || recipe is null
            || request.ProductTypeId is int product && product != recipe.Output
        )
        {
            throw new InvalidDataException("Use an available reaction formula in this refinery's Item Hangar.");
        }
        CheckBlueprintContainer(world, catalog, item, request);
        if (
            station.StructureServices?.Contains(recipe.Service) != true
            || !double.IsFinite(security)
            || security >= StructureRules.HighSecurityBoundary
        )
        {
            throw new InvalidDataException("Use the formula's online reactor in a low- or null-security refinery.");
        }
        foreach (var skill in recipe.Skills)
        {
            if (Level(world, catalog, pilotId, skill.TypeId) < skill.Level)
            {
                throw new InvalidDataException("Train the formula's required reaction skills first.");
            }
        }

        int activeJobs = world
            .Filter<IndustryJob>(j =>
                j.PilotId == pilotId && j.Status == IndustryJobStatus.Installed && j.Activity == IndustryActivity.Reaction
            )
            .Count();
        var bonuses = Bonuses(world, catalog, pilotId);
        double facilityTime = FacilityMultiplier(catalog, station, (int)DogmaAttributeId.RefineryReactionTimeMultiplier);
        var plan = supplyCorporation.HasValue
            ? ReactionRules.Plan(request.Runs, recipe.Seconds, activeJobs, bonuses, recipe.Materials, facilityTime)
            : ReactionRules.Plan(request, recipe.Seconds, activeJobs, bonuses, recipe.Materials, facilityTime);
        var charge = ReactionCharge(world, pilotId, station.SolarSystemId, item.TypeId, request.Runs, security);
        if (request.QuotedCharge is { } quote && (quote.CostCents != charge.CostCents || quote.TaxCents != charge.TaxCents))
        {
            throw new InvalidDataException("Reaction price changed; reopen the formula to refresh the quote.");
        }

        List<InventoryChange> changes = [];
        ConsumeMaterials(world, catalog, pilotId, Storage(world, catalog, pilotId, station.Id, request.Input), plan.Materials, changes);
        item = ReserveBlueprint(world, catalog, item, changes);
        IndustryJob job = new()
        {
            PilotId = pilotId,
            BlueprintId = item.Id,
            BlueprintTypeId = item.TypeId,
            StationId = station.Id,
            OutputCorporationId = supplyCorporation,
            Activity = IndustryActivity.Reaction,
            ProductTypeId = recipe.Output,
            Runs = request.Runs,
            OutputQuantity = checked(recipe.Quantity * request.Runs),
            StartTime = now,
            EndTime = checked(now + plan.DurationSeconds * TimeSpan.TicksPerSecond),
            DurationSeconds = plan.DurationSeconds,
            Status = IndustryJobStatus.Installed,
            MaterialsJson = JsonSerializer.Serialize(plan.Materials),
            CostCents = charge.TotalCents,
        };
        SetJobOutput(world, catalog, job, request);
        world.Add(job);
        world.SaveChanges();
        long? balance = null;
        if (charge.TotalCents > 0)
        {
            if (supplyCorporation is int corporation)
            {
                CorporationWalletStore.Pay(
                    world,
                    corporation,
                    station.OwnerId,
                    job.Id,
                    WalletEntryType.Manufacturing,
                    -charge.TotalCents,
                    now
                );
            }
            else
            {
                balance = EconomicStore.Pay(world, pilotId, station.OwnerId, job.Id, WalletEntryType.Manufacturing, charge.TotalCents, now);
            }
        }
        return new(ReadJob(world, catalog, job), changes, balance);
    }
}
