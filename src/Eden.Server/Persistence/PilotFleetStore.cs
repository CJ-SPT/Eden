using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Protocol;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotFleetStore(WorldDatabase database, SqliteWorld lookup, TimeProvider? clock = null) : IPilotFleet
{
    private static readonly (int TypeId, InventoryFlag Slot)[] s_recoveryEquipment =
    [
        (InventoryDefinitions.CivilianMinerType, InventoryFlag.HighSlot0),
        ((int)CombatWeaponType.CivilianGatlingRailgun, InventoryFlag.HighSlot1),
    ];

    internal static InventoryItem Read(CatalogDbContext catalog, Ship ship)
    {
        var type = catalog.Set<ItemType>().Include(t => t.Group).Single(t => t.Id == ship.TypeId);
        return new(
            ship.Id,
            ship.TypeId,
            ship.OwnerId,
            ship.StoredInId ?? (ship.Position is null ? ship.StationId : ship.Position.SystemId ?? ship.StationId),
            ship.StoredInId is not null ? ship.StorageFlagId
                : ship.Position is null ? (int)InventoryFlag.Hangar
                : (int)InventoryFlag.AutoFit,
            1,
            type.GroupId,
            type.Group.CategoryId,
            PilotInventoryStore.Volume(catalog, ship.TypeId, singleton: true),
            true
        );
    }

    public IReadOnlyList<InventoryItem> Ships(long pilotId, int stationId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return world
            .Ships.AsNoTracking()
            .Include(s => s.Position)
            .Where(s => s.OwnerId == pilotId && s.StationId == stationId && s.Position == null && s.StoredInId == null)
            .OrderBy(s => s.Id)
            .AsEnumerable()
            .Select(s => Read(catalog, s))
            .ToArray();
    }

    public InventoryItem? Find(long pilotId, long shipId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var ship = world.Ships.AsNoTracking().Include(s => s.Position).SingleOrDefault(s => s.Id == shipId && s.OwnerId == pilotId);
        return ship is null ? null : Read(catalog, ship);
    }

    public string Name(long pilotId, long shipId)
    {
        using WorldDbContext world = new(database.Path);
        return world.Ships.Where(s => s.OwnerId == pilotId && s.Id == shipId).Select(s => s.Name).Single();
    }

    public ShipAssembly Assemble(long pilotId, IReadOnlyList<long> itemIds, string? name)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        if (
            pilot.ActiveShip.Position is not null
            || itemIds.Count == 0
            || itemIds.Distinct().Count() != itemIds.Count
            || name?.Length > FleetDefinitions.MaximumShipNameLength
            || name?.Any(char.IsControl) == true
        )
        {
            throw new InvalidDataException("Assemble distinct packaged ships in your current station hangar with a valid name.");
        }
        List<long> ships = [];
        List<InventoryChange> changes = [];
        long nextId = InventoryPersistence.NextId(world);
        foreach (long id in itemIds)
        {
            // Retrying an assembled ID must not consume a second unit from its old stack.
            if (world.Ships.Include(s => s.Position).SingleOrDefault(s => s.Id == id) is { } assembled)
            {
                if (
                    assembled.OwnerId != pilotId
                    || assembled.StationId != pilot.ActiveShip.StationId
                    || assembled.Position is not null
                    || assembled.StoredInId is not null
                )
                {
                    throw new InvalidDataException("The ship is unavailable in your current hangar.");
                }
                ships.Add(id);
                continue;
            }
            var item = world.Set<OwnedItem>().Find(id);
            if (
                item is null
                || item.OwnerId != pilotId
                || item.LocationId != pilot.ActiveShip.StationId
                || item.FlagId != (int)InventoryFlag.Hangar
                || item.Singleton
            )
            {
                throw new InvalidDataException("The packaged ship is unavailable in your current hangar.");
            }
            var type = catalog.Set<ItemType>().Include(t => t.Group).Single(t => t.Id == item.TypeId);
            if (!HullCatalog.CanAssemble(catalog, type, type.Group.CategoryId))
            {
                throw new InvalidDataException("This hull is not supported for assembly yet.");
            }
            var before = PilotInventoryStore.Read(catalog, item);
            long shipId = item.Quantity == 1 ? id : nextId++;
            if (item.Quantity == 1)
            {
                world.Remove(item);
            }
            else
            {
                item.Quantity--;
                changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
            }
            var ship = world
                .Ships.Add(
                    new()
                    {
                        Id = shipId,
                        OwnerId = pilotId,
                        TypeId = item.TypeId,
                        StationId = pilot.ActiveShip.StationId,
                        Name = string.IsNullOrWhiteSpace(name) ? type.Name : name.Trim(),
                    }
                )
                .Entity;
            changes.Add(new(Read(catalog, ship), shipId == id ? before : null));
            ships.Add(shipId);
        }
        world.SaveChanges();
        transaction.Commit();
        return new(ships, changes);
    }

    public ShipBoarding Leave(long pilotId, long expectedShipId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var old = pilot.ActiveShip;
        if (
            old.Id != expectedShipId
            || old.OwnerId != pilotId
            || old.Position is not null
            || old.StoredInId is not null
            || catalog.Set<ItemType>().Find(old.TypeId)?.GroupId == FleetDefinitions.CapsuleGroup
        )
        {
            throw new InvalidDataException("Leave the ship you are currently piloting while docked in a station.");
        }
        var type =
            catalog.Set<ItemType>().SingleOrDefault(t => t.Id == FleetDefinitions.CapsuleType && t.GroupId == FleetDefinitions.CapsuleGroup)
            ?? throw new InvalidDataException("The catalog has no capsule.");
        var capsule = world
            .Ships.Where(s =>
                s.OwnerId == pilotId && s.TypeId == type.Id && s.StationId == old.StationId && s.Position == null && s.StoredInId == null
            )
            .OrderBy(s => s.Id)
            .FirstOrDefault();
        bool created = capsule is null;
        capsule ??= world
            .Add(
                new Ship
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = pilotId,
                    TypeId = type.Id,
                    StationId = old.StationId,
                    Name = type.Name,
                }
            )
            .Entity;
        pilot.ActiveShip = capsule;
        pilot.ActiveShipId = capsule.Id;
        world.SaveChanges();
        var changes = created ? new InventoryChange[] { new(Read(catalog, capsule), null) } : [];
        transaction.Commit();
        return new(lookup.FindPilot(pilot.AccountId, pilotId)!, changes);
    }

    public DockedPilot Board(long pilotId, long shipId, long previousShipId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var ship = world.Ships.Include(s => s.Position).SingleOrDefault(s => s.Id == shipId && s.OwnerId == pilotId);
        if (
            pilot.ActiveShip.Position is not null
            || ship is null
            || ship.Position is not null
            || ship.StoredInId is not null
            || ship.StationId != pilot.ActiveShip.StationId
            || (pilot.ActiveShipId != previousShipId && pilot.ActiveShipId != shipId)
        )
        {
            throw new InvalidDataException("Board an assembled ship you own at your current docked station.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, ship.TypeId);
        if (pilot.ActiveShipId != shipId)
        {
            PilotTaskTrackingStore.Record(
                world,
                catalog,
                pilotId,
                (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                TaskEvents.Boarded
            );
        }
        pilot.ActiveShipId = shipId;
        world.SaveChanges();
        transaction.Commit();
        return lookup.FindPilot(pilot.AccountId, pilotId)!;
    }

    public ShipBoarding RecoverCorvette(long pilotId, long previousShipId, long stationId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var active = pilot.ActiveShip;
        if (active.Position is not null || active.StationId != stationId)
        {
            throw new InvalidDataException("Board your recovery corvette while docked at your current station.");
        }
        int activeGroup = catalog.Set<ItemType>().Single(t => t.Id == active.TypeId).GroupId;
        if (pilot.ActiveShipId != previousShipId)
        {
            // A repeated request for the previous hull cannot create another kit.
            if (
                activeGroup == FleetDefinitions.CorvetteGroup
                && world.Ships.Any(s =>
                    s.Id == previousShipId && s.OwnerId == pilotId && s.StationId == stationId && s.Position == null && s.StoredInId == null
                )
            )
            {
                return new(lookup.FindPilot(pilot.AccountId, pilotId)!, []);
            }
            throw new InvalidDataException("The active ship changed before recovery.");
        }
        if (activeGroup == FleetDefinitions.CorvetteGroup)
        {
            throw new InvalidDataException("You are already aboard a corvette.");
        }
        int typeId = checked(
            (int)(
                CatalogData.Client.Races.Get(catalog, pilot.RaceId).ShipTypeID
                ?? throw new InvalidDataException("The pilot's race has no starter corvette.")
            )
        );
        var type = catalog.Set<ItemType>().Single(t => t.Id == typeId && t.GroupId == FleetDefinitions.CorvetteGroup);
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, typeId);
        foreach (var equipment in s_recoveryEquipment)
        {
            PilotSkillReader.CheckRequirements(world, catalog, pilotId, equipment.TypeId);
        }

        var candidates = world
            .Ships.Include(s => s.Position)
            .Where(s =>
                s.OwnerId == pilotId && s.TypeId == typeId && s.StationId == stationId && s.Position == null && s.StoredInId == null
            )
            .OrderBy(s => s.Id)
            .ToArray();
        // Eden reuses an undamaged, equipped recovery hull at this station.
        // Stripped/damaged hulls and ships elsewhere remain untouched.
        var recovered = candidates.FirstOrDefault(s =>
            !world.Set<ShipHealthState>().Any(h => h.ShipId == s.Id && (h.ArmorDamage > 0 || h.StructureDamage > 0))
            && s_recoveryEquipment.All(e =>
                world
                    .Set<OwnedItem>()
                    .Any(i =>
                        i.OwnerId == pilotId
                        && i.LocationId == s.Id
                        && i.TypeId == e.TypeId
                        && i.FlagId == (int)e.Slot
                        && i.Singleton
                        && i.Online
                    )
            )
        );
        List<InventoryChange> changes = [];
        if (recovered is null)
        {
            var attributes = catalog
                .Set<TypeAttribute>()
                .Where(a => a.ItemTypeId == typeId)
                .ToDictionary(a => (DogmaAttributeId)a.AttributeId, a => a.Value);
            double Load(DogmaAttributeId attribute) =>
                s_recoveryEquipment.Sum(e =>
                    catalog
                        .Set<TypeAttribute>()
                        .Where(a => a.ItemTypeId == e.TypeId && a.AttributeId == (int)attribute)
                        .Select(a => a.Value)
                        .SingleOrDefault()
                );
            if (
                attributes.GetValueOrDefault(DogmaAttributeId.HighSlots) < s_recoveryEquipment.Length
                || attributes.GetValueOrDefault(DogmaAttributeId.TurretSlotsLeft) < s_recoveryEquipment.Length
                || Load(DogmaAttributeId.Cpu) > attributes.GetValueOrDefault(DogmaAttributeId.CpuOutput)
                || Load(DogmaAttributeId.Power) > attributes.GetValueOrDefault(DogmaAttributeId.PowerOutput)
            )
            {
                throw new InvalidDataException("The catalog's corvette cannot support the recovery equipment.");
            }
            long nextId = InventoryPersistence.NextId(world);
            recovered = world
                .Ships.Add(
                    new()
                    {
                        Id = nextId++,
                        OwnerId = pilotId,
                        TypeId = typeId,
                        StationId = active.StationId,
                        Name = type.Name,
                    }
                )
                .Entity;
            changes.Add(new(Read(catalog, recovered), null));
            foreach (var equipment in s_recoveryEquipment)
            {
                var module = world
                    .Set<OwnedItem>()
                    .Add(
                        new()
                        {
                            Id = nextId++,
                            OwnerId = pilotId,
                            TypeId = equipment.TypeId,
                            LocationId = recovered.Id,
                            FlagId = (int)equipment.Slot,
                            Quantity = 1,
                            Singleton = true,
                            Online = true,
                        }
                    )
                    .Entity;
                changes.Add(new(PilotInventoryStore.Read(catalog, module), null));
            }
        }
        PilotTaskTrackingStore.Record(
            world,
            catalog,
            pilotId,
            (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc(),
            TaskEvents.Boarded
        );
        pilot.ActiveShipId = recovered.Id;
        world.SaveChanges();
        transaction.Commit();
        return new(lookup.FindPilot(pilot.AccountId, pilotId)!, changes);
    }
}
