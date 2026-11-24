using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private const string FleetLossesProperty = "\"" + nameof(CapsuleerOrganization.FleetLosses) + "\":";

    internal static void RecordFleetLoss(WorldAction world, CatalogDbContext catalog, Pilot pilot, long shipId, long now)
    {
        if (!CapsuleerPatrolFit.CombatHull(catalog, pilot.ActiveShip.TypeId) || Employer(world, pilot.Id, pilot.CorporationId) is null)
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

    internal static bool CanLaunchOffensiveFleet(
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
            return true;
        }

        var personal = Hangar(world, pilot.PilotId, pilot.StationId).ToLookup(item => item.TypeId);
        var depot = DepotStacks(world, employer.Id, pilot.StationId).ToLookup(item => item.TypeId);
        return MissingParts(MissingParts(parts, personal), depot).Length == 0;
    }

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
