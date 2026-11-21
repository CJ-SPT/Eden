using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Mining;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerMiner
{
    private bool TryTravelToMoonSupply(FactoryMoonSupply supply)
    {
        if (
            Pilot.InStructure
            || Pilot.StationId == supply.StationId
            || CapsuleerTrafficRoutes.ToStation(
                _operation.Catalog,
                Pilot.StationId,
                supply.StationId,
                (HullClass)Pilot.ShipGroupId,
                maximumJumps: CapsuleerIndustryRules.MaximumRefinerySupplyJumps,
                policy: database.Settings.Capsuleers
            )
                is not { } route
        )
        {
            return false;
        }
        RefreshObjects();
        UndockMiner();
        ContinueExpedition(new(Pilot.StationId, route.Systems) { RefineryStationId = supply.StationId });
        return true;
    }

    private bool TryLaunchRefineryIce(EconomyTransaction world, IReadOnlyList<InventoryItem> aboard, int[] products)
    {
        var catalog = _operation.Catalog;
        var harvester = aboard.FirstOrDefault(item => CapsuleerIndustry.IsIceHarvester(catalog, item));
        if (harvester is null)
        {
            return false;
        }
        MiningSiteStore.EnsureIce(world, catalog, Pilot.SystemId, Now);
        world.SaveChanges();
        var target = AsteroidFieldStore
            .List(world, catalog, Pilot.SystemId)
            .Where(rock => rock.GroupId == MiningDefinitions.IceGroup)
            .Select(rock =>
                (
                    Rock: rock,
                    Matches: catalog
                        .Read(CatalogData.Client.Typematerials, (long)rock.TypeId)
                        ?.Materials?.Count(material => material.MaterialTypeID is long type && products.Contains((int)type))
                        ?? 0
                )
            )
            .Where(ice => ice.Matches > 0)
            .OrderByDescending(ice => ice.Matches)
            .ThenBy(ice => ice.Rock.Id)
            .Select(ice => ice.Rock)
            .FirstOrDefault();
        if (target is null)
        {
            return false;
        }
        if (!harvester.Online)
        {
            PilotFittingStore.SetOnline(world, catalog, pilotId, Pilot.ShipId, harvester.Id, true, Now);
        }
        _laser = harvester with { Online = true };
        _miningReach = MiningReach(world, catalog, _laser);
        _work = Work with { RefineryIceType = target.TypeId, MoonExtractionId = null, Mercoxit = null };
        RefreshObjects();
        LaunchMiningShip(0);
        StartCourse(target, MinerActivity.FlyingToBelt);
        return true;
    }
}
