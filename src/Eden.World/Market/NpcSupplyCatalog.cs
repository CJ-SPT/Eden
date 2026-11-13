using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Eden.Data.Catalog;
using Eden.Data.World.Market;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

internal sealed partial class PilotMarketStore
{
    private sealed record InstitutionalOffer(int TypeId, bool Bid, long PriceCents);

    private sealed record InstitutionalType(int TypeId, bool Bid);

    private static readonly ConditionalWeakTable<CatalogProvider, Lazy<int[]>> BlueprintSupplies = new();

    private static readonly ConditionalWeakTable<
        CatalogProvider,
        ConcurrentDictionary<int, Lazy<IReadOnlyList<InstitutionalOffer>>>
    > InstitutionalCatalogs = new();

    internal void PrepareInstitutional(CatalogDbContext catalog, int stationId) =>
        _ = InstitutionalOffers(catalog, catalog.RequireStation(stationId));

    private IReadOnlyList<InstitutionalOffer> InstitutionalOffers(CatalogDbContext catalog, CatalogStationDefinition station) =>
        InstitutionalCatalogs
            .GetValue(catalog.Definitions, _ => new())
            .GetOrAdd(station.Id, _ => new(() => LoadInstitutionalOffers(catalog, station)))
            .Value;

    private IReadOnlyList<InstitutionalOffer> LoadInstitutionalOffers(CatalogDbContext catalog, CatalogStationDefinition station)
    {
        var corporation = catalog.Read(CatalogData.Client.Npccorporations, (long)station.OwnerId);
        var trades = corporation?.CorporationTrades ?? [];
        bool hub = RegionalMarketCatalog.Read(catalog).Stations.Any(s => s.Station.Id == station.Id && s.Role == RegionalMarketRole.Hub);
        // Hubs share the catalog's NPC blueprint sell assortment, regardless of
        // the local corporation. Other institutional goods retain their suppliers.
        int[] suppliedBlueprints = hub ? HubBlueprints(catalog) : [];
        var offered = trades
            .Where(t => t.Value != 0)
            .Select(t => new InstitutionalType(int.Parse(t.Key, CultureInfo.InvariantCulture), t.Value < 0))
            .Union(
                database
                    .PublishedSkills.Values.Where(skill => skill.UserCreated && !skill.Deleted && skill.Definition.Published)
                    .Select(skill => new InstitutionalType(
                        skill.Definition.TypeId,
                        trades.GetValueOrDefault(skill.Definition.TypeId.ToString(CultureInfo.InvariantCulture)) < 0
                    ))
            )
            .Union(suppliedBlueprints.Select(type => new InstitutionalType(type, false)))
            .OrderBy(offer => offer.TypeId)
            .ThenBy(offer => offer.Bid)
            .ToArray();
        var types = offered.Select(offer => offer.TypeId).Distinct().Select(catalog.FittingType).OfType<CatalogItemDefinition>().ToArray();
        var items = types.ToDictionary(type => type.Id);
        var definitions = catalog.ReadMany(CatalogData.Client.Types, types.Select(type => (long)type.Id));
        var blueprints = types.Where(type => type.CategoryId == (int)InventoryCategory.Blueprint).ToArray();
        var recipes = catalog.ReadMany(
            CatalogData.Client.Blueprints,
            blueprints.Select(type => type.Id.ToString(CultureInfo.InvariantCulture))
        );
        _ = catalog.Definitions.AttributeSets(
            blueprints
                .Select(type => type.Id)
                .Concat(
                    recipes
                        .Values.SelectMany(recipe => recipe.Activities?.Manufacturing?.Products ?? [])
                        .Where(product => product.TypeID is > 0 and <= int.MaxValue)
                        .Select(product => (int)product.TypeID!.Value)
                )
        );
        List<InstitutionalOffer> result = [];
        foreach (var offer in offered)
        {
            int typeId = offer.TypeId;
            if (!items.TryGetValue(typeId, out var type))
            {
                continue;
            }
            var definition = definitions.GetValueOrDefault(typeId);
            bool bid = offer.Bid;
            if (
                !RegionalMarketRules.CanUnlock((InventoryCategory)type.CategoryId, type.GroupId, true, true)
                && type.CategoryId != (int)InventoryCategory.Blueprint
            )
            {
                continue;
            }
            if (!bid && RegionalMarketRules.IsPlayerSuppliedIndustryInput(typeId, (InventoryCategory)type.CategoryId, type.GroupId))
            {
                continue;
            }
            // Invented/T2 blueprints never become NPC supply, including if an
            // inconsistent corporation offer is present in an imported catalog.
            if (
                type.CategoryId == (int)InventoryCategory.Blueprint
                && (
                    catalog.Definitions.Attributes(typeId).GetValueOrDefault(DogmaAttributeId.TechLevel) > 1
                    || catalog
                        .Definitions.ManufacturingRecipe(typeId)
                        ?.Products?.Any(p =>
                            p.TypeID is > 0 and <= int.MaxValue
                            && catalog.Definitions.Attributes((int)p.TypeID.Value).GetValueOrDefault(DogmaAttributeId.TechLevel) > 1
                        ) == true
                )
            )
            {
                continue;
            }
            long? price = MarketSnapshotCatalog.Quotes.TryGetValue(typeId, out var quote)
                ? bid
                    ? quote.BuyCents
                    : quote.SellCents
                : null;
            bool useCatalogPrice =
                database.PublishedSkills.TryGetValue(typeId, out var editedSkill) && editedSkill.PriceChanged
                || !bid && price is null && NpcSupplyRules.UsesIndustrialCatalogPrice(typeId);
            price = NpcSupplyRules.Price(
                new(
                    typeId,
                    (InventoryCategory)type.CategoryId,
                    definition?.Published == 1,
                    useCatalogPrice ? (decimal)(definition?.BasePrice ?? 0) : (decimal)(price ?? 0) / MarketDefinitions.CentsPerIsk,
                    definition?.MarketGroupID is > 0
                )
            );
            if (price is null)
            {
                continue;
            }
            result.Add(new(typeId, bid, price.Value));
        }
        return result.AsReadOnly();
    }

