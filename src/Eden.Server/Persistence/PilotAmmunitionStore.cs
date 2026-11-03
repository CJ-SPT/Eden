using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Protocol;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotAmmunitionStore(WorldDatabase database) : IPilotAmmunition
{
    // A magazine is a real owned stack located inside its module. The client
    // sees the native (ship, slot, type) sublocation rather than this stack ID.
    internal static OwnedItem? Contents(WorldDbContext world, long moduleId) =>
        world.Set<OwnedItem>().SingleOrDefault(i => i.LocationId == moduleId);

    internal static Magazine Read(WorldDbContext world, OwnedItem module, OwnedItem charge)
    {
        var cooldown = world.Set<ModuleCooldown>().Find(module.Id);
        return new(
            module.Id,
            module.LocationId,
            module.FlagId,
            charge.TypeId,
            charge.Quantity,
            cooldown?.Kind == ModuleCooldownKind.Reload ? cooldown.ReadyAt : 0
        );
    }

    public IReadOnlyList<Magazine> Read(long pilotId, long shipId)
    {
        using WorldDbContext world = new(database.Path);
        var pilot = Pilot(world, pilotId, shipId);
        var modules = world
            .Set<OwnedItem>()
            .Where(i =>
                i.OwnerId == pilot.Id
                && i.LocationId == shipId
                && i.FlagId >= (int)InventoryFlag.HighSlot0
                && i.FlagId <= (int)InventoryFlag.HighSlot7
            )
            .OrderBy(i => i.FlagId)
            .ToArray();
        return modules
            .Select(module => (module, charge: Contents(world, module.Id)))
            .Where(p => p.charge is not null)
            .Select(p => Read(world, p.module, p.charge!))
            .ToArray();
    }

    private static Pilot Pilot(WorldDbContext world, long pilotId, long shipId)
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        if (pilot.ActiveShipId != shipId)
        {
            throw new InvalidDataException("Load ammunition on your active ship.");
        }
        return pilot;
    }

    private static OwnedItem[] Modules(
        WorldDbContext world,
        CatalogDbContext catalog,
        Pilot pilot,
        IReadOnlyList<long> ids,
        long now,
        bool unloading = false
    )
    {
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count)
        {
            throw new InvalidDataException("Select distinct fitted weapons.");
        }
        var modules = world
            .Set<OwnedItem>()
            .Where(i =>
                ids.Contains(i.Id)
                && i.OwnerId == pilot.Id
                && i.LocationId == pilot.ActiveShipId
                && i.Singleton
                && i.FlagId >= (int)InventoryFlag.HighSlot0
                && i.FlagId <= (int)InventoryFlag.HighSlot7
            )
            .OrderBy(i => i.FlagId)
            .ToArray();
        if (modules.Length != ids.Count)
        {
            throw new InvalidDataException("The fitted weapons are unavailable.");
        }
        foreach (var module in modules)
        {
            var attributes = Attributes(catalog, module.TypeId);
            int group = catalog.Set<ItemType>().Single(t => t.Id == module.TypeId).GroupId;
            if (
                !CombatDefinitions.IsSupportedHybrid(group, attributes)
                && !(
                    unloading
                    && (group == InventoryDefinitions.MiningLaserGroup || module.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun)
                    && Contents(world, module.Id) is null
                )
            )
            {
                throw new InvalidDataException("This weapon does not use supported small hybrid charges.");
            }
            PilotSkillReader.CheckRequirements(world, catalog, pilot.Id, module.TypeId);
            if (world.Set<ModuleCooldown>().Find(module.Id) is { } cooldown && now < cooldown.ReadyAt)
            {
                throw new InvalidDataException("Wait for the weapon's current cycle or reload to finish.");
            }
        }
        return modules;
    }

    private static Dictionary<DogmaAttributeId, double> Attributes(CatalogDbContext catalog, int typeId) =>
        catalog.Set<TypeAttribute>().Where(a => a.ItemTypeId == typeId).ToDictionary(a => (DogmaAttributeId)a.AttributeId, a => a.Value);

    private static void CheckCargo(WorldDbContext world, CatalogDbContext catalog, Pilot pilot)
    {
        double capacity = PilotInventoryStore.HoldCapacity(catalog, pilot.ActiveShip.TypeId, (int)InventoryFlag.Cargo);
        double used = InventoryPersistence.UsedVolume(world, catalog, pilot.Id, pilot.ActiveShipId, (int)InventoryFlag.Cargo);
        if (used > capacity + InventoryDefinitions.ResourceTolerance)
        {
            throw new CargoCapacityException(0, used - capacity);
        }
    }

    public AmmunitionTransfer Load(
        long pilotId,
        long shipId,
        IReadOnlyList<long> moduleIds,
        IReadOnlyList<long> chargeIds,
        long sourceId,
        long now
    )
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId, shipId);
        int sourceFlag = sourceId == shipId ? (int)InventoryFlag.Cargo : (int)InventoryFlag.Hangar;
        if (
            (sourceId != shipId && (pilot.ActiveShip.Position is not null || sourceId != pilot.ActiveShip.StationId))
            || chargeIds.Count == 0
            || chargeIds.Distinct().Count() != chargeIds.Count
        )
        {
            throw new InvalidDataException("Load distinct ammunition stacks from your cargo or current docked station hangar.");
        }
        var modules = Modules(world, catalog, pilot, moduleIds, now);
        var sources = world
            .Set<OwnedItem>()
            .Where(i =>
                chargeIds.Contains(i.Id) && i.OwnerId == pilotId && i.LocationId == sourceId && i.FlagId == sourceFlag && !i.Singleton
            )
            .OrderBy(i => i.Id)
            .ToArray();
        if (sources.Length != chargeIds.Count || sources.Select(i => i.TypeId).Distinct().Count() != 1)
        {
            throw new InvalidDataException("Choose available stacks of one ammunition type.");
        }
        int typeId = sources[0].TypeId;
        var chargeType = catalog.Set<ItemType>().Single(t => t.Id == typeId);
        var chargeAttributes = Attributes(catalog, typeId);
        if (
            chargeType.GroupId != AmmunitionDefinitions.HybridChargeGroup
            || chargeAttributes.GetValueOrDefault(DogmaAttributeId.ChargeSize) != AmmunitionDefinitions.SmallChargeSize
        )
        {
            throw new InvalidDataException("Use small hybrid charges in these weapons.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, typeId);
        double volume = PilotInventoryStore.Volume(catalog, typeId);
        if (volume <= 0)
        {
            throw new InvalidDataException("Ammunition needs a positive catalog volume.");
        }
        List<InventoryChange> changes = [];
        List<MagazineChange> magazines = [];
        var beforeSources = sources.ToDictionary(i => i.Id, i => PilotInventoryStore.Read(catalog, i));
        long nextId = InventoryPersistence.NextId(world);
        double duration = 0;
        foreach (var module in modules)
        {
            if (sources.All(s => s.Quantity == 0))
            {
                break;
            }
            var attributes = Attributes(catalog, module.TypeId);
            if (attributes.GetValueOrDefault(DogmaAttributeId.ChargeGroup1) != chargeType.GroupId)
            {
                throw new InvalidDataException("The ammunition group does not match the weapon.");
            }
            double capacity = catalog.Set<ItemType>().Single(t => t.Id == module.TypeId).Capacity ?? 0;
            int limit = checked((int)Math.Floor(capacity / volume + InventoryDefinitions.ResourceTolerance));
            if (limit <= 0)
            {
                throw new InvalidDataException("The weapon's magazine has no room for this ammunition.");
            }
            var loaded = Contents(world, module.Id);
            var previous = loaded is null ? null : Read(world, module, loaded);
            if (loaded is not null && loaded.TypeId != typeId)
            {
                var old = PilotInventoryStore.Read(catalog, loaded);
                loaded.LocationId = sourceId;
                loaded.FlagId = sourceFlag;
                changes.Add(new(PilotInventoryStore.Read(catalog, loaded), old));
                loaded = null;
            }
            int available = limit - (loaded?.Quantity ?? 0);
            if (available <= 0)
            {
                continue;
            }
            loaded ??= world
                .Set<OwnedItem>()
                .Add(
                    new()
                    {
                        Id = nextId++,
                        OwnerId = pilotId,
                        TypeId = typeId,
                        LocationId = module.Id,
                        FlagId = (int)InventoryFlag.Cargo,
                    }
                )
                .Entity;
            foreach (var source in sources.Where(s => s.Quantity > 0))
            {
                int moved = Math.Min(available, source.Quantity);
                loaded.Quantity = checked(loaded.Quantity + moved);
                source.Quantity -= moved;
                available -= moved;
                if (source.Quantity == 0)
                {
                    world.Remove(source);
                }
                if (available == 0)
                {
                    break;
                }
            }
            double reload = attributes.GetValueOrDefault(DogmaAttributeId.ReloadTime);
            if (!double.IsFinite(reload) || reload < 0)
            {
                throw new InvalidDataException("Invalid weapon reload duration.");
            }
            var cooldown =
                world.Set<ModuleCooldown>().Find(module.Id) ?? world.Set<ModuleCooldown>().Add(new() { ModuleId = module.Id }).Entity;
            cooldown.Kind = ModuleCooldownKind.Reload;
            cooldown.ReadyAt = checked(now + (long)Math.Ceiling(reload * TimeSpan.TicksPerMillisecond));
            duration = Math.Max(duration, reload);
            magazines.Add(new(previous, new(module.Id, shipId, module.FlagId, typeId, loaded.Quantity, cooldown.ReadyAt)));
        }
        if (magazines.Count == 0)
        {
            throw new InvalidDataException("The selected magazines are already full.");
        }
        foreach (var source in sources.Where(s => s.Quantity != beforeSources[s.Id].Quantity))
        {
            changes.Add(new(beforeSources[source.Id] with { Quantity = source.Quantity }, beforeSources[source.Id]));
        }
        world.SaveChanges();
        CheckCargo(world, catalog, pilot);
        transaction.Commit();
        return new(changes, magazines, duration);
    }

    public AmmunitionTransfer Unload(
        long pilotId,
        long shipId,
        IReadOnlyList<long> moduleIds,
        long destinationId,
        int flagId,
        int? quantity,
        long now
    )
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId, shipId);
        if (
            !(
                (destinationId == shipId && flagId == (int)InventoryFlag.Cargo)
                || (pilot.ActiveShip.Position is null && destinationId == pilot.ActiveShip.StationId && flagId == (int)InventoryFlag.Hangar)
            )
            || quantity is <= 0
            || quantity.HasValue && moduleIds.Count != 1
        )
        {
            throw new InvalidDataException("Unload into your cargo or current docked station hangar with a valid quantity.");
        }
        var modules = Modules(world, catalog, pilot, moduleIds, now, unloading: true);
        List<InventoryChange> items = [];
        List<MagazineChange> magazines = [];
        long nextId = InventoryPersistence.NextId(world);
        foreach (var module in modules)
        {
            var loaded = Contents(world, module.Id);
            if (loaded is null)
            {
                continue;
            }
            var previous = Read(world, module, loaded);
            int moved = quantity ?? loaded.Quantity;
            if (moved > loaded.Quantity)
            {
                throw new InvalidDataException("The magazine no longer contains that quantity.");
            }
            var before = PilotInventoryStore.Read(catalog, loaded);
            if (moved == loaded.Quantity)
            {
                loaded.LocationId = destinationId;
                loaded.FlagId = flagId;
                items.Add(new(PilotInventoryStore.Read(catalog, loaded), before));
                magazines.Add(new(previous, null));
            }
            else
            {
                loaded.Quantity -= moved;
                var split = world
                    .Set<OwnedItem>()
                    .Add(
                        new()
                        {
                            Id = nextId++,
                            OwnerId = pilotId,
                            TypeId = loaded.TypeId,
                            LocationId = destinationId,
                            FlagId = flagId,
                            Quantity = moved,
                        }
                    )
                    .Entity;
                items.Add(new(PilotInventoryStore.Read(catalog, split), null));
                magazines.Add(new(previous, Read(world, module, loaded)));
            }
        }
        world.SaveChanges();
        CheckCargo(world, catalog, pilot);
        transaction.Commit();
        return new(items, magazines);
    }
}
