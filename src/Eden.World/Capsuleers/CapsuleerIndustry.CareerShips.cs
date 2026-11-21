using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Industry;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Industry;
using Eden.World.Market;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;

namespace Eden.World.Capsuleers;

internal readonly record struct CareerShipTurn(bool Boarded = false, bool MarketChanged = false, int[]? OreTypes = null)
{
    internal CapsuleerTrafficRoute? Supplier { get; init; }
}

internal sealed partial class CapsuleerIndustry
{
    private static readonly CatalogDefinition<ReplacementKit?> s_careerKits = new("career-ship-kits", LoadCareerKit);

    internal static ReplacementKit? CareerKit(CatalogDbContext catalog, int type) => catalog.Definition(s_careerKits, type);

    private static ReplacementKit? LoadCareerKit(CatalogDbContext catalog, int type)
    {
        var hull = (HullClass)(catalog.FittingType(type)?.GroupId ?? 0);
        const int high = (int)InventoryFlag.HighSlot0;
        var profession = CapsuleerCareerTraining.Profession(catalog, type, hull);
        ReplacementKit? kit = null;
        if (
            profession == CapsuleerProfession.Mining
            && hull is HullClass.Frigate or HullClass.ExpeditionFrigate or HullClass.MiningBarge or HullClass.Exhumer
        )
        {
            int laser = (int)CapsuleerMiningRules.Laser((CapsuleerMiningHull)type);
            kit = new(type, [new(high, laser)], new HashSet<int> { high }, [], []);
        }
        else if (profession == CapsuleerProfession.Hauling)
        {
            int drive = (int)StarterMarketType.Afterburner;
            int mid = (int)InventoryFlag.MediumSlot0;
            var slots =
                catalog.FittingType(drive) is not null
                && catalog.FittingAttributes(type).GetValueOrDefault(DogmaAttributeId.MediumSlots) > 0
                && WithinBaseFittingResources(catalog, type, [new(mid, drive)])
                    ? new FittingSlot[] { new(mid, drive) }
                    : [];
            kit = new(type, slots, slots.Select(slot => slot.FlagId).ToHashSet(), [], []);
        }
        else if (
            profession == CapsuleerProfession.Combat
            && hull != HullClass.StrategicCruiser
            && CapsuleerPatrolFit.CareerFit(catalog, type) is { } fit
        )
        {
            var weapons = fit.Equipment.Where(item => InventoryDefinitions.IsHighSlot(item.Flag)).ToLookup(item => item.Flag);
            var slots = weapons
                .Select(slot => new FittingSlot(
                    slot.Key,
                    slot.Single(item => catalog.FittingType(item.TypeId)?.CategoryId == (int)InventoryCategory.Module).TypeId
                ))
                .ToArray();
            var magazines = slots
                .Select(slot =>
                    Magazine(
                        catalog,
                        slot.FlagId,
                        slot.TypeId,
                        weapons[slot.FlagId]
                            .Single(item => catalog.FittingType(item.TypeId)?.CategoryId == (int)InventoryCategory.Charge)
                            .TypeId
                    )
                )
                .OfType<ReplacementMagazine>()
                .ToArray();
            if (slots.Length > 0 && magazines.Length == slots.Length && WithinBaseFittingResources(catalog, type, slots))
            {
                kit = new(type, slots, slots.Select(slot => slot.FlagId).ToHashSet(), [], magazines);
            }
        }
        return kit is not null && SupportedKit(catalog, kit, kit.Parts()) ? kit : null;
    }

