using System.Text.Json;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Economy;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Structures;

namespace Eden.World.Industry;

internal sealed partial class PilotIndustryStore
{
    private IndustryResult InstallReaction(long pilotId, IndustryRequest request)
    {
        using var scope = new WorldActionScope(database, new(pilots: [pilotId]));
        var world = scope.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        AdvanceFacility(world, catalog, request.FacilityId, now);
        var station = CurrentStation(world, catalog, pilotId, request.FacilityId, null);
        double security = ManufacturingCatalog.Security(catalog, station.SolarSystemId);
        var item = world.Set<OwnedItem>().Find(request.BlueprintId);
        var recipe = ReactionCatalog.Recipes(catalog).GetValueOrDefault(request.BlueprintTypeId);
        if (
            item is null
            || item.OwnerId != pilotId
            || item.LocationId != station.Id
            || item.FlagId != (int)InventoryFlag.Hangar
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
        var plan = ReactionRules.Plan(
            request,
            recipe.Seconds,
            activeJobs,
            Bonuses(world, catalog, pilotId),
            recipe.Materials,
            FacilityMultiplier(catalog, station, (int)DogmaAttributeId.RefineryReactionTimeMultiplier)
        );
        double value = (double)EconomicStore.AdjustedPrice(world, item.TypeId) * request.Runs;
        var charge = EconomicStore.IndustryCharge(world, pilotId, station.SolarSystemId, value, request.Activity, security);
        if (request.QuotedCharge is { } quote && (quote.CostCents != charge.CostCents || quote.TaxCents != charge.TaxCents))
        {
            throw new InvalidDataException("Reaction price changed; reopen the formula to refresh the quote.");
        }

        List<InventoryChange> changes = [];
        ConsumeMaterials(world, catalog, pilotId, station.Id, plan.Materials, changes);
        item = ReserveBlueprint(world, catalog, item, changes);
        IndustryJob job = new()
        {
            PilotId = pilotId,
            BlueprintId = item.Id,
            BlueprintTypeId = item.TypeId,
            StationId = station.Id,
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
        world.Add(job);
        world.SaveChanges();
        long? balance =
            charge.TotalCents > 0
                ? EconomicStore.Pay(world, pilotId, station.OwnerId, job.Id, WalletEntryType.Manufacturing, charge.TotalCents, now)
                : null;
        var result = new IndustryResult(ReadJob(world, catalog, job), changes, balance);
        scope.Commit();
        return result;
    }
}
