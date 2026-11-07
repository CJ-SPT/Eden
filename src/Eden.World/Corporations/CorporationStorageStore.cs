using System.Text.Json;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Industry;
using Eden.World.Insurance;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Corporations;

public sealed class SavedCorporationOffice
{
    public long Id { get; set; }
    public int CorporationId { get; set; }
    public int StationId { get; set; }
    public int SystemId { get; set; }
    public long RentedAt { get; set; }
    public long FeeCents { get; set; }

    public CorporationOffice Snapshot() => new(Id, CorporationId, StationId, SystemId, RentedAt, FeeCents);
}

public sealed class SavedCorporationOfficeSettings
{
    public int Id { get; set; }
    public long RentalFeeCents { get; set; }
    public int OfficeSlots { get; set; }
}

[Injectable]
internal sealed class CorporationStorageStore(WorldDatabase database, TimeProvider clock) : ICorporationStorage
{
    public IReadOnlyList<CorporationOffice> Offices(long actorId)
    {
        using WorldDbContext world = new(database.Path);
        var pilot = world.Pilots.Find(actorId) ?? throw new InvalidDataException("Unknown pilot.");
        return world
            .Set<SavedCorporationOffice>()
            .Where(o => o.CorporationId == pilot.CorporationId)
            .OrderBy(o => o.StationId)
            .AsEnumerable()
            .Select(o => o.Snapshot())
            .ToArray();
    }

    public IReadOnlyList<int> Tenants(long actorId)
    {
        using WorldDbContext world = new(database.Path);
        int station = DockedStation(world, actorId);
        return world
            .Set<SavedCorporationOffice>()
            .Where(o => o.StationId == station)
            .OrderBy(o => o.CorporationId)
            .Select(o => o.CorporationId)
            .ToArray();
    }

    public int EmptyOffices(long actorId)
    {
        using WorldDbContext world = new(database.Path);
        int station = DockedStation(world, actorId);
        return Math.Max(0, Settings(world).OfficeSlots - world.Set<SavedCorporationOffice>().Count(o => o.StationId == station));
    }

    public long Quote(long actorId)
    {
        using WorldDbContext world = new(database.Path);
        DockedStation(world, actorId);
        CorporationRoleStore.Current(world, actorId);
        return Settings(world).RentalFeeCents;
    }

