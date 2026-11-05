using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
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
            cooldown?.Kind == ModuleCooldownKind.Reload ? cooldown.ReadyAt : 0,
            charge.CrystalDamage
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
                && i.FlagId >= (int)InventoryFlag.LowSlot0
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

    public IReadOnlyDictionary<DogmaAttributeId, double> ChargeAttributes(long pilotId, long shipId, long moduleId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId, shipId);
        var module = world.Set<OwnedItem>().SingleOrDefault(i => i.Id == moduleId && i.OwnerId == pilotId && i.LocationId == shipId);
        if (
            module is null
            || !InventoryDefinitions.IsFittedSlot(module.FlagId)
            || Contents(world, moduleId) is not { } charge
            || charge.OwnerId != pilotId
        )
        {
            throw new InvalidDataException("The loaded ammunition is unavailable.");
        }
        int group = catalog.Set<ItemType>().Single(t => t.Id == charge.TypeId).GroupId;
        return MissileDefinitions.ChargeGroups.Contains(group)
            ? FittingCalculation.Read(world, catalog, pilot).Graph.Attributes(charge.Id)
            : Attributes(catalog, charge.TypeId);
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
        ids = ids.SelectMany(id => PilotWeaponBankStore.Members(world, pilot.ActiveShipId, id)).Distinct().ToArray();
        int firstFlag = (int)InventoryFlag.LowSlot0;
        var modules = world
            .Set<OwnedItem>()
            .Where(i =>
                ids.Contains(i.Id)
                && i.OwnerId == pilot.Id
                && i.LocationId == pilot.ActiveShipId
                && i.Singleton
                && i.FlagId >= firstFlag
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
                !(WeaponCatalog.Supported(catalog, module.TypeId, group) && InventoryDefinitions.IsHighSlot(module.FlagId))
                && !(SupportModuleCatalog.Supported(catalog, module.TypeId, group) && InventoryDefinitions.IsMediumSlot(module.FlagId))
                && !(
                    CapacitorBoosterDefinitions.IsSupported(group, attributes)
                    && InventoryDefinitions.IsMediumSlot(module.FlagId)
                    && FittingCatalog.Definition(catalog, module.TypeId) is not null
                )
                && !(
                    unloading
                    // The native unfit path first requests UnloadAmmo even for empty passive modules.
                    && FittingCatalog.Definition(catalog, module.TypeId) is not null
                    && Contents(world, module.Id) is null
                )
            )
            {
                throw new InvalidDataException("This module does not use supported ammunition.");
            }
            PilotSkillReader.CheckRequirements(world, catalog, pilot.Id, module.TypeId);
            if (SupportModuleDefinitions.Effect(group) is not null && world.Set<ActiveFittedModule>().Find(module.Id) is not null)
            {
                throw new InvalidDataException("Deactivate the support module and finish its paid cycle before changing scripts.");
            }
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
        double capacity = PilotInventoryStore.HoldCapacity(world, catalog, pilot.Id, pilot.ActiveShipId, (int)InventoryFlag.Cargo);
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
        var fitting = FittingCalculation.Read(world, catalog, pilot);
        var sources = world
            .Set<OwnedItem>()
            .Where(i => chargeIds.Contains(i.Id) && i.OwnerId == pilotId && i.LocationId == sourceId && i.FlagId == sourceFlag)
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
            !(
                TurretChargeCatalog.Supported(catalog, typeId, chargeType.GroupId)
                || MissileChargeCatalog.Supported(catalog, typeId, chargeType.GroupId)
                || ScriptCatalog.Supported(catalog, typeId, chargeType.GroupId)
            )
            && !(
                chargeType.GroupId == CapacitorBoosterDefinitions.ChargeGroup
                && chargeAttributes.GetValueOrDefault(DogmaAttributeId.CapacitorBonus) > 0
            )
        )
        {
            throw new InvalidDataException("Use supported ammunition, capacitor booster charges or module scripts.");
        }
        bool crystal = AmmunitionDefinitions.IsCrystalGroup(chargeType.GroupId);
        bool script = SupportModuleDefinitions.IsScript(chargeType.GroupId);
        if (sources.Any(s => s.Singleton && (!crystal || s.Quantity != 1)))
        {
            throw new InvalidDataException("Only individual frequency crystals can be loaded from singleton items.");
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
        var banks = PilotWeaponBankStore.Read(world, shipId);
        for (int index = 0; index < modules.Length; index++)
        {
            var module = modules[index];
            if (sources.All(s => s.Quantity == 0 || s.LocationId != sourceId))
            {
                break;
            }
            var attributes = Attributes(catalog, module.TypeId);
            int moduleGroup = catalog.Set<ItemType>().Single(t => t.Id == module.TypeId).GroupId;
            bool compatible =
                script ? SupportModuleDefinitions.CompatibleScript(moduleGroup, chargeType.GroupId, attributes, chargeAttributes)
                : chargeType.GroupId == CapacitorBoosterDefinitions.ChargeGroup
                    ? attributes.GetValueOrDefault(DogmaAttributeId.ChargeGroup1) == chargeType.GroupId
                : MissileDefinitions.ChargeGroups.Contains(chargeType.GroupId)
                    ? MissileDefinitions.CompatibleCharge(attributes, chargeType.GroupId)
                : AmmunitionDefinitions.CompatibleTurretCharge(attributes, chargeType.GroupId, chargeAttributes);
            if (!compatible)
            {
                throw new InvalidDataException("The ammunition group or size does not match the weapon.");
            }
            double capacity = catalog.Set<ItemType>().Single(t => t.Id == module.TypeId).Capacity ?? 0;
            int limit = checked((int)Math.Floor(capacity / volume + InventoryDefinitions.ResourceTolerance));
            if (crystal || script)
            {
                limit = Math.Min(limit, 1);
            }
            if (limit <= 0)
            {
                throw new InvalidDataException("The weapon's magazine has no room for this ammunition.");
            }
            var loaded = Contents(world, module.Id);
            var previous = loaded is null ? null : Read(world, module, loaded);
            if (loaded is not null && (loaded.TypeId != typeId || crystal))
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
            var bank = banks.FirstOrDefault(b => b.MasterId == module.Id || b.SlaveIds.Contains(module.Id));
            if (bank is not null && !crystal && !script)
            {
                int remaining = modules
                    .Skip(index)
                    .Count(candidate =>
                    {
                        var contents = Contents(world, candidate.Id);
                        return (candidate.Id == bank.MasterId || bank.SlaveIds.Contains(candidate.Id))
                            && (contents?.TypeId != typeId || contents.Quantity < limit);
                    });
                long roundsLeft = sources.Where(s => s.LocationId == sourceId).Sum(s => (long)s.Quantity);
                int share = checked((int)Math.Min(limit, (roundsLeft + remaining - 1) / remaining));
                available = Math.Min(available, share);
            }
            var individual = crystal ? sources.FirstOrDefault(s => s.Singleton && s.LocationId == sourceId) : null;
            if (individual is not null)
            {
                individual.LocationId = module.Id;
                individual.FlagId = (int)InventoryFlag.Cargo;
                loaded = individual;
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
                        Singleton = crystal && chargeAttributes.GetValueOrDefault(DogmaAttributeId.CrystalsGetDamaged) != 0,
                    }
                )
                .Entity;
            if (individual is not null)
            {
                available = 0;
            }
            foreach (var source in sources.Where(s => available > 0 && s.Quantity > 0 && s.LocationId == sourceId))
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
            double reload = fitting.Graph.Attributes(module.Id).GetValueOrDefault(DogmaAttributeId.ReloadTime);
            if (!double.IsFinite(reload) || reload < 0)
            {
                throw new InvalidDataException("Invalid weapon reload duration.");
            }
            var cooldown =
                world.Set<ModuleCooldown>().Find(module.Id) ?? world.Set<ModuleCooldown>().Add(new() { ModuleId = module.Id }).Entity;
            cooldown.Kind = ModuleCooldownKind.Reload;
            cooldown.ReadyAt = checked(now + (long)Math.Ceiling(reload * TimeSpan.TicksPerMillisecond));
            duration = Math.Max(duration, reload);
            magazines.Add(
                new(previous, new(module.Id, shipId, module.FlagId, typeId, loaded.Quantity, cooldown.ReadyAt, loaded.CrystalDamage))
            );
        }
        if (magazines.Count == 0)
        {
            throw new InvalidDataException("The selected magazines are already full.");
        }
        foreach (var source in sources.Where(s => s.Quantity != beforeSources[s.Id].Quantity || s.LocationId != sourceId))
        {
            changes.Add(new(PilotInventoryStore.Read(catalog, source), beforeSources[source.Id]));
        }
        world.SaveChanges();
        var grouped = banks.Where(b => modules.Any(m => m.Id == b.MasterId || b.SlaveIds.Contains(m.Id)));
        foreach (var bank in grouped)
        {
            if (new[] { bank.MasterId }.Concat(bank.SlaveIds).Any(id => Contents(world, id)?.TypeId != typeId))
            {
                throw new InvalidDataException("Supply ammunition for every weapon in the group.");
            }
        }
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
