using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Economy;
using Eden.World.Fittings;
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Planetary;
using Eden.World.Reprocessing;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Rules = Eden.Gameplay.Capsuleers.CapsuleerAmmunitionSupplyRules;

namespace Eden.World.Capsuleers;

[Injectable]
internal sealed partial class CapsuleerIndustry(
    PilotIndustryStore industry,
    PilotReprocessingStore refinery,
    PilotMarketStore market,
    PilotFittingStore fitting,
    PilotPlanetaryIndustryStore planetary
)
{
    private static readonly CatalogDefinition<bool> s_ironCompatible = new(
        "capsuleer-iron-ammunition-compatibility",
        (catalog, module) =>
            catalog.Definitions.Items.TryGetValue(Rules.ChargeType, out var charge)
            && AmmunitionDefinitions.CompatibleTurretCharge(
                catalog.FittingAttributes(module),
                charge.GroupId,
                catalog.FittingAttributes(charge.Id)
            )
    );

    internal static bool CanUseIron(CatalogDbContext catalog, int module) => catalog.Definition(s_ironCompatible, module);

    internal static SavedCorporation? Employer(WorldAction world, long pilotId, int corporationId) =>
        world is EconomyTransaction
        && world.Settings.Capsuleers.Enabled
        && corporationId >= CorporationRules.FirstCorporationId
        && world.Set<SavedCapsuleerAgent>().Find(pilotId) is not null
        && world.Set<SavedCapsuleerSuspension>().Find(pilotId) is null
        && world.Pilots.Find(pilotId)?.CorporationId == corporationId
        && world.Set<SavedCorporation>().Find(corporationId) is { ClosedAt: null } corporation
        && CorporationMembershipStore.BotLed(world, corporation)
        && CorporationRoleStore.Has(world, corporation.CeoId, corporation, CorporationRole.Director)
            ? corporation
            : null;

    internal static OwnedItem[] DepotStock(WorldAction world, int corporation, long station) =>
        DepotStacks(world, corporation, station).Where(item => item.TypeId == Rules.ChargeType).OrderBy(item => item.Id).ToArray();

    private static CorporationSupplyRun? ActiveSupplyRun(LiveWorld live, Pilot pilot) =>
        live.HaulerCheckpoints.GetValueOrDefault(pilot.Id)?.Work is { CorporationSupply: { } run } work
        && work.ShipId == pilot.ActiveShipId
        && run.CorporationId == pilot.CorporationId
            ? run
            : null;

    private static IEnumerable<OwnedItem> DepotStacks(WorldAction world, int corporation, long station) =>
        world
            .Filter<OwnedItem>(item => item.OwnerId == corporation)
            .Where(item => item.LocationId == station && item.FlagId == CorporationStorageRules.MarketDeliveriesFlag && !item.Singleton)
            .AsEnumerable();

    private static OwnedItem[] Hangar(WorldAction world, long pilot, int station) =>
        world
            .Filter<OwnedItem>(item => item.OwnerId == pilot)
            .Where(item => item.LocationId == station && item.FlagId == (int)InventoryFlag.Hangar)
            .OrderBy(item => item.Id)
            .ToArray();

    // The operator contributes already-owned ISK only for the immediate shortfall. The corporate
    // and personal ledgers commit with the purchase or expense under the same resident owner.
    private static bool Fund(WorldAction world, SavedCorporation corporation, long pilot, long needed, long now)
    {
        long gap = Math.Max(0, needed - corporation.BalanceCents);
        if (gap == 0)
        {
            return true;
        }
        if (world.Set<PilotWallet>().Find(pilot) is not { } wallet || wallet.Cents / RattingRules.BudgetDivisor < gap)
        {
            return false;
        }
        wallet.Cents -= gap;
        CorporationWalletStore.AddEntry(
            world,
            pilot,
            CorporationWalletRules.MasterKey,
            now,
            corporation.Id,
            -gap,
            wallet.Cents,
            WalletEntryType.PlayerDonation,
            corporation.Id,
            "Capsuleer supply contribution"
        );
        CorporationWalletStore.Pay(world, corporation.Id, pilot, pilot, WalletEntryType.PlayerDonation, gap, now);
        return true;
    }

    private static readonly CatalogDefinition<long> s_referencePrice = new(
        "capsuleer-industry-reference-price",
        (catalog, type) =>
            MarketSnapshotCatalog.Quotes.GetValueOrDefault(type)?.SellCents
            ?? (
                catalog.Read(CatalogData.Client.Types, (long)type)?.BasePrice is > 0 and double price
                    ? PersonalMarketRules.Round((decimal)price * MarketDefinitions.CentsPerIsk)
                    : 0
            )
    );

    internal static long ReferencePrice(CatalogDbContext catalog, int type) => catalog.Definition(s_referencePrice, type);

    internal bool Produce(EconomyTransaction world, CatalogDbContext catalog, DockedPilot operatorPilot, long now) =>
        Produce(world, catalog, operatorPilot, now, out _);

    internal bool Produce(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot operatorPilot,
        long now,
        out FactorySupplyRequest? supply
    )
    {
        supply = null;
        long pilot = operatorPilot.PilotId;
        int location = operatorPilot.StationId;
        if (
            operatorPilot.Position is not null
            || operatorPilot.InStructure
            || !StationCapabilities.Has(catalog, operatorPilot.OperationId, StationService.Market)
            || !StationCapabilities.Has(catalog, operatorPilot.OperationId, StationService.Manufacturing)
        )
        {
            return false;
        }

        var running = world
            .Filter<IndustryJob>(job => job.PilotId == pilot)
            .Where(job =>
                job.Status == IndustryJobStatus.Installed
                && (
                    job.Activity == IndustryActivity.Manufacturing
                    || job.OutputCorporationId.HasValue
                        && (job.Activity == IndustryActivity.Copying || job.Activity == IndustryActivity.Invention)
                )
            )
            .ToArray();
        foreach (
            var job in running.Where(job =>
                job.OutputCorporationId.HasValue && job.StationId == location && job.EndTime <= now && job.SuspendedAt == null
            )
        )
        {
            industry.Finish(world, catalog, pilot, job.Id, operatorPilot.SystemId, cancel: false);
        }
        if (Employer(world, pilot, operatorPilot.CorpId) is not { } corporation)
        {
            return false;
        }
        var jobs = world.Filter<IndustryJob>(job => job.OutputCorporationId == corporation.Id);
        var listed = world
            .Filter<SavedPersonalOrder>(order => order.CorporationId == corporation.Id)
            .Where(order => order.StationId == location && !order.Bid && order.Status == PersonalOrderStatus.Open && order.Expires > now)
            .Select(order => order.TypeId)
            .ToHashSet();
        var depot = DepotStacks(world, corporation.Id, location).OrderBy(item => item.Id).ToArray();
        bool pilotBusy = running.Any(job => job.Status == IndustryJobStatus.Installed);
        if ((pilotBusy && depot.Length == 0) || !PilotMarketStore.HasSupplyListingSlot(world, catalog, pilot))
        {
            return false;
        }
        var bids = LocalProductionBids(world, pilot, corporation.Id, location, now);
        foreach (var stock in depot.Where(item => !listed.Contains(item.TypeId)))
        {
            var demand = bids[stock.TypeId];
            if (stock.TypeId == CapsuleerMinerTwoRules.Module)
            {
                demand = demand.Where(order => order.PriceCents >= ReferencePrice(catalog, stock.TypeId));
            }
            var bid = demand.FirstOrDefault(order => order.Remaining >= stock.Quantity) ?? demand.FirstOrDefault();
            if (
                jobs.Any(job =>
                    job.StationId == location
                    && job.ProductTypeId == stock.TypeId
                    && job.OutputFlagId == CorporationStorageRules.MarketDeliveriesFlag
                    && job.Status == IndustryJobStatus.Delivered
                ) && ListStock(world, catalog, corporation, pilot, stock, bid, now)
            )
            {
                return true;
            }
        }
        if (pilotBusy)
        {
            return false;
        }

        var hangar = Hangar(world, pilot, location);
        listed.UnionWith(depot.Select(item => item.TypeId));
        var installed = jobs.Where(job => job.Status == IndustryJobStatus.Installed).ToArray();
        // Each station's pending output covers the same demand while other pilots work on different orders.
        listed.UnionWith(installed.Where(job => job.StationId == location).Select(job => job.ProductTypeId));
        FactorySupplyRequest? minerTwoSupply = null;
        if (installed.Length == 0 && !listed.Contains(CapsuleerMinerTwoRules.Module))
        {
            var minerTwo = ProduceMinerTwo(
                world,
                catalog,
                operatorPilot,
                corporation,
                hangar,
                bids[CapsuleerMinerTwoRules.Module],
                now,
                out minerTwoSupply
            );
            if (minerTwo.HasValue)
            {
                supply = minerTwoSupply;
                return minerTwo.Value;
            }
        }
        if (TryProduceForDemand(world, catalog, operatorPilot, corporation, hangar, bids, listed, now, out supply) is bool changed)
        {
            supply ??= minerTwoSupply;
            return changed;
        }
        supply ??= minerTwoSupply;
        if (
            installed.Length > 0
            || listed.Contains(Rules.ChargeType)
            || CapsuleerReplacementRules.MerlinParts.Any(part => bids.Contains(part.TypeId))
        )
        {
            return false;
        }
        bool boughtIronInputs = ProduceIron(world, catalog, operatorPilot, corporation, hangar, now, out var ironSupply);
        supply ??= ironSupply;
        return boughtIronInputs;
    }

    private bool ProduceIron(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot operatorPilot,
        SavedCorporation corporation,
        OwnedItem[] hangar,
        long now,
        out FactorySupplyRequest? supply
    )
    {
        supply = null;
        if (
            ManufacturingCatalog.Recipes(catalog).GetValueOrDefault(Rules.BlueprintType) is not { } recipe
            || recipe.ProductTypeId != Rules.ChargeType
        )
        {
            return false;
        }
        long pilot = operatorPilot.PilotId;
        int location = operatorPilot.StationId;
        var blueprint = hangar.FirstOrDefault(item => item.TypeId == Rules.BlueprintType && item.BlueprintRuns < 0);
        var materials = IndustryMaterials.Calculate(recipe.Materials, Rules.ProductionRuns, blueprint?.MaterialEfficiency ?? 0);
        var missing = materials
            .Where(row => hangar.Where(item => item.TypeId == row.Key).Sum(item => item.Quantity) < row.Value)
            .ToDictionary(
                row => row.Key,
                row => checked(row.Value - (int)hangar.Where(item => item.TypeId == row.Key).Sum(item => item.Quantity))
            );
        if (missing.Count > 0)
        {
            if (
                hangar.Any(item =>
                    catalog.Definitions.Items.GetValueOrDefault(item.TypeId)?.CategoryId == (int)InventoryCategory.Asteroid
                    && refinery.RefineForProduction(world, catalog, pilot, location, item, missing)
                )
            )
            {
                return false;
            }
            var material = missing.OrderBy(row => row.Key).First();
            if (BuyInput(world, catalog, pilot, location, material.Key, material.Value))
            {
                return true;
            }
            if (blueprint is not null && ReferencePrice(catalog, material.Key) is > 0 and long reference && corporation.BalanceCents > 0)
            {
                long maximum = Math.Min(reference * 2, corporation.BalanceCents / material.Value);
                if (maximum > 0)
                {
                    int required = materials[material.Key];
                    supply = new(corporation.Id, location, null, [new(material.Key, required, required, material.Value, maximum)]);
                }
            }
            return false;
        }
        if (blueprint is null)
        {
            return BuyInput(world, catalog, pilot, location, Rules.BlueprintType, 1);
        }
        foreach (var skill in recipe.Skills)
        {
            if (!Qualify(world, catalog, pilot, skill))
            {
                return false;
            }
        }

        double security = ManufacturingCatalog.Security(catalog, operatorPilot.SystemId);
        var charge = EconomicStore.Manufacturing(
            world,
            pilot,
            operatorPilot.SystemId,
            IndustryMaterials.Calculate(recipe.Materials, Rules.ProductionRuns),
            security
        );
        if (Fund(world, corporation, pilot, charge.TotalCents, now))
        {
            industry.InstallManufacturing(
                world,
                catalog,
                pilot,
                new(blueprint.Id, blueprint.TypeId, location, Rules.ProductionRuns, Rules.ChargeType, 0, materials),
                corporation.Id
            );
        }
        return false;
    }

    private bool ListStock(
        EconomyTransaction world,
        CatalogDbContext catalog,
        SavedCorporation corporation,
        long pilot,
        OwnedItem stock,
        SavedPersonalOrder? bid,
        long now
    )
    {
        int quantity = stock.Quantity;
        long reference = bid?.PriceCents ?? ReferencePrice(catalog, stock.TypeId);
        long minimum = bid is not null ? 1 : (PersonalMarketRules.MinimumBrokerFeeCents * 2 + quantity - 1) / quantity;
        long price = Math.Clamp(reference, minimum, (long)(MarketDefinitions.MaximumOrderPrice * MarketDefinitions.CentsPerIsk));
        quantity = (int)Math.Min(quantity, long.MaxValue / price);
        int location = checked((int)stock.LocationId);
        long fee = PilotMarketStore.SupplyBrokerFee(world, catalog, pilot, location, price, quantity);
        if (!Fund(world, corporation, pilot, fee, now))
        {
            return false;
        }
        market.ListCorporationSupply(world, catalog, pilot, corporation, stock, quantity, price, Rules.ListingDays);
        return true;
    }

    private static bool Qualify(WorldAction world, CatalogDbContext catalog, long pilot, ManufacturingRequiredSkill skill)
    {
        double rank = catalog.FittingAttributes(skill.TypeId).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant);
        if (rank <= 0)
        {
            return false;
        }
        var trained =
            world.Set<PilotSkill>().Find(pilot, skill.TypeId)
            ?? world.Add(new PilotSkill { PilotId = pilot, TypeId = skill.TypeId }).Entity;
        trained.Points = Math.Max(trained.Points, TrainedSkill.PointsForLevel(rank, skill.Level));
        return true;
    }

    private bool BuyInput(EconomyTransaction world, CatalogDbContext catalog, long pilot, int station, int type, int quantity)
    {
        long money = world.Set<PilotWallet>().Find(pilot)?.Cents ?? 0;
        long reference = ReferencePrice(catalog, type);
        var listing = market
            .SupplyOrders(world, catalog, station, [type])
            .FirstOrDefault(order => order.OwnerId != pilot && order.PriceCents <= reference * 2 && order.MinimumVolume <= quantity);
        if (listing is null)
        {
            return false;
        }
        int units = (int)Math.Min(Math.Min(quantity, listing.Remaining), money / listing.PriceCents);
        if (units < listing.MinimumVolume)
        {
            return false;
        }
        market.ExecuteBuy(world, catalog, pilot, station, [new(type, listing.PriceCents, units)], 0);
        return true;
    }
}
