using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Exploration;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    // Standard exploration equipment from the build 3662168 catalog.
    internal const int ExplorerHull = 605;
    internal const int ExplorerLauncher = 17938;
    internal const int ExplorerProbe = 30013;
    internal const int DataAnalyzer = 22175;
    internal const int RelicAnalyzer = 22177;

    private static readonly CatalogDefinition<ReplacementKit?> s_explorationKit = new(
        "bot-exploration-kit",
        (catalog, _) =>
        {
            const int high = (int)InventoryFlag.HighSlot0;
            const int mid = (int)InventoryFlag.MediumSlot0;
            FittingSlot[] slots = [new(high, ExplorerLauncher), new(mid, DataAnalyzer), new(mid + 1, RelicAnalyzer)];
            var magazine = Magazine(catalog, high, ExplorerLauncher, ExplorerProbe);
            if (
                magazine is null
                || magazine.Quantity < ProbeScanRules.MaximumProbes
                || !ProbeCatalog.Launcher(catalog, ExplorerLauncher, ProbeScanRules.LauncherGroup)
                || !HackingCatalog.Supported(catalog, DataAnalyzer, HackingRules.AnalyzerGroup)
                || !HackingCatalog.Supported(catalog, RelicAnalyzer, HackingRules.AnalyzerGroup)
            )
            {
                return null;
            }
            ReplacementKit kit = new(
                ExplorerHull,
                slots,
                slots.Select(slot => slot.FlagId).ToHashSet(),
                [],
                [magazine with { Quantity = ProbeScanRules.MaximumProbes }]
            );
            return SupportedKit(catalog, kit, kit.Parts()) && WithinBaseFittingResources(catalog, ExplorerHull, slots) ? kit : null;
        }
    );

    internal static ReplacementKit? ExplorerKit(CatalogDbContext catalog) => catalog.Definition(s_explorationKit, 0);

    internal CareerShipTurn BoardExplorer(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        ReplacementKit kit,
        long now
    )
    {
        var parts = kit.Parts();
        if (!CanReplaceKit(world, catalog, pilot.PilotId, CapsuleerCareer.Hauling, kit, parts))
        {
            return default;
        }
        var missing = MissingParts(parts, Hangar(world, pilot.PilotId, pilot.StationId).ToLookup(item => item.TypeId));
        if (!BuyExplorationParts(world, catalog, pilot, missing))
        {
            return default;
        }
        BoardKit(world, catalog, pilot, kit, now);
        return new(Boarded: true, MarketChanged: missing.Length > 0);
    }

    internal (bool Loaded, bool MarketChanged) LoadExplorationProbes(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        long launcher,
        long now
    )
    {
        var magazine = PilotAmmunitionStore.Contents(world, launcher);
        int loaded = magazine?.TypeId == ExplorerProbe ? magazine.Quantity : 0;
        if (loaded >= ProbeScanRules.MaximumProbes)
        {
            return (true, false);
        }
        var cargo = world
            .Filter<OwnedItem>(item => item.LocationId == pilot.ShipId)
            .Where(item => item.OwnerId == pilot.PilotId && item.FlagId == (int)InventoryFlag.Cargo && item.TypeId == ExplorerProbe)
            .ToArray();
        int fromCargo = cargo.Sum(item => item.Quantity);
        int need = Math.Max(0, ProbeScanRules.MaximumProbes - loaded - fromCargo);
        var stock = Hangar(world, pilot.PilotId, pilot.StationId);
        var missing = MissingParts([new(ExplorerProbe, need)], stock.ToLookup(item => item.TypeId));
        if (!BuyExplorationParts(world, catalog, pilot, missing))
        {
            return (false, false);
        }
        if (need > 0)
        {
            if (cargo.Length > 0)
            {
                PilotInventoryStore.Move(
                    world,
                    catalog,
                    pilot.PilotId,
                    pilot.StationId,
                    new(cargo.Select(item => item.Id).ToArray(), pilot.ShipId, (int)InventoryFlag.Hangar, null),
                    now
                );
            }
            var reserve = Hangar(world, pilot.PilotId, pilot.StationId)
                .Where(item => item.TypeId == ExplorerProbe && !item.Singleton)
                .Select(item => item.Id)
                .ToArray();
            PilotAmmunitionStore.Load(world, catalog, pilot.PilotId, pilot.ShipId, [launcher], reserve, pilot.StationId, now);
        }
        else
        {
            PilotAmmunitionStore.Load(
                world,
                catalog,
                pilot.PilotId,
                pilot.ShipId,
                [launcher],
                cargo.Select(item => item.Id).ToArray(),
                pilot.ShipId,
                now
            );
        }
        return (PilotAmmunitionStore.Contents(world, launcher)?.Quantity >= ProbeScanRules.MaximumProbes, missing.Length > 0);
    }

    private bool BuyExplorationParts(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, ReplacementPart[] missing)
    {
        if (missing.Length == 0)
        {
            return true;
        }
        if (!StationCapabilities.Has(catalog, pilot.OperationId, StationService.Market))
        {
            return false;
        }
        long budget = WalletBalance(world, pilot.PilotId) / RattingRules.BudgetDivisor;
        if (budget <= 0)
        {
            return false;
        }
        var asks = market
            .SupplyOrders(world, catalog, pilot.StationId, missing.Select(part => part.TypeId).ToArray(), pilot.PilotId)
            .ToLookup(order => order.TypeId);
        var purchases = missing.Select(part => QuoteInput(asks[part.TypeId], part.TypeId, part.Quantity)).ToArray();
        var quoted = purchases.OfType<MarketPurchase>().ToArray();
        if (
            purchases.Where((purchase, index) => purchase?.Quantity != missing[index].Quantity).Any()
            || quoted.Sum(purchase => (decimal)purchase.LimitPriceCents * purchase.Quantity) > budget
        )
        {
            return false;
        }
        market.ExecuteBuy(world, catalog, pilot.PilotId, pilot.StationId, quoted, 0);
        return true;
    }
}
