using Eden.Data.Catalog;
using Eden.Data.World.Alliances;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Sovereignty;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Sovereignty;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerAlliances
{
    private static readonly CatalogDefinition<int[]> s_firstClaimSystems = new("capsuleer-first-claim-systems", FirstClaimSystems);

    private bool TryProcureFirstHub(EconomyTransaction world, CatalogDbContext catalog, SavedCorporation corporation, long now)
    {
        const int hubType = EntosisRules.HubTypeId;
        int stationId = corporation.StationId;
        if (
            world.Set<SavedAllianceMember>().Find(corporation.Id) is not { } membership
            || world.Set<SavedAlliance>().Find(membership.AllianceId) is not { ClosedAt: null } alliance
            || alliance.ExecutorCorporationId != corporation.Id
            || world.Set<SavedCapsuleerAgent>().Find(alliance.CreatorId) is null
            || world.Filter<SavedSovereigntyClaim>(claim => claim.AllianceId == alliance.Id).Any()
            || catalog.Definitions.Stations.GetValueOrDefault(stationId) is not { } station
            || !StationCapabilities.Has(catalog, station.OperationId, StationService.Market)
            || catalog.Read(CatalogData.Client.Types, (long)hubType) is not { Published: 1, MarketGroupID: > 0 }
        )
        {
            return false;
        }
        int memberCount = world.Filter<SavedAllianceMember>(row => row.AllianceId == alliance.Id).Count();
        if (memberCount < CapsuleerAllianceRules.FoundingCorporations)
        {
            return false;
        }

        if (ExpeditionActive(world, corporation) || HasHubSupply(world, corporation, now))
        {
            return false;
        }
        long price = CapsuleerIndustry.ReferencePrice(catalog, hubType);
        if (price <= 0 || !CanFund(world, corporation, memberCount, price))
        {
            return false;
        }
        if (!UnclaimedSites(world, catalog, station.SolarSystemId).Any())
        {
            return false;
        }

        foreach (var pilot in world.Filter<Pilot>(row => row.CorporationId == corporation.Id).OrderBy(row => row.Id))
        {
            long operatorId = pilot.Id;
            if (
                !ActiveBot(world, operatorId)
                || pilot.ActiveShip.Position is not null
                || pilot.ActiveShip.DockedLocationId != stationId
                || !PilotMarketStore.HasSupplyListingSlot(world, catalog, operatorId)
            )
            {
                continue;
            }
            long cost = checked(price + PilotMarketStore.SupplyBrokerFee(world, catalog, operatorId, stationId, price, 1));
            if (CanFund(world, corporation, memberCount, cost))
            {
                market.BuyCorporationStock(
                    world,
                    catalog,
                    operatorId,
                    stationId,
                    corporation,
                    [new(hubType, price, 1)],
                    CapsuleerAllianceRules.FirstHubOrderDays
                );
                world.AfterCommit(() => marketSignal.Changed());
                return true;
            }
        }
        return false;
    }

    internal static bool ExpeditionActive(WorldAction world, SavedCorporation corporation, long? exceptPilot = null) =>
        LiveWorld.Find(world.WorldPath) is { } live
        && world
            .Filter<Pilot>(pilot => pilot.CorporationId == corporation.Id)
            .AsEnumerable()
            .Any(pilot =>
                pilot.Id != exceptPilot
                && live.HaulerCheckpoints.GetValueOrDefault(pilot.Id)?.Work is { SovereigntyExpedition: not null } work
                && work.ShipId == pilot.ActiveShipId
            );

    internal static IEnumerable<int> UnclaimedSites(WorldAction world, CatalogDbContext catalog, int headquarters) =>
        catalog
            .Definition(s_firstClaimSystems, headquarters)
            .Where(system => world.Settings.Capsuleers.CanWork(catalog.Definitions, system) && !PilotEntosisStore.HasHub(world, system));

    private static bool HasHubSupply(WorldAction world, SavedCorporation corporation, long now)
    {
        const int hubType = EntosisRules.HubTypeId;
        if (
            world
                .Filter<OwnedItem>(item => item.OwnerId == corporation.Id)
                .Any(item => item.TypeId == hubType && !item.Singleton && item.Quantity > 0)
        )
        {
            return true;
        }
        // Corporate bids charge the corporation and deliver the package to the placing pilot's hangar.
        var bids = world
            .Filter<SavedPersonalOrder>(order => order.CorporationId == corporation.Id)
            .Where(order => order.TypeId == hubType && order.Bid);
        if (bids.Any(order => order.Status == PersonalOrderStatus.Open && order.Expires > now))
        {
            return true;
        }
        return HeadquartersHub(world, corporation) is not null;
    }

    internal static OwnedItem? HeadquartersHub(WorldAction world, SavedCorporation corporation)
    {
        const int hubType = EntosisRules.HubTypeId;
        var owners = world
            .Filter<Pilot>(pilot => pilot.CorporationId == corporation.Id)
            .AsEnumerable()
            .Where(pilot => world.Set<SavedCapsuleerAgent>().Find(pilot.Id) is not null)
            .Select(pilot => pilot.Id)
            .Order()
            .Prepend(corporation.Id);
        return owners
            .SelectMany(owner => world.Filter<OwnedItem>(item => item.OwnerId == owner).AsEnumerable())
            .Where(item =>
                item.LocationId == corporation.StationId
                && item.TypeId == hubType
                && !item.Singleton
                && item.Quantity > 0
                && (item.FlagId == (int)InventoryFlag.Hangar || item.FlagId == CorporationStorageRules.MarketDeliveriesFlag)
            )
            .OrderBy(item => item.Id)
            .FirstOrDefault();
    }

    private static int[] FirstClaimSystems(CatalogDbContext catalog, int headquarters)
    {
        List<int> candidates = [];
        Queue<(int System, int Jumps)> pending = new();
        HashSet<int> seen = [headquarters];
        pending.Enqueue((headquarters, 0));
        while (pending.TryDequeue(out var current))
        {
            if (
                SovereigntyStore.System(catalog.Definitions, current.System) is { } system
                && SovereigntyRules.CanClaim(system)
                && SystemObjects.Geometry(catalog, current.System).Count(body => body.GroupId == EntosisRules.SunGroup) == 1
            )
            {
                candidates.Add(current.System);
            }
            if (current.Jumps == CapsuleerAllianceRules.FirstClaimMaximumJumps)
            {
                continue;
            }
            foreach (int neighbor in CapsuleerTrafficRoutes.Neighbours(catalog, current.System))
            {
                if (seen.Add(neighbor))
                {
                    pending.Enqueue((neighbor, current.Jumps + 1));
                }
            }
        }
        return candidates.ToArray();
    }

    private static bool TryMaintainAllianceDues(WorldAction world, SavedCorporation corporation) =>
        world.Set<SavedAllianceMember>().Find(corporation.Id) is { } member
        && world.Set<SavedAlliance>().Find(member.AllianceId) is { ClosedAt: null } alliance
        && alliance.ExecutorCorporationId == corporation.Id
        && world.Set<SavedCapsuleerAgent>().Find(alliance.CreatorId) is not null
        && world.Filter<SavedSovereigntyClaim>(claim => claim.AllianceId == alliance.Id).Any()
        && CorporationBillingStore.DefaultAllianceMaintenance(world, corporation.CeoId);
}
