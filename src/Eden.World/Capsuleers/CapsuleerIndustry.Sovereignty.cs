using Eden.Data.Catalog;
using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
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
using Eden.World.Market;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Sovereignty;

namespace Eden.World.Capsuleers;

internal sealed record SovereigntyReagentCargo(long ItemId, int Quantity);

internal readonly record struct ExpeditionOutfit(bool Ready, bool MarketChanged);

internal sealed partial class CapsuleerIndustry
{
    internal static int ExpeditionHaulingSkill(CatalogDbContext catalog) =>
        (int)catalog.FittingAttributes(CapsuleerAllianceRules.ExpeditionSquall).GetValueOrDefault(DogmaAttributeId.RequiredSkill1);

    internal static bool CanTrainExpedition(CatalogDbContext catalog) =>
        ExpeditionHaulingSkill(catalog) > 0 && EntosisCatalog.Supported(catalog, CapsuleerAllianceRules.ExpeditionLink);

    internal ExpeditionOutfit OutfitExpedition(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        SavedCorporation corporation,
        int alliance,
        long captureDuration,
        long now,
        EntosisProfile? fittedProfile = null
    )
    {
        const int carrier = CapsuleerAllianceRules.ExpeditionSquall;
        const int link = CapsuleerAllianceRules.ExpeditionLink;
        bool refueling = fittedProfile is not null;
        if (!refueling && (catalog.FittingType(carrier) is null || !EntosisCatalog.Supported(catalog, link)))
        {
            return default;
        }
        var profile = fittedProfile ?? EntosisCatalog.Module(catalog.FittingAttributes(link), 1);
        int fuelType = profile.FuelType;
        long actor = pilot.PilotId;
        long ship = pilot.ShipId;
        int station = pilot.StationId;
        bool boarding = !refueling && pilot.ShipTypeId != carrier;
        var fittedLink =
            boarding || refueling
                ? null
                : world
                    .Filter<OwnedItem>(item => item.LocationId == ship)
                    .AsEnumerable()
                    .FirstOrDefault(item =>
                        item.OwnerId == actor && item.TypeId == link && item.Singleton && InventoryDefinitions.IsHighSlot(item.FlagId)
                    );
        int fuelNeeded = checked(CapsuleerHauler.RequiredExpeditionCycles(captureDuration, profile) * profile.FuelQuantity);
        if (!boarding)
        {
            fuelNeeded -= world
                .Filter<OwnedItem>(item => item.LocationId == ship)
                .Where(item =>
                    item.OwnerId == actor && item.TypeId == fuelType && item.FlagId == (int)InventoryFlag.Cargo && !item.Singleton
                )
                .Sum(item => item.Quantity);
        }
        int minimumFuel = Math.Max(0, fuelNeeded);
        int preferredFuel = Math.Max(0, checked(fuelNeeded + profile.FuelQuantity));
        if (!boarding && preferredFuel > 0)
        {
            double fuelVolume = PilotInventoryStore.Volume(catalog, fuelType);
            double free = FreeFreightVolume(world, catalog, pilot);
            if (minimumFuel > 0 && minimumFuel * fuelVolume > free)
            {
                return default;
            }
            if (preferredFuel * fuelVolume > free)
            {
                preferredFuel = minimumFuel;
            }
        }
        var hangar = Hangar(world, actor, station).Where(item => !item.Singleton).ToLookup(item => item.TypeId);
        var depot = DepotStacks(world, corporation.Id, station).OrderBy(item => item.Id).ToLookup(item => item.TypeId);
        ReplacementPart[] kit =
        [
            new(carrier, boarding ? 1 : 0),
            new(link, !refueling && fittedLink is null ? 1 : 0),
            new(fuelType, preferredFuel),
        ];
        var supplies = BuySovereigntySupplies(world, catalog, pilot, corporation, alliance, MissingParts(MissingParts(kit, hangar), depot));
        if (!supplies.Ready && kit[^1].Quantity > minimumFuel)
        {
            // A spare cycle helps with interruptions. Dispatch can proceed on the required fuel alone.
            kit[^1] = new(fuelType, minimumFuel);
            supplies = BuySovereigntySupplies(world, catalog, pilot, corporation, alliance, MissingParts(MissingParts(kit, hangar), depot));
        }
        if (!supplies.Ready)
        {
            return supplies;
        }
        if (supplies.MarketChanged)
        {
            hangar = Hangar(world, actor, station).Where(item => !item.Singleton).ToLookup(item => item.TypeId);
        }
        var requisition = MissingParts(kit, hangar);
        if (requisition.Length > 0)
        {
            foreach (var part in requisition)
            {
                if (TransferReplacementStock(world, catalog, actor, station, part, depot[part.TypeId]) != 0)
                {
                    throw new InvalidDataException("The paid expedition supplies are no longer available.");
                }
            }
            hangar = Hangar(world, actor, station).Where(item => !item.Singleton).ToLookup(item => item.TypeId);
        }
        fuelNeeded = kit[^1].Quantity;

        if (!refueling)
        {
            CapsuleerCommunityStore.GrantRequirements(world, catalog, actor, carrier, []);
            CapsuleerCommunityStore.GrantRequirements(world, catalog, actor, link, []);
            if (!Qualify(world, catalog, actor, new(ExpeditionHaulingSkill(catalog), CapsuleerAllianceRules.ExpeditionHaulingSkillLevel)))
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
        return supplies;
    }

    private ExpeditionOutfit BuySovereigntySupplies(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        SavedCorporation corporation,
        int alliance,
        ReplacementPart[] shortfalls
    )
    {
        if (shortfalls.Length == 0)
        {
            return new(true, false);
        }
        var offers = market
            .SupplyOrders(world, catalog, pilot.StationId, shortfalls.Select(part => part.TypeId).ToArray(), pilot.PilotId, corporation.Id)
            .ToLookup(order => order.TypeId);
        int members = world.Filter<SavedAllianceMember>(member => member.AllianceId == alliance).Count();
        List<MarketPurchase> purchases = [];
        foreach (var part in shortfalls)
        {
            long ceiling = ReferencePrice(catalog, part.TypeId);
            var purchase = QuoteInput(offers[part.TypeId].Where(order => order.PriceCents <= ceiling), part.TypeId, part.Quantity);
            if (purchase is null || purchase.Quantity < part.Quantity)
            {
                return default;
            }
            purchases.Add(purchase);
        }
        long cost = purchases.Sum(part => checked(part.LimitPriceCents * part.Quantity));
        if (!CapsuleerAlliances.CanFund(world, corporation, members, cost))
        {
            return default;
        }
        market.BuyCorporationStock(world, catalog, pilot.PilotId, pilot.StationId, corporation, purchases);
        return new(true, true);
    }

    internal static bool HasConfiguredSovereigntyUpgrades(WorldAction world, long hubId) =>
        world.Filter<SavedSovereigntyUpgrade>(row => row.HubId == hubId).Any(upgrade => upgrade.ConfiguredOnline);

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
            || !HasConfiguredSovereigntyUpgrades(world, claim.HubId)
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
                if (!BuySovereigntySupplies(world, catalog, pilot, corporation, claim.AllianceId, [new(shortage.TypeId, missing)]).Ready)
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
