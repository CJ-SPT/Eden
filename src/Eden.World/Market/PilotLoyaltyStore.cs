using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.Catalog.Market;
using Eden.Data.World.Corporations;
using Eden.Data.World.Economy;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Redemption;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

[Injectable]
internal sealed class PilotLoyaltyStore(WorldDatabase database, TimeProvider clock) : IPilotLoyaltyStore
{
    private static readonly LoyaltyOfferSnapshot Snapshot = Load();
    private static readonly IReadOnlyDictionary<int, LoyaltyOfferDefinition> Definitions = Snapshot.Offers.ToDictionary(offer => offer.Id);

    private static LoyaltyOfferSnapshot Load()
    {
        using var stream =
            typeof(PilotLoyaltyStore).Assembly.GetManifestResourceStream("Eden.World.LoyaltyOffers.json")
            ?? throw new InvalidDataException("Missing loyalty store offers.");
        return JsonSerializer.Deserialize<LoyaltyOfferSnapshot>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Invalid loyalty store offers.");
    }

    private static CatalogStationDefinition Station(WorldDbContext world, CatalogDbContext catalog, long pilotId)
    {
        var pilot =
            world.Pilots.Include(p => p.ActiveShip).ThenInclude(ship => ship.Position).SingleOrDefault(p => p.Id == pilotId)
            ?? throw new InvalidDataException("Unknown loyalty store customer.");
        var station = catalog.Definitions.FindStation(pilot.ActiveShip.StationId);
        if (
            pilot.ActiveShip.Position is not null
            || station is null
            || !StationCapabilities.Has(catalog, station.OperationId, StationService.LoyaltyStore)
        )
        {
            throw new InvalidDataException("Use a loyalty point store at your current NPC station while docked.");
        }
        return station;
    }

    public void CheckStation(long pilotId, int stationId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (Station(world, catalog, pilotId).Id != stationId)
        {
            throw new InvalidDataException("Use your current station's loyalty store.");
        }
    }

    private static IReadOnlyList<LoyaltyOffer> Offers(CatalogDbContext catalog, int corporationId)
    {
        if (!catalog.Definitions.NpcCorporations.ContainsKey(corporationId))
        {
            throw new InvalidDataException("Choose a known NPC loyalty point issuer.");
        }
        return Snapshot
            .Stores.GetValueOrDefault(corporationId, [])
            .Select(id => Definitions[id])
            .Where(offer => Supported(catalog, offer))
            .Select(offer => new LoyaltyOffer(
                offer.Id,
                corporationId,
                offer.TypeId,
                offer.Quantity,
                offer.LpCost,
                offer.IskCents,
                offer.RequiredItems
            ))
            .ToArray();
    }

    private static bool Supported(CatalogDbContext catalog, LoyaltyOfferDefinition offer)
    {
        // The snapshot's blueprint quantity is the run count of one licensed copy.
        // Analysis Kredits and cosmetic licenses require their own authoritative ledgers.
        return offer.AnalysisKredits == 0
            && catalog.Definitions.Items.GetValueOrDefault(offer.TypeId) is { } type
            && (
                type.CategoryId == (int)InventoryCategory.Blueprint
                    ? offer.Quantity > 0 && ManufacturingCatalog.Recipes(catalog).ContainsKey(offer.TypeId)
                    : RedemptionRules.Deliverable((InventoryCategory)type.CategoryId)
            )
            && offer.RequiredItems.All(item => catalog.Definitions.Items.ContainsKey(item.TypeId));
    }

