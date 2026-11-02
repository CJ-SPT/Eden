using Eden.Protocol;
using Eden.Protocol.Docking;
using Eden.Protocol.Skills;
using Eden.Protocol.Targeting;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotFittingStore(WorldDatabase database, IPilotInventory inventory) : IPilotFitting
{
    public IReadOnlyDictionary<DogmaAttributeId, double> Attributes(int typeId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return Attributes(catalog, typeId);
    }

    private static Dictionary<DogmaAttributeId, double> Attributes(CatalogDbContext catalog, int typeId) =>
        catalog.Set<TypeAttribute>().Where(a => a.ItemTypeId == typeId).ToDictionary(a => (DogmaAttributeId)a.AttributeId, a => a.Value);

    private static Dictionary<DogmaAttributeId, double> ShipAttributes(WorldDbContext world, CatalogDbContext catalog, Pilot pilot)
    {
        var attributes = Attributes(catalog, pilot.ActiveShip.TypeId);
        // These four skills each grant five percent per trained level to their hull attribute.
        foreach (var (skill, output) in new[]
        {
            ((int)FittingSkill.CpuManagement, DogmaAttributeId.CpuOutput),
            ((int)FittingSkill.PowerGridManagement, DogmaAttributeId.PowerOutput),
            ((int)TargetingSkill.LongRangeTargeting, DogmaAttributeId.MaxTargetRange),
            ((int)TargetingSkill.SignatureAnalysis, DogmaAttributeId.ScanResolution),
        })
        {
            attributes[output] = attributes.GetValueOrDefault(output) * (1 + InventoryDefinitions.OutputBonusPerSkillLevel * SkillLevel(world, catalog, pilot.Id, skill));
        }
        int targetLimit = TargetingDefinitions.BasePilotTargetLimit + SkillLevel(world, catalog, pilot.Id, (int)TargetingSkill.TargetManagement)
            + SkillLevel(world, catalog, pilot.Id, (int)TargetingSkill.AdvancedTargetManagement);
        attributes[DogmaAttributeId.MaxLockedTargets] = Math.Min(attributes.GetValueOrDefault(DogmaAttributeId.MaxLockedTargets), targetLimit);
        var fitted = world.Set<OwnedItem>().Where(i => i.OwnerId == pilot.Id && i.LocationId == pilot.ActiveShipId && i.FlagId >= (int)InventoryFlag.HighSlot0 && i.FlagId <= (int)InventoryFlag.HighSlot7).ToArray();
        attributes[DogmaAttributeId.CpuLoad] = fitted.Where(i => i.Online).Sum(i => Attributes(catalog, i.TypeId).GetValueOrDefault(DogmaAttributeId.Cpu));
        attributes[DogmaAttributeId.PowerLoad] = fitted.Where(i => i.Online).Sum(i => Attributes(catalog, i.TypeId).GetValueOrDefault(DogmaAttributeId.Power));
        attributes[DogmaAttributeId.TurretSlotsLeft] = Math.Max(0, attributes.GetValueOrDefault(DogmaAttributeId.TurretSlotsLeft) - fitted.Length);
        return attributes;
    }

    private static int SkillLevel(WorldDbContext world, CatalogDbContext catalog, long pilotId, int skill)
    {
        long points = world.Set<PilotSkill>().Find(pilotId, skill)?.Points ?? 0;
        double rank = Attributes(catalog, skill).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant, 1);
        return new TrainedSkill(skill, points, rank).Level;
    }

    public IReadOnlyDictionary<DogmaAttributeId, double> ShipAttributes(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return ShipAttributes(world, catalog, world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId));
    }

    public IReadOnlyList<InventoryChange> Fit(long pilotId, IReadOnlyList<long> itemIds, long sourceId, int flagId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var ship = pilot.ActiveShip;
        if (ship.Position is not null || (sourceId != ship.Id && sourceId != ship.StationId) ||
            itemIds.Count == 0 || itemIds.Distinct().Count() != itemIds.Count ||
            (flagId != (int)InventoryFlag.AutoFit && (!InventoryDefinitions.IsHighSlot(flagId) || itemIds.Count != 1)))
        {
            throw new InvalidDataException("Fit equipment from your cargo or current hangar while docked.");
        }
        var items = world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && itemIds.Contains(i.Id) && i.LocationId == sourceId &&
            i.FlagId == (sourceId == ship.Id ? (int)InventoryFlag.Cargo : (int)InventoryFlag.Hangar)).OrderBy(i => i.Id).ToArray();
        if (items.Length != itemIds.Count)
        {
            throw new InvalidDataException("Equipment is unavailable.");
        }
        var before = inventory.List(pilotId, sourceId).ToDictionary(i => i.Id);
        var hull = Attributes(catalog, ship.TypeId);
        var occupied = world.Set<OwnedItem>().Where(i => i.LocationId == ship.Id && i.FlagId >= (int)InventoryFlag.HighSlot0 && i.FlagId <= (int)InventoryFlag.HighSlot7).Select(i => i.FlagId).ToHashSet();
        int slots = Math.Min((int)InventoryFlag.HighSlot7 - (int)InventoryFlag.HighSlot0 + 1, checked((int)Math.Min(hull.GetValueOrDefault(DogmaAttributeId.HighSlots), hull.GetValueOrDefault(DogmaAttributeId.TurretSlotsLeft))));
        long nextId = (world.Set<OwnedItem>().Max(i => (long?)i.Id) ?? InventoryDefinitions.FirstOwnedItemId) + 1;
        List<(OwnedItem Item, InventoryItem? Previous)> changed = [];
        foreach (var item in items)
        {
            // Mining lasers are the first supported family. Other modules can have passive
            // modifiers that must be implemented before they can safely be fitted.
            if (catalog.Set<ItemType>().Single(t => t.Id == item.TypeId).GroupId != InventoryDefinitions.MiningLaserGroup)
            {
                throw new InvalidDataException("This module family is not supported yet.");
            }
            var attributes = Attributes(catalog, item.TypeId);
            foreach (var (skillAttribute, levelAttribute) in InventoryDefinitions.RequiredSkills)
            {
                int skill = checked((int)attributes.GetValueOrDefault(skillAttribute));
                if (skill == 0) { continue; }
                long points = world.Set<PilotSkill>().Find(pilotId, skill)?.Points ?? 0;
                double rank = Attributes(catalog, (int)skill).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant, 1);
                if (new TrainedSkill((int)skill, points, rank).Level < attributes.GetValueOrDefault(levelAttribute))
                {
                    throw new InvalidDataException("The pilot has not trained the required module skills.");
                }
            }
            int slot = flagId == (int)InventoryFlag.AutoFit ? Enumerable.Range((int)InventoryFlag.HighSlot0, slots).FirstOrDefault(f => !occupied.Contains(f)) : flagId;
            if (slot < (int)InventoryFlag.HighSlot0 || slot >= (int)InventoryFlag.HighSlot0 + slots || !occupied.Add(slot))
            {
                throw new InvalidDataException("No compatible turret slot is available.");
            }
            var previous = before[item.Id];
            OwnedItem fitted = item;
            if (item.Quantity > 1)
            {
                item.Quantity--;
                changed.Add((item, previous));
                fitted = world.Set<OwnedItem>().Add(new OwnedItem { Id = nextId++, OwnerId = pilotId, TypeId = item.TypeId, Quantity = 1 }).Entity;
                previous = null;
            }
            fitted.LocationId = ship.Id;
            fitted.FlagId = slot;
            fitted.Singleton = true;
            fitted.Online = false;
            changed.Add((fitted, previous));
        }
        world.SaveChanges();
        var result = changed.Select(c => new InventoryChange(
            before.Values.First(i => i.TypeId == c.Item.TypeId) with
            {
                Id = c.Item.Id,
                LocationId = c.Item.LocationId,
                FlagId = c.Item.FlagId,
                Quantity = c.Item.Quantity,
                Singleton = c.Item.Singleton,
                Online = c.Item.Online,
            }, c.Previous)).ToArray();
        transaction.Commit();
        return result;
    }

    public InventoryChange SetOnline(long pilotId, long shipId, long itemId, bool online)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var item = world.Set<OwnedItem>().SingleOrDefault(i => i.Id == itemId && i.OwnerId == pilotId && i.LocationId == shipId && i.FlagId >= (int)InventoryFlag.HighSlot0 && i.FlagId <= (int)InventoryFlag.HighSlot7);
        if (shipId != pilot.ActiveShipId || item is null || pilot.ActiveShip.Position is not null)
        {
            throw new InvalidDataException("Change module online state on your docked active ship.");
        }
        var previous = inventory.List(pilotId, shipId).Single(i => i.Id == itemId);
        if (online && !item.Online)
        {
            var ship = ShipAttributes(world, catalog, pilot);
            var module = Attributes(catalog, item.TypeId);
            if (ship[DogmaAttributeId.CpuLoad] + module.GetValueOrDefault(DogmaAttributeId.Cpu) > ship[DogmaAttributeId.CpuOutput] + InventoryDefinitions.ResourceTolerance || ship[DogmaAttributeId.PowerLoad] + module.GetValueOrDefault(DogmaAttributeId.Power) > ship[DogmaAttributeId.PowerOutput] + InventoryDefinitions.ResourceTolerance)
            {
                throw new InvalidDataException("Insufficient CPU or power grid to bring this module online.");
            }
        }
        item.Online = online;
        world.SaveChanges();
        transaction.Commit();
        return new(previous with { Online = online }, previous);
    }
}
