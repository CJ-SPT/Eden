using System.Globalization;
using Eden.Protocol;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Loot;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class WorldWreck
{
    public long Id { get; set; }
    public long NpcId { get; set; }
    public long OwnerId { get; set; }
    public int CorporationId { get; set; }
    public int TypeId { get; set; }
    public int SystemId { get; set; }
    public required string Name { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
}

[Injectable]
internal sealed class WreckStore(WorldDatabase database) : IWrecks
{
    // Called within the fatal weapon transaction: a failed catalog lookup or
    // bounty debit rolls back the wreck and its contents along with the kill.
    internal static void Create(WorldDbContext world, CatalogDbContext catalog, WorldNpc npc, Pilot pilot, long now)
    {
        var definition = catalog.Set<SourceRecord>().Find("client/types", npc.TypeId.ToString(CultureInfo.InvariantCulture))?.Read<ClientTypes>();
        var type = definition?.WreckTypeID is long wreckType
            ? catalog.Set<ItemType>().SingleOrDefault(t => t.Id == wreckType && t.GroupId == WreckDefinitions.Group) : null;
        if (type is null) { throw new InvalidDataException("The defeated NPC has no supported catalog wreck."); }
        _ = catalog.Set<ItemType>().Single(t => t.Id == WreckDefinitions.StarterLootType);
        var wreck = world.Set<WorldWreck>().Add(new()
        {
            Id = checked(WreckDefinitions.FirstWreckId + npc.Id - NpcEncounterDefinitions.FirstNpcId),
            NpcId = npc.Id,
            OwnerId = pilot.Id,
            CorporationId = pilot.CorporationId,
            TypeId = type.Id,
            SystemId = npc.SystemId,
            Name = catalog.Set<ItemType>().Single(t => t.Id == npc.TypeId).Name + " " + type.Name,
            X = npc.X,
            Y = npc.Y,
            Z = npc.Z,
            CreatedAt = now,
            ExpiresAt = checked(now + WreckDefinitions.LifetimeSeconds * TimeSpan.TicksPerSecond),
        }).Entity;
        world.Set<OwnedItem>().Add(new()
        {
            Id = PilotInventoryStore.NextId(world),
            OwnerId = pilot.Id,
            TypeId = WreckDefinitions.StarterLootType,
            LocationId = wreck.Id,
            FlagId = (int)InventoryFlag.Cargo,
            Quantity = WreckDefinitions.StarterLootQuantity,
        });
    }

    public IReadOnlyList<WreckContact> List(int systemId, long now)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        // Expired containers cannot be opened, even before this lazy cleanup.
        world.Set<OwnedItem>().Where(i => world.Set<WorldWreck>().Where(w => w.ExpiresAt <= now).Select(w => w.Id).Contains(i.LocationId)).ExecuteDelete();
        var wrecks = world.Set<WorldWreck>().AsNoTracking().Where(w => w.SystemId == systemId && w.ExpiresAt > now).OrderBy(w => w.Id).ToArray();
        var occupied = world.Set<OwnedItem>().Where(i => wrecks.Select(w => w.Id).Contains(i.LocationId)).Select(i => i.LocationId).Distinct().ToHashSet();
        return wrecks.Select(w =>
        {
            var type = catalog.Set<ItemType>().Single(t => t.Id == w.TypeId);
            return new WreckContact(w.Id, w.NpcId, w.OwnerId, w.CorporationId, w.TypeId, w.Name, w.SystemId, new(w.X, w.Y, w.Z),
                type.Mass ?? 0, (float)(type.Radius ?? 0), !occupied.Contains(w.Id));
        }).ToArray();
    }

    private static (Pilot Pilot, WorldWreck Wreck) Access(WorldDbContext world, CatalogDbContext catalog, long pilotId, long wreckId, long now)
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var wreck = world.Set<WorldWreck>().Find(wreckId);
        if (wreck is null || wreck.ExpiresAt <= now || wreck.OwnerId != pilotId || pilot.ActiveShip.Position is not { } position ||
            (position.SystemId ?? catalog.Set<Station>().Single(s => s.Id == pilot.ActiveShip.StationId).SolarSystemId) != wreck.SystemId)
        {
            throw new InvalidDataException("The wreck is unavailable or you do not have its loot rights.");
        }
        var hull = catalog.Set<ItemType>().Single(t => t.Id == pilot.ActiveShip.TypeId);
        var type = catalog.Set<ItemType>().Single(t => t.Id == wreck.TypeId);
        if (new SpacePosition(position.X, position.Y, position.Z).DistanceTo(new(wreck.X, wreck.Y, wreck.Z)) - (hull.Radius ?? 0) - (type.Radius ?? 0) > WreckDefinitions.TransferRange)
        {
            throw new InvalidDataException("Approach within 2,500 m of the wreck to transfer its contents.");
        }
        return (pilot, wreck);
    }

    public WreckInventory Read(long pilotId, long wreckId, long now)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var (_, wreck) = Access(world, catalog, pilotId, wreckId, now);
        return new(new(wreck.Id, wreck.TypeId, wreck.OwnerId, wreck.SystemId, (int)InventoryFlag.AutoFit, 1,
            WreckDefinitions.Group, WreckDefinitions.Category, PilotInventoryStore.Volume(catalog, wreck.TypeId), true),
            world.Set<OwnedItem>().Where(i => i.LocationId == wreckId && i.OwnerId == pilotId).OrderBy(i => i.Id)
                .AsEnumerable().Select(i => PilotInventoryStore.Read(catalog, i)).ToArray());
    }

    public IReadOnlyList<InventoryChange> Take(long pilotId, long shipId, long wreckId, IReadOnlyList<long> itemIds, int? quantity, long now)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var (pilot, _) = Access(world, catalog, pilotId, wreckId, now);
        if (pilot.ActiveShipId != shipId || itemIds.Count == 0 || itemIds.Distinct().Count() != itemIds.Count || quantity is <= 0 || quantity.HasValue && itemIds.Count != 1)
        {
            throw new InvalidDataException("Select available wreck contents and a valid quantity for your active ship's cargo.");
        }
        var items = world.Set<OwnedItem>().Where(i => itemIds.Contains(i.Id) && i.OwnerId == pilotId && i.LocationId == wreckId).OrderBy(i => i.Id).ToArray();
        if (items.Length != itemIds.Count || items.Any(i => i.Quantity <= 0 || quantity > i.Quantity)) { throw new InvalidDataException("The requested wreck contents are no longer available."); }
        double capacity = PilotInventoryStore.HoldCapacity(catalog, pilot.ActiveShip.TypeId, (int)InventoryFlag.Cargo);
        double used = world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && i.LocationId == shipId && i.FlagId == (int)InventoryFlag.Cargo)
            .AsEnumerable().Sum(i => PilotInventoryStore.Volume(catalog, i.TypeId) * i.Quantity);
        double added = items.Sum(i => PilotInventoryStore.Volume(catalog, i.TypeId) * (quantity ?? i.Quantity));
        if (added > capacity - used + InventoryDefinitions.ResourceTolerance) { throw new CargoCapacityException(Math.Max(0, capacity - used), added); }
        List<InventoryChange> changes = [];
        foreach (var item in items)
        {
            var before = PilotInventoryStore.Read(catalog, item);
            int moved = quantity ?? item.Quantity;
            if (moved < item.Quantity)
            {
                item.Quantity -= moved;
                changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
                var split = world.Set<OwnedItem>().Add(new()
                {
                    Id = PilotInventoryStore.NextId(world),
                    OwnerId = pilotId,
                    TypeId = item.TypeId,
                    LocationId = shipId,
                    FlagId = (int)InventoryFlag.Cargo,
                    Quantity = moved,
                }).Entity;
                changes.Add(new(PilotInventoryStore.Read(catalog, split), null));
            }
            else
            {
                item.LocationId = shipId;
                item.FlagId = (int)InventoryFlag.Cargo;
                item.Online = false;
                changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
            }
        }
        world.SaveChanges();
        transaction.Commit();
        return changes;
    }
}