    public IReadOnlyList<LoyaltyOffer> Offers(long pilotId, int corporationId)
    {
        using WorldDbContext world = new(database.Path);
        if (!world.Pilots.Any(p => p.Id == pilotId))
        {
            throw new InvalidDataException("Unknown loyalty store customer.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return Offers(catalog, corporationId);
    }

    public LoyaltyPurchase Purchase(long pilotId, int corporationId, int offerId, int count, bool corporate = false)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var station = Station(world, catalog, pilotId);
        if (station.OwnerId != corporationId)
        {
            throw new InvalidDataException("Purchase from the corporation that owns your current station.");
        }
        var offer =
            Offers(catalog, corporationId).SingleOrDefault(offer => offer.Id == offerId)
            ?? throw new InvalidDataException("This offer is not available from this loyalty store.");
        var cost = LoyaltyStoreRules.Cost(offer, count);
        var owner = corporate ? CorporationRoleStore.Current(world, pilotId) : null;
        if (owner is not null && !CorporationRoleStore.Has(world, pilotId, owner, CorporationRole.Director))
        {
            throw new InvalidDataException("Corporation loyalty store purchases require CEO or Director access.");
        }
        int? key = owner is null ? null : CorporationWalletStore.SelectedKey(world, pilotId, owner);
        var loyalty = corporate ? null : world.Set<SavedLoyaltyBalance>().Find(pilotId, corporationId);
        var corporateLoyalty = owner is null ? null : world.Set<SavedCorporationLoyaltyBalance>().Find(owner.Id, corporationId);
        var wallet = corporate ? null : world.Set<PilotWallet>().Find(pilotId);
        long pointsBefore = corporate ? corporateLoyalty?.Points ?? 0 : loyalty?.Points ?? 0;
        long balanceBefore = owner is null ? wallet?.Cents ?? 0 : CorporationWalletStore.ReadBalance(world, owner, key!.Value);
        LoyaltyStoreRules.CheckFunds(pointsBefore, balanceBefore, cost);
        var stacks = world
            .Set<OwnedItem>()
            .Where(item =>
                item.OwnerId == pilotId
                && item.LocationId == station.Id
                && item.FlagId == (int)InventoryFlag.Hangar
                && !item.Singleton
                && item.Quantity > 0
            )
            .OrderBy(item => item.Id)
            .ToArray();
        foreach (var required in cost.RequiredItems)
        {
            if (stacks.Where(item => item.TypeId == required.TypeId).Sum(item => (long)item.Quantity) < required.Quantity)
            {
                throw new InvalidDataException("Required items must be in your personal Item Hangar at this station.");
            }
        }
        List<InventoryChange> changes = [];
        foreach (var required in cost.RequiredItems)
        {
            int remaining = required.Quantity;
            foreach (var item in stacks.Where(item => item.TypeId == required.TypeId))
            {
                if (remaining == 0)
                {
                    break;
                }
                var previous = PilotInventoryStore.Read(catalog, item);
                int consumed = Math.Min(remaining, item.Quantity);
                item.Quantity -= consumed;
                remaining -= consumed;
                changes.Add(new(previous with { Quantity = item.Quantity }, previous));
                if (item.Quantity == 0)
                {
                    world.Remove(item);
                }
            }
        }
        if (catalog.RequireItem(offer.TypeId).CategoryId == (int)InventoryCategory.Blueprint)
        {
            long first = InventoryPersistence.NextId(world);
            for (int index = 0; index < count; index++)
            {
                var copy = new OwnedItem
                {
                    Id = checked(first + index),
                    TypeId = offer.TypeId,
                    OwnerId = pilotId,
                    LocationId = station.Id,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 1,
                    Singleton = true,
                    BlueprintRuns = offer.Quantity,
                };
                world.Add(copy);
                changes.Add(new(PilotInventoryStore.Read(catalog, copy), null));
            }
        }
        else
        {
            changes.Add(
                InventoryPersistence.MergeOutput(
                    world,
                    catalog,
                    pilotId,
                    station.Id,
                    (int)InventoryFlag.Hangar,
                    offer.TypeId,
                    cost.Quantity
                )
            );
        }
        if (loyalty is not null)
        {
            loyalty.Points -= cost.Points;
        }
        if (wallet is not null)
        {
            wallet.Cents -= cost.IskCents;
        }
        if (corporateLoyalty is not null)
        {
            corporateLoyalty.Points -= cost.Points;
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (owner is not null)
        {
            CorporationWalletStore.WriteBalance(world, owner, key!.Value, balanceBefore - cost.IskCents);
            CorporationWalletStore.AddEntry(
                world,
                owner.Id,
                key.Value,
                now,
                corporationId,
                -cost.IskCents,
                balanceBefore - cost.IskCents,
                WalletEntryType.LoyaltyStore,
                offerId,
                "Loyalty point store purchase"
            );
        }
        else
        {
            world.Add(
                new SavedServicePayment
                {
                    PilotId = pilotId,
                    CounterpartyId = corporationId,
                    ContextId = offerId,
                    Kind = WalletEntryType.LoyaltyStore,
                    AmountCents = -cost.IskCents,
                    BalanceCents = balanceBefore - cost.IskCents,
                    Time = now,
                }
            );
        }
        world.SaveChanges();
        transaction.Commit();
        return new(pointsBefore, pointsBefore - cost.Points, balanceBefore - cost.IskCents, changes, owner?.Id, key);
    }

    public LoyaltyExchange ExchangeConcord(long pilotId, int corporationId, long points)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(ship => ship.Position).SingleOrDefault(p => p.Id == pilotId);
        if (
            pilot is null
            || pilot.ActiveShip.Position is not null
            || catalog.Definitions.FindStation(pilot.ActiveShip.StationId) is not { } station
            || !catalog.Definitions.NpcCorporations.ContainsKey(station.OwnerId)
        )
        {
            throw new InvalidDataException("Exchange personal CONCORD loyalty points while docked at an NPC station.");
        }
        var rates = catalog.Read(CatalogData.Client.Npccorporations, (long)LoyaltyStoreRules.ConcordCorporation)?.ExchangeRates;
        if (
            !catalog.Definitions.NpcCorporations.ContainsKey(corporationId)
            || rates?.GetValueOrDefault(corporationId.ToString(CultureInfo.InvariantCulture)) is not > 0
            || corporationId == LoyaltyStoreRules.ConcordCorporation
        )
        {
            throw new InvalidDataException("CONCORD LP cannot be exchanged for this corporation.");
        }
        // The pinned FSD stores 0.8/0.4 as single-precision values. Recover the
        // decimal policy rate before rounding the required source LP upward.
        decimal rate = decimal.Round((decimal)rates[corporationId.ToString(CultureInfo.InvariantCulture)], 7);
        long cost = LoyaltyStoreRules.ExchangeCost(points, rate);
        var source = world.Set<SavedLoyaltyBalance>().Find(pilotId, LoyaltyStoreRules.ConcordCorporation);
        var destination = world.Set<SavedLoyaltyBalance>().Find(pilotId, corporationId);
        long before = destination?.Points ?? 0;
        if (source is null || source.Points < cost || before < 0 || before > long.MaxValue - points)
        {
            throw new InvalidDataException("Insufficient CONCORD LP or the destination balance is too large.");
        }
        long sourceBefore = source.Points;
        destination ??= world.Add(new SavedLoyaltyBalance { PilotId = pilotId, IssuerCorporationId = corporationId }).Entity;
        source.Points -= cost;
        destination.Points += points;
        world.SaveChanges();
        transaction.Commit();
        return new(sourceBefore, source.Points, before, destination.Points);
    }
}
