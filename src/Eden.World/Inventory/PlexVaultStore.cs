using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Inventory;

[Injectable]
internal sealed class PlexVaultStore(WorldDatabase database, TimeProvider clock) : IPlexVault
{
    public long Balance(long accountId) =>
        accountId > 0 && LiveWorld.Find(database.Path) is { } live
            ? live.Economy.Inspect(world => world.Set<Account>().Find(accountId)?.PlexBalance ?? 0)
            : 0;

    public IReadOnlyList<PlexVaultEntry> History(long accountId) =>
        accountId > 0 && LiveWorld.Find(database.Path) is { } live ? live.Economy.VaultHistory(accountId) : [];

    public PlexVaultEntry? HistoryEntry(long accountId, long id) =>
        accountId > 0 && id > 0 && LiveWorld.Find(database.Path) is { } live ? live.Economy.VaultHistoryEntry(accountId, id) : null;

    public InventoryChange Deposit(long pilotId, long? locationId, long itemId, int quantity)
    {
        using var world = Resident().Economy.Begin(new(pilots: [pilotId]));
        var pilot = world.Pilots.Find(pilotId) ?? throw new PlexVaultRejected(PlexVaultFailure.Account);
        var item = InventoryPersistence.OwnedStack(world, pilotId, itemId);
        if (item is not { TypeId: MarketDefinitions.PlexTypeId, Singleton: false } || quantity <= 0 || quantity > item.Quantity)
        {
            throw new PlexVaultRejected(PlexVaultFailure.Amount);
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var ship = pilot.ActiveShip;
        bool currentDock = ship.Position is null && locationId == ship.DockedLocationId;
        bool hangar =
            item.FlagId == (int)InventoryFlag.Hangar
            && locationId == item.LocationId
            && (currentDock || catalog.FindStation(item.LocationId) is not null);
        bool cargo =
            item.FlagId == (int)InventoryFlag.Cargo
            && item.LocationId == ship.Id
            && (currentDock || ship.Position is not null && locationId is null);
        if (!hangar && !cargo)
        {
            throw new PlexVaultRejected(PlexVaultFailure.Location);
        }
        if (cargo)
        {
            world.RequireUncloaked(ship.Id);
        }
        var account = world.Set<Account>().Find(pilot.AccountId)!;
        if (account.PlexBalance > PlexVaultRules.MaximumBalance - quantity)
        {
            throw new PlexVaultRejected(PlexVaultFailure.Amount);
        }
        account.PlexBalance += quantity;
        var before = PilotInventoryStore.Read(catalog, item);
        item.Quantity -= quantity;
        if (item.Quantity == 0)
        {
            world.Remove(item);
        }
        Record(world, pilot, account.PlexBalance, PlexVaultTransferKind.Deposit, quantity, Now);
        world.Commit();
        return new(before with { Quantity = before.Quantity - quantity }, before);
    }

    public InventoryChange Withdraw(long pilotId, long locationId, int flagId, int quantity, bool forMarket)
    {
        using var world = Resident().Economy.Begin(new(pilots: [pilotId]));
        var pilot = world.Pilots.Find(pilotId) ?? throw new PlexVaultRejected(PlexVaultFailure.Account);
        var account = world.Set<Account>().Find(pilot.AccountId)!;
        if (quantity <= 0 || quantity > account.PlexBalance)
        {
            throw new PlexVaultRejected(quantity <= 0 ? PlexVaultFailure.Amount : PlexVaultFailure.Funds);
        }
        var ship = pilot.ActiveShip;
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (flagId == (int)InventoryFlag.Cargo && locationId == ship.Id && !forMarket)
        {
            world.RequireUncloaked(ship.Id);
            double capacity = PilotInventoryStore.HoldCapacity(world, catalog, pilotId, ship.Id, flagId);
            double used = InventoryPersistence.UsedVolume(world, catalog, pilotId, ship.Id, flagId);
            double required = quantity * PilotInventoryStore.Volume(catalog, MarketDefinitions.PlexTypeId);
            if (required > capacity - used + InventoryDefinitions.ResourceTolerance)
            {
                throw new PlexVaultRejected(PlexVaultFailure.Location);
            }
        }
        else if (
            flagId != (int)InventoryFlag.Hangar
            || locationId != ship.DockedLocationId
            || ship.Position is not null
            || forMarket && ship.StructureId is not null
        )
        {
            throw new PlexVaultRejected(PlexVaultFailure.Location);
        }
        account.PlexBalance -= quantity;
        var change = InventoryPersistence.MergeOutput(world, catalog, pilotId, locationId, flagId, MarketDefinitions.PlexTypeId, quantity);
        Record(
            world,
            pilot,
            account.PlexBalance,
            forMarket ? PlexVaultTransferKind.MarketWithdrawal : PlexVaultTransferKind.InventoryWithdrawal,
            -quantity,
            Now
        );
        world.Commit();
        return change;
    }

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    internal static void Record(
        EconomyTransaction world,
        Pilot pilot,
        long balance,
        PlexVaultTransferKind kind,
        long amount,
        long now,
        long? otherPilotId = null
    ) =>
        world.Add(
            new SavedPlexVaultEntry
            {
                Id = world.Next(PlexVaultSchema.HistoryTable),
                AccountId = pilot.AccountId,
                PilotId = pilot.Id,
                Kind = kind,
                Amount = amount,
                Balance = balance,
                Time = now,
                CounterpartyPilotId = otherPilotId,
            }
        );

    private LiveWorld Resident() => LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("A resident world is required.");
}
