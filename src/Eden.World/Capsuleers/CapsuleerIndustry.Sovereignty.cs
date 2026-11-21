using Eden.Data.Catalog;
using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Sovereignty;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Sovereignty;

namespace Eden.World.Capsuleers;

internal sealed record SovereigntyReagentCargo(long ItemId, int Quantity);

internal sealed partial class CapsuleerIndustry
{
    internal bool OutfitExpedition(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        SavedCorporation corporation,
        int alliance,
        long captureDuration,
        long now
    )
    {
        const int carrier = CapsuleerAllianceRules.ExpeditionSquall;
        const int link = CapsuleerAllianceRules.ExpeditionLink;
        if (catalog.FittingType(carrier) is null || !EntosisCatalog.Supported(catalog, link))
        {
            return false;
        }
        var profile = EntosisCatalog.Module(catalog.FittingAttributes(link), 1);
        int fuelType = profile.FuelType;
        long actor = pilot.PilotId;
        long ship = pilot.ShipId;
        int station = pilot.StationId;
        bool boarding = pilot.ShipTypeId != carrier;
        var fittedLink = boarding
            ? null
            : world
                .Filter<OwnedItem>(item => item.LocationId == ship)
                .AsEnumerable()
                .FirstOrDefault(item =>
                    item.OwnerId == actor && item.TypeId == link && item.Singleton && InventoryDefinitions.IsHighSlot(item.FlagId)
                );
        int fuelNeeded = checked((CapsuleerHauler.RequiredExpeditionCycles(captureDuration, profile) + 1) * profile.FuelQuantity);
        if (!boarding)
        {
            fuelNeeded -= world
                .Filter<OwnedItem>(item => item.LocationId == ship)
                .Where(item =>
                    item.OwnerId == actor && item.TypeId == fuelType && item.FlagId == (int)InventoryFlag.Cargo && !item.Singleton
                )
                .Sum(item => item.Quantity);
        }
        var hangar = Hangar(world, actor, station).Where(item => !item.Singleton).ToLookup(item => item.TypeId);
        (int Type, int Quantity)[] kit = [(carrier, boarding ? 1 : 0), (link, fittedLink is null ? 1 : 0), (fuelType, fuelNeeded)];
        var shortfalls = kit.Select(part => (part.Type, Quantity: part.Quantity - hangar[part.Type].Sum(item => item.Quantity)))
            .Where(part => part.Quantity > 0)
            .ToArray();
        if (shortfalls.Length != 0)
        {
            if (!BuySovereigntySupplies(world, catalog, pilot, corporation, alliance, shortfalls))
            {
                return false;
            }
            hangar = Hangar(world, actor, station).Where(item => !item.Singleton).ToLookup(item => item.TypeId);
        }

        CapsuleerCommunityStore.GrantRequirements(world, catalog, actor, carrier, []);
        CapsuleerCommunityStore.GrantRequirements(world, catalog, actor, link, []);
        int skill = (int)catalog.FittingAttributes(carrier).GetValueOrDefault(DogmaAttributeId.RequiredSkill1);
        if (!Qualify(world, catalog, actor, new(skill, CapsuleerAllianceRules.ExpeditionHaulingSkillLevel)))
        {
            throw new InvalidDataException("The expedition carrier's hauling skill is unavailable.");
        }
        if (boarding)
        {
            long assembled = PilotFleetStore.Assemble(world, catalog, actor, [hangar[carrier].First().Id], name: null).ShipIds.Single();
            PilotFleetStore.Board(world, catalog, actor, assembled, ship, now);
            ship = assembled;
        }
        if (fittedLink is null)
        {
            long itemId = hangar[link].First().Id;
            var changes = PilotFittingStore.Fit(world, catalog, actor, [itemId], station, (int)InventoryFlag.AutoFit, now);
            long fittedId = changes.Single(change => change.Item.LocationId == ship && change.Item.TypeId == link).Item.Id;
            fittedLink = world.Set<OwnedItem>().Find(fittedId)!;
        }
        if (!fittedLink.Online)
        {
            PilotFittingStore.SetOnline(world, catalog, actor, ship, fittedLink.Id, true, now);
        }
        foreach (var stack in hangar[fuelType])
        {
            if (fuelNeeded <= 0)
            {
                break;
            }
            int quantity = Math.Min(stack.Quantity, fuelNeeded);
            PilotInventoryStore.Move(world, catalog, actor, ship, new([stack.Id], station, (int)InventoryFlag.Cargo, quantity), now);
            fuelNeeded -= quantity;
        }
        return true;
    }

