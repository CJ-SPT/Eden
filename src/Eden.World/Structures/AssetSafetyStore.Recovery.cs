using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Economy;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime.Economy;

namespace Eden.World.Structures;

internal sealed partial class AssetSafetyStore
{
    private sealed record ValuationItem(long Id, int? Quantity);

    public AssetSafetyRecovery Recover(long pilotId, long destinationId, InventoryMoveRequest request) =>
        Execute(pilotId, (world, catalog, now) => Recover(world, catalog, pilotId, destinationId, request, now));

    internal static AssetSafetyRecovery Recover(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long destinationId,
        InventoryMoveRequest request,
        long now
    )
    {
        var source = Ancestor(world, request.SourceId) ?? throw new InvalidDataException("The source is not an asset safety inventory.");
        var wrap = Authorize(world, pilotId, source.Id);
        var pilot = world.Pilots.Find(pilotId)!;
        var ship = pilot.ActiveShip;
        if (
            wrap.DeliveredAt is null
            || wrap.DestinationId != ship.DockedLocationId
            || ship.Position is not null
            || world.Set<OwnedItem>().Find(wrap.Id)?.LocationId != wrap.DestinationId
        )
        {
            throw new InvalidDataException("Recover assets while docked at their delivered wrap.");
        }
        if (
            request.ItemIds.Count is 0 or > AssetSafetyRules.MaximumItemsPerCall
            || request.ItemIds.Distinct().Count() != request.ItemIds.Count
            || request.Quantity is <= 0
            || request.Quantity.HasValue && request.ItemIds.Count != 1
        )
        {
            throw new InvalidDataException("Choose distinct safety items and a valid quantity.");
        }
        if (request.SourceId != wrap.Id)
        {
            var parent = world.Set<OwnedItem>().Find(request.SourceId);
            if (
                parent is null
                || parent.OwnerId != wrap.OwnerId
                || !Enum.IsDefined((PersonalAssetContainerGroup)catalog.RequireItem(parent.TypeId).GroupId)
            )
            {
                throw new InvalidDataException("The source must be this wrap or an owned container inside it.");
            }
        }
        bool corporation = wrap.OwnerId != pilotId;
        var office = corporation ? world.Set<SavedCorporationOffice>().Find(destinationId) : null;
        bool hangar = destinationId == ship.DockedLocationId && request.FlagId == (int)InventoryFlag.Hangar && !corporation;
        bool deliveries =
            corporation && destinationId == ship.DockedLocationId && request.FlagId == CorporationStorageRules.MarketDeliveriesFlag;
        bool division =
            corporation
            && office?.CorporationId == wrap.OwnerId
            && office.StationId == ship.DockedLocationId
            && CorporationStorageRules.IsDivision(request.FlagId);
        bool cargo = !corporation && destinationId == ship.Id && ShipHoldRules.IsHold(request.FlagId);
        if (!hangar && !deliveries && !division && !cargo)
        {
            throw new InvalidDataException("Recover into your local hangar/cargo or the owning corporation's local office/deliveries.");
        }
        List<InventoryItem> selected = [];
        foreach (long id in request.ItemIds)
        {
            InventoryItem? item =
                world.Set<OwnedItem>().Find(id) is { } saved && saved.LocationId == request.SourceId && saved.OwnerId == wrap.OwnerId
                    ? PilotInventoryStore.Read(catalog, saved)
                : world.Ships.Find(id) is { } hull && hull.StoredInId == request.SourceId && hull.OwnerId == wrap.OwnerId
                    ? PilotFleetStore.Read(catalog, hull)
                : null;
            if (item is null || request.Quantity > item.Quantity || request.Quantity is not (null or 1) && item.Singleton)
            {
                throw new InvalidDataException("The selected safety item or quantity is unavailable.");
            }
            selected.Add(item);
        }
        if (cargo)
        {
            var hold = ShipHoldRules.Definition(request.FlagId)!;
            double additional = selected.Sum(item => hold.RequiredCapacity(item, request.Quantity ?? item.Quantity));
            double used = InventoryPersistence.UsedVolume(world, catalog, pilotId, ship.Id, request.FlagId);
            double capacity = PilotInventoryStore.HoldCapacity(world, catalog, pilotId, ship.Id, request.FlagId);
            if (
                selected.Any(item =>
                    !hold.Allows(item) || item.CategoryId == (int)InventoryCategory.Ship && item.Singleton && !hold.AllowsAssembledShip
                )
                || used + additional > capacity
            )
            {
                throw new InvalidDataException("The selected items do not fit the destination hold.");
            }
        }
        var prices = MarketValuations.ReferencePrices(catalog);
        decimal value = selected.Sum(item => Value(world, catalog, prices, item.Id, wrap.OwnerId, request.Quantity));
        int destinationSystem =
            catalog.FindStation(wrap.DestinationId!.Value)?.SolarSystemId
            ?? world.Set<SavedStructure>().Find(wrap.DestinationId.Value)!.SystemId;
        long fee = AssetSafetyRules.RecoveryFee(value, destinationSystem == wrap.SystemId);
        long counterparty =
            catalog.FindStation(wrap.DestinationId.Value)?.OwnerId
            ?? world.Set<SavedStructure>().Find(wrap.DestinationId.Value)!.CorporationId;
        long balance;
        if (corporation)
        {
            var corp = world.Set<SavedCorporation>().Find(wrap.OwnerId)!;
            balance = CorporationWalletStore.ReadBalance(world, corp, CorporationWalletRules.MasterKey);
            if (balance < fee)
            {
                throw new InvalidDataException("The corporation master wallet cannot cover asset safety recovery.");
            }
            balance = checked(balance - fee);
            CorporationWalletStore.WriteBalance(world, corp, CorporationWalletRules.MasterKey, balance);
            if (fee > 0)
            {
                CorporationWalletStore.AddEntry(
                    world,
                    corp.Id,
                    CorporationWalletRules.MasterKey,
                    now,
                    counterparty,
                    -fee,
                    balance,
                    WalletEntryType.AssetSafetyRecoveryTax,
                    wrap.Id,
                    "Asset safety recovery"
                );
            }
        }
        else
        {
            balance = EconomicStore.Pay(world, pilotId, counterparty, wrap.Id, WalletEntryType.AssetSafetyRecoveryTax, fee, now);
        }
        List<InventoryChange> changes = [];
        foreach (var before in selected)
        {
            if (world.Ships.Find(before.Id) is { } hull)
            {
                hull.StoredInId = division || cargo ? destinationId : null;
                hull.StorageFlagId = request.FlagId;
                if (!division && !cargo)
                {
                    hull.StructureId = ship.StructureId;
                    if (destinationId <= int.MaxValue)
                    {
                        hull.StationId = checked((int)destinationId);
                    }
                }
                changes.Add(new(PilotFleetStore.Read(catalog, hull), before));
                continue;
            }
            var item = world.Set<OwnedItem>().Find(before.Id)!;
            int quantity = request.Quantity ?? item.Quantity;
            if (quantity != item.Quantity)
            {
                item.Quantity -= quantity;
                changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
                item = new()
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = item.OwnerId,
                    TypeId = item.TypeId,
                    Quantity = quantity,
                    Singleton = item.Singleton,
                    BlueprintRuns = item.BlueprintRuns,
                    MaterialEfficiency = item.MaterialEfficiency,
                    TimeEfficiency = item.TimeEfficiency,
                    CrystalDamage = item.CrystalDamage,
                    VolumeOverride = item.VolumeOverride,
                };
                world.Add(item);
            }
            item.LocationId = destinationId;
            item.FlagId = request.FlagId;
            changes.Add(new(PilotInventoryStore.Read(catalog, item), quantity == before.Quantity ? before : null));
        }
        world.SaveChanges();
        if (!world.Filter<OwnedItem>(i => i.LocationId == wrap.Id).Any() && !world.Filter<Ship>(s => s.StoredInId == wrap.Id).Any())
        {
            var item = world.Set<OwnedItem>().Find(wrap.Id)!;
            changes.Add(new(PilotInventoryStore.Read(catalog, item) with { Quantity = 0 }, PilotInventoryStore.Read(catalog, item)));
            world.Remove(item);
            world.Remove(wrap);
        }
        return new(changes, wrap.OwnerId, balance);
    }

    private static decimal Value(
        WorldAction world,
        CatalogDbContext catalog,
        IReadOnlyDictionary<int, decimal> prices,
        long root,
        long owner,
        int? quantity
    )
    {
        HashSet<long> visited = [];
        Stack<ValuationItem> pending = new([new(root, quantity)]);
        decimal total = 0;
        while (pending.TryPop(out var next))
        {
            long id = next.Id;
            if (!visited.Add(id))
            {
                throw new InvalidDataException("The asset safety inventory contains a cycle.");
            }
            var item = world.Set<OwnedItem>().Find(id);
            var ship = item is null ? world.Ships.Find(id) : null;
            if ((item?.OwnerId ?? ship?.OwnerId) != owner)
            {
                throw new InvalidDataException("The asset safety inventory contains foreign assets.");
            }
            int type = item?.TypeId ?? ship!.TypeId;
            decimal price = prices.GetValueOrDefault(type);
            if (price <= 0 && catalog.Read(CatalogData.Client.Types, (long)type)?.BasePrice is > 0 and var fallback)
            {
                price = checked((decimal)fallback);
            }
            total = checked(total + price * MarketDefinitions.CentsPerIsk * (next.Quantity ?? item?.Quantity ?? 1));
            foreach (var child in world.Filter<OwnedItem>(i => i.LocationId == id))
            {
                pending.Push(new(child.Id, null));
            }
            foreach (var child in world.Filter<Ship>(s => s.StoredInId == id))
            {
                pending.Push(new(child.Id, null));
            }
        }
        return total;
    }
}
