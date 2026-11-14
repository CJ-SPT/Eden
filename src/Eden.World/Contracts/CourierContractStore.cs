using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Contracts;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Insurance;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Contracts;

internal sealed partial class PilotContractStore
{
    private static PersonalContract AcceptCourier(
        WorldAction world,
        CatalogDbContext catalog,
        SavedContract contract,
        long pilotId,
        long now
    )
    {
        var type = catalog.FittingType(CourierRules.PackageType);
        if (type?.GroupId != CourierRules.PackageGroup || type.CategoryId != CourierRules.PackageCategory)
        {
            throw new InvalidDataException("The pinned catalog has no supported courier package.");
        }
        var package = world
            .Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = pilotId,
                    TypeId = CourierRules.PackageType,
                    LocationId = contract.StationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 1,
                    Singleton = true,
                    VolumeOverride = CourierRules.PackageVolume(contract.Volume),
                }
            )
            .Entity;
        Pay(
            world,
            contract,
            pilotId,
            MarketDefinitions.SccCorporationId,
            WalletEntryType.ContractCollateral,
            -contract.CollateralCents,
            now
        );
        contract.AcceptorId = pilotId;
        contract.Accepted = now;
        contract.PackageId = package.Id;
        contract.Status = ContractStatus.InProgress;
        Change(world, contract.Id, contract.IssuerId, ContractChangeKind.CourierAccepted, [], null);
        Change(
            world,
            contract.Id,
            pilotId,
            ContractChangeKind.CourierAccepted,
            [new(PilotInventoryStore.Read(catalog, package), null)],
            Balance(world, pilotId)
        );
        return contract.Snapshot();
    }

    public CourierPackageInfo? Package(long pilotId, long itemId) =>
        Read(
            (world, catalog) =>
            {
                Pilot(world, pilotId);
                var contract = world.Set<SavedContract>().SingleOrDefault(c => c.PackageId == itemId && c.AcceptorId == pilotId);
                var item = world
                    .Set<OwnedItem>()
                    .SingleOrDefault(i => i.Id == itemId && i.OwnerId == pilotId && i.TypeId == CourierRules.PackageType);
                return contract is null || item is null
                    ? null
                    : new CourierPackageInfo(contract.Id, item.Id, pilotId, contract.Status, PilotInventoryStore.Read(catalog, item));
            }
        );

    private static Pilot CourierPilot(WorldAction world, long pilotId, bool docked)
    {
        var pilot = world.Pilots.Find(pilotId);
        if (pilot is null || (docked && pilot.ActiveShip.Position is not null))
        {
            throw new InvalidDataException("Deliver courier packages while docked.");
        }
        return pilot;
    }

    private static OwnedItem AccessiblePackage(WorldAction world, SavedContract contract, Pilot pilot)
    {
        var package = world
            .Set<OwnedItem>()
            .SingleOrDefault(i =>
                i.Id == contract.PackageId
                && i.OwnerId == pilot.Id
                && i.TypeId == CourierRules.PackageType
                && i.Singleton
                && i.Quantity == 1
            );
        if (
            package is null
            || !(
                (
                    pilot.ActiveShip.Position is null
                    && package.LocationId == pilot.ActiveShip.DockedLocationId
                    && package.FlagId == (int)InventoryFlag.Hangar
                ) || (package.LocationId == pilot.ActiveShipId && package.FlagId == (int)InventoryFlag.Cargo)
            )
        )
        {
            throw new InvalidDataException("Use the intact courier package in this station hangar or your active ship's cargo.");
        }
        return package;
    }

    public void Complete(long pilotId, long contractId) =>
        Write(
            (world, catalog) =>
            {
                var contract = Contract(world, pilotId, contractId);
                if (contract.Type != ContractType.Courier || contract.AcceptorId != pilotId)
                {
                    throw new InvalidDataException("Only the courier can deliver this contract.");
                }
                if (contract.Status == ContractStatus.Finished)
                {
                    return true;
                }
                if (contract.Status != ContractStatus.InProgress)
                {
                    throw new InvalidDataException("This courier contract is not in progress.");
                }
                var pilot = CourierPilot(world, pilotId, docked: true);
                if (pilot.ActiveShip.DockedLocationId != contract.EndStationId)
                {
                    throw new InvalidDataException("Deliver the package at the contract's destination station.");
                }
                var package = AccessiblePackage(world, contract, pilot);
                var previous = PilotInventoryStore.Read(catalog, package);
                var deliveries = ReleaseCourierGoods(
                    world,
                    catalog,
                    contract,
                    contract.IssuerId,
                    contract.IssuerId,
                    contract.EndStationId,
                    (int)InventoryFlag.Hangar
                );
                world.Remove(package);
                long now = Now;
                Pay(world, contract, pilotId, contract.IssuerId, WalletEntryType.ContractReward, contract.RewardCents, now);
                Pay(
                    world,
                    contract,
                    pilotId,
                    MarketDefinitions.SccCorporationId,
                    WalletEntryType.ContractCollateralRefund,
                    contract.CollateralCents,
                    now
                );
                Pay(
                    world,
                    contract,
                    contract.IssuerId,
                    MarketDefinitions.SccCorporationId,
                    WalletEntryType.ContractDepositRefund,
                    contract.DepositCents,
                    now
                );
                contract.Status = ContractStatus.Finished;
                contract.Completed = now;
                contract.Reclaimed = true;
                Change(world, contractId, contract.IssuerId, ContractChangeKind.Completed, deliveries, Balance(world, contract.IssuerId));
                Change(
                    world,
                    contractId,
                    pilotId,
                    ContractChangeKind.Completed,
                    [new(previous with { Quantity = 0 }, previous)],
                    Balance(world, pilotId)
                );
                return true;
            }
        );

    public void Fail(long pilotId, long contractId) =>
        Write(
            (world, catalog) =>
            {
                var contract = Contract(world, pilotId, contractId);
                FailCourier(world, catalog, contract, pilotId, Now);
                return true;
            }
        );

    private static void FailCourier(WorldAction world, CatalogDbContext catalog, SavedContract contract, long pilotId, long now)
    {
        if (
            contract.Type != ContractType.Courier
            || contract.AcceptorId is not { } acceptor
            || (pilotId != acceptor && pilotId != contract.IssuerId)
        )
        {
            throw new InvalidDataException("Only a party to this courier contract can mark it failed.");
        }
        if (contract.Status == ContractStatus.Failed)
        {
            return;
        }
        if (
            contract.Status != ContractStatus.InProgress
            || (pilotId == contract.IssuerId && now <= CourierRules.Deadline(contract.Accepted!.Value, contract.DurationDays))
        )
        {
            throw new InvalidDataException("The courier can fail a delivery; the issuer must wait until it is overdue.");
        }
        bool packageExists = world
            .Set<OwnedItem>()
            .Any(i => i.Id == contract.PackageId && i.OwnerId == acceptor && i.TypeId == CourierRules.PackageType);
        foreach (var entry in world.Set<SavedContractEscrow>().Where(e => e.ContractId == contract.Id).ToArray())
        {
            if (packageExists)
            {
                // Collateral purchases the failed shipment. Keep it sealed until
                // its current holder breaks the intact package at its real location.
                Transfer(world, catalog, entry.ItemId, contract.IssuerId, acceptor);
            }
            else
            {
                var item = world.Set<OwnedItem>().FindUnfiltered(new(entry.ItemId));
                if (item is not null)
                {
                    world.Remove(item);
                }
                else if (world.Ships.FindUnfiltered(new(entry.ItemId)) is { } hull)
                {
                    PilotInsuranceStore.VoidRemovedHull(world, hull.Id);
                    world.Remove(hull);
                }
                world.Remove(entry);
            }
        }
        Pay(
            world,
            contract,
            contract.IssuerId,
            MarketDefinitions.SccCorporationId,
            WalletEntryType.ContractCollateralPayout,
            contract.CollateralCents,
            now
        );
        Pay(
            world,
            contract,
            contract.IssuerId,
            MarketDefinitions.SccCorporationId,
            WalletEntryType.ContractRewardRefund,
            contract.RewardCents,
            now
        );
        contract.Status = ContractStatus.Failed;
        contract.Completed = now;
        contract.Reclaimed = true;
        Change(world, contract.Id, contract.IssuerId, ContractChangeKind.Failed, [], Balance(world, contract.IssuerId));
        Change(world, contract.Id, acceptor, ContractChangeKind.Failed, [], Balance(world, acceptor));
    }

    public void BreakPackage(long pilotId, long itemId) =>
        Write(
            (world, catalog) =>
            {
                var contract =
                    world.Set<SavedContract>().SingleOrDefault(c => c.PackageId == itemId && c.AcceptorId == pilotId)
                    ?? throw new InvalidDataException("Unknown owned courier package.");
                var pilot = CourierPilot(world, pilotId, docked: false);
                var package = AccessiblePackage(world, contract, pilot);
                if (contract.Status is not (ContractStatus.InProgress or ContractStatus.Failed))
                {
                    throw new InvalidDataException("This courier package is no longer available.");
                }
                if (package.LocationId == pilot.ActiveShipId)
                {
                    var roots = world
                        .Set<SavedContractItem>()
                        .Where(i => i.ContractId == contract.Id && i.Offered && i.ParentId == null)
                        .ToArray();
                    if (roots.Any(i => i.CategoryId == (int)InventoryCategory.Ship && i.Singleton))
                    {
                        throw new InvalidDataException("Move this package to your station hangar before unpacking assembled hulls.");
                    }
                    // The package rounds upward, so releasing its roots cannot increase
                    // used cargo volume. The enclosed graph was validated at creation.
                }
                if (contract.Status == ContractStatus.InProgress)
                {
                    FailCourier(world, catalog, contract, pilotId, Now);
                }
                var previous = PilotInventoryStore.Read(catalog, package);
                var contents = ReleaseCourierGoods(world, catalog, contract, pilotId, pilotId, package.LocationId, package.FlagId);
                world.Remove(package);
                Change(
                    world,
                    contract.Id,
                    pilotId,
                    ContractChangeKind.Reclaimed,
                    contents.Append(new InventoryChange(previous with { Quantity = 0 }, previous)).ToArray(),
                    null
                );
                return true;
            }
        );

    private static IReadOnlyList<InventoryChange> ReleaseCourierGoods(
        WorldAction world,
        CatalogDbContext catalog,
        SavedContract contract,
        long previousOwner,
        long owner,
        long location,
        int flag
    )
    {
        var roots = world
            .Set<SavedContractItem>()
            .Where(i => i.ContractId == contract.Id && i.Offered && i.ParentId == null)
            .Select(i => i.ItemId)
            .ToHashSet();
        List<InventoryChange> changes = [];
        foreach (var entry in world.Set<SavedContractEscrow>().Where(e => e.ContractId == contract.Id).ToArray())
        {
            var item = Transfer(world, catalog, entry.ItemId, previousOwner, owner);
            if (roots.Contains(entry.ItemId))
            {
                if (world.Set<OwnedItem>().FindUnfiltered(new(entry.ItemId)) is { } stack)
                {
                    stack.LocationId = location;
                    stack.FlagId = flag;
                    stack.Online = false;
                    item = PilotInventoryStore.Read(catalog, stack);
                }
                else
                {
                    var hull =
                        world.Ships.FindUnfiltered(new(entry.ItemId)) ?? throw new InvalidDataException("The escrow hull is unavailable.");
                    hull.StationId = checked((int)location);
                    hull.StoredInId = null;
                    hull.StorageFlagId = (int)InventoryFlag.Hangar;
                    item = item with { LocationId = location, FlagId = flag };
                }
            }
            changes.Add(new(item, null));
            world.Remove(entry);
        }
        return changes;
    }
}
