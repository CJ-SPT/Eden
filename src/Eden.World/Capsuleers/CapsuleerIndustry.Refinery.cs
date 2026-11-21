using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Mining;
using Eden.Data.World.Structures;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Structures;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    internal const int AthanorType = 35835;
    internal const int MoonDrillType = 45009;
    private const int ReprocessingServiceType = 35899;
    private const int CompositeReactorType = 45537;
    private const int NitrogenFuelBlock = 4051;
    private const int MaximumFuelShipment = 1600;

    // Build 3662168 Miner II manufacturing and the two advanced component recipes.
    private static readonly int[] s_minerTwoReactionComponents = [11541, 11689];
    private static readonly int[] s_refineryServices = [MoonDrillType, ReprocessingServiceType, CompositeReactorType];
    private static readonly CatalogDefinition<IReadOnlySet<int>> s_requiredMoonOres = new(
        "capsuleers/miner-ii-moon-ores",
        (catalog, _) => RequiredMoonOres(catalog)
    );

    private static IReadOnlySet<int> RequiredMoonOres(CatalogDbContext catalog)
    {
        var manufactured = catalog.Definitions.GlobalDefinition(s_recipesByProduct);
        var reactions = ReactionCatalog
            .Recipes(catalog)
            .Values.Where(recipe => recipe.Service == StructureService.CompositeReactions)
            .ToLookup(recipe => recipe.Output);
        HashSet<int> requirements = [];
        Queue<int> pending = new(
            s_minerTwoReactionComponents
                .SelectMany(type => manufactured[type])
                .SelectMany(recipe => recipe.Materials)
                .Select(part => (int)part.TypeId!.Value)
        );
        while (pending.TryDequeue(out int type))
        {
            if (requirements.Add(type))
            {
                foreach (int input in reactions[type].SelectMany(recipe => recipe.Materials).Select(part => (int)part.TypeId!.Value))
                {
                    pending.Enqueue(input);
                }
            }
        }
        return MoonMiningRules
            .OreTypes.Where(ore =>
                catalog
                    .Read(CatalogData.Client.Typematerials, (long)ore)
                    ?.Materials?.Any(material => material.MaterialTypeID is long type && requirements.Contains((int)type)) == true
            )
            .ToHashSet();
    }

    private bool? BuildMoonRefinery(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot factory,
        SavedCorporation corporation,
        OwnedItem[] hangar,
        long now,
        out FactorySupplyRequest? request
    )
    {
        request = null;
        if (
            StructureCatalog.Read(catalog, AthanorType) is not { CoreTypeId: int core } profile
            || s_refineryServices.Any(type => catalog.FittingType(type) is null)
            || catalog.FittingType(core) is null
            || catalog.FittingType(NitrogenFuelBlock) is null
            || !StructureRules.CorporationIsOldEnough(corporation.CreatedAt, now, world.Settings.Structures)
        )
        {
            return null;
        }
        if (
            LiveWorld.Find(world.WorldPath) is { } live
            && world
                .Filter<Pilot>(pilot => pilot.CorporationId == corporation.Id)
                .AsEnumerable()
                .Any(pilot => ActiveSupplyRun(live, pilot)?.FactorySupply?.Refinery is not null)
        )
        {
            // A courier assigned to its active hull can still deliver the package.
            return null;
        }
        var refinery = world
            .Filter<SavedStructure>(structure => structure.CorporationId == corporation.Id)
            .AsEnumerable()
            .Where(OwnedMoonRefinery)
            .OrderBy(structure => structure.Id)
            .FirstOrDefault();
        var site = refinery is null
            ? SelectRefinerySite(world, catalog, factory)
            : RefinerySiteAt(catalog, refinery.SystemId, refinery.MoonId!.Value);
        if (site is null)
        {
            return null;
        }
        if (refinery?.StateEndsAt is long deadline && deadline <= now)
        {
            PilotStructureStore.Advance(world, catalog, now, refinery.SystemId);
        }
        var task = NextRefineryWork(world, catalog, factory, site, refinery, profile, core, now);
        if (task is null)
        {
            return null;
        }
        if (
            task.Quantity == 0
            || hangar.Where(item => item.TypeId == task.ItemTypeId && !item.Singleton).Sum(item => (long)item.Quantity) >= task.Quantity
        )
        {
            request = new(corporation.Id, factory.StationId, null, []) { Refinery = task };
            return null;
        }

        bool? changed = ProduceRefineryEquipment(
            world,
            catalog,
            factory,
            corporation,
            hangar,
            task.ItemTypeId,
            task.Quantity,
            out var needs
        );
        if (needs.Length > 0)
        {
            request = new(corporation.Id, factory.StationId, null, needs);
        }
        return changed;
    }

    private sealed record MoonRefinerySite(int Station, int Moon);

    private sealed record RefineryObstacle(SpacePosition Position, double Radius);

    private static bool OwnedMoonRefinery(SavedStructure structure) =>
        structure.TypeId == AthanorType
        && structure.MoonId.HasValue
        && structure.State != StructureState.Destroyed
        && structure.State != StructureState.Unanchored;

    private static MoonRefinerySite? RefinerySiteAt(CatalogDbContext catalog, int system, int moon) =>
        catalog.Definitions.StationsInSystem(system).OrderBy(station => station.Id).FirstOrDefault() is { } station
            ? new(station.Id, moon)
            : null;

    private static MoonRefinerySite? SelectRefinerySite(EconomyTransaction world, CatalogDbContext catalog, DockedPilot factory)
    {
        var ores = catalog.Definitions.GlobalDefinition(s_requiredMoonOres);
        if (ores.Count == 0)
        {
            return null;
        }
        int maxJumps = CapsuleerIndustryRules.MaximumRefinerySupplyJumps;
        var occupied = world
            .Set<SavedStructure>()
            .Where(structure =>
                structure.MoonId.HasValue && structure.State != StructureState.Destroyed && structure.State != StructureState.Unanchored
            )
            .Select(structure => structure.MoonId!.Value)
            .ToHashSet();
        var profile = StructureCatalog.Require(catalog, AthanorType);
        List<(MoonRefinerySite Site, int Jumps, int Grades)> candidates = [];
        foreach (int system in NearbySystems(catalog, factory.SystemId, maxJumps))
        {
            double security = catalog.Definitions.SystemSecurity.GetValueOrDefault(system, 1);
            if (
                security >= StructureRules.HighSecurityBoundary
                || catalog.Definitions.StationsInSystem(system).OrderBy(station => station.Id).FirstOrDefault() is not { } station
                || CapsuleerTrafficRoutes.ToStation(
                    catalog,
                    factory.StationId,
                    station.Id,
                    HullClass.Hauler,
                    maximumJumps: maxJumps,
                    policy: world.Settings.Capsuleers
                )
                    is not { } route
            )
            {
                continue;
            }
            var obstacles = RefineryObstacles(world, catalog, system);
            foreach (var moon in MoonMiningCatalog.Points(catalog, system))
            {
                int grades = MoonMiningRules.Composition(moon.MoonId, security).Count(grade => ores.Contains(grade.TypeId));
                if (grades > 0 && !occupied.Contains(moon.MoonId) && CanPlaceRefinery(moon.Position, profile, obstacles))
                {
                    candidates.Add((new(station.Id, moon.MoonId), route.Systems.Length, grades));
                }
            }
        }
        return candidates
            .OrderByDescending(candidate => candidate.Grades)
            .ThenBy(candidate => candidate.Jumps)
            .ThenBy(candidate => candidate.Site.Moon)
            .Select(candidate => candidate.Site)
            .FirstOrDefault();
    }

    private static RefineryObstacle[] RefineryObstacles(WorldAction world, CatalogDbContext catalog, int system) =>
        [
            .. SystemObjects
                .Geometry(catalog, system)
                .Where(body => body.Solid)
                .Select(body => new RefineryObstacle(body.Position, body.Radius)),
            .. world
                .Filter<SavedStructure>(structure => structure.SystemId == system && structure.State != StructureState.Destroyed)
                .AsEnumerable()
                .Select(structure => new RefineryObstacle(
                    new(structure.X, structure.Y, structure.Z),
                    StructureCatalog.Require(catalog, structure.TypeId).Radius
                )),
        ];

    private static bool CanPlaceRefinery(SpacePosition point, StructureProfile profile, RefineryObstacle[] obstacles) =>
        obstacles.All(body => point.DistanceTo(body.Position) >= body.Radius + profile.Radius + StructureRules.Separation);

    private static FactoryRefineryTask? NextRefineryWork(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot factory,
        MoonRefinerySite site,
        SavedStructure? structure,
        StructureProfile profile,
        int core,
        long now,
        bool restockFracturedField = false
    )
    {
        if (structure is null)
        {
            return Task(RefineryWork.Deploy, AthanorType, 1);
        }
        if (structure.State == StructureState.Onlining && !structure.HasCore)
        {
            return Task(RefineryWork.Core, core, 1);
        }
        if (!StructureRules.Operational(structure.State) || !structure.HasCore || structure.UnanchorAt.HasValue)
        {
            return null;
        }
        var services = world
            .Filter<SavedStructureService>(service => service.StructureId == structure.Id)
            .AsEnumerable()
            .ToLookup(service => world.Set<OwnedItem>().Find(service.ItemId)!.TypeId);
        foreach (int type in s_refineryServices)
        {
            if (!services.Contains(type))
            {
                return Task(RefineryWork.FitService, type, 1);
            }
        }
        var extraction = PilotStructureStore.ActiveExtraction(world, structure.Id);
        bool unminedField =
            extraction is null
            && world
                .Filter<WorldAsteroid>(rock => rock.SystemId == structure.SystemId)
                .Any(rock => rock.BeltId == site.Moon && rock.Quantity > 0);
        if (unminedField && !restockFracturedField)
        {
            return null;
        }
        long until =
            extraction?.AutoFractureAt
            ?? checked(now + MoonMiningRules.MinimumExtractionSeconds * TimeSpan.TicksPerSecond + MoonMiningRules.AutoFractureDelay);
        long hours = (long)Math.Ceiling(Math.Max(0, until - now) / (double)StructureRules.FuelInterval) + 1;
        long needed = services.Sum(entry =>
        {
            var service = StructureCatalog.Service(catalog, entry.Key);
            int startup = PilotStructureStore.AdjustedFuel(catalog, profile, service, service.OnlineFuel);
            long operating = hours * PilotStructureStore.AdjustedFuel(catalog, profile, service, service.HourlyFuel);
            return entry.Where(module => module.Online).Sum(_ => operating)
                + (s_refineryServices.Contains(entry.Key) && entry.All(module => !module.Online) ? operating + startup : 0);
        });
        long fuel = world
            .Filter<OwnedItem>(item => item.LocationId == structure.Id)
            .Where(item => item.FlagId == StructureRules.FuelBayFlag)
            .Sum(item => (long)item.Quantity);
        return fuel < needed ? Task(RefineryWork.Fuel, NitrogenFuelBlock, (int)Math.Min(MaximumFuelShipment, needed - fuel))
            : extraction is null && !unminedField ? Task(RefineryWork.StartExtraction, 0, 0)
            : restockFracturedField && s_refineryServices.Any(type => services[type].All(module => !module.Online))
                ? Task(RefineryWork.OnlineServices, 0, 0)
            : null;

        FactoryRefineryTask Task(RefineryWork work, int type, int quantity) =>
            new(factory.PilotId, factory.StationId, site.Station, site.Moon, structure?.Id, work, type, quantity);
    }
}
