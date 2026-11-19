using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Industry;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private sealed record ResearchSupplier(CatalogAgent Agent, ResearchField Field);

    private static readonly CatalogDefinition<ILookup<int, ResearchSupplier>> s_researchSuppliers = new(
        "capsuleers/datacore-suppliers",
        (catalog, _) =>
            NpcDirectory
                .Agents(catalog)
                .Where(agent =>
                    agent.Type == AgentType.Research
                    && agent.Level is >= 1 and <= 5
                    && agent.StationId is int station
                    && catalog.Definitions.Stations.ContainsKey(station)
                    && agent.SystemId.HasValue
                )
                .SelectMany(agent =>
                    PilotMissionStore
                        .ResearchFields(catalog, agent.Id)
                        .Where(field => field.DatacoreTypeId.HasValue)
                        .Select(field => new ResearchSupplier(agent, field))
                )
                .ToLookup(supplier => supplier.Field.DatacoreTypeId!.Value)
    );

    private Collection? PlanResearchCollection(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        SavedCorporation employer,
        long manufacturer,
        FactorySupplyRequest request,
        IReadOnlySet<int> avoided,
        long now
    )
    {
        if (employer.BalanceCents < ResearchRules.CentsPerDatacore)
        {
            return null;
        }
        var projects = world.Filter<SavedResearchProject>(project => project.PilotId == courier.PilotId).ToArray();
        if (projects.Length >= CapsuleerIndustryRules.ResearchCourierProjects && projects.All(project => ResearchUnits(project, now) == 0))
        {
            return null;
        }
        var shortages = FactoryShortages(world, catalog, manufacturer, request);
        if (shortages.Length == 0)
        {
            return null;
        }
        var suppliers = catalog.Definitions.GlobalDefinition(s_researchSuppliers);
        var pilot = world.Pilots.Find(courier.PilotId)!;
        int maximumJumps = CapsuleerIndustryRules.MaximumResearchSupplyJumps;
        var reachable = NearbySystems(catalog, courier.SystemId, maximumJumps);
        var factory = catalog.RequireStation(request.StationId);
        reachable.IntersectWith(NearbySystems(catalog, factory.SolarSystemId, maximumJumps));
        Collection? best = null;
        int bestDistance = int.MaxValue;
        foreach (var shortage in shortages)
        {
            var sources = suppliers[shortage.Need.TypeId];
            var trained = sources
                .Where(source => projects.Any(project => project.AgentId == source.Agent.Id && project.SkillId == source.Field.SkillId))
                .ToArray();
            foreach (var supplier in trained.Length > 0 ? trained : sources)
            {
                var agent = supplier.Agent;
                int station = agent.StationId!.Value;
                if (!reachable.Contains(agent.SystemId!.Value) || !PilotMissionStore.HasResearchAccess(world, catalog, pilot, agent))
                {
                    continue;
                }
                var project = projects.FirstOrDefault(row => row.AgentId == agent.Id);
                if (
                    project is null
                        ? projects.Length >= CapsuleerIndustryRules.ResearchCourierProjects
                        : project.SkillId != supplier.Field.SkillId || ResearchUnits(project, now) == 0
                )
                {
                    continue;
                }
                var outbound = CapsuleerTrafficRoutes.ToStation(
                    catalog,
                    courier.StationId,
                    station,
                    HullClass.Hauler,
                    avoided,
                    maximumJumps
                );
                var inbound = CapsuleerTrafficRoutes.ToStation(
                    catalog,
                    station,
                    request.StationId,
                    HullClass.Hauler,
                    avoided,
                    maximumJumps
                );
                if (outbound is null || inbound is null || outbound.Systems.Length + inbound.Systems.Length >= bestDistance)
                {
                    continue;
                }
                bestDistance = outbound.Systems.Length + inbound.Systems.Length;
                best = new(
                    new(employer.Id, 0)
                    {
                        FactorySupply = new(manufacturer, request.StationId, station, null)
                        {
                            Research = new(agent.Id, supplier.Field.SkillId, shortage.Need.TypeId),
                        },
                    },
                    outbound
                );
            }
        }
        return best;
    }

    private static FreightManifest? ResearchPickup(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        SavedCorporation employer,
        FactorySupplyShipment shipment,
        FactoryResearchSource source,
        IReadOnlyList<FactoryShortage> shortages,
        long now
    )
    {
        var supplier = catalog
            .Definitions.GlobalDefinition(s_researchSuppliers)[source.DatacoreTypeId]
            .FirstOrDefault(entry => entry.Agent.Id == source.AgentId && entry.Field.SkillId == source.SkillId);
        var shortage = shortages.FirstOrDefault(line => line.Need.TypeId == source.DatacoreTypeId);
        var pilot = world.Pilots.Find(courier.PilotId)!;
        if (supplier is null || shortage is null || !PilotMissionStore.HasResearchAccess(world, catalog, pilot, supplier.Agent))
        {
            return null;
        }
        var projects = world.Filter<SavedResearchProject>(project => project.PilotId == courier.PilotId).ToArray();
        var project = projects.FirstOrDefault(row => row.AgentId == source.AgentId);
        if (project is null)
        {
            if (projects.Length >= CapsuleerIndustryRules.ResearchCourierProjects)
            {
                return null;
            }
            List<ManufacturingRequiredSkill> training =
            [
                new(ResearchRules.Science, 5),
                new(source.SkillId, Math.Max(supplier.Agent.Level, CapsuleerIndustryRules.ResearchCourierFieldLevel)),
            ];
            if (projects.Length > 0)
            {
                training.Add(new(ResearchRules.ProjectManagement, 1));
            }
            if (training.Any(skill => catalog.FittingAttributes(skill.TypeId).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant) <= 0))
            {
                return null;
            }
            foreach (var skill in training)
            {
                CapsuleerCommunityStore.GrantRequirements(world, catalog, courier.PilotId, skill.TypeId, []);
                Qualify(world, catalog, courier.PilotId, skill);
            }
            PilotMissionStore.StartResearch(world, catalog, pilot, supplier.Agent, source.SkillId, now);
            return null;
        }
        if (project.SkillId != source.SkillId)
        {
            return null;
        }
        double free = FreeFreightVolume(world, catalog, courier);
        int capacity = (int)
            Math.Clamp(Math.Floor((free + InventoryDefinitions.ResourceTolerance) / shortage.UnitVolume), 0, shortage.Quantity);
        int quantity = (int)
            Math.Min(Math.Min(capacity, ResearchUnits(project, now)), employer.BalanceCents / ResearchRules.CentsPerDatacore);
        if (quantity <= 0)
        {
            return null;
        }
        PilotMissionStore.BuyDatacores(world, catalog, pilot, supplier.Agent, project.Id, quantity, now, employer.Id);
        return new(project.Id, shipment.FactoryStationId, [StowPurchased(world, courier, source.DatacoreTypeId, quantity)]);
    }

    private static int ResearchUnits(SavedResearchProject project, long now) =>
        (int)
            Math.Min(
                int.MaxValue,
                Math.Floor(
                    ResearchRules.Accrue(project.Points, project.PointsPerDay, project.SettledAt, now) / ResearchRules.PointsPerDatacore
                )
            );
}
