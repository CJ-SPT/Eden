using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
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
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Reprocessing;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Rules = Eden.Gameplay.Capsuleers.CapsuleerAmmunitionSupplyRules;

namespace Eden.World.Capsuleers;

[Injectable]
internal sealed partial class CapsuleerAmmunitionSupply(
    PilotIndustryStore industry,
    PilotReprocessingStore refinery,
    PilotMarketStore market
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
        world
            .Filter<OwnedItem>(item => item.OwnerId == corporation)
            .Where(item =>
                item.LocationId == station
                && item.FlagId == CorporationStorageRules.MarketDeliveriesFlag
                && item.TypeId == Rules.ChargeType
                && !item.Singleton
            )
            .OrderBy(item => item.Id)
            .ToArray();

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

    private static long ReferencePrice(CatalogDbContext catalog, int type) =>
        MarketSnapshotCatalog.Quotes.GetValueOrDefault(type)?.SellCents
        ?? (
            catalog.Read(CatalogData.Client.Types, (long)type)?.BasePrice is > 0 and double price
                ? PersonalMarketRules.Round((decimal)price * MarketDefinitions.CentsPerIsk)
                : 0
        );

    internal bool Produce(EconomyTransaction world, CatalogDbContext catalog, DockedPilot operatorPilot, long now)
    {
        long pilot = operatorPilot.PilotId;
        int location = operatorPilot.StationId;
        if (
            operatorPilot.Position is not null
            || operatorPilot.InStructure
            || !StationCapabilities.Has(catalog, operatorPilot.OperationId, StationService.Market)
            || !StationCapabilities.Has(catalog, operatorPilot.OperationId, StationService.Manufacturing)
            || ManufacturingCatalog.Recipes(catalog).GetValueOrDefault(Rules.BlueprintType) is not { } recipe
            || recipe.ProductTypeId != Rules.ChargeType
        )
        {
            return false;
        }

        var running = world
            .Filter<IndustryJob>(job => job.PilotId == pilot)
            .Where(job => job.Status == IndustryJobStatus.Installed && job.Activity == IndustryActivity.Manufacturing)
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
        var listing = world
            .Filter<SavedPersonalOrder>(order => order.CorporationId == corporation.Id)
            .Any(order =>
                !order.Bid && order.TypeId == Rules.ChargeType && order.Status == PersonalOrderStatus.Open && order.Expires > now
            );
        if (listing)
        {
            return false;
        }

        var stock = DepotStock(world, corporation.Id, location).FirstOrDefault();
        if (stock is not null)
        {
            if (
                !world
                    .Filter<IndustryJob>(job => job.OutputCorporationId == corporation.Id)
                    .Any(job =>
                        job.StationId == location && job.ProductTypeId == Rules.ChargeType && job.Status == IndustryJobStatus.Delivered
                    )
            )
            {
                return false;
            }
            long price = Math.Max(
                ReferencePrice(catalog, Rules.ChargeType),
                (PersonalMarketRules.MinimumBrokerFeeCents * 2 + stock.Quantity - 1) / stock.Quantity
            );
            int quantity = checked((int)stock.Quantity);
            if (Fund(world, corporation, pilot, PilotMarketStore.SupplyBrokerFee(world, catalog, pilot, location, price, quantity), now))
            {
                market.ListCorporationSupply(world, catalog, pilot, corporation, stock, price, Rules.ListingDays);
                return true;
            }
            return false;
        }
        if (
            running.Any(job => job.Status == IndustryJobStatus.Installed)
            || world
                .Filter<IndustryJob>(job => job.OutputCorporationId == corporation.Id)
                .Any(job => job.Status == IndustryJobStatus.Installed)
        )
        {
            return false;
        }

        var hangar = Hangar(world, pilot, location);
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
            return BuyInput(world, catalog, pilot, location, material.Key, material.Value);
        }
        if (blueprint is null)
        {
            return BuyInput(world, catalog, pilot, location, Rules.BlueprintType, 1);
        }
        foreach (var skill in recipe.Skills)
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
