using System.Globalization;
using System.Text.Json;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Corporations;
using Eden.World.Industry;
using Eden.World.Insurance;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Contracts;

[Injectable]
internal sealed partial class PilotContractStore(WorldDatabase database, ContractChangeSignal signal, TimeProvider clock) : IPilotContracts
{
    public long Revision => signal.Revision;
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private T Read<T>(Func<WorldDbContext, CatalogDbContext, T> read)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        return read(world, catalog);
    }

    private T Write<T>(Func<WorldDbContext, CatalogDbContext, T> write)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        T result = write(world, catalog);
        world.SaveChanges();
        transaction.Commit();
        signal.Publish();
        return result;
    }

    private static Pilot Pilot(WorldDbContext world, long id) =>
        world.Pilots.Include(p => p.Account).SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException("Unknown pilot.");

    private static ContractSkills Skills(WorldDbContext world, CatalogDbContext catalog, Pilot pilot)
    {
        var ids = Enum.GetValues<ContractSkill>().Select(s => (int)s).ToArray();
        bool trained = world.Set<PilotSkill>().Any(s => s.PilotId == pilot.Id && ids.Contains(s.TypeId) && s.Points > 0);
        var alpha =
            trained && pilot.Account.CloneGrade == AccountCloneGrade.Alpha
                ? CatalogData
                    .Client.Clonegrades.Get(catalog, pilot.RaceId.ToString(CultureInfo.InvariantCulture))
                    .Skills!.ToDictionary(s => s.TypeID!.Value, s => s.Level!.Value)
                : null;
        int Level(ContractSkill skill)
        {
            int level = PilotSkillReader.Level(world, catalog, pilot.Id, (int)skill);
            return alpha is null ? level : Math.Min(level, checked((int)alpha.GetValueOrDefault((int)skill)));
        }
        return new(
            Level(ContractSkill.Contracting),
            Level(ContractSkill.AdvancedContracting),
            Level(ContractSkill.BrokerRelations),
            Level(ContractSkill.Accounting)
        );
    }

    public long Create(long pilotId, ContractDraft draft)
    {
        ContractRules.Check(draft);
        if (draft.MultiContract)
        {
            throw new InvalidDataException("Create bulk fitted-ship contracts through the batch operation.");
        }
        return Write((world, catalog) => CreateOne(world, catalog, pilotId, draft, Now));
    }

    private static long CreateOne(WorldDbContext world, CatalogDbContext catalog, long pilotId, ContractDraft draft, long now)
    {
        var pilot = Pilot(world, pilotId);
        if (draft.AssigneeId == pilotId || (draft.AssigneeId is { } assignee && !world.Pilots.Any(p => p.Id == assignee)))
        {
            throw new InvalidDataException("Assign this contract to another local pilot or make it public.");
        }
        var station =
            catalog
                .Set<Station>()
                .Include(s => s.SolarSystem)
                    .ThenInclude(s => s.Constellation)
                .SingleOrDefault(s => s.Id == draft.StationId)
            ?? throw new InvalidDataException("Unknown contract station.");
        var destination = draft.Courier is { } courier
            ? catalog
                .Set<Station>()
                .Include(s => s.SolarSystem)
                    .ThenInclude(s => s.Constellation)
                .SingleOrDefault(s => s.Id == courier.DestinationStationId)
                ?? throw new InvalidDataException("Unknown courier destination station.")
            : null;
        var skills = Skills(world, catalog, pilot);
        // Expired contracts retain escrow for collection but release a creation slot.
        if (
            world
                .Set<SavedContract>()
                .Count(c =>
                    c.IssuerId == pilotId
                    && ((c.Status == ContractStatus.Outstanding && c.Expires > now) || c.Status == ContractStatus.InProgress)
                ) >= ContractRules.Limit(skills)
        )
        {
            throw new InvalidDataException("Your outstanding contract limit has been reached.");
        }
        var available = PilotContractAssetStore.Assets(world, catalog, pilotId, draft.StationId).ToDictionary(i => i.Id);
        var fees = ContractRules.Fees(draft, skills);
        var contract = world
            .Add(
                new SavedContract
                {
                    BuyoutCents = draft.Auction?.BuyoutCents ?? 0,
                    AuctionAccountingLevel = skills.Accounting,
                    Type = draft.Type,
                    EndStationId = destination?.Id ?? 0,
                    EndSystemId = destination?.SolarSystemId ?? 0,
                    EndConstellationId = destination?.SolarSystem.ConstellationId ?? 0,
                    EndRegionId = destination?.SolarSystem.Constellation.RegionId ?? 0,
                    DurationDays = draft.Courier?.DurationDays ?? 0,
                    CollateralCents = draft.Courier?.CollateralCents ?? 0,
                    IssuerId = pilotId,
                    IssuerCorporationId = pilot.CorporationId,
                    AssigneeId = draft.AssigneeId,
                    StationId = station.Id,
                    SystemId = station.SolarSystemId,
                    ConstellationId = station.SolarSystem.ConstellationId,
                    RegionId = station.SolarSystem.Constellation.RegionId,
                    Issued = now,
                    Expires = checked(now + TimeSpan.FromMinutes(draft.ExpiryMinutes).Ticks),
                    PriceCents = draft.PriceCents,
                    RewardCents = draft.RewardCents,
                    Title = draft.Title,
                    Description = draft.Description,
                    BrokerCents = fees.BrokerCents,
                    DepositCents = fees.DepositCents,
                    SalesTaxCents = fees.SalesTaxCents,
                }
            )
            .Entity;
        world.SaveChanges();
        List<InventoryChange> changes = [];
        HashSet<long> escrowIds = [];
        foreach (var offer in draft.Items)
        {
            if (!available.TryGetValue(offer.ItemId, out var root) || offer.Quantity > root.Quantity)
            {
                throw new InvalidDataException("The offered items or quantities are unavailable.");
            }
            var contents = PilotContractAssetStore.Contents(world, catalog, pilotId, root.Id);
            if (contents.Count > 0 && (!root.Singleton || offer.Quantity != 1))
            {
                throw new InvalidDataException("A container must be offered whole.");
            }
            var parentIds = contents.Select(i => i.Id).Append(root.Id).ToArray();
            if (
                world.Set<OwnedItem>().Any(i => parentIds.Contains(i.LocationId) && i.OwnerId != pilotId)
                || world.Ships.Any(s => s.StoredInId != null && parentIds.Contains(s.StoredInId.Value) && s.OwnerId != pilotId)
            )
            {
                throw new InvalidDataException("A contract cannot include another owner's nested assets.");
            }
            if (offer.Quantity < root.Quantity)
            {
                ContractAssetRules.Split(root, offer.Quantity);
                var stack = world.Set<OwnedItem>().Find(root.Id)!;
                stack.Quantity -= offer.Quantity;
                changes.Add(new(PilotInventoryStore.Read(catalog, stack), root));
                var separated = world
                    .Add(
                        new OwnedItem
                        {
                            Id = InventoryPersistence.NextId(world),
                            OwnerId = pilotId,
                            TypeId = root.TypeId,
                            LocationId = root.LocationId,
                            FlagId = root.FlagId,
                            Quantity = offer.Quantity,
                            CrystalDamage = root.CrystalDamage,
                        }
                    )
                    .Entity;
                root = PilotInventoryStore.Read(catalog, separated);
            }
            else
            {
                changes.Add(new(root with { Quantity = 0 }, root));
            }
            changes.AddRange(contents.Select(item => new InventoryChange(item with { Quantity = 0 }, item)));
            foreach (var item in contents.Prepend(root))
            {
                if (item.TypeId == CourierRules.PackageType)
                {
                    throw new InvalidDataException("Deliver or break an existing courier package before contracting it again.");
                }
                if (!escrowIds.Add(item.Id) || escrowIds.Count + draft.RequestedItems.Count > ContractRules.MaximumItems)
                {
                    throw new InvalidDataException("The contract has too many or overlapping nested items.");
                }
                if (PilotIndustryStore.IsInstalled(world, item.Id))
                {
                    throw new InvalidDataException("Deliver or cancel the manufacturing job before contracting its blueprint.");
                }
                world.Add(new SavedContractEscrow { ItemId = item.Id, ContractId = contract.Id });
                SaveItem(world, contract.Id, item, true, item.Id == root.Id ? null : item.LocationId);
            }
            contract.Volume += root.Volume * offer.Quantity;
        }
        if (contract.Type == ContractType.Courier)
        {
            contract.Volume = CourierRules.PackageVolume(contract.Volume);
        }
        foreach (var request in draft.RequestedItems)
        {
            var type =
                catalog.Set<ItemType>().Include(t => t.Group).SingleOrDefault(t => t.Id == request.TypeId)
                ?? throw new InvalidDataException("Unknown requested item type.");
            if (Enum.IsDefined((ContractRequestGroup)type.GroupId))
            {
                throw new InvalidDataException("This type cannot be requested on a contract.");
            }
            world.Add(
                new SavedContractItem
                {
                    ContractId = contract.Id,
                    TypeId = type.Id,
                    Quantity = request.Quantity,
                    GroupId = type.GroupId,
                    CategoryId = type.Group.CategoryId,
                    FlagId = (int)InventoryFlag.Hangar,
                }
            );
        }
        Pay(world, contract, pilotId, MarketDefinitions.SccCorporationId, WalletEntryType.ContractBrokersFee, -fees.BrokerCents, now);
        Pay(world, contract, pilotId, MarketDefinitions.SccCorporationId, WalletEntryType.ContractDeposit, -fees.DepositCents, now);
        Pay(world, contract, pilotId, MarketDefinitions.SccCorporationId, WalletEntryType.ContractRewardAdded, -draft.RewardCents, now);
        Change(world, contract.Id, pilotId, ContractChangeKind.Created, changes, Balance(world, pilotId));
        if (draft.AssigneeId is { } receiver)
        {
            Change(world, contract.Id, receiver, ContractChangeKind.Assigned, [], null);
        }
        return contract.Id;
    }

    private static void SaveItem(WorldDbContext world, long contract, InventoryItem item, bool offered, long? parent) =>
        world.Add(
            new SavedContractItem
            {
                ContractId = contract,
                ItemId = item.Id,
                TypeId = item.TypeId,
                Quantity = item.Quantity,
                Offered = offered,
                ParentId = parent,
                FlagId = item.FlagId,
                Singleton = item.Singleton,
                Damage =
                    item.CategoryId == (int)InventoryCategory.Ship ? world.Set<ShipHealthState>().Find(item.Id)?.StructureDamage ?? 0 : 0,
                GroupId = item.GroupId,
                CategoryId = item.CategoryId,
            }
        );

    public PersonalContract Accept(long pilotId, long contractId) =>
        Write(
            (world, catalog) =>
            {
                Pilot(world, pilotId);
                var contract = Contract(world, pilotId, contractId);
                long now = Now;
                if (contract.IssuerId == pilotId || contract.Status != ContractStatus.Outstanding || contract.Expires <= now)
                {
                    throw new InvalidDataException("This contract is no longer available for acceptance.");
                }
                if (contract.Type == ContractType.Courier)
                {
                    return AcceptCourier(world, catalog, contract, pilotId, now);
                }
                if (contract.Type != ContractType.ItemExchange)
                {
                    throw new InvalidDataException("Place a bid on this auction instead of accepting it.");
                }
                List<InventoryChange> issuerChanges = [];
                List<InventoryChange> acceptorChanges = [];
                var requested = world
                    .Set<SavedContractItem>()
                    .Where(i => i.ContractId == contractId && !i.Offered)
                    .OrderBy(i => i.Id)
                    .ToArray();
                foreach (var request in requested)
                {
                    int remaining = request.Quantity;
                    var stacks = world
                        .Set<OwnedItem>()
                        .Where(i =>
                            i.OwnerId == pilotId
                            && i.LocationId == contract.StationId
                            && i.FlagId == (int)InventoryFlag.Hangar
                            && i.TypeId == request.TypeId
                            && !i.Singleton
                            && !i.Online
                        )
                        .OrderBy(i => i.Id)
                        .ToArray();
                    foreach (var stack in stacks)
                    {
                        if (PilotIndustryStore.IsInstalled(world, stack.Id) || world.Set<OwnedItem>().Any(i => i.LocationId == stack.Id))
                        {
                            continue;
                        }
                        int quantity = Math.Min(remaining, stack.Quantity);
                        var before = PilotInventoryStore.Read(catalog, stack);
                        if (quantity == stack.Quantity)
                        {
                            stack.OwnerId = contract.IssuerId;
                            acceptorChanges.Add(new(before with { Quantity = 0 }, before));
                            issuerChanges.Add(new(PilotInventoryStore.Read(catalog, stack), null));
                        }
                        else
                        {
                            stack.Quantity -= quantity;
                            acceptorChanges.Add(new(PilotInventoryStore.Read(catalog, stack), before));
                            var output = world
                                .Add(
                                    new OwnedItem
                                    {
                                        Id = InventoryPersistence.NextId(world),
                                        OwnerId = contract.IssuerId,
                                        TypeId = stack.TypeId,
                                        LocationId = contract.StationId,
                                        FlagId = (int)InventoryFlag.Hangar,
                                        Quantity = quantity,
                                        CrystalDamage = stack.CrystalDamage,
                                    }
                                )
                                .Entity;
                            issuerChanges.Add(new(PilotInventoryStore.Read(catalog, output), null));
                        }
                        remaining -= quantity;
                        if (remaining == 0)
                        {
                            break;
                        }
                    }
                    if (remaining != 0)
                    {
                        throw new InvalidDataException("The requested packaged items are missing from your station hangar.");
                    }
                }
                foreach (var entry in world.Set<SavedContractEscrow>().Where(e => e.ContractId == contractId).ToArray())
                {
                    var transferred = Transfer(world, catalog, entry.ItemId, contract.IssuerId, pilotId);
                    acceptorChanges.Add(new(transferred, null));
                    world.Remove(entry);
                }
                Pay(world, contract, pilotId, contract.IssuerId, WalletEntryType.ContractPrice, -contract.PriceCents, now);
                Pay(world, contract, contract.IssuerId, pilotId, WalletEntryType.ContractPrice, contract.PriceCents, now);
                Pay(world, contract, pilotId, contract.IssuerId, WalletEntryType.ContractReward, contract.RewardCents, now);
                // Refund the held deposit before charging the quoted sales tax. Insufficient
                // issuer funds still reject the entire acceptance, including all asset moves.
                Pay(
                    world,
                    contract,
                    contract.IssuerId,
                    MarketDefinitions.SccCorporationId,
                    WalletEntryType.ContractDepositRefund,
                    contract.DepositCents,
                    now
                );
                Pay(
                    world,
                    contract,
                    contract.IssuerId,
                    MarketDefinitions.SccCorporationId,
                    WalletEntryType.ContractSalesTax,
                    -contract.SalesTaxCents,
                    now
                );
                contract.Status = ContractStatus.Finished;
                contract.AcceptorId = pilotId;
                contract.Accepted = now;
                contract.Completed = now;
                contract.Reclaimed = true;
                Change(world, contractId, contract.IssuerId, ContractChangeKind.Accepted, issuerChanges, Balance(world, contract.IssuerId));
                Change(world, contractId, pilotId, ContractChangeKind.Completed, acceptorChanges, Balance(world, pilotId));
                return contract.Snapshot();
            }
        );

    private static InventoryItem Transfer(WorldDbContext world, CatalogDbContext catalog, long itemId, long previousOwner, long owner)
    {
        var item = world.Set<OwnedItem>().IgnoreQueryFilters().SingleOrDefault(i => i.Id == itemId);
        if (item is not null)
        {
            if (item.OwnerId != previousOwner)
            {
                throw new InvalidDataException("An escrow item has changed ownership.");
            }
            item.OwnerId = owner;
            return PilotInventoryStore.Read(catalog, item);
        }
        var ship = world.Ships.IgnoreQueryFilters().AsNoTracking().Include(s => s.Position).SingleOrDefault(s => s.Id == itemId);
        if (ship is null || ship.OwnerId != previousOwner || ship.Position is not null || world.Pilots.Any(p => p.ActiveShipId == itemId))
        {
            throw new InvalidDataException("The escrow hull is unavailable.");
        }
        if (owner != previousOwner)
        {
            PilotInsuranceStore.VoidRemovedHull(world, itemId);
            // OwnerId participates in the alternate key that protects active-ship
            // ownership. EF treats that key as immutable; update the inactive hull
            // inside this transaction without replacing its health/storage identity.
            int changed = world
                .Ships.IgnoreQueryFilters()
                .Where(s => s.Id == itemId && s.OwnerId == previousOwner && !world.Pilots.Any(p => p.ActiveShipId == s.Id))
                .ExecuteUpdate(update => update.SetProperty(s => s.OwnerId, owner));
            if (changed != 1)
            {
                throw new InvalidDataException("The escrow hull could not be transferred.");
            }
        }
        ship.OwnerId = owner;
        return PilotFleetStore.Read(catalog, ship);
    }

    public void Reject(long pilotId, long contractId) =>
        Write(
            (world, _) =>
            {
                var contract = Contract(world, pilotId, contractId);
                if (contract.AssigneeId != pilotId || contract.Status != ContractStatus.Outstanding || contract.Expires <= Now)
                {
                    throw new InvalidDataException("Only the assigned pilot can reject an outstanding contract.");
                }
                if (contract.Type == ContractType.Auction && contract.AcceptorId is not null)
                {
                    throw new InvalidDataException("An auction with bids cannot be rejected.");
                }
                contract.Status = ContractStatus.Rejected;
                Change(world, contractId, contract.IssuerId, ContractChangeKind.Rejected, [], null);
                return true;
            }
        );

    public void Delete(long pilotId, long contractId) =>
        Write(
            (world, catalog) =>
            {
                var contract = Contract(world, pilotId, contractId);
                if (contract.IssuerId != pilotId)
                {
                    throw new InvalidDataException("Only the issuer can cancel or reclaim this contract.");
                }
                if (contract.Reclaimed)
                {
                    return true;
                }
                if (contract.Type == ContractType.Auction && contract.AcceptorId is not null)
                {
                    throw new InvalidDataException("Collect auction proceeds and goods through auction settlement.");
                }
                if (contract.Status is not (ContractStatus.Outstanding or ContractStatus.Rejected))
                {
                    throw new InvalidDataException("This contract cannot be reclaimed.");
                }
                List<InventoryChange> changes = [];
                foreach (var entry in world.Set<SavedContractEscrow>().Where(e => e.ContractId == contractId).ToArray())
                {
                    changes.Add(new(Transfer(world, catalog, entry.ItemId, pilotId, pilotId), null));
                    world.Remove(entry);
                }
                long now = Now;
                Pay(
                    world,
                    contract,
                    pilotId,
                    MarketDefinitions.SccCorporationId,
                    WalletEntryType.ContractRewardRefund,
                    contract.RewardCents,
                    now
                );
                contract.Status = contract.Expires <= now ? ContractStatus.Deleted : ContractStatus.Cancelled;
                contract.Completed = now;
                contract.Reclaimed = true;
                Change(world, contractId, pilotId, ContractChangeKind.Reclaimed, changes, Balance(world, pilotId));
                return true;
            }
        );

    public void Dismiss(long pilotId, long contractId) =>
        Write(
            (world, _) =>
            {
                var contract = Contract(world, pilotId, contractId);
                if (contract.IssuerId != pilotId || !contract.Reclaimed)
                {
                    throw new InvalidDataException("Collect the contract's escrow before dismissing its notification.");
                }
                contract.Dismissed = true;
                return true;
            }
        );

    private static SavedContract Contract(WorldDbContext world, long pilotId, long contractId)
    {
        var contract = world.Set<SavedContract>().Find(contractId);
        if (
            contract is null
            || (
                contract.AssigneeId is not null
                && contract.IssuerId != pilotId
                && contract.AssigneeId != pilotId
                && contract.AcceptorId != pilotId
            )
        )
        {
            throw new InvalidDataException("This contract is unavailable.");
        }
        return contract;
    }

    private static long Balance(WorldDbContext world, long pilotId) => world.Set<PilotWallet>().Find(pilotId)?.Cents ?? 0;

    private static void Pay(
        WorldDbContext world,
        SavedContract contract,
        long pilot,
        long other,
        WalletEntryType type,
        long amount,
        long now
    )
    {
        if (amount == 0)
        {
            return;
        }
        var wallet = world.Set<PilotWallet>().Find(pilot) ?? world.Add(new PilotWallet { PilotId = pilot }).Entity;
        long balance = checked(wallet.Cents + amount);
        if (balance < 0)
        {
            throw new InvalidDataException("There is not enough ISK to complete the contract.");
        }
        wallet.Cents = balance;
        world.Add(
            new SavedContractPayment
            {
                PilotId = pilot,
                CounterpartyId = other,
                ContractId = contract.Id,
                Type = type,
                AmountCents = amount,
                BalanceCents = balance,
                Time = now,
            }
        );
    }

    private static void Change(
        WorldDbContext world,
        long contract,
        long pilot,
        ContractChangeKind kind,
        IReadOnlyList<InventoryChange> inventory,
        long? balance
    ) =>
        world.Add(
            new SavedContractChange
            {
                ContractId = contract,
                PilotId = pilot,
                Kind = kind,
                InventoryJson = JsonSerializer.Serialize(inventory),
                BalanceCents = balance,
            }
        );

    public ContractDetails Details(long pilotId, long contractId) =>
        Read(
            (world, _) =>
            {
                Pilot(world, pilotId);
                return Details(world, Contract(world, pilotId, contractId));
            }
        );

    private static ContractDetails Details(WorldDbContext world, SavedContract contract) =>
        new(
            contract.Snapshot(),
            world
                .Set<SavedContractItem>()
                .Where(i => i.ContractId == contract.Id)
                .OrderBy(i => i.Id)
                .AsEnumerable()
                .Select(i => i.Snapshot())
                .ToArray(),
            world
                .Set<SavedContractBid>()
                .Where(b => b.ContractId == contract.Id)
                .OrderByDescending(b => b.AmountCents)
                .ThenByDescending(b => b.Id)
                .AsEnumerable()
                .Select(b => b.Snapshot())
                .ToArray()
        );

    public IReadOnlyList<ContractDetails> Available(long pilotId) =>
        Read(
            (world, _) =>
            {
                Pilot(world, pilotId);
                long now = Now;
                return world
                    .Set<SavedContract>()
                    .Where(c =>
                        c.Status == ContractStatus.Outstanding && c.Expires > now && (c.AssigneeId == null || c.AssigneeId == pilotId)
                    )
                    .OrderByDescending(c => c.Id)
                    .AsEnumerable()
                    .Select(c => Details(world, c))
                    .ToArray();
            }
        );

    public IReadOnlyList<ContractDetails> List(long pilotId, ContractOwnerFilter filter) =>
        Read(
            (world, _) =>
            {
                Pilot(world, pilotId);
                if (filter.Limit is < 1 or > ContractAssetRules.MaximumSelection || filter.BeforeId is <= 0)
                {
                    throw new InvalidDataException("Invalid contract page.");
                }
                var rows = world
                    .Set<SavedContract>()
                    .Where(c => c.AssigneeId == null || c.IssuerId == pilotId || c.AssigneeId == pilotId || c.AcceptorId == pilotId);
                rows =
                    filter.Status == (int)ContractStatus.RequiresAttention
                        ? rows.Where(c => c.IssuerId == filter.OwnerId || c.AcceptorId == filter.OwnerId)
                    : filter.Status == (int)ContractStatus.BidOnBy
                        ? rows.Where(c => world.Set<SavedContractBid>().Any(b => b.ContractId == c.Id && b.BidderId == filter.OwnerId))
                    : filter.IssuedTo switch
                    {
                        false => rows.Where(c => c.IssuerId == filter.OwnerId),
                        true => rows.Where(c => c.AssigneeId == filter.OwnerId || c.AcceptorId == filter.OwnerId),
                        null => rows.Where(c =>
                            c.IssuerId == filter.OwnerId || c.AssigneeId == filter.OwnerId || c.AcceptorId == filter.OwnerId
                        ),
                    };
                if (filter.BeforeId is { } before)
                {
                    rows = rows.Where(c => c.Id < before);
                }
                if (filter.Type is not (0 or 1 or 2 or 3 or 10 or 11))
                {
                    return [];
                }
                if (filter.Type is 1 or 2 or 3)
                {
                    rows = rows.Where(c => (int)c.Type == filter.Type);
                }
                else if (filter.Type == (int)ContractType.AuctionAndItemExchange)
                {
                    rows = rows.Where(c => c.Type == ContractType.ItemExchange || c.Type == ContractType.Auction);
                }
                if (filter.Status is >= 0 && filter.Status != (int)ContractStatus.BidOnBy)
                {
                    rows = rows.Where(c => (int)c.Status == filter.Status);
                }
                else if (filter.Status == (int)ContractStatus.RequiresAttention)
                {
                    long now = Now;
                    rows = rows.Where(c =>
                        c.Type == ContractType.Auction && c.AcceptorId != null
                            ? c.Expires <= now
                                && (
                                    (c.IssuerId == filter.OwnerId && !c.IssuerCollected)
                                    || (c.AcceptorId == filter.OwnerId && !c.WinnerCollected)
                                )
                            : c.IssuerId == filter.OwnerId
                                && !c.Dismissed
                                && (
                                    (c.Status == ContractStatus.Outstanding && c.Expires <= now)
                                    || c.Status == ContractStatus.Rejected
                                    || c.Status == ContractStatus.Finished
                                    || c.Status == ContractStatus.Failed
                                )
                    );
                }
                return rows.OrderByDescending(c => c.Id).Take(filter.Limit).AsEnumerable().Select(c => Details(world, c)).ToArray();
            }
        );

    public ContractPageInfo Page(long pilotId) =>
        Read<ContractPageInfo>(
            (world, _) =>
            {
                Pilot(world, pilotId);
                long now = Now;
                var rows = world
                    .Set<SavedContract>()
                    .Where(c => c.IssuerId == pilotId || c.AssigneeId == pilotId || c.AcceptorId == pilotId)
                    .ToArray();
                int bidding = world
                    .Set<SavedContract>()
                    .Count(c =>
                        c.Type == ContractType.Auction
                        && world.Set<SavedContractBid>().Any(b => b.ContractId == c.Id && b.BidderId == pilotId && !b.Dismissed)
                    );
                return new(
                    rows.Count(c =>
                        c.IssuerId == pilotId
                        && ((c.Status == ContractStatus.Outstanding && c.Expires > now) || c.Status == ContractStatus.InProgress)
                    ),
                    rows.Where(c => c.AssigneeId == pilotId && c.Status == ContractStatus.Outstanding && c.Expires > now)
                        .Select(c => c.Snapshot())
                        .ToArray(),
                    rows.Where(c =>
                            c.Type == ContractType.Auction && c.AcceptorId is not null
                                ? c.Expires <= now
                                    && ((c.IssuerId == pilotId && !c.IssuerCollected) || (c.AcceptorId == pilotId && !c.WinnerCollected))
                                : c.IssuerId == pilotId
                                    && !c.Dismissed
                                    && (
                                        (c.Status == ContractStatus.Outstanding && c.Expires <= now)
                                        || c.Status is ContractStatus.Rejected or ContractStatus.Finished or ContractStatus.Failed
                                    )
                        )
                        .Select(c => c.Id)
                        .ToArray(),
                    rows.Where(c => c.Status == ContractStatus.InProgress && c.AcceptorId == pilotId).Select(c => c.Snapshot()).ToArray(),
                    bidding
                );
            }
        );

    public ContractEscrow Escrow(long pilotId) =>
        Read<ContractEscrow>(
            (world, catalog) =>
            {
                Pilot(world, pilotId);
                long isk =
                    world
                        .Set<SavedContract>()
                        .Where(c =>
                            c.IssuerId == pilotId && (c.Type == ContractType.Auction ? !c.IssuerCollected && !c.Reclaimed : !c.Reclaimed)
                        )
                        .Sum(c => (long?)(c.RewardCents + c.DepositCents))
                    ?? 0;
                isk = checked(
                    isk
                    + (
                        world
                            .Set<SavedContract>()
                            .Where(c => c.AcceptorId == pilotId && c.Status == ContractStatus.InProgress)
                            .Sum(c => (long?)c.CollateralCents)
                        ?? 0
                    )
                );
                isk = checked(
                    isk
                    + (
                        world
                            .Set<SavedContract>()
                            .Where(c => c.Type == ContractType.Auction && c.AcceptorId == pilotId && !c.IssuerCollected)
                            .Sum(c => (long?)c.CurrentBidCents)
                        ?? 0
                    )
                );
                var held = world
                    .Set<SavedContractEscrow>()
                    .Where(e => world.Set<SavedContract>().Any(c => c.Id == e.ContractId && c.IssuerId == pilotId && !c.Reclaimed));
                double value = world
                    .Set<SavedContractItem>()
                    .Where(i => i.Offered && held.Any(e => e.ItemId == i.ItemId && e.ContractId == i.ContractId))
                    .AsEnumerable()
                    .Sum(i =>
                        (
                            catalog
                                .Set<SourceRecord>()
                                .Find("client/types", i.TypeId.ToString(CultureInfo.InvariantCulture))
                                ?.Read<ClientTypes>()
                                .BasePrice
                            ?? 0
                        ) * i.Quantity
                    );
                return new(isk, value);
            }
        );

    public long Observe(long pilotId) =>
        Read(
            (world, _) =>
            {
                Pilot(world, pilotId);
                return world.Set<SavedContractChange>().Where(c => c.PilotId == pilotId).Select(c => (long?)c.Id).Max() ?? 0;
            }
        );

    public ContractChanges Changes(long pilotId, long after) =>
        Read<ContractChanges>(
            (world, _) =>
            {
                Pilot(world, pilotId);
                if (after < 0)
                {
                    throw new InvalidDataException("Invalid contract change cursor.");
                }
                var rows = world
                    .Set<SavedContractChange>()
                    .Where(c => c.PilotId == pilotId && c.Id > after)
                    .OrderBy(c => c.Id)
                    .Take(ContractRules.ChangeBatchSize + 1)
                    .ToArray();
                var page = rows.Take(ContractRules.ChangeBatchSize).ToArray();
                long now = Now;
                var assigned = page.Where(c => c.Kind == ContractChangeKind.Assigned).Select(c => c.ContractId).ToArray();
                var active = world
                    .Set<SavedContract>()
                    .Where(c =>
                        assigned.Contains(c.Id) && c.AssigneeId == pilotId && c.Status == ContractStatus.Outstanding && c.Expires > now
                    )
                    .Select(c => c.Id)
                    .ToHashSet();
                var batch = page.Where(c => c.Kind != ContractChangeKind.Assigned || active.Contains(c.ContractId))
                    .Select(c => new ContractChange(
                        c.Id,
                        c.ContractId,
                        c.Kind,
                        JsonSerializer.Deserialize<InventoryChange[]>(c.InventoryJson) ?? [],
                        c.BalanceCents
                    ))
                    .ToArray();
                return new(batch, page.LastOrDefault()?.Id ?? after, rows.Length > page.Length);
            }
        );
}
