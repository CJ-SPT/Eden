using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Inventory;
using Eden.World.Propulsion;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Fittings;

[Injectable]
internal sealed class PilotFittingStore(WorldDatabase database, TimeProvider clock) : IPilotFitting, IPilotRigRemoval
{
    public IReadOnlyDictionary<DogmaAttributeId, double> Attributes(int typeId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return ReadAttributes(catalog, typeId);
    }

    internal static IReadOnlyDictionary<DogmaAttributeId, double> ReadAttributes(CatalogDbContext catalog, int typeId)
    {
        var attributes = Attributes(catalog, typeId);
        if (
            !attributes.ContainsKey(DogmaAttributeId.SignatureRadius)
            && catalog.Read(CatalogData.Client.Dogmaattributes, (long)(((int)DogmaAttributeId.SignatureRadius)))?.DefaultValue
                is double signature
        )
        {
            attributes[DogmaAttributeId.SignatureRadius] = signature;
        }
        return attributes;
    }

    private static Dictionary<DogmaAttributeId, double> Attributes(CatalogDbContext catalog, int typeId) =>
        catalog.FittingAttributes(typeId).ToDictionary();

    internal static Dictionary<DogmaAttributeId, double> ShipAttributes(WorldDbContext world, CatalogDbContext catalog, long pilotId) =>
        ShipAttributes(world, catalog, world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId));

    internal static Dictionary<DogmaAttributeId, double> ShipAttributes(
        WorldDbContext world,
        CatalogDbContext catalog,
        Pilot pilot,
        Ship? ship = null,
        FittingCalculation? calculation = null
    )
    {
        ship ??= pilot.ActiveShip;
        var key = new FitCacheKey(pilot.Id, ship.Id, 0);
        string? revision = world.FitRevision(pilot.Id);
        if (world.CachedFit(key, revision) is { } cached)
        {
            return cached;
        }
        calculation ??= FittingCalculation.Read(world, catalog, pilot, ship);
        var attributes = calculation.Graph.Attributes(ship.Id);
        attributes[DogmaAttributeId.MaxVelocity] = PilotPropulsionStore.NavigationSpeed(
            world,
            catalog,
            pilot.Id,
            attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity)
        );
        var usage = calculation.ResourceUsage(catalog);
        var result = FittingRules.Apply(attributes, calculation.SkillLevels, usage);
        world.StageFit(key, revision, result);
        return result;
    }

    public IReadOnlyDictionary<DogmaAttributeId, double> ShipAttributes(long pilotId)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("read.fit");
        return LiveReadModels.Find(database.Path) is { } resident
            ? resident.Fit(pilotId, () => ShipAttributesSql(pilotId))
            : ShipAttributesSql(pilotId);
    }

    private IReadOnlyDictionary<DogmaAttributeId, double> ShipAttributesSql(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        ExpireTimedModules(world);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var result = ShipAttributes(world, catalog, world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId));
        transaction.Commit();
        return result;
    }

    public double SignatureRadius(long itemId, int typeId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        ExpireTimedModules(world);
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (world.Ships.Find(itemId) is not { } ship)
        {
            double npcSignature = PlayerEwarStore
                .NpcAttributes(world, itemId, ReadAttributes(catalog, typeId))
                .GetValueOrDefault(DogmaAttributeId.SignatureRadius);
            transaction.Commit();
            return npcSignature;
        }
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == ship.OwnerId);
        double signature = ShipAttributes(world, catalog, pilot, ship).GetValueOrDefault(DogmaAttributeId.SignatureRadius);
        transaction.Commit();
        return signature;
    }

    private void ExpireTimedModules(WorldDbContext world)
    {
        if (LiveWorld.Find(database.Path) is null)
        {
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            world.Set<ActiveFittedModule>().Where(m => m.ExpiresAt > 0 && m.ExpiresAt <= now).ExecuteDelete();
        }
    }

    public IReadOnlyList<InventoryChange> Fit(long pilotId, IReadOnlyList<long> itemIds, long sourceId, int flagId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var ship = pilot.ActiveShip;
        if (
            ship.Position is not null
            || (sourceId != ship.Id && sourceId != ship.StationId)
            || itemIds.Count == 0
            || itemIds.Distinct().Count() != itemIds.Count
            || (flagId != (int)InventoryFlag.AutoFit && (!InventoryDefinitions.IsFittedSlot(flagId) || itemIds.Count != 1))
        )
        {
            throw new InvalidDataException("Fit equipment from your cargo or current hangar while docked.");
        }
        var items = world
            .Set<OwnedItem>()
            .Where(i =>
                i.OwnerId == pilotId
                && itemIds.Contains(i.Id)
                && i.LocationId == sourceId
                && (
                    sourceId == ship.Id
                        ? i.FlagId == (int)InventoryFlag.Cargo
                            || (i.FlagId >= (int)InventoryFlag.LowSlot0 && i.FlagId <= (int)InventoryFlag.HighSlot7)
                        : i.FlagId == (int)InventoryFlag.Hangar
                )
            )
            .OrderBy(i => i.Id)
            .ToArray();
        if (items.Length != itemIds.Count)
        {
            throw new InvalidDataException("Equipment is unavailable.");
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var checkpoint = FittingChanges.Capture(world, catalog, pilot, now);
        var before = PilotInventoryStore.List(world, catalog, pilotId, sourceId).ToDictionary(i => i.Id);
        var capacity = checkpoint.Capacity;
        var rack = world
            .Set<OwnedItem>()
            .Where(i =>
                i.LocationId == ship.Id
                && (
                    (i.FlagId >= (int)InventoryFlag.LowSlot0 && i.FlagId <= (int)InventoryFlag.HighSlot7)
                    || (i.FlagId >= (int)InventoryFlag.RigSlot0 && i.FlagId <= (int)InventoryFlag.RigSlot2)
                )
            )
            .ToArray();
        var occupied = rack.Select(i => i.FlagId).ToHashSet();
        double calibrationUsed = rack.Where(i => InventoryDefinitions.IsRigSlot(i.FlagId))
            .Sum(i => Attributes(catalog, i.TypeId).GetValueOrDefault(DogmaAttributeId.CalibrationCost));
        var equipment = rack.Select(i =>
                (i.TypeId, GroupId: catalog.FittingType(i.TypeId)!.GroupId, Effects: FittingCatalog.Effects(catalog, i.TypeId))
            )
            .ToList();
        long nextId = InventoryPersistence.NextId(world);
        List<(OwnedItem Item, InventoryItem? Previous)> changed = [];
        foreach (var item in items)
        {
            var (first, slots) = SlotsFor(item.TypeId);
            bool moving = InventoryDefinitions.IsFittedSlot(item.FlagId);
            int slot =
                flagId == (int)InventoryFlag.AutoFit
                    ? moving
                        ? item.FlagId
                        : Enumerable.Range(first, slots).FirstOrDefault(f => !occupied.Contains(f))
                    : flagId;
            if (slot < first || slot >= first + slots)
            {
                throw new InvalidDataException("No compatible module slot is available.");
            }
            if (moving)
            {
                if (!item.Singleton || item.Quantity != 1 || item.FlagId < first || item.FlagId >= first + slots)
                {
                    throw new InvalidDataException("The fitted module is unavailable.");
                }
                if (item.FlagId == slot)
                {
                    continue;
                }
                var other = rack.SingleOrDefault(i => i.FlagId == slot);
                if (other is not null)
                {
                    var (otherFirst, otherSlots) = SlotsFor(other.TypeId);
                    if (
                        other.OwnerId != pilotId
                        || !other.Singleton
                        || other.Quantity != 1
                        || item.FlagId < otherFirst
                        || item.FlagId >= otherFirst + otherSlots
                    )
                    {
                        throw new InvalidDataException("The modules cannot exchange slots.");
                    }
                    other.FlagId = item.FlagId;
                    changed.Add((other, before[other.Id]));
                }
                else
                {
                    occupied.Remove(item.FlagId);
                    occupied.Add(slot);
                }
                // Charges and paid cooldowns belong to the module identity.
                // Changing its slot must not unload it or change online costs.
                item.FlagId = slot;
                changed.Add((item, before[item.Id]));
                continue;
            }
            if (!occupied.Add(slot))
            {
                throw new InvalidDataException("No compatible module slot is available.");
            }
            var definition = FittingCatalog.Definition(catalog, item.TypeId)!;
            if (definition.Rack == ModuleRack.Rig)
            {
                var rigAttributes = Attributes(catalog, item.TypeId);
                RigRules.CheckFit(checkpoint.Resources.Attributes, rigAttributes, calibrationUsed);
                calibrationUsed += rigAttributes.GetValueOrDefault(DogmaAttributeId.CalibrationCost);
            }
            int group = catalog.FittingType(item.TypeId)!.GroupId;
            if (
                (definition.Turret && equipment.Count(e => e.Effects.Contains(DogmaEffect.TurretFitted)) >= capacity.Turrets)
                || (definition.Launcher && equipment.Count(e => e.Effects.Contains(DogmaEffect.LauncherFitted)) >= capacity.Launchers)
            )
            {
                throw new InvalidDataException("The hull has no compatible hardpoint or module allowance remaining.");
            }
            equipment.Add((item.TypeId, group, FittingCatalog.Effects(catalog, item.TypeId)));
            var previous = before[item.Id];
            OwnedItem fitted = item;
            if (item.Quantity > 1)
            {
                item.Quantity--;
                changed.Add((item, previous));
                fitted = world
                    .Set<OwnedItem>()
                    .Add(
                        new OwnedItem
                        {
                            Id = nextId++,
                            OwnerId = pilotId,
                            TypeId = item.TypeId,
                            Quantity = 1,
                        }
                    )
                    .Entity;
                previous = null;
            }
            fitted.LocationId = ship.Id;
            fitted.FlagId = slot;
            fitted.Singleton = true;
            fitted.Online = false;
            changed.Add((fitted, previous));
        }
        world.SaveChanges();
        var result = changed
            .Select(c => new InventoryChange(
                before.Values.First(i => i.TypeId == c.Item.TypeId) with
                {
                    Id = c.Item.Id,
                    LocationId = c.Item.LocationId,
                    FlagId = c.Item.FlagId,
                    Quantity = c.Item.Quantity,
                    Singleton = c.Item.Singleton,
                    Online = c.Item.Online,
                },
                c.Previous
            ))
            .ToArray();
        foreach (
            var change in result.Where(change =>
                change.Item.LocationId == ship.Id
                && InventoryDefinitions.IsFittedSlot(change.Item.FlagId)
                && (
                    change.Previous is null
                    || change.Previous.LocationId != ship.Id
                    || !InventoryDefinitions.IsFittedSlot(change.Previous.FlagId)
                )
            )
        )
        {
            PilotTaskTrackingStore.Record(world, catalog, pilotId, now, TaskEvents.Fitted);
            if (InventoryDefinitions.IsRigSlot(change.Item.FlagId))
            {
                continue;
            }
            string rackEvent =
                InventoryDefinitions.IsHighSlot(change.Item.FlagId) ? TaskEvents.FittedHigh
                : InventoryDefinitions.IsMediumSlot(change.Item.FlagId) ? TaskEvents.FittedMedium
                : TaskEvents.FittedLow;
            PilotTaskTrackingStore.Record(world, catalog, pilotId, now, rackEvent);
        }
        var cascaded = FittingChanges.Finish(world, catalog, pilot, checkpoint, now);
        transaction.Commit();
        return result.Concat(cascaded).ToArray();

        (int First, int Count) SlotsFor(int typeId)
        {
            var definition =
                FittingCatalog.Definition(catalog, typeId)
                ?? throw new InvalidDataException("This module family or effect is not supported yet.");
            PilotSkillReader.CheckRequirements(world, catalog, pilotId, typeId);
            CheckHull(catalog, typeId, ship.TypeId);
            int first = definition.FirstFlag;
            int slots = capacity.Slots(definition.Rack);
            return (first, slots);
        }
    }

    public IReadOnlyList<InventoryChange> DestroyRig(long pilotId, long shipId, long itemId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var item = world.Set<OwnedItem>().Find(itemId);
        if (
            shipId != pilot.ActiveShipId
            || pilot.ActiveShip.Position is not null
            || item is null
            || item.OwnerId != pilotId
            || item.LocationId != shipId
            || !InventoryDefinitions.IsRigSlot(item.FlagId)
        )
        {
            throw new InvalidDataException("Remove an owned rig from your docked active ship.");
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var checkpoint = FittingChanges.Capture(world, catalog, pilot, now);
        var previous = PilotInventoryStore.Read(catalog, item);
        world.Remove(item);
        world.SaveChanges();
        var cascaded = FittingChanges.Finish(world, catalog, pilot, checkpoint, now);
        transaction.Commit();
        return new[] { new InventoryChange(previous with { Quantity = 0, Online = false }, previous) }.Concat(cascaded).ToArray();
    }

    public IReadOnlyList<InventoryChange> SetOnline(long pilotId, long shipId, long itemId, bool online)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var item = world
            .Set<OwnedItem>()
            .SingleOrDefault(i =>
                i.Id == itemId
                && i.OwnerId == pilotId
                && i.LocationId == shipId
                && i.FlagId >= (int)InventoryFlag.LowSlot0
                && i.FlagId <= (int)InventoryFlag.HighSlot7
            );
        if (shipId != pilot.ActiveShipId || item is null || pilot.ActiveShip.Position is not null)
        {
            throw new InvalidDataException("Change module online state on your docked active ship.");
        }
        var previous = PilotInventoryStore.List(world, catalog, pilotId, shipId).Single(i => i.Id == itemId);
        if (item.Online == online)
        {
            return [];
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var checkpoint = FittingChanges.Capture(world, catalog, pilot, now);
        if (online)
        {
            _ =
                FittingCatalog.Definition(catalog, item.TypeId)
                ?? throw new InvalidDataException("This module effect is not supported yet.");
            PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
            CheckHull(catalog, item.TypeId, pilot.ActiveShip.TypeId);
        }
        item.Online = online;
        if (online)
        {
            var calculation = FittingCalculation.Read(world, catalog, pilot);
            ModuleFittingConstraints.CheckOnline(FittingChanges.ReadConstraints(catalog, calculation));
            var attributes = ShipAttributes(world, catalog, pilot, calculation: calculation);
            if (
                attributes[DogmaAttributeId.CpuLoad] > attributes[DogmaAttributeId.CpuOutput] + InventoryDefinitions.ResourceTolerance
                || attributes[DogmaAttributeId.PowerLoad]
                    > attributes[DogmaAttributeId.PowerOutput] + InventoryDefinitions.ResourceTolerance
            )
            {
                throw new InvalidDataException("Insufficient CPU or power grid to bring this module online.");
            }
        }
        world.SaveChanges();
        var cascaded = FittingChanges.Finish(world, catalog, pilot, checkpoint, now);
        transaction.Commit();
        return new[] { new InventoryChange(previous with { Online = online }, previous) }.Concat(cascaded).ToArray();
    }

    public IReadOnlyList<FittingSkillBonus> SkillBonuses(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        var snapshot = world.CachedFittingSnapshot(pilotId, pilot.ActiveShipId, world.FitRevision(pilotId));
        var result = snapshot?.SkillBonuses ?? FittingCalculation.Read(world, catalog, pilot).SkillBonuses;
        transaction.Commit();
        return result;
    }

    internal static void CheckHull(CatalogDbContext catalog, int moduleType, int hullType)
    {
        if (!InventoryDefinitions.CanFitShip(ReadAttributes(catalog, moduleType), hullType, catalog.FittingType(hullType)!.GroupId))
        {
            throw new InvalidDataException("This module is restricted to a different ship type or class.");
        }
    }

    internal static IReadOnlyDictionary<DogmaAttributeId, double> ReadModuleAttributes(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        InventoryItem module,
        FittingCalculation? calculation = null
    )
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        var snapshot = calculation is null ? world.CachedFittingSnapshot(pilotId, pilot.ActiveShipId, world.FitRevision(pilotId)) : null;
        Dictionary<DogmaAttributeId, double> attributes;
        if (snapshot is not null)
        {
            attributes = snapshot.Modules.TryGetValue(module.Id, out var fitted)
                ? fitted.ToDictionary()
                : ReadAttributes(catalog, module.TypeId).ToDictionary();
        }
        else
        {
            calculation ??= FittingCalculation.Read(world, catalog, pilot);
            attributes = (
                calculation.Modules.Any(m => m.Item.Id == module.Id)
                    ? calculation.Graph.Attributes(module.Id)
                    : ReadAttributes(catalog, module.TypeId)
            ).ToDictionary();
        }
        // Deadlines are live state and must not be captured with the fitted graph.
        if (
            module.GroupId is TackleDefinitions.WarpCoreStabilizerGroup or SignatureSuppressionRules.Group
            || FittingCatalog.Effects(catalog, module.TypeId).Contains(DogmaEffect.AssaultDamageControl)
        )
        {
            attributes[DogmaAttributeId.NextActivationTime] = world.Set<ModuleCooldown>().Find(module.Id)?.ReadyAt ?? 0;
        }
        if (module.GroupId == CapacitorBoosterDefinitions.Group || MissileDefinitions.LauncherGroups.Contains(module.GroupId))
        {
            var cooldown = world.Set<ModuleCooldown>().Find(module.Id);
            attributes[DogmaAttributeId.NextActivationTime] = cooldown?.Kind == ModuleCooldownKind.Reload ? cooldown.ReadyAt : 0;
        }
        return attributes;
    }

    public IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var result = ReadModuleAttributes(world, catalog, pilotId, module);
        transaction.Commit();
        return result;
    }
}