    internal CareerShipTurn AdvanceCareerShip(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        CapsuleerCareerPlan? plan,
        long now,
        bool starterWorkshop = false,
        IReadOnlySet<int>? avoidedSuppliers = null
    )
    {
        long id = pilot.PilotId;
        int station = pilot.StationId;
        if (pilot.Position is not null || station <= 0)
        {
            return default;
        }
        if (plan is not { ShipTypeId: > 0 } || plan.ShipTypeId == pilot.ShipTypeId || CareerKit(catalog, plan.ShipTypeId) is not { } kit)
        {
            return new(MarketChanged: starterWorkshop && SellStarterOre(world, catalog, pilot, Hangar(world, id, station)));
        }
        var parts = kit.Parts();
        if (
            !CanReplaceKit(
                world,
                catalog,
                id,
                plan.Profession == CapsuleerProfession.Mining ? CapsuleerCareer.Mining : CapsuleerCareer.Hauling,
                kit,
                parts
            )
        )
        {
            return new(MarketChanged: starterWorkshop && SellStarterOre(world, catalog, pilot, Hangar(world, id, station)));
        }

        var hangar = Hangar(world, id, station);
        var missing = MissingParts(parts, hangar.ToLookup(item => item.TypeId));
        var bids = world.Settings.FreshUniverse && missing.Length > 0 ? LocalCareerBids(world, id, station, missing, now) : [];
        if (
            world.Settings.FreshUniverse
            && missing.Length > 0
            && pilot.ShipGroupId == (int)HullClass.Corvette
            && Employer(world, id, pilot.CorpId) is { } employer
            && world.Filter<ShipLoss>(loss => loss.PilotId == id).Any(loss => loss.TypeId == kit.HullTypeId)
        )
        {
            var depot = DepotStacks(world, employer.Id, station).ToLookup(item => item.TypeId);
            if (MissingParts(missing, depot).Length == 0)
            {
                foreach (var part in missing)
                {
                    TransferReplacementStock(world, catalog, id, station, part, depot[part.TypeId]);
                }
                CancelCareerBids(world, catalog, bids);
                BoardKit(world, catalog, pilot, kit, now);
                return new(Boarded: true, MarketChanged: bids.Length > 0);
            }
        }
        if (missing.Length == 0)
        {
            BoardKit(world, catalog, pilot, kit, now);
            return new(Boarded: true);
        }
        if (starterWorkshop && FinishCareerManufacturing(world, catalog, pilot, missing, now))
        {
            return new(MarketChanged: SellStarterOre(world, catalog, pilot, hangar));
        }
        if (!StationCapabilities.Has(catalog, pilot.OperationId, StationService.Market))
        {
            return default;
        }
        long balance = StarterBalance(world, id);
        decimal spending = balance + bids.Sum(bid => (decimal)bid.EscrowCents);
        if (QuoteCareerKit(world, catalog, id, station, missing, spending) is { } purchases)
        {
            CancelCareerBids(world, catalog, bids);
            market.ExecuteBuy(world, catalog, id, station, purchases, 0);
            if (MissingParts(parts, Hangar(world, id, station).ToLookup(item => item.TypeId)).Length == 0)
            {
                BoardKit(world, catalog, pilot, kit, now);
                return new(Boarded: true, MarketChanged: true);
            }
            return new(MarketChanged: true);
        }
        if (starterWorkshop)
        {
            var sources = catalog.Definition(CapsuleerProvisioningCatalog.StarterOreSources, pilot.SystemId);
            var recipes = ManufacturingCatalog.Recipes(catalog).Values;
            foreach (var part in missing.OrderBy(part => part.TypeId == kit.HullTypeId))
            {
                var recipe = recipes.FirstOrDefault(recipe =>
                    recipe.ProductTypeId == part.TypeId
                    && recipe.Materials.All(material =>
                        material.TypeId is > 0 and <= int.MaxValue && sources.Contains((int)material.TypeId.Value)
                    )
                    && recipe.Skills.All(skill => PilotSkillReader.Level(world, catalog, id, skill.TypeId) >= skill.Level)
                );
                if (recipe is not null)
                {
                    bool changed = PrepareStarterJob(world, catalog, pilot, hangar, recipe, out var oreTypes);
                    return new(MarketChanged: changed, OreTypes: oreTypes);
                }
            }
            if (SellStarterOre(world, catalog, pilot, hangar))
            {
                return new(MarketChanged: true);
            }
        }
        if (
            world.Settings.FreshUniverse
            && avoidedSuppliers is not null
            && CareerSupplier(world, catalog, pilot, kit.HullTypeId, parts, spending, avoidedSuppliers, now) is { } supplier
        )
        {
            CancelCareerBids(world, catalog, bids);
            return new(MarketChanged: bids.Length > 0) { Supplier = supplier };
        }
        return new(MarketChanged: PlaceCareerBid(world, catalog, pilot, missing, balance, now));
    }

    private MarketPurchase[]? QuoteCareerKit(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long pilot,
        int station,
        ReplacementPart[] missing,
        decimal budget
    )
    {
        var offers = market
            .SupplyOrders(world, catalog, station, missing.Select(part => part.TypeId).ToArray(), pilot)
            .ToLookup(order => order.TypeId);
        List<MarketPurchase> purchases = [];
        foreach (var part in missing)
        {
            long cap = CapsuleerReplacementRules.LimitPrice(ReferencePrice(catalog, part.TypeId));
            if (
                QuoteInput(offers[part.TypeId].Where(order => order.PriceCents <= cap), part.TypeId, part.Quantity) is not { } purchase
                || purchase.Quantity != part.Quantity
            )
            {
                return null;
            }
            budget -= (decimal)purchase.LimitPriceCents * purchase.Quantity;
            if (budget < 0)
            {
                return null;
            }
            purchases.Add(purchase);
        }
        return purchases.ToArray();
    }

