using System.Globalization;
using Eden.Protocol;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Repair;
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
        var attributes = Attributes(catalog, typeId);
        if (!attributes.ContainsKey(DogmaAttributeId.SignatureRadius) &&
            catalog.Set<SourceRecord>().Find("client/dogmaattributes", ((int)DogmaAttributeId.SignatureRadius).ToString(CultureInfo.InvariantCulture))
                ?.Read<ClientDogmaattributes>().DefaultValue is double signature)
        {
            attributes[DogmaAttributeId.SignatureRadius] = signature;
        }
        return attributes;
    }

    private static Dictionary<DogmaAttributeId, double> Attributes(CatalogDbContext catalog, int typeId) =>
        catalog.Set<TypeAttribute>().Where(a => a.ItemTypeId == typeId).ToDictionary(a => (DogmaAttributeId)a.AttributeId, a => a.Value);

    internal static Dictionary<DogmaAttributeId, double> ShipAttributes(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, Ship? ship = null)
    {
        ship ??= pilot.ActiveShip;
        var attributes = Attributes(catalog, ship.TypeId);
        // Catalog corvette role effects multiply the corresponding resonances.
        // A -8 percent bonus means 0.92 times damage, not eight resistance points.
        foreach (var (bonus, resonances) in new[]
        {
            (DogmaAttributeId.RookieShieldResistanceBonus, new[] { DogmaAttributeId.ShieldEmResonance, DogmaAttributeId.ShieldExplosiveResonance,
                DogmaAttributeId.ShieldKineticResonance, DogmaAttributeId.ShieldThermalResonance }),
            (DogmaAttributeId.RookieArmorResistanceBonus, new[] { DogmaAttributeId.ArmorEmResonance, DogmaAttributeId.ArmorExplosiveResonance,
                DogmaAttributeId.ArmorKineticResonance, DogmaAttributeId.ArmorThermalResonance }),
        })
        {
            foreach (var resonance in resonances)
            {
                attributes[resonance] = attributes.GetValueOrDefault(resonance, 1) * (1 + attributes.GetValueOrDefault(bonus) / 100);
            }
        }
        // These skills each grant five percent per trained level to their hull attribute.
        foreach (var (skill, output) in new[]
        {
            ((int)FittingSkill.CpuManagement, DogmaAttributeId.CpuOutput),
            ((int)FittingSkill.PowerGridManagement, DogmaAttributeId.PowerOutput),
            ((int)FittingSkill.CapacitorManagement, DogmaAttributeId.CapacitorCapacity),
            ((int)TargetingSkill.LongRangeTargeting, DogmaAttributeId.MaxTargetRange),
            ((int)TargetingSkill.SignatureAnalysis, DogmaAttributeId.ScanResolution),
            ((int)DefenseSkill.Mechanics, DogmaAttributeId.StructureHitPoints),
            ((int)DefenseSkill.HullUpgrades, DogmaAttributeId.ArmorHitPoints),
            ((int)DefenseSkill.ShieldManagement, DogmaAttributeId.ShieldCapacity),
        })
        {
            attributes[output] = attributes.GetValueOrDefault(output) * (1 + InventoryDefinitions.OutputBonusPerSkillLevel * SkillLevel(world, catalog, pilot.Id, skill));
        }
        attributes[DogmaAttributeId.CapacitorRechargeTime] = attributes.GetValueOrDefault(DogmaAttributeId.CapacitorRechargeTime) *
            (1 - InventoryDefinitions.OutputBonusPerSkillLevel * SkillLevel(world, catalog, pilot.Id, (int)FittingSkill.CapacitorSystemsOperation));
        attributes[DogmaAttributeId.ShieldRechargeTime] = attributes.GetValueOrDefault(DogmaAttributeId.ShieldRechargeTime) *
            (1 - InventoryDefinitions.OutputBonusPerSkillLevel * SkillLevel(world, catalog, pilot.Id, (int)DefenseSkill.ShieldOperation));
        int targetLimit = TargetingDefinitions.BasePilotTargetLimit + SkillLevel(world, catalog, pilot.Id, (int)TargetingSkill.TargetManagement)
            + SkillLevel(world, catalog, pilot.Id, (int)TargetingSkill.AdvancedTargetManagement);
        attributes[DogmaAttributeId.MaxLockedTargets] = Math.Min(attributes.GetValueOrDefault(DogmaAttributeId.MaxLockedTargets), targetLimit);
        var fitted = world.Set<OwnedItem>().Where(i => i.OwnerId == pilot.Id && i.LocationId == ship.Id && i.FlagId >= (int)InventoryFlag.MediumSlot0 && i.FlagId <= (int)InventoryFlag.HighSlot7).ToArray();
        attributes[DogmaAttributeId.CpuLoad] = fitted.Where(i => i.Online).Sum(i => Attributes(catalog, i.TypeId).GetValueOrDefault(DogmaAttributeId.Cpu));
        attributes[DogmaAttributeId.PowerLoad] = fitted.Where(i => i.Online).Sum(i => Attributes(catalog, i.TypeId).GetValueOrDefault(DogmaAttributeId.Power));
        attributes[DogmaAttributeId.TurretSlotsLeft] = Math.Max(0, attributes.GetValueOrDefault(DogmaAttributeId.TurretSlotsLeft) - fitted.Count(i => InventoryDefinitions.IsHighSlot(i.FlagId)));
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
            (flagId != (int)InventoryFlag.AutoFit && (!InventoryDefinitions.IsFittedSlot(flagId) || itemIds.Count != 1)))
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
        var occupied = world.Set<OwnedItem>().Where(i => i.LocationId == ship.Id && i.FlagId >= (int)InventoryFlag.MediumSlot0 && i.FlagId <= (int)InventoryFlag.HighSlot7).Select(i => i.FlagId).ToHashSet();
        long nextId = PilotInventoryStore.NextId(world);
        List<(OwnedItem Item, InventoryItem? Previous)> changed = [];
        foreach (var item in items)
        {
            // Regular weapons need ammunition and additional effects; the civilian
            // railgun has a built-in charge and shares turret/CPU/grid accounting.
            int group = catalog.Set<ItemType>().Single(t => t.Id == item.TypeId).GroupId;
            bool booster = group == ShieldBoostDefinitions.Group;
            if (group != InventoryDefinitions.MiningLaserGroup && !booster &&
                item.TypeId != (int)CombatWeaponType.CivilianGatlingRailgun)
            {
                throw new InvalidDataException("This module family is not supported yet.");
            }
            PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
            int first = (int)(booster ? InventoryFlag.MediumSlot0 : InventoryFlag.HighSlot0);
            int slots = Math.Clamp(checked((int)(booster ? hull.GetValueOrDefault(DogmaAttributeId.MediumSlots) :
                Math.Min(hull.GetValueOrDefault(DogmaAttributeId.HighSlots), hull.GetValueOrDefault(DogmaAttributeId.TurretSlotsLeft)))), 0, InventoryDefinitions.SlotsPerRack);
            int slot = flagId == (int)InventoryFlag.AutoFit ? Enumerable.Range(first, slots).FirstOrDefault(f => !occupied.Contains(f)) : flagId;
            if (slot < first || slot >= first + slots || !occupied.Add(slot))
            {
                throw new InvalidDataException("No compatible module slot is available.");
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
        var item = world.Set<OwnedItem>().SingleOrDefault(i => i.Id == itemId && i.OwnerId == pilotId && i.LocationId == shipId && i.FlagId >= (int)InventoryFlag.MediumSlot0 && i.FlagId <= (int)InventoryFlag.HighSlot7);
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
