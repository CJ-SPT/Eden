using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Economy;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Industry;
using Eden.World.Market;
using Eden.World.Mining;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Economy;

[Injectable]
internal sealed class EconomicStore(WorldDatabase database) : IEconomicPrices
{
    private sealed record BeltLocation(int BeltId, int SystemId);

    internal static void CheckCatalog(CatalogDbContext catalog, EconomicConfiguration settings)
    {
        if (
            settings.AdjustedPrices.Any(p => !catalog.Definitions.Items.ContainsKey(p.TypeId))
            || settings.ManufacturingIndices.Any(i => !catalog.Definitions.Systems.ContainsKey(i.SystemId))
        )
        {
            throw new InvalidDataException("Economic configuration references an unknown type or solar system.");
        }
    }

    public IReadOnlyList<AdjustedPrice> Prices()
    {
        using WorldDbContext world = new(database.Path);
        return Enabled(world)
            ? world
                .Set<SavedAdjustedPrice>()
                .OrderBy(p => p.TypeId)
                .AsEnumerable()
                .Select(p => new AdjustedPrice(p.TypeId, p.PriceIsk))
                .ToArray()
            : [];
    }

    internal static bool Enabled(WorldDbContext world) => Policy(world).ChargeServiceCosts;

    internal static SavedEconomicPolicy Policy(WorldDbContext world) => world.Set<SavedEconomicPolicy>().Single();

    internal static decimal Index(WorldDbContext world, int system) =>
        world.Set<SavedManufacturingIndex>().Find(system)?.Index ?? Policy(world).DefaultManufacturingIndex;

    internal static Dictionary<int, decimal> Prices(WorldDbContext world) =>
        world.Set<SavedAdjustedPrice>().AsNoTracking().ToDictionary(p => p.TypeId, p => p.PriceIsk);

    internal static void Initialize(WorldDbContext world, CatalogDbContext catalog, bool newEconomy, EconomicConfiguration? configuration)
    {
        if (!world.Set<SavedEconomicPolicy>().Any())
        {
            var settings = configuration ?? new EconomicConfiguration { ChargeServiceCosts = newEconomy };
            EconomicRules.Validate(settings);
            world.Add(
                new SavedEconomicPolicy
                {
                    ChargeServiceCosts = settings.ChargeServiceCosts,
                    RenewBelts = settings.RenewBelts,
                    RenewalHourUtc = settings.RenewalHourUtc,
                    DefaultManufacturingIndex = settings.DefaultManufacturingIndex,
                }
            );
            if (settings.ChargeServiceCosts)
            {
                SeedPrices(world, catalog, settings);
            }
            world.SaveChanges();
        }
    }

    internal static void ReconcileBelts(WorldDbContext world, long now)
    {
        var policy = Policy(world);
        var known = world.Set<SavedBeltRenewal>().Select(b => b.BeltId).ToHashSet();
        var belts = world.Set<WorldAsteroid>().Select(a => new BeltLocation(a.BeltId, a.SystemId)).Distinct().ToArray();
        foreach (var belt in belts.Where(b => !known.Contains(b.BeltId)))
        {
            world.Add(
                new SavedBeltRenewal
                {
                    BeltId = belt.BeltId,
                    SystemId = belt.SystemId,
                    NextRenewal = ResourceRenewalRules.Next(now, policy.RenewalHourUtc),
                }
            );
        }
        world.SaveChanges();
    }

    internal static void SeedPrices(WorldDbContext world, CatalogDbContext catalog, EconomicConfiguration settings)
    {
        var prices = IndustryReferencePrices(catalog);
        foreach (var price in settings.AdjustedPrices)
        {
            if (!catalog.Definitions.Items.ContainsKey(price.TypeId))
            {
                throw new InvalidDataException($"Unknown adjusted-price type {price.TypeId}.");
            }
            prices[price.TypeId] = price.PriceIsk;
        }
        world.AddRange(prices.Select(p => new SavedAdjustedPrice { TypeId = p.Key, PriceIsk = p.Value }));
        foreach (var index in settings.ManufacturingIndices)
        {
            if (!catalog.Definitions.Systems.ContainsKey(index.SystemId))
            {
                throw new InvalidDataException($"Unknown manufacturing system {index.SystemId}.");
            }
            world.Add(new SavedManufacturingIndex { SystemId = index.SystemId, Index = index.Index });
        }
    }