    private CapsuleerTrafficRoute? CareerSupplier(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        int hull,
        ReplacementPart[] parts,
        decimal budget,
        IReadOnlySet<int> avoided,
        long now
    )
    {
        if (budget <= 0)
        {
            return null;
        }
        const int jumps = CapsuleerRegionalStockRules.MaximumJumps;
        var nearby = NearbySystems(catalog, pilot.SystemId, jumps);
        var stations = world
            .Filter<SavedPersonalOrder>(order => order.TypeId == hull)
            .AsEnumerable()
            .Where(order =>
                !order.Bid
                && order.PilotId != pilot.PilotId
                && order.Status == PersonalOrderStatus.Open
                && order.Expires > now
                && order.Remaining > 0
                && order.StationId != pilot.StationId
                && nearby.Contains(order.SystemId)
            )
            .Select(order => order.StationId)
            .Distinct()
            .Where(station =>
                catalog.FindStation(station) is { } stop && StationCapabilities.Has(catalog, stop.OperationId, StationService.Market)
            );
        return stations
            .Select(station =>
                CapsuleerTrafficRoutes.ToStation(
                    catalog,
                    pilot.StationId,
                    station,
                    (HullClass)pilot.ShipGroupId,
                    avoided,
                    jumps,
                    world.Settings.Capsuleers
                )
            )
            .OfType<CapsuleerTrafficRoute>()
            .OrderBy(route => route.Systems.Length)
            .ThenBy(route => route.DestinationStationId)
            .FirstOrDefault(route => QuoteCareerKit(world, catalog, pilot.PilotId, route.DestinationStationId, parts, budget) is not null);
    }

    private static SavedPersonalOrder[] LocalCareerBids(
        EconomyTransaction world,
        long pilot,
        int station,
        ReplacementPart[] missing,
        long now
    ) =>
        world
            .Filter<SavedPersonalOrder>(row => row.PilotId == pilot)
            .AsEnumerable()
            .Where(row =>
                row.CorporationId is null
                && row.Bid
                && row.StationId == station
                && row.Status == PersonalOrderStatus.Open
                && row.Expires > now
                && missing.Any(part => part.TypeId == row.TypeId)
            )
            .ToArray();

    private void CancelCareerBids(EconomyTransaction world, CatalogDbContext catalog, SavedPersonalOrder[] bids)
    {
        foreach (var bid in bids)
        {
            market.CancelPersonalSupplyBid(world, catalog, bid);
        }
    }

    private bool FinishCareerManufacturing(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        ReplacementPart[] missing,
        long now
    )
    {
        var job = world
            .Filter<IndustryJob>(job => job.PilotId == pilot.PilotId)
            .FirstOrDefault(job =>
                job.Status == IndustryJobStatus.Installed
                && job.StationId == pilot.StationId
                && job.OutputCorporationId == null
                && missing.Any(part => part.TypeId == job.ProductTypeId)
            );
        if (job is null)
        {
            return false;
        }
        if (job.EndTime <= now && job.SuspendedAt is null)
        {
            industry.Finish(world, catalog, pilot.PilotId, job.Id, pilot.SystemId, cancel: false);
        }
        return true;
    }

    private bool PlaceCareerBid(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        ReplacementPart[] missing,
        long balance,
        long now
    )
    {
        long id = pilot.PilotId;
        int station = pilot.StationId;
        var part = missing[0];
        if (
            balance <= 0
            || world
                .Filter<SavedPersonalOrder>(order => order.PilotId == id)
                .Any(order => order.Bid && order.TypeId == part.TypeId && order.Status == PersonalOrderStatus.Open && order.Expires > now)
            || !PilotMarketStore.HasSupplyListingSlot(world, catalog, id)
        )
        {
            return false;
        }
        long price = ReferencePrice(catalog, part.TypeId);
        if (
            price <= 0
            || (decimal)price * part.Quantity > balance
            || PilotMarketStore.SupplyBrokerFee(world, catalog, id, station, price, part.Quantity) > balance - price * part.Quantity
        )
        {
            return false;
        }
        market.ExecuteBuy(world, catalog, id, station, [new(part.TypeId, price, part.Quantity)], CapsuleerReplacementRules.OrderDays);
        return true;
    }
}
