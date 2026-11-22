using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private static readonly CatalogDefinition<ReplacementKit?> s_earnedPvpKit = new(
        "earned-pvp-kit",
        (catalog, _) =>
            LoadCareerKit(catalog, (int)StarterMarketType.Merlin, includePatrolSupport: true) is { } kit
            && kit.Slots.Any(slot => slot.TypeId == (int)StarterMarketType.WarpScrambler)
                ? kit
                : null
    );

    internal static bool RequiresEarnedPvp(WorldAction world) => !world.Settings.FreshUniverse && world.Settings.Capsuleers.EarnedPvpShips;

    internal static bool HasPvpShip(WorldAction world, long pilot, long ship) =>
        !RequiresEarnedPvp(world) || CapsuleerCorporations.Organization(world, pilot).EarnedPvpShipId == ship;

    internal static bool CanStagePvp(WorldAction world, CatalogDbContext catalog, int system)
    {
        var policy = world.Settings.Capsuleers;
        double security = catalog.Definition(CapsuleerProvisioningCatalog.Security, system);
        return policy.RoamPercent > 0 && PatrolRules.Allowed(new(PatrolKind.Roam, system), security, policy)
            || policy.CampPercent > 0 && policy.CampSeconds > 0 && PatrolRules.Allowed(new(PatrolKind.GateCamp, system), security, policy);
    }

    internal CareerShipTurn TryBuyEarnedPvpShip(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        long now,
        CapsuleerCareerTraining? training = null
    )
    {
        long id = pilot.PilotId;
        int station = pilot.StationId;
        if (
            !RequiresEarnedPvp(world)
            || world.Set<SavedCapsuleerAgent>().Find(id) is not { } agent
            || !CapsuleerWorkHulls.CanLeaveShip(world, catalog, pilot)
            || !CanStagePvp(world, catalog, pilot.SystemId)
            || !StationCapabilities.Has(catalog, pilot.OperationId, StationService.Market)
            || catalog.Definition(s_earnedPvpKit, 0) is not { } kit
        )
        {
            return default;
        }

        var organization = CapsuleerCorporations.Organization(world, id);
        if (organization.EarnedPvpShipId is long ownedId && world.Ships.Find(ownedId) is { } owned && owned.OwnerId == id)
        {
            // Leave an existing fit at its station if the pilot cannot reach it from this hangar.
            if (
                owned.Id == pilot.ShipId
                || owned.Position is not null
                || owned.StationId != station
                || owned.StructureId.HasValue
                || owned.StoredInId.HasValue
            )
            {
                return default;
            }
            CapsuleerWorkHulls.Board(world, catalog, pilot, owned, now);
            CapsuleerCorporations.SaveOrganization(world, id, organization with { PvpWorkShipId = pilot.ShipId });
            return new(Boarded: true);
        }

        var parts = kit.Parts();
        long budget = StarterBalance(world, id) - agent.StartingCents;
        // The opening grant remains available for the pilot's work. Buy the whole combat kit in one market transaction.
        if (budget <= 0 || QuoteCareerKit(world, catalog, id, station, parts, budget) is not { } purchases)
        {
            return default;
        }
        if (!CanReplaceKit(world, catalog, id, CapsuleerCareer.Hauling, kit, parts))
        {
            var types = parts.Select(part => part.TypeId).ToArray();
            bool marketChanged = training?.TrainForEquipment(world, catalog, pilot, types, now) == true;
            return new(MarketChanged: marketChanged);
        }
        var bought = market.ExecuteBuy(world, catalog, id, station, purchases, 0);
        if (bought.Trades.Sum(trade => (long)trade.Quantity) != purchases.Sum(purchase => (long)purchase.Quantity))
        {
            throw new InvalidDataException("PvP ship offers changed before purchase.");
        }
        long ship = BoardKit(world, catalog, pilot, kit, now);
        CapsuleerCorporations.SaveOrganization(world, id, organization with { EarnedPvpShipId = ship, PvpWorkShipId = pilot.ShipId });
        return new(Boarded: true, MarketChanged: true);
    }
}
