using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Sovereignty;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private const string FleetLossesProperty = "\"" + nameof(CapsuleerOrganization.FleetLosses) + "\":";

    internal static void RecordFleetLoss(WorldAction world, CatalogDbContext catalog, Pilot pilot, long shipId, long now, bool playerAttack)
    {
        bool combatLoss = playerAttack && CapsuleerPatrolFit.CombatHull(catalog, pilot.ActiveShip.TypeId);
        if ((!combatLoss && !LostSovereigntyFit(world, catalog, pilot, shipId)) || Employer(world, pilot.Id, pilot.CorporationId) is null)
        {
            return;
        }

        var organization = CapsuleerCorporations.Organization(world, pilot.Id);
        var losses = (organization.FleetLosses ?? [])
            .Where(loss => loss.CorporationId == pilot.CorporationId && loss.ShipId != shipId && RecentFleetLoss(loss.Time, now))
            .Prepend(new(shipId, pilot.CorporationId, now))
            .Take(CapsuleerRegionalStockRules.MaximumLossesPerPilot)
            .ToArray();
        CapsuleerCorporations.SaveOrganization(world, pilot.Id, organization with { FleetLosses = losses });
    }

    private static bool LostSovereigntyFit(WorldAction world, CatalogDbContext catalog, Pilot pilot, long ship) =>
        CapsuleerAlliances.SovereigntyExecutorAlliance(world, pilot.CorporationId).HasValue
        && catalog.FittingType(pilot.ActiveShip.TypeId)?.GroupId != (int)HullClass.Interceptor
        && world
            .Filter<OwnedItem>(item => item.LocationId == ship)
            .AsEnumerable()
            .Any(item =>
                item.OwnerId == pilot.Id
                && item.Online
                && item.Singleton
                && InventoryDefinitions.IsHighSlot(item.FlagId)
                && EntosisCatalog.Supported(catalog, item.TypeId)
            );

    internal static bool CanLaunchOffensiveFleet(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        long now,
        Ship? candidate = null
    )
    {
        var parts = OffensiveFleetReserve(world, catalog, pilot, now, candidate);
        if (parts.Length == 0)
        {
            return true;
        }
        var personal = Hangar(world, pilot.PilotId, pilot.StationId).ToLookup(item => item.TypeId);
        var depot = DepotStacks(world, pilot.CorpId, pilot.StationId).ToLookup(item => item.TypeId);
        return MissingParts(MissingParts(parts, personal), depot).Length == 0;
    }

    internal static ReplacementPart[] OffensiveFleetReserve(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        long now,
        Ship? candidate = null
    )
    {
        int hull = candidate?.TypeId ?? pilot.ShipTypeId;
        if (
            !CapsuleerPatrolFit.CombatHull(catalog, hull)
            || Employer(world, pilot.PilotId, pilot.CorpId) is not { } employer
            || employer.StationId != pilot.StationId
            || !RecentFleetLosses(world, employer.Id, world.Filter<Pilot>(member => member.CorporationId == employer.Id), now)
                .Any(loss => loss.TypeId == hull)
            || HuntKit(world, catalog, pilot, candidate) is not { } parts
            || parts.Any(part => !IsMarketSupply(catalog, part.TypeId))
        )
        {
            return [];
        }
        return parts;
    }

    internal static long DepotStockQuantity(WorldAction world, int corporation, int station, int type) =>
        DepotStacks(world, corporation, station).Where(item => item.TypeId == type).Sum(item => (long)item.Quantity);

    private static IEnumerable<ShipLoss> RecentFleetLosses(EconomyTransaction world, int corporation, IEnumerable<Pilot> members, long now)
    {
        foreach (var pilot in members)
        {
            var row = world.Set<SavedCapsuleerOrganization>().Find(pilot.Id);
            if (row?.StateJson.Contains(FleetLossesProperty, StringComparison.Ordinal) != true)
            {
                continue;
            }

            foreach (var loss in CapsuleerCorporations.ReadOrganization(row).FleetLosses ?? [])
            {
                if (
                    loss.CorporationId == corporation
                    && RecentFleetLoss(loss.Time, now)
                    && world.Set<ShipLoss>().Find(loss.ShipId) is { } ship
                )
                {
                    yield return ship;
                }
            }
        }
    }

    private static bool RecentFleetLoss(long time, long now) =>
        time <= now && now - time < CapsuleerRegionalStockRules.FleetLossMemory.Ticks;
}