    private static int[] HubBlueprints(CatalogDbContext catalog) =>
        BlueprintSupplies
            .GetValue(
                catalog.Definitions,
                definitions =>
                    new(() =>
                    {
                        using CatalogDbContext reader = new(definitions.Identity.Path);
                        return reader
                            .Records(CatalogData.Client.Npccorporations)
                            .SelectMany(entry => entry.Value.CorporationTrades ?? [])
                            .Where(trade => trade.Value > 0)
                            .Select(trade => int.Parse(trade.Key, CultureInfo.InvariantCulture))
                            .Distinct()
                            .Where(type => reader.FittingType(type)?.CategoryId == (int)InventoryCategory.Blueprint)
                            .ToArray();
                    })
            )
            .Value;

    private void SeedInstitutional(WorldDbContext world, CatalogDbContext catalog, CatalogStationDefinition station)
    {
        int stock = world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.InstitutionalStock;
        NpcSupplyRules.ValidateStock(stock);
        var preparation = new InstitutionalPreparation(station.Id, catalog.Definitions.Identity.Generation, stock);
        var live = LiveWorld.Find(world.WorldPath);
        if (
            live?.InstitutionalSupply.GetValueOrDefault(station.Id) == preparation
            && !WorldConnections.HasChanges(world, "WorldMarketDefinition", "WorldEconomy")
            && !world
                .ChangeTracker.Entries<NpcMarketOrder>()
                .Any(entry =>
                    entry.State is EntityState.Added or EntityState.Deleted
                    || entry.Properties.Any(property =>
                        property.IsModified
                        && property.Metadata.Name
                            is not (nameof(NpcMarketOrder.Remaining) or nameof(NpcMarketOrder.Entered) or nameof(NpcMarketOrder.Issued))
                    )
                )
        )
        {
            Refresh(world, world.Set<NpcMarketOrder>().Where(o => o.StationId == station.Id));
            return;
        }
        if (WorldConnections.PreparedInstitutional(world, preparation))
        {
            Refresh(world, world.Set<NpcMarketOrder>().Where(o => o.StationId == station.Id));
            return;
        }
        using var preparing = WorldConnections.PreparingInstitutional(world, station.Id);
        var existing = world
            .Set<NpcMarketOrder>()
            .Where(o => o.StationId == station.Id && o.Kind == NpcMarketKind.Institutional)
            .ToDictionary(o => (o.TypeId, o.Bid));
        HashSet<(int TypeId, bool Bid)> eligible = [];
        foreach (var offer in InstitutionalOffers(catalog, station))
        {
            int typeId = offer.TypeId;
            bool bid = offer.Bid;
            eligible.Add((typeId, bid));
            if (existing.TryGetValue((typeId, bid), out var order))
            {
                order.PriceCents = offer.PriceCents;
                if (order.Issued == 0)
                {
                    order.Issued = Now;
                    order.Remaining = stock;
                    order.Entered = stock;
                }
                continue;
            }
            world.Add(
                new NpcMarketOrder
                {
                    Kind = NpcMarketKind.Institutional,
                    StationId = station.Id,
                    SystemId = station.SolarSystemId,
                    RegionId = station.RegionId,
                    OwnerId = station.OwnerId,
                    TypeId = typeId,
                    Bid = bid,
                    PriceCents = offer.PriceCents,
                    Remaining = stock,
                    Entered = stock,
                    Issued = Now,
                }
            );
        }
        foreach (var retired in existing.Where(pair => !eligible.Contains(pair.Key)).Select(pair => pair.Value))
        {
            retired.Remaining = 0;
            retired.Issued = 0;
        }
        world.SaveChanges();
        WorldConnections.RememberInstitutional(world, preparation);
        Refresh(world, world.Set<NpcMarketOrder>().Where(o => o.StationId == station.Id));
    }
}
