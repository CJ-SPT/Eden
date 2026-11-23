using Eden.Data.World.Capsuleers;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Economy;
using Eden.World.Industry;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private const string HuntReserveJobProperty = "\"" + nameof(CapsuleerOrganization.HuntReserveJobId) + "\":";

    internal long? DeliverHuntReserveJob(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, long now)
    {
        var row = world.Set<SavedCapsuleerOrganization>().Find(pilot.PilotId);
        if (row?.StateJson.Contains(HuntReserveJobProperty, StringComparison.Ordinal) != true)
        {
            return null;
        }
        var organization = CapsuleerCorporations.ReadOrganization(row);
        if (organization.HuntReserveJobId is not long id)
        {
            return null;
        }
        if (world.Set<IndustryJob>().Find(id) is { Status: IndustryJobStatus.Installed } job)
        {
            if (job.PilotId != pilot.PilotId || job.StationId != pilot.StationId)
            {
                return null;
            }
            if (!PilotIndustryStore.Ready(world, job, now))
            {
                return job.SuspendedAt is null && job.CapsuleerEndTicks is long due
                    ? due
                    : checked(world.ActiveTicks + TimeSpan.FromSeconds(HaulingRules.PollSeconds).Ticks);
            }
            industry.Finish(world, catalog, pilot.PilotId, id, pilot.SystemId, cancel: false);
        }
        CapsuleerCorporations.SaveOrganization(world, pilot.PilotId, organization with { HuntReserveJobId = null });
        return null;
    }

    private bool TryManufactureHuntReserve(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        ReplacementPart[] missing,
        decimal budget,
        CapsuleerOrganization organization
    )
    {
        long id = pilot.PilotId;
        if (
            !StationCapabilities.Has(catalog, pilot.OperationId, StationService.Manufacturing)
            || world.Filter<IndustryJob>(job => job.PilotId == id).Any(job => job.Status == IndustryJobStatus.Installed)
        )
        {
            return false;
        }
        int station = pilot.StationId;
        var hangar = Hangar(world, id, station);
        var recipes = ManufacturingCatalog.Recipes(catalog);
        double security = ManufacturingCatalog.Security(catalog, pilot.SystemId);
        if (double.IsNaN(security))
        {
            return false;
        }
        foreach (var blueprint in hangar.Where(item => item.BlueprintRuns != 0))
        {
            if (
                recipes.GetValueOrDefault(blueprint.TypeId) is not { } recipe
                || !missing.Any(part => part.TypeId == recipe.ProductTypeId)
                || !ManufacturingCatalog.Allowed(catalog, recipe.ProductTypeId, security)
                || recipe.Skills.Any(skill => PilotSkillReader.Level(world, catalog, id, skill.TypeId) < skill.Level)
            )
            {
                continue;
            }
            var materials = IndustryMaterials.Calculate(recipe.Materials, 1, blueprint.MaterialEfficiency);
            if (MissingHuntMaterials(materials, hangar).Count > 0)
            {
                continue;
            }
            var baseMaterials = IndustryMaterials.Calculate(recipe.Materials, 1);
            var charge = EconomicStore.Manufacturing(world, id, pilot.SystemId, baseMaterials, security);
            if (charge.TotalCents > budget)
            {
                continue;
            }
            var installed = industry.InstallManufacturing(
                world,
                catalog,
                id,
                new(blueprint.Id, recipe.BlueprintTypeId, station, 1, recipe.ProductTypeId, 0, materials),
                serverQuote: true
            );
            var job = world.Set<IndustryJob>().Find(installed.Job.Id)!;
            job.CapsuleerEndTicks = checked(world.ActiveTicks + job.EndTime - job.StartTime);
            CapsuleerCorporations.SaveOrganization(world, id, organization with { HuntReserveJobId = job.Id });
            return true;
        }
        return false;
    }

    private static Dictionary<int, int> MissingHuntMaterials(IReadOnlyDictionary<int, int> required, OwnedItem[] hangar) =>
        required
            .Select(material =>
                (
                    material.Key,
                    Quantity: material.Value - hangar.Where(item => item.TypeId == material.Key).Sum(item => (long)item.Quantity)
                )
            )
            .Where(material => material.Quantity > 0)
            .ToDictionary(material => material.Key, material => checked((int)material.Quantity));
}
