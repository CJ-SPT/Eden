using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Weapons;
using Eden.World.Exploration;
using Eden.World.Fittings;
using Eden.World.Fleets;
using Eden.World.Inventory;
using Eden.World.Mining;
using Eden.World.Repair;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Ammunition;

internal sealed partial class PilotAmmunitionStore
{
    internal static OwnedItem? Contents(WorldAction world, long moduleId) =>
        world.Set<OwnedItem>().SingleOrDefault(i => i.LocationId == moduleId);

    internal static Magazine Read(WorldAction world, OwnedItem module, OwnedItem charge)
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

    private static Pilot Pilot(WorldAction world, long pilotId, long shipId)
    {
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        if (pilot.ActiveShipId != shipId)
        {
            throw new InvalidDataException("Load ammunition on your active ship.");
        }
        return pilot;
    }

    private static OwnedItem[] Modules(
        WorldAction world,
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
                Enumerable.Contains(ids, i.Id)
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
            int group = catalog.RequireItem(module.TypeId).GroupId;
            if (
                !(WeaponCatalog.Supported(catalog, module.TypeId, group) && InventoryDefinitions.IsHighSlot(module.FlagId))
                && !(
                    group == InventoryDefinitions.ModulatedMinerGroup
                    && MiningLaserCatalog.Supported(catalog, module.TypeId, group)
                    && InventoryDefinitions.IsHighSlot(module.FlagId)
                )
                && !(CommandBurstCatalog.Module(catalog, module.TypeId, group) && InventoryDefinitions.IsHighSlot(module.FlagId))
                && !(ProbeCatalog.Launcher(catalog, module.TypeId, group) && InventoryDefinitions.IsHighSlot(module.FlagId))
                && !(PlayerEwarCatalog.Supported(catalog, module.TypeId, group) && InventoryDefinitions.IsMediumSlot(module.FlagId))
                && !(SupportModuleCatalog.Supported(catalog, module.TypeId, group) && InventoryDefinitions.IsMediumSlot(module.FlagId))
                && !(RemoteAssistanceCatalog.Supported(catalog, module.TypeId, group) && InventoryDefinitions.IsMediumSlot(module.FlagId))
                && !(
                    RemoteRepairRules.AncillaryKind(group) is not null
                    && RemoteRepairCatalog.Supported(catalog, module.TypeId, group)
                    && InventoryDefinitions.IsHighSlot(module.FlagId)
                )
                && !(
                    AncillaryRepairCatalog.Supported(catalog, module.TypeId, group)
                    && (
                        group == AncillaryRepairRules.ArmorGroup
                            ? InventoryDefinitions.IsLowSlot(module.FlagId)
                            : InventoryDefinitions.IsMediumSlot(module.FlagId)
                    )
                )
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
            if (
                (
                    SupportModuleDefinitions.Effect(group) is not null
                    || PlayerEwarRules.Groups.Contains(group)
                    || RemoteAssistanceRules.Effect(group) is not null
                ) && world.Set<ActiveFittedModule>().Find(module.Id) is not null
            )
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

    private static void CheckCargo(WorldAction world, CatalogDbContext catalog, Pilot pilot)
    {
        double capacity = PilotInventoryStore.HoldCapacity(world, catalog, pilot.Id, pilot.ActiveShipId, (int)InventoryFlag.Cargo);
        double used = InventoryPersistence.UsedVolume(world, catalog, pilot.Id, pilot.ActiveShipId, (int)InventoryFlag.Cargo);
        if (used > capacity + InventoryDefinitions.ResourceTolerance)
        {
            throw new CargoCapacityException(0, used - capacity);
        }
    }

    internal static AmmunitionTransfer Load(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long shipId,
        IReadOnlyList<long> moduleIds,
        IReadOnlyList<long> chargeIds,
        long sourceId,
        long now
    )
    {
        var pilot = Pilot(world, pilotId, shipId);
        world.RequireUncloaked(shipId);
        int sourceFlag = sourceId == shipId ? (int)InventoryFlag.Cargo : (int)InventoryFlag.Hangar;
        if (
            (sourceId != shipId && (pilot.ActiveShip.Position is not null || sourceId != pilot.ActiveShip.DockedLocationId))
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
            .Where(i => Enumerable.Contains(chargeIds, i.Id) && i.OwnerId == pilotId && i.LocationId == sourceId && i.FlagId == sourceFlag)
            .OrderBy(i => i.Id)
            .ToArray();
        if (sources.Length != chargeIds.Count || sources.Select(i => i.TypeId).Distinct().Count() != 1)
        {
            throw new InvalidDataException("Choose available stacks of one ammunition type.");
        }
        int typeId = sources[0].TypeId;
        var chargeType = catalog.RequireItem(typeId);
        var chargeAttributes = Attributes(catalog, typeId);
        if (!AmmunitionCatalog.Supported(catalog, typeId, chargeType.GroupId))
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
            int moduleGroup = catalog.RequireItem(module.TypeId).GroupId;
            bool compatible =
                chargeType.GroupId is MiningCrystalRules.Group or MiningCrystalRules.MercoxitGroup
                    ? MiningCrystalRules.Compatible(attributes, chargeType.GroupId)
                : CommandBurstRules.IsCharge(chargeType.GroupId) ? CommandBurstRules.Compatible(moduleGroup, chargeType.GroupId, attributes)
                : chargeType.GroupId is ProbeScanRules.ProbeGroup or (int)HoldCargoGroup.SurveyProbe
                    ? attributes.GetValueOrDefault(DogmaAttributeId.ChargeGroup1) == chargeType.GroupId
                        && ProbeCatalog.Compatible(attributes, chargeAttributes)
                : AncillaryRepairRules.Kind(moduleGroup) is { } ancillary
                    ? AncillaryRepairRules.Compatible(ancillary, chargeType.GroupId, attributes, chargeAttributes)
                : RemoteRepairRules.AncillaryKind(moduleGroup) is { } remoteAncillary
                    ? AncillaryRepairRules.Compatible(
                        remoteAncillary,
                        chargeType.GroupId,
                        attributes,
                        chargeAttributes,
                        DogmaAttributeId.ChargeGroup1
                    )
                : script ? SupportModuleDefinitions.CompatibleScript(moduleGroup, chargeType.GroupId, attributes, chargeAttributes)
                : chargeType.GroupId == CapacitorBoosterDefinitions.ChargeGroup
                    ? attributes.GetValueOrDefault(DogmaAttributeId.ChargeGroup1) == chargeType.GroupId
                : MissileDefinitions.ChargeGroups.Contains(chargeType.GroupId)
                    ? MissileDefinitions.CompatibleCharge(attributes, chargeType.GroupId, chargeAttributes)
                : AmmunitionDefinitions.CompatibleTurretCharge(attributes, chargeType.GroupId, chargeAttributes);
            if (!compatible)
            {
                throw new InvalidDataException("The ammunition group or size does not match the weapon.");
            }
            double capacity = catalog.RequireItem(module.TypeId).Capacity ?? 0;
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
        return new(changes, magazines, duration);
    }
}
