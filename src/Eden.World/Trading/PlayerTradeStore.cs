using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Economy;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Trading;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Contracts;
using Eden.World.Corporations;
using Eden.World.Industry;
using Eden.World.Insurance;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Trading;

[Injectable]
internal sealed class PlayerTradeStore(WorldDatabase database, TimeProvider clock) : IPlayerTradeStore
{
    // Build 3662168 inventoryrestrictions.common.checks.is_tradable and evetypes.const.
    private const long NonTradableTypeList = 36;
    private const long SoulboundTypeList = 142;
    private const int AssetSafetyWrapType = 60;

    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Player trading requires an active resident world.");

    public long Balance(long pilotId) => Resident.WalletBalance(pilotId);

    public IReadOnlyList<TradeItemOffer> Quote(long pilotId, long stationId, InventoryMoveRequest request)
    {
        if (
            request.ItemIds.Count is 0 or > PlayerTradeRules.MaximumItemsPerPilot
            || request.ItemIds.Count != request.ItemIds.Distinct().Count()
        )
        {
            throw new InvalidDataException("Choose distinct items to offer.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return Resident.Economy.Inspect(
            world =>
            {
                long active = RequireDocked(world, pilotId, stationId);
                var restrictions = Restrictions(catalog);
                return request
                    .ItemIds.Select(id =>
                    {
                        var asset = ReadAsset(world, catalog, restrictions, pilotId, stationId, active, id, request.SourceId);
                        return new TradeItemOffer(asset, PlayerTradeRules.Quantity(asset, request.Quantity));
                    })
                    .ToArray();
            },
            new(pilots: [pilotId])
        );
    }

    public InventoryItem QuoteVaultPlex(long pilotId, long partnerId, long stationId, int quantity, long alreadyOffered)
    {
        if (quantity <= 0 || alreadyOffered < 0 || alreadyOffered > PlayerTradeRules.MaximumVaultPlex - quantity)
        {
            throw new PlexVaultRejected(PlexVaultFailure.Amount);
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return Resident.Economy.Inspect(
            world =>
            {
                RequireDocked(world, pilotId, stationId);
                RequireDocked(world, partnerId, stationId);
                var pilot = world.Pilots.Find(pilotId)!;
                var partner = world.Pilots.Find(partnerId)!;
                if (pilot.AccountId == partner.AccountId)
                {
                    throw new PlexVaultRejected(PlexVaultFailure.Location);
                }
                if (world.Set<Account>().Find(pilot.AccountId)!.PlexBalance < alreadyOffered + quantity)
                {
                    throw new PlexVaultRejected(PlexVaultFailure.Funds);
                }
                return PilotInventoryStore.Read(
                    catalog,
                    new OwnedItem
                    {
                        OwnerId = pilotId,
                        TypeId = MarketDefinitions.PlexTypeId,
                        LocationId = stationId,
                        FlagId = (int)InventoryFlag.Hangar,
                        Quantity = quantity,
                    }
                );
            },
            new(pilots: [pilotId, partnerId])
        );
    }

    public TradeSettlement Settle(long stationId, TradeSide initiator, TradeSide partner)
    {
        if (initiator.PilotId == partner.PilotId)
        {
            throw new InvalidDataException("Choose another pilot to trade with.");
        }
        using var world = Resident.Economy.Begin(new(pilots: [initiator.PilotId, partner.PilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var restrictions = Restrictions(catalog);
        VerifySide(world, catalog, restrictions, stationId, initiator);
        VerifySide(world, catalog, restrictions, stationId, partner);
        var firstWallet = world.Set<PilotWallet>().Find(initiator.PilotId);
        var secondWallet = world.Set<PilotWallet>().Find(partner.PilotId);
        long firstBalance = PlayerTradeRules.BalanceAfterTrade(firstWallet?.Cents ?? 0, initiator.MoneyCents, partner.MoneyCents);
        long secondBalance = PlayerTradeRules.BalanceAfterTrade(secondWallet?.Cents ?? 0, partner.MoneyCents, initiator.MoneyCents);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        SettleVaultPlex(world, initiator, partner, now);

        List<InventoryChange> firstChanges = [];
        List<InventoryChange> secondChanges = [];
        Transfer(world, catalog, initiator.Items, partner.PilotId, stationId, firstChanges, secondChanges);
        Transfer(world, catalog, partner.Items, initiator.PilotId, stationId, secondChanges, firstChanges);
        Pay(world, stationId, initiator, partner, firstWallet, firstBalance, now);
        Pay(world, stationId, partner, initiator, secondWallet, secondBalance, now);
        world.Commit();
        return new(firstChanges, secondChanges);
    }

    private static void SettleVaultPlex(EconomyTransaction world, TradeSide first, TradeSide second, long now)
    {
        if (first.VaultPlex == 0 && second.VaultPlex == 0)
        {
            return;
        }
        var firstPilot = world.Pilots.Find(first.PilotId)!;
        var secondPilot = world.Pilots.Find(second.PilotId)!;
        var firstAccount = world.Set<Account>().Find(firstPilot.AccountId)!;
        var secondAccount = world.Set<Account>().Find(secondPilot.AccountId)!;
        if (
            firstAccount.Id == secondAccount.Id
            || first.VaultPlex is < 0 or > PlayerTradeRules.MaximumVaultPlex
            || second.VaultPlex is < 0 or > PlayerTradeRules.MaximumVaultPlex
            || first.VaultPlex > firstAccount.PlexBalance
            || second.VaultPlex > secondAccount.PlexBalance
            || firstAccount.PlexBalance - first.VaultPlex > PlexVaultRules.MaximumBalance - second.VaultPlex
            || secondAccount.PlexBalance - second.VaultPlex > PlexVaultRules.MaximumBalance - first.VaultPlex
        )
        {
            throw new InvalidDataException("The PLEX vault balances cannot complete this trade.");
        }
        SettleSide(firstPilot, firstAccount, first.VaultPlex, secondPilot.Id, second.VaultPlex);
        SettleSide(secondPilot, secondAccount, second.VaultPlex, firstPilot.Id, first.VaultPlex);

        void SettleSide(Pilot pilot, Account account, long sent, long other, long received)
        {
            if (sent > 0)
            {
                account.PlexBalance -= sent;
                PlexVaultStore.Record(world, pilot, account.PlexBalance, PlexVaultTransferKind.TradeSent, -sent, now, other);
            }
            if (received > 0)
            {
                account.PlexBalance += received;
                PlexVaultStore.Record(world, pilot, account.PlexBalance, PlexVaultTransferKind.TradeReceived, received, now, other);
            }
        }
    }

    private static long RequireDocked(WorldAction world, long pilotId, long stationId)
    {
        var pilot = world.Pilots.Find(pilotId);
        if (
            pilot is null
            || world.Ships.Find(pilot.ActiveShipId) is not { Position: null, StoredInId: null } ship
            || ship.DockedLocationId != stationId
        )
        {
            throw new InvalidDataException("Both pilots must remain docked at the same location.");
        }
        return pilot.ActiveShipId;
    }

    private static InventoryTypeList[] Restrictions(CatalogDbContext catalog)
    {
        // Small fixture catalogs may omit the restriction lists.
        return new[] { NonTradableTypeList, SoulboundTypeList }
            .Where(id => catalog.Read(CatalogData.Client.Typelist, id) is not null)
            .Select(id => CatalogTypeLists.Read(catalog, id))
            .ToArray();
    }

    private static TradeAsset ReadAsset(
        WorldAction world,
        CatalogDbContext catalog,
        InventoryTypeList[] restrictions,
        long owner,
        long station,
        long activeShip,
        long id,
        long source
    )
    {
        var hull = world.Ships.Find(id);
        var stack = world.Set<OwnedItem>().Find(id);
        InventoryItem? root =
            hull is { Position: null } && !world.Filter<Pilot>(p => p.ActiveShipId == id).Any() ? PilotFleetStore.Read(catalog, hull)
            : stack is not null ? PilotInventoryStore.Read(catalog, stack)
            : null;
        if (
            root is null
            || root.LocationId != source
            || id == activeShip
            || hull is not null && source != station
            || (source == station ? root.FlagId != (int)InventoryFlag.Hangar : source != activeShip || !ShipHoldRules.IsHold(root.FlagId))
        )
        {
            throw new InvalidDataException("Offer items from your personal hangar or the holds of your docked ship.");
        }
        CheckItem(world, restrictions, root, owner);
        var contents = PilotContractAssetStore.Contents(world, catalog, owner, id);
        var parentIds = contents.Select(item => item.Id).Append(id).ToArray();
        if (
            world.Filter<OwnedItem>(item => Enumerable.Contains(parentIds, item.LocationId) && item.OwnerId != owner).Any()
            || world
                .Filter<Ship>(ship =>
                    ship.StoredInId != null && Enumerable.Contains(parentIds, ship.StoredInId.Value) && ship.OwnerId != owner
                )
                .Any()
        )
        {
            throw new InvalidDataException("A trade cannot include another owner's assets.");
        }
        foreach (var item in contents)
        {
            CheckItem(world, restrictions, item, owner);
        }
        return new(root, contents, hull?.Name);
    }

    private static void CheckItem(WorldAction world, InventoryTypeList[] restrictions, InventoryItem item, long owner)
    {
        if (
            item.OwnerId != owner
            || item.Quantity <= 0
            || item.TypeId is AssetSafetyWrapType or CourierRules.PackageType
            || restrictions.Any(list => list.Contains(item))
            || PilotIndustryStore.IsInstalled(world, item.Id)
        )
        {
            throw new InvalidDataException("The selected asset or its contents cannot be traded.");
        }
    }

    private static void VerifySide(
        WorldAction world,
        CatalogDbContext catalog,
        InventoryTypeList[] restrictions,
        long stationId,
        TradeSide side
    )
    {
        long active = RequireDocked(world, side.PilotId, stationId);
        HashSet<long> checkedItems = [];
        foreach (var offer in side.Items)
        {
            var asset = offer.Asset;
            var root = asset.Root;
            var current = ReadAsset(world, catalog, restrictions, side.PilotId, stationId, active, root.Id, root.LocationId);
            if (
                current.Root != root
                || current.ShipName != asset.ShipName
                || !current.Contents.SequenceEqual(asset.Contents)
                || !checkedItems.Add(root.Id)
                || current.Contents.Any(item => !checkedItems.Add(item.Id))
            )
            {
                throw new InvalidDataException("An offered asset or its contents changed. Cancel and start a new trade.");
            }
            PlayerTradeRules.Quantity(current, offer.Quantity);
        }
    }

    private static void Transfer(
        WorldAction world,
        CatalogDbContext catalog,
        IReadOnlyList<TradeItemOffer> offers,
        long owner,
        long destination,
        List<InventoryChange> outgoing,
        List<InventoryChange> incoming
    )
    {
        foreach (var offer in offers)
        {
            var root = offer.Asset.Root;
            if (offer.Quantity < root.Quantity)
            {
                var source = world.Set<OwnedItem>().Find(root.Id)!;
                var changes = InventoryPersistence.SplitTransfer(
                    world,
                    catalog,
                    source,
                    root,
                    destination,
                    (int)InventoryFlag.Hangar,
                    offer.Quantity
                );
                var separated = changes[1].Item;
                world.Set<OwnedItem>().Local.Single(i => i.Id == separated.Id).OwnerId = owner;
                outgoing.Add(changes[0]);
                incoming.Add(new(separated with { OwnerId = owner }, null));
                continue;
            }
            Move(world, root.Id, owner, destination);
            foreach (var child in offer.Asset.Contents)
            {
                Move(world, child.Id, owner, null);
            }
            outgoing.Add(new(root with { Quantity = 0 }, root));
            incoming.Add(new(root with { OwnerId = owner, LocationId = destination, FlagId = (int)InventoryFlag.Hangar }, null));
        }
    }

    private static void Move(WorldAction world, long id, long owner, long? destination)
    {
        if (world.Set<OwnedItem>().Find(id) is { } item)
        {
            item.OwnerId = owner;
            if (destination is long location)
            {
                item.LocationId = location;
                item.FlagId = (int)InventoryFlag.Hangar;
            }
            return;
        }
        // Hull roots are already in the shared hangar. Descendants retain their containment.
        var hull = world.Ships.Find(id)!;
        PilotInsuranceStore.VoidRemovedHull(world, id);
        hull.OwnerId = owner;
    }

    private static void Pay(WorldAction world, long station, TradeSide pilot, TradeSide other, PilotWallet? wallet, long balance, long now)
    {
        if (pilot.MoneyCents == 0 && other.MoneyCents == 0)
        {
            return;
        }
        wallet ??= world.Add(new PilotWallet { PilotId = pilot.PilotId }).Entity;
        wallet.Cents = balance;
        Entry(-pilot.MoneyCents, balance - other.MoneyCents);
        Entry(other.MoneyCents, balance);

        void Entry(long amount, long runningBalance)
        {
            if (amount != 0)
            {
                world.Add(
                    new SavedServicePayment
                    {
                        PilotId = pilot.PilotId,
                        CounterpartyId = other.PilotId,
                        ContextId = station,
                        Kind = WalletEntryType.PlayerTrading,
                        AmountCents = amount,
                        BalanceCents = runningBalance,
                        Time = now,
                    }
                );
            }
        }
    }
}
