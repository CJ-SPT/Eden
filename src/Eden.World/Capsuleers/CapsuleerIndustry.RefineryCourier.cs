using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Data.World.Structures;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Skills;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Structures;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private const int BulkHaulerSkillLevel = 5;

    private static Collection? PlanRefineryCollection(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        SavedCorporation employer,
        FactorySupplyRequest request,
        IReadOnlySet<int> avoided
    )
    {
        if (
            request.Refinery is not { } task
            || task.Quantity > 0
                && PilotInventoryStore.HoldCapacity(world, catalog, courier.PilotId, courier.ShipId, (int)InventoryFlag.Cargo)
                    < PilotInventoryStore.Volume(catalog, task.ItemTypeId)
                && RefineryCargoSkill(world, catalog, courier) is null
        )
        {
            return null;
        }
        int maximum = CapsuleerIndustryRules.MaximumRefinerySupplyJumps;
        var route = CapsuleerTrafficRoutes.ToStation(catalog, courier.StationId, task.FactoryStationId, HullClass.Hauler, avoided, maximum);
        if (
            route is null
            || CapsuleerTrafficRoutes.ToStation(catalog, task.FactoryStationId, task.StagingStationId, HullClass.Hauler, avoided, maximum)
                is null
        )
        {
            return null;
        }
        return new(
            new(employer.Id, 0)
            {
                FactorySupply = new(task.ManufacturerId, task.FactoryStationId, task.StagingStationId, null)
                {
                    Refinery = new(task, RefineryVisitPhase.Warehouse),
                },
            },
            route
        );
    }

    internal static bool LoadRefineryCargo(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        CorporationSupplyRun run,
        FactoryRefineryTask task,
        long now,
        out FreightStack? freight
    )
    {
        freight = null;
        if (
            courier.StationId != task.FactoryStationId
            || Employer(world, courier.PilotId, run.CorporationId) is not { } corporation
            || Employer(world, task.ManufacturerId, run.CorporationId) is null
        )
        {
            return false;
        }
        if (task.Quantity == 0)
        {
            return CorporationRoleStore.AssignBotStationManager(world, corporation, courier.PilotId, now);
        }
        double volume = PilotInventoryStore.Volume(catalog, task.ItemTypeId);
        var stock = Hangar(world, task.ManufacturerId, task.FactoryStationId)
            .Where(item => item.TypeId == task.ItemTypeId && !item.Singleton)
            .OrderByDescending(item => item.Quantity)
            .FirstOrDefault();
        if (stock is null || volume <= 0)
        {
            return false;
        }
        double free = MakeRefineryCargoSpace(world, catalog, courier, volume, now);
        int quantity = (int)Math.Clamp(Math.Floor(free / volume), 0, Math.Min(task.Quantity, stock.Quantity));
        if (quantity == 0 || !CorporationRoleStore.AssignBotStationManager(world, corporation, courier.PilotId, now))
        {
            return false;
        }
        stock.Quantity -= quantity;
        if (stock.Quantity == 0)
        {
            world.Remove(stock);
        }
        var cargo = world
            .Add(
                new OwnedItem
                {
                    Id = world.NextItemId(),
                    OwnerId = courier.PilotId,
                    LocationId = courier.ShipId,
                    FlagId = (int)InventoryFlag.Cargo,
                    TypeId = task.ItemTypeId,
                    Quantity = quantity,
                }
            )
            .Entity;
        freight = new(cargo.Id, cargo.TypeId, quantity);
        return true;
    }

    private static int? RefineryCargoSkill(WorldAction world, CatalogDbContext catalog, DockedPilot courier)
    {
        if (courier.ShipTypeId != (int)FreightHull.Tayra)
        {
            return null;
        }
        int skill = (int)catalog.FittingAttributes(courier.ShipTypeId).GetValueOrDefault(DogmaAttributeId.RequiredSkill1);
        double rank = catalog.FittingAttributes(skill).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant);
        long points = world.Set<PilotSkill>().Find(courier.PilotId, skill)?.Points ?? 0;
        return rank > 0 && points < TrainedSkill.PointsForLevel(rank, BulkHaulerSkillLevel) ? skill : null;
    }

    private static double MakeRefineryCargoSpace(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        double volume,
        long now
    )
    {
        const int cargoFlag = (int)InventoryFlag.Cargo;
        double capacity = PilotInventoryStore.HoldCapacity(world, catalog, courier.PilotId, courier.ShipId, cargoFlag);
        if (capacity < volume && RefineryCargoSkill(world, catalog, courier) is int skill)
        {
            CapsuleerCommunityStore.GrantRequirements(world, catalog, courier.PilotId, skill, []);
            Qualify(world, catalog, courier.PilotId, new(skill, BulkHaulerSkillLevel));
            capacity = PilotInventoryStore.HoldCapacity(world, catalog, courier.PilotId, courier.ShipId, cargoFlag);
        }
        double free = FreeFreightVolume(world, catalog, courier);
        if (free < volume && capacity >= volume)
        {
            var cargo = PilotInventoryStore
                .List(world, catalog, courier.PilotId, courier.ShipId)
                .Where(item => item.FlagId == cargoFlag)
                .Select(item => item.Id)
                .ToArray();
            if (cargo.Length > 0)
            {
                PilotInventoryStore.Move(
                    world,
                    catalog,
                    courier.PilotId,
                    courier.StationId,
                    new(cargo, courier.ShipId, (int)InventoryFlag.Hangar, null),
                    now
                );
            }
            free = FreeFreightVolume(world, catalog, courier);
        }
        return free;
    }

    internal static StaticSpaceObject RefineryTarget(WorldAction world, CatalogDbContext catalog, FactoryRefineryTask task)
    {
        if (task.StructureId is long id && world.Set<SavedStructure>().Find(id) is { } structure)
        {
            var hull = StructureCatalog.Require(catalog, structure.TypeId);
            return new(
                id,
                hull.TypeId,
                catalog.RequireItem(hull.TypeId).GroupId,
                hull.CategoryId,
                structure.CorporationId,
                new(structure.X, structure.Y, structure.Z),
                (float)hull.Radius
            );
        }
        int system = catalog.RequireStation(task.StagingStationId).SolarSystemId;
        var moon =
            MoonMiningCatalog.Points(catalog, system).FirstOrDefault(point => point.MoonId == task.MoonId)
            ?? throw new InvalidDataException("The refinery's moon mining point is unavailable.");
        var type = catalog.RequireItem(MoonMiningRules.BeaconType);
        return new(MoonMiningCatalog.BeaconId(moon.MoonId), type.Id, type.GroupId, type.CategoryId, 0, moon.Position, 0) { Solid = false };
    }

    internal static void PerformRefineryWork(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        CorporationSupplyRun run,
        FactoryRefineryVisit visit,
        long now
    )
    {
        var task = visit.Task;
        if (
            Employer(world, courier.PilotId, run.CorporationId) is not { } corporation
            || !CorporationRoleStore.Has(world, courier.PilotId, corporation, Eden.Gameplay.Corporations.CorporationRole.StationManager)
            || courier.SystemId != catalog.RequireStation(task.StagingStationId).SolarSystemId
            || task.Quantity > 0 && visit.Cargo is null
        )
        {
            return;
        }
        if (task.Work == RefineryWork.Deploy)
        {
            var profile = StructureCatalog.Require(catalog, AthanorType);
            var target = RefineryTarget(world, catalog, task);
            if (
                !world.Filter<SavedStructure>(structure => structure.CorporationId == corporation.Id).AsEnumerable().Any(OwnedMoonRefinery)
                && !world
                    .Filter<SavedStructure>(structure => structure.SystemId == courier.SystemId)
                    .Any(structure =>
                        structure.MoonId == task.MoonId
                        && structure.State != StructureState.Destroyed
                        && structure.State != StructureState.Unanchored
                    )
                && CanPlaceRefinery(target.Position, profile, RefineryObstacles(world, catalog, courier.SystemId))
            )
            {
                PilotStructureStore.Deploy(
                    world,
                    catalog,
                    courier.PilotId,
                    new(
                        visit.Cargo!.ItemId,
                        target.Position,
                        0,
                        "Moon Refinery",
                        MoonId: task.MoonId,
                        ReinforceWeekday: StructureRules.NoReinforcementWeekday
                    ),
                    now
                );
            }
            return;
        }
        PilotStructureStore.Advance(world, catalog, now, courier.SystemId);
        if (
            task.StructureId is not long id
            || world.Set<SavedStructure>().Find(id) is not { } owned
            || owned.State is StructureState.Destroyed or StructureState.Unanchored
            || owned.UnanchorAt.HasValue
            || owned.CorporationId != corporation.Id
            || owned.MoonId != task.MoonId
        )
        {
            return;
        }
        var pilot = PilotStructureStore.Manage(world, owned, courier.PilotId);
        if (task.Work == RefineryWork.Core && !owned.HasCore && owned.State == StructureState.Onlining)
        {
            PilotStructureStore.InstallCore(world, catalog, owned, pilot, visit.Cargo!.ItemId, now);
            return;
        }
        if (!owned.HasCore || !StructureRules.Operational(owned.State))
        {
            return;
        }
        var fitted = world.Filter<SavedStructureService>(service => service.StructureId == id).ToArray();
        switch (task.Work)
        {
            case RefineryWork.FitService when fitted.All(service => world.Set<OwnedItem>().Find(service.ItemId)?.TypeId != task.ItemTypeId):
                PilotStructureStore.FitServices(world, catalog, owned, pilot, [new(visit.Cargo!.ItemId)]);
                break;
            case RefineryWork.Fuel:
                PilotStructureStore.DepositFuel(world, catalog, owned, pilot, visit.Cargo!.ItemId, visit.Cargo.Quantity);
                break;
            case RefineryWork.StartExtraction when PilotStructureStore.ActiveExtraction(world, id) is null:
                foreach (
                    var service in fitted.Where(service =>
                        !service.Online && s_refineryServices.Contains(world.Set<OwnedItem>().Find(service.ItemId)!.TypeId)
                    )
                )
                {
                    PilotStructureStore.SetServiceOnline(world, catalog, owned, service.ItemId, true, now);
                }
                PilotStructureStore.Start(world, catalog, pilot.Id, id, MoonMiningRules.MinimumExtractionSeconds, now);
                break;
        }
    }
}