    public CorporationOffice Rent(long actorId, long quotedCents)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        int station = DockedStation(world, actorId);
        var corporation = RentalAuthority(world, actorId);
        var settings = Settings(world);
        if (quotedCents != settings.RentalFeeCents || quotedCents < 0)
        {
            throw new InvalidDataException("The office rental quote changed; request a new quote.");
        }
        if (
            world.Set<SavedCorporationOffice>().Any(o => o.CorporationId == corporation.Id && o.StationId == station)
            || world.Set<SavedCorporationOffice>().Count(o => o.StationId == station) >= settings.OfficeSlots
        )
        {
            throw new InvalidDataException("Your corporation already rents an office here or no office is available.");
        }
        if (corporation.BalanceCents < quotedCents)
        {
            throw new InvalidDataException("The corporation's master wallet cannot cover the rental fee.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var location = catalog.Set<Station>().Single(s => s.Id == station);
        long last = world
            .Database.SqlQueryRaw<long>("SELECT COALESCE(MAX(seq),0) AS Value FROM sqlite_sequence WHERE name='WorldCorporationOffice'")
            .AsEnumerable()
            .Single();
        long id = Math.Max(CorporationStorageRules.FirstOfficeId, checked(last + 1));
        if (id > CorporationStorageRules.LastOfficeId)
        {
            throw new InvalidDataException("The office identifier range is exhausted.");
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var office = world
            .Add(
                new SavedCorporationOffice
                {
                    Id = id,
                    CorporationId = corporation.Id,
                    StationId = station,
                    SystemId = location.SolarSystemId,
                    RentedAt = now,
                    FeeCents = quotedCents,
                }
            )
            .Entity;
        corporation.BalanceCents -= quotedCents;
        CorporationWalletStore.AddEntry(
            world,
            corporation.Id,
            CorporationWalletRules.MasterKey,
            now,
            location.OwnerId,
            -quotedCents,
            corporation.BalanceCents,
            WalletEntryType.OfficeRentalFee,
            id,
            "Office rental"
        );
        Notify(world, new(actorId, office.Snapshot(), true, []));
        world.SaveChanges();
        transaction.Commit();
        return office.Snapshot();
    }

    public CorporationOffice Unrent(long actorId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        int station = DockedStation(world, actorId);
        var corporation = RentalAuthority(world, actorId);
        var office =
            world.Set<SavedCorporationOffice>().SingleOrDefault(o => o.CorporationId == corporation.Id && o.StationId == station)
            ?? throw new InvalidDataException("Your corporation does not rent an office here.");
        if (world.Set<OwnedItem>().Any(i => i.LocationId == office.Id) || world.Ships.Any(s => s.StoredInId == office.Id))
        {
            throw new InvalidDataException("Empty every hangar division before unrenting the office.");
        }
        Notify(world, new(actorId, office.Snapshot(), true, []));
        world.Remove(office);
        world.SaveChanges();
        transaction.Commit();
        return office.Snapshot();
    }

    public CorporationOffice Office(long actorId, long officeId)
    {
        using WorldDbContext world = new(database.Path);
        return LocalOffice(world, actorId, officeId).Snapshot();
    }

    public IReadOnlyList<InventoryItem> List(long actorId, long officeId, int? flag)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var office = LocalOffice(world, actorId, officeId);
        var corporation = CorporationRoleStore.Current(world, actorId);
        var access = CorporationRoleStore.Effective(world, actorId, corporation);
        if (flag.HasValue && !CorporationStorageRules.CanQuery(access, office.StationId, corporation.StationId, flag.Value))
        {
            throw new InvalidDataException("You cannot view this corporation hangar division.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return PilotInventoryStore
            .List(world, catalog, corporation.Id, officeId)
            .Where(i =>
                (!flag.HasValue || i.FlagId == flag)
                && CorporationStorageRules.CanQuery(access, office.StationId, corporation.StationId, i.FlagId)
            )
            .ToArray();
    }

    public bool CanObserve(long actorId, long officeId, int flag)
    {
        using WorldDbContext world = new(database.Path);
        var pilot = world.Pilots.Find(actorId);
        var office = world.Set<SavedCorporationOffice>().Find(officeId);
        var corporation = office is null ? null : world.Set<SavedCorporation>().Find(office.CorporationId);
        return pilot is not null
            && office is not null
            && corporation is not null
            && pilot.CorporationId == corporation.Id
            && CorporationStorageRules.CanQuery(
                CorporationRoleStore.Effective(world, actorId, corporation),
                office.StationId,
                corporation.StationId,
                flag
            );
    }

    public IReadOnlyList<InventoryItem> Assets(long actorId, int corporationId, int stationId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var corporation = CorporationRoleStore.Current(world, actorId);
        if (corporation.Id != corporationId)
        {
            throw new InvalidDataException("Read assets for your current corporation.");
        }
        var access = CorporationRoleStore.Effective(world, actorId, corporation);
        var office = world.Set<SavedCorporationOffice>().SingleOrDefault(o => o.CorporationId == corporationId && o.StationId == stationId);
        if (office is null)
        {
            return [];
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return PilotInventoryStore
            .List(world, catalog, corporationId, office.Id)
            .Where(i => CorporationStorageRules.CanQuery(access, stationId, corporation.StationId, i.FlagId))
            .ToArray();
    }

    public IReadOnlyList<InventoryChange> Move(long actorId, long destinationId, InventoryMoveRequest request)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        int station = DockedStation(world, actorId);
        var corporation = CorporationRoleStore.Current(world, actorId);
        var access = CorporationRoleStore.Effective(world, actorId, corporation);
        var office =
            world.Set<SavedCorporationOffice>().SingleOrDefault(o => o.CorporationId == corporation.Id && o.StationId == station)
            ?? throw new InvalidDataException("Your corporation does not rent an office here.");
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == actorId);
        bool fromOffice = request.SourceId == office.Id;
        bool toOffice = destinationId == office.Id;
        if (
            (!fromOffice && !toOffice)
            || request.ItemIds.Count is 0 or > CorporationStorageRules.MaximumBatch
            || request.ItemIds.Distinct().Count() != request.ItemIds.Count
            || request.Quantity is <= 0
            || (request.Quantity.HasValue && request.ItemIds.Count != 1)
            || (!fromOffice && request.SourceId != station && request.SourceId != pilot.ActiveShipId)
            || (!toOffice && destinationId != station && destinationId != pilot.ActiveShipId)
            || (
                toOffice ? !CorporationStorageRules.IsDivision(request.FlagId)
                : destinationId == station ? request.FlagId != (int)InventoryFlag.Hangar
                : !ShipHoldRules.IsHold(request.FlagId)
            )
        )
        {
            throw new InvalidDataException("Choose local personal cargo or hangar items and a corporation hangar division.");
        }
        long sourceOwner = fromOffice ? corporation.Id : actorId;
        long targetOwner = toOffice ? corporation.Id : actorId;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var source = (
            fromOffice
                ? PilotInventoryStore.List(world, catalog, sourceOwner, office.Id)
                : PilotInventoryStore
                    .List(world, catalog, sourceOwner, request.SourceId)
                    .Concat(
                        request.SourceId == station
                            ? world
                                .Ships.Include(s => s.Position)
                                .Where(s => s.OwnerId == actorId && s.StationId == station && s.StoredInId == null && s.Position == null)
                                .AsEnumerable()
                                .Select(s => PilotFleetStore.Read(catalog, s))
                            : []
                    )
        )
            .Where(i => request.ItemIds.Contains(i.Id))
            .ToArray();
        if (source.Length != request.ItemIds.Count)
        {
            throw new InvalidDataException("One or more requested assets are unavailable.");
        }
        foreach (var item in source)
        {
            if (
                (
                    fromOffice
                    && (
                        !CorporationStorageRules.CanQuery(access, station, corporation.StationId, item.FlagId)
                        || !CorporationStorageRules.CanTake(access, station, corporation.StationId, item.FlagId)
                    )
                )
                || (
                    !fromOffice
                    && (request.SourceId == station ? item.FlagId != (int)InventoryFlag.Hangar : !ShipHoldRules.IsHold(item.FlagId))
                )
                || request.Quantity > item.Quantity
                || (item.Singleton && request.Quantity is not null and not 1)
                || item.TypeId == CourierRules.PackageType
                || PilotIndustryStore.IsInstalled(world, item.Id)
            )
            {
                throw new InvalidDataException("The source asset, quantity or hangar withdrawal permission is unavailable.");
            }
        }
        if (!toOffice && destinationId == pilot.ActiveShipId)
        {
            var hold = ShipHoldRules.Definition(request.FlagId)!;
            double capacity = PilotInventoryStore.HoldCapacity(world, catalog, actorId, destinationId, request.FlagId);
            if (source.Any(i => !hold.Allows(i) || i.Singleton && i.CategoryId == (int)InventoryCategory.Ship))
            {
                throw new InvalidDataException("This ship hold does not accept the requested assets.");
            }
            double used = InventoryPersistence.UsedVolume(world, catalog, actorId, destinationId, request.FlagId);
            double required = source.Sum(i => hold.RequiredCapacity(i, request.Quantity ?? i.Quantity));
            if (used + required > capacity)
            {
                throw new CargoCapacityException(Math.Max(0, capacity - used), required);
            }
        }
        List<InventoryChange> changes = [];
        foreach (var before in source)
        {
            if (world.Ships.Find(before.Id) is { } hull)
            {
                if (
                    hull.Position is not null
                    || world.Pilots.Any(p => p.ActiveShipId == hull.Id)
                    || world.Ships.Any(s => s.StoredInId == hull.Id)
                )
                {
                    throw new InvalidDataException("Only inactive ships without stored hulls can change corporation storage.");
                }
                var contents = ShipStoragePersistence.Contents(world, sourceOwner, hull.Id);
                var contentIds = contents.Select(i => i.Id).ToArray();
                if (
                    contents.Any(i => i.TypeId == CourierRules.PackageType || PilotIndustryStore.IsInstalled(world, i.Id))
                    || world
                        .Set<OwnedItem>()
                        .Any(i =>
                            (i.LocationId == hull.Id || contentIds.Contains(i.LocationId))
                            && (i.OwnerId != sourceOwner || !contentIds.Contains(i.Id))
                        )
                )
                {
                    throw new InvalidDataException("The ship contains assets that cannot be transferred.");
                }
                // The active-ship ownership constraint uses (Id, OwnerId) as an alternate key.
                // EF keys are immutable; update this inactive hull in-place within our transaction.
                world.Entry(hull).State = EntityState.Detached;
                long? container = toOffice ? office.Id : null;
                int changed = world
                    .Ships.Where(s => s.Id == hull.Id && s.OwnerId == sourceOwner && !world.Pilots.Any(p => p.ActiveShipId == s.Id))
                    .ExecuteUpdate(update =>
                        update
                            .SetProperty(s => s.OwnerId, targetOwner)
                            .SetProperty(s => s.StoredInId, container)
                            .SetProperty(s => s.StorageFlagId, request.FlagId)
                            .SetProperty(s => s.StationId, station)
                    );
                if (changed != 1)
                {
                    throw new InvalidDataException("The inactive ship could not be transferred.");
                }
                hull.OwnerId = targetOwner;
                hull.StoredInId = container;
                hull.StorageFlagId = request.FlagId;
                hull.StationId = station;
                foreach (var child in contents)
                {
                    child.OwnerId = targetOwner;
                }
                if (sourceOwner != targetOwner)
                {
                    PilotInsuranceStore.VoidRemovedHull(world, hull.Id);
                }
                changes.Add(new(PilotFleetStore.Read(catalog, hull), before));
            }
            else
            {
                var item = world.Set<OwnedItem>().Find(before.Id)!;
                // Sealed contract packages and nested containers require their own ownership lifecycle.
                if (world.Set<OwnedItem>().Any(i => i.LocationId == item.Id))
                {
                    throw new InvalidDataException("Empty containers before transferring them to corporation storage.");
                }
                int quantity = request.Quantity ?? item.Quantity;
                if (quantity < item.Quantity)
                {
                    item.Quantity = InventoryStackRules.Split(before, quantity).Remainder;
                    changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
                    var split = world
                        .Add(
                            new OwnedItem
                            {
                                Id = InventoryPersistence.NextId(world),
                                OwnerId = targetOwner,
                                TypeId = item.TypeId,
                                LocationId = destinationId,
                                FlagId = request.FlagId,
                                Quantity = quantity,
                                Singleton = item.Singleton,
                                VolumeOverride = item.VolumeOverride,
                                CrystalDamage = item.CrystalDamage,
                            }
                        )
                        .Entity;
                    changes.Add(new(PilotInventoryStore.Read(catalog, split), null));
                }
                else
                {
                    item.OwnerId = targetOwner;
                    item.LocationId = destinationId;
                    item.FlagId = request.FlagId;
                    changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
                }
            }
        }
        Notify(world, new(actorId, office.Snapshot(), false, changes));
        world.SaveChanges();
        transaction.Commit();
        return changes;
    }

    public IReadOnlyList<InventoryChange> Stack(long actorId, long officeId, int flag)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var office = LocalOffice(world, actorId, officeId);
        var corporation = CorporationRoleStore.Current(world, actorId);
        var access = CorporationRoleStore.Effective(world, actorId, corporation);
        if (
            !CorporationStorageRules.CanQuery(access, office.StationId, corporation.StationId, flag)
            || !CorporationStorageRules.CanTake(access, office.StationId, corporation.StationId, flag)
        )
        {
            throw new InvalidDataException("Viewing and withdrawal rights are required to consolidate this division.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        List<InventoryChange> changes = [];
        var groups = world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == corporation.Id && i.LocationId == officeId && i.FlagId == flag && !i.Singleton)
            .OrderBy(i => i.Id)
            .AsEnumerable()
            .Where(i =>
                !i.Online && !PilotIndustryStore.IsInstalled(world, i.Id) && !world.Set<OwnedItem>().Any(child => child.LocationId == i.Id)
            )
            .GroupBy(i => new
            {
                i.TypeId,
                i.VolumeOverride,
                i.CrystalDamage,
            })
            .ToArray();
        foreach (var group in groups.Where(g => g.Count() > 1))
        {
            var target = group.First();
            var before = PilotInventoryStore.Read(catalog, target);
            target.Quantity = InventoryStackRules.CombinedQuantity(group.Select(i => i.Quantity));
            changes.Add(new(PilotInventoryStore.Read(catalog, target), before));
            foreach (var removed in group.Skip(1))
            {
                var old = PilotInventoryStore.Read(catalog, removed);
                changes.Add(new(old with { Quantity = 0 }, old));
                world.Remove(removed);
            }
        }
        Notify(world, new(actorId, office.Snapshot(), false, changes));
        world.SaveChanges();
        transaction.Commit();
        return changes;
    }

    private static SavedCorporationOfficeSettings Settings(WorldDbContext world) => world.Set<SavedCorporationOfficeSettings>().Find(1)!;

    private static int DockedStation(WorldDbContext world, long actorId)
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == actorId);
        return pilot?.ActiveShip is { Position: null } ship
            ? ship.StationId
            : throw new InvalidDataException("Dock at the office's station before using corporation storage.");
    }

    private static SavedCorporation RentalAuthority(WorldDbContext world, long actorId)
    {
        var corporation = CorporationRoleStore.Current(world, actorId);
        if (!CorporationRoleStore.Has(world, actorId, corporation, CorporationRole.CanRentOffice))
        {
            throw new InvalidDataException("Office rental authority is required.");
        }
        return corporation;
    }

    private static SavedCorporationOffice LocalOffice(WorldDbContext world, long actorId, long officeId)
    {
        int station = DockedStation(world, actorId);
        var corporation = CorporationRoleStore.Current(world, actorId);
        return
            world.Set<SavedCorporationOffice>().Find(officeId) is { } office
            && office.CorporationId == corporation.Id
            && office.StationId == station
            ? office
            : throw new InvalidDataException("This corporation office is unavailable at your current station.");
    }

    private static void Notify(WorldDbContext world, CorporationStorageChange change)
    {
        if (!change.RentalChanged && change.Items.Count == 0)
        {
            return;
        }
        string json = JsonSerializer.Serialize(change);
        foreach (long member in world.Pilots.Where(p => p.CorporationId == change.Office.CorporationId).Select(p => p.Id))
        {
            world.Add(new SavedCorporationChange { PilotId = member, StorageJson = json });
        }
    }
}