    private bool BuySovereigntySupplies(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        SavedCorporation corporation,
        int alliance,
        (int Type, int Quantity)[] shortfalls
    )
    {
        var offers = market
            .SupplyOrders(world, catalog, pilot.StationId, shortfalls.Select(part => part.Type).ToArray(), pilot.PilotId, corporation.Id)
            .ToLookup(order => order.TypeId);
        List<MarketPurchase> purchases = [];
        foreach (var part in shortfalls)
        {
            long ceiling = ReferencePrice(catalog, part.Type);
            var purchase = QuoteInput(offers[part.Type].Where(order => order.PriceCents <= ceiling), part.Type, part.Quantity);
            if (purchase is null || purchase.Quantity < part.Quantity)
            {
                return false;
            }
            purchases.Add(purchase);
        }
        long cost = purchases.Sum(part => checked(part.LimitPriceCents * part.Quantity));
        int members = world.Filter<SavedAllianceMember>(member => member.AllianceId == alliance).Count();
        if (!CapsuleerAlliances.CanFund(world, corporation, members, cost))
        {
            return false;
        }
        market.BuyCorporationStock(world, catalog, pilot.PilotId, pilot.StationId, corporation, purchases);
        return true;
    }

    internal SovereigntyReagentCargo? LoadSovereigntyReagent(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        SavedCorporation corporation,
        SavedSovereigntyClaim claim,
        long now
    )
    {
        if (
            world.Set<SavedSovereigntyResourceHub>().Find(claim.HubId) is not { } hub
            || !world.Filter<SavedSovereigntyUpgrade>(row => row.HubId == claim.HubId).Any(upgrade => upgrade.ConfiguredOnline)
        )
        {
            return null;
        }
        SovereigntyResourcePersistence.SettleHub(world, catalog, hub, now);
        var allocation = SovereigntyResourcePersistence.Allocation(world, hub.HubId, now);
        var shortages = SovereigntyResourceRules.ReagentShortfalls(SovereigntyResourceCatalog.Upgrades(catalog), allocation);
        if (shortages.Count == 0)
        {
            return null;
        }
        const int cargo = (int)InventoryFlag.Cargo;
        long actor = pilot.PilotId;
        double space =
            PilotInventoryStore.HoldCapacity(world, catalog, actor, pilot.ShipId, cargo)
            - InventoryPersistence.UsedVolume(world, catalog, actor, pilot.ShipId, cargo);
        foreach (var shortage in shortages)
        {
            if (
                catalog.Read(CatalogData.Client.Types, (long)shortage.TypeId)?.Volume is not double volume
                || volume <= 0
                || !double.IsFinite(volume)
            )
            {
                continue;
            }
            int quantity = (int)Math.Clamp(Math.Floor(space / volume), 0, shortage.Quantity);
            if (quantity == 0)
            {
                continue;
            }
            var stock = Hangar(world, actor, pilot.StationId).Where(item => item.TypeId == shortage.TypeId && !item.Singleton).ToArray();
            int missing = quantity - stock.Sum(item => item.Quantity);
            if (missing > 0)
            {
                if (!BuySovereigntySupplies(world, catalog, pilot, corporation, claim.AllianceId, [(shortage.TypeId, missing)]))
                {
                    continue;
                }
                stock = Hangar(world, actor, pilot.StationId).Where(item => item.TypeId == shortage.TypeId && !item.Singleton).ToArray();
            }
            if (!CorporationRoleStore.AssignBotStationManager(world, corporation, actor, now))
            {
                throw new InvalidDataException("A bot sovereignty courier requires the structure manager role.");
            }
            int remaining = quantity;
            foreach (var stack in stock)
            {
                int moved = Math.Min(stack.Quantity, remaining);
                PilotInventoryStore.Move(world, catalog, actor, pilot.ShipId, new([stack.Id], pilot.StationId, cargo, moved), now);
                remaining -= moved;
                if (remaining == 0)
                {
                    break;
                }
            }
            var loaded = world
                .Filter<OwnedItem>(item => item.LocationId == pilot.ShipId)
                .First(item => item.OwnerId == actor && item.FlagId == cargo && item.TypeId == shortage.TypeId && !item.Singleton);
            return new(loaded.Id, quantity);
        }
        return null;
    }
}