    private static Dictionary<int, decimal> IndustryReferencePrices(CatalogDbContext catalog)
    {
        var types = catalog
            .Definitions.Items.Values.Where(t => t.CategoryId == (int)InventoryCategory.Material)
            .Select(t => (long)t.Id)
            .Concat(ManufacturingCatalog.Recipes(catalog).Values.SelectMany(r => r.Materials.Select(m => m.TypeId!.Value)))
            .Concat(ReactionCatalog.Recipes(catalog).Keys.Select(id => (long)id))
            .Distinct();
        Dictionary<int, decimal> prices = [];
        foreach (var (id, definition) in catalog.Definitions.FindMany(CatalogData.Client.Types, types))
        {
            decimal? price =
                definition.BasePrice is > 0 and var value && double.IsFinite(value)
                    ? (decimal)value
                    : MarketSnapshotCatalog.Quotes.GetValueOrDefault((int)id)?.SellCents / (decimal?)MarketDefinitions.CentsPerIsk;
            if (price > 0)
            {
                prices[(int)id] = price.Value;
            }
        }
        return prices;
    }

    internal static void ReconcileIndustryPrices(WorldDbContext world, CatalogDbContext catalog)
    {
        if (!Enabled(world))
        {
            return;
        }
        var existing = world.Set<SavedAdjustedPrice>().Select(p => p.TypeId).ToHashSet();
        foreach (var (id, price) in IndustryReferencePrices(catalog).Where(p => !existing.Contains(p.Key)))
        {
            world.Add(new SavedAdjustedPrice { TypeId = id, PriceIsk = price });
        }
        world.SaveChanges();
    }

    internal static ManufacturingCharge Manufacturing(
        WorldDbContext world,
        long pilotId,
        int system,
        IReadOnlyDictionary<int, int> materials,
        double security = 0
    )
    {
        if (!Enabled(world))
        {
            return new(0, 0, 0);
        }
        long account = world.Pilots.Where(p => p.Id == pilotId).Select(p => p.AccountId).Single();
        bool alpha = world.Set<Account>().Find(account)!.CloneGrade == AccountCloneGrade.Alpha;
        return EconomicRules.ManufacturingFromClientValue(
            EconomicRules.ClientValue(materials, Prices(world)),
            (double)Index(world, system),
            alpha,
            security
        );
    }

    internal static ManufacturingCharge IndustryCharge(
        WorldDbContext world,
        long pilotId,
        int system,
        double value,
        IndustryActivity activity,
        double security,
        double blueprintCostMultiplier = 1
    )
    {
        if (!Enabled(world))
        {
            return new(0, 0, 0);
        }
        long account = world.Pilots.Where(p => p.Id == pilotId).Select(p => p.AccountId).Single();
        bool alpha = world.Set<Account>().Find(account)!.CloneGrade == AccountCloneGrade.Alpha;
        return EconomicRules.ManufacturingFromClientValue(
            value,
            (double)Index(world, system) * blueprintCostMultiplier,
            alpha,
            security,
            activity is IndustryActivity.ResearchMaterial or IndustryActivity.ResearchTime
        );
    }

    internal static long Pay(WorldDbContext world, long pilot, long counterparty, long context, WalletEntryType kind, long cents, long now)
    {
        var wallet = world.Set<PilotWallet>().Find(pilot) ?? world.Add(new PilotWallet { PilotId = pilot }).Entity;
        if (cents < 0 || wallet.Cents < cents)
        {
            throw new InvalidDataException("You do not have enough ISK to pay this service charge.");
        }
        wallet.Cents = checked(wallet.Cents - cents);
        if (cents > 0)
        {
            world.Add(
                new SavedServicePayment
                {
                    PilotId = pilot,
                    CounterpartyId = counterparty,
                    ContextId = context,
                    Kind = kind,
                    AmountCents = -cents,
                    BalanceCents = wallet.Cents,
                    Time = now,
                }
            );
        }
        return wallet.Cents;
    }
}
