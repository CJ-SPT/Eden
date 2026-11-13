using System.Collections.Frozen;
using System.Data;
using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Data.World.Skills;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Targeting;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Combat.Weapons;
using Eden.World.Defenses;
using Eden.World.Inventory;
using Eden.World.Mining;
using Eden.World.Propulsion;
using Eden.World.Repair;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Fittings;

internal sealed partial record FittingCalculation
{
    internal static FittingCalculation Read(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        Ship? ship = null,
        int? ownerTypeId = null,
        IReadOnlySet<long>? overloadedModules = null
    )
    {
        // A paid-cycle owner supplies its explicit overload selection. Neither
        // the module snapshot nor derived ship values may enter the normal cache.
        string? revision = ownerTypeId is null && overloadedModules is null ? world.FitRevision(pilot.Id) : null;
        SessionPerformance.FittingCalculated();
        ship ??= pilot.ActiveShip;
        if (overloadedModules is not null && ship.OwnerId != pilot.Id)
        {
            throw new InvalidDataException("Overload selection requires an owned ship.");
        }
        var hull = catalog.FittingType(ship.TypeId)!;
        var hullAttributes = PilotFittingStore.ReadAttributes(catalog, ship.TypeId).ToDictionary();
        hullAttributes[DogmaAttributeId.Mass] = hull.Mass ?? 0;
        hullAttributes[DogmaAttributeId.CargoCapacity] = hull.Capacity ?? 0;
        var items = world
            .Set<OwnedItem>()
            .Where(i =>
                i.OwnerId == pilot.Id
                && i.LocationId == ship.Id
                && (
                    (i.FlagId >= (int)InventoryFlag.LowSlot0 && i.FlagId <= (int)InventoryFlag.HighSlot7)
                    || (i.FlagId >= (int)InventoryFlag.RigSlot0 && i.FlagId <= (int)InventoryFlag.RigSlot2)
                )
            )
            .ToArray();
        var moduleAttributes = items
            .Select(i => i.TypeId)
            .Distinct()
            .ToDictionary(id => id, id => PilotFittingStore.ReadAttributes(catalog, id));
        var modules = items
            .Select(i => new FittingLocation(
                i,
                catalog.FittingType(i.TypeId)!.GroupId,
                InventoryDefinitions
                    .RequiredSkills.Select(p => moduleAttributes[i.TypeId].GetValueOrDefault(p.Skill))
                    .Where(v => v > 0)
                    .Select(v => checked((int)v))
                    .ToHashSet()
            ))
            .ToArray();
        var moduleIds = items.Select(i => i.Id).ToArray();
        var overloads = overloadedModules?.ToFrozenSet() ?? FrozenSet<long>.Empty;
        if (overloads.Except(moduleIds).Any())
        {
            throw new InvalidDataException("Overload selection must belong to this owned ship's fitted modules.");
        }
        var loaded = world.Set<OwnedItem>().Where(i => i.OwnerId == pilot.Id && Enumerable.Contains(moduleIds, i.LocationId)).ToArray();
        var chargeAttributes = loaded
            .Select(i => i.TypeId)
            .Distinct()
            .ToDictionary(id => id, id => PilotFittingStore.ReadAttributes(catalog, id));
        var charges = loaded
            .Select(i => new FittingLocation(
                i,
                catalog.FittingType(i.TypeId)!.GroupId,
                InventoryDefinitions
                    .RequiredSkills.Select(p => chargeAttributes[i.TypeId].GetValueOrDefault(p.Skill))
                    .Where(v => v > 0)
                    .Select(v => checked((int)v))
                    .ToHashSet()
            ))
            .ToArray();
        var locations = modules.Concat(charges).ToArray();
        List<DogmaNode> nodes =
        [
            new(ship.Id, hullAttributes),
            new(
                pilot.Id,
                new Dictionary<DogmaAttributeId, double>
                {
                    [DogmaAttributeId.MissileDamageMultiplier] = 1,
                    [DogmaAttributeId.DroneControlDistance] = DroneRules.BaseControlRange,
                    [DogmaAttributeId.MaxActiveDrones] = 0,
                }
            ),
        ];
        nodes.AddRange(loaded.Select(i => new DogmaNode(i.Id, chargeAttributes[i.TypeId])));
        nodes.AddRange(items.Select(i => new DogmaNode(i.Id, moduleAttributes[i.TypeId])));
        // Pinned IsOwnerModifiable excludes fitted modules. Pilot-owner modifiers
        // select loaded ammunition and explicit deployed-drone/fighter profiles.
        // Including modules lets mixed weapon/drone upgrades modify their own source bonuses.
        FittingLocation[] ownerTargets = charges;
        if (ownerTypeId is int ownerType)
        {
            var ownerAttributes = PilotFittingStore.ReadAttributes(catalog, ownerType);
            nodes.Add(new(OwnerTargetId, ownerAttributes));
            ownerTargets =
            [
                .. charges,
                new(
                    new OwnedItem { Id = OwnerTargetId, TypeId = ownerType },
                    catalog.FittingType(ownerType)!.GroupId,
                    InventoryDefinitions
                        .RequiredSkills.Select(p => checked((int)ownerAttributes.GetValueOrDefault(p.Skill)))
                        .Where(id => id > 0)
                        .ToHashSet()
                ),
            ];
        }
        List<DogmaModifier> modifiers = [];
        foreach (var charge in charges.Where(c => c.GroupId is MiningCrystalRules.Group or MiningCrystalRules.MercoxitGroup))
        {
            var parent = modules.Single(m => m.Item.Id == charge.Item.LocationId);
            if (
                !MiningLaserCatalog.Supported(catalog, parent.Item.TypeId, parent.GroupId)
                || !MiningCrystalCatalog.Supported(catalog, charge.Item.TypeId, charge.GroupId)
                || !MiningCrystalRules.Compatible(moduleAttributes[parent.Item.TypeId], charge.GroupId)
                || charge.Item.Quantity != 1
            )
            {
                throw new InvalidDataException("Invalid fitted mining crystal.");
            }
            foreach (var modifier in MiningCrystalRules.Modifiers)
            {
                // Older crystal definitions omit the duration program.
                if (!FittingCatalog.Effects(catalog, charge.Item.TypeId).Contains((DogmaEffect)modifier.EffectId))
                {
                    continue;
                }
                modifiers.Add(
                    new(
                        parent.Item.Id,
                        modifier.Attribute,
                        modifier.Operation,
                        new DogmaValue.Attribute(charge.Item.Id, modifier.Source),
                        true
                    )
                );
            }
        }
        foreach (var charge in charges.Where(c => SupportModuleDefinitions.IsScript(c.GroupId)))
        {
            var parent = modules.Single(m => m.Item.Id == charge.Item.LocationId);
            if (
                !(
                    SupportModuleCatalog.Supported(catalog, parent.Item.TypeId, parent.GroupId)
                    || PlayerEwarCatalog.Supported(catalog, parent.Item.TypeId, parent.GroupId)
                    || RemoteAssistanceCatalog.Supported(catalog, parent.Item.TypeId, parent.GroupId)
                )
                || !ScriptCatalog.Supported(catalog, charge.Item.TypeId, charge.GroupId)
                || !SupportModuleDefinitions.CompatibleScript(
                    parent.GroupId,
                    charge.GroupId,
                    moduleAttributes[parent.Item.TypeId],
                    chargeAttributes[charge.Item.TypeId]
                )
                || charge.Item.Quantity != 1
            )
            {
                throw new InvalidDataException("Invalid fitted support module script.");
            }
            foreach (var modifier in ScriptCatalog.Modifiers(catalog, charge.Item.TypeId))
            {
                // Scripts modify their containing module, independently of the
                // stacking sequence applied by that module to equipment/ship.
                modifiers.Add(
                    new(
                        parent.Item.Id,
                        modifier.Attribute,
                        modifier.Operation,
                        new DogmaValue.Attribute(charge.Item.Id, modifier.SourceAttribute),
                        true
                    )
                );
            }
        }
        bool catalogHull = HullCatalog.UsesCatalogBonuses(catalog, ship.TypeId);
        foreach (var role in catalogHull ? [] : FittingRules.RookieResistanceModifiers)
        {
            hullAttributes.TryAdd(role.Resonance, 1);
            modifiers.Add(new(ship.Id, role.Resonance, DogmaOperation.PostPercent, new DogmaValue.Attribute(ship.Id, role.Bonus), true));
        }
        // Build 3662168 effect 5000 applies the Ibis role to pilot-owned
        // missile ammunition, independently of launcher damage upgrades.
        foreach (
            var charge in charges.Where(c =>
                !catalogHull
                && MissileDefinitions.ChargeGroups.Contains(c.GroupId)
                && c.RequiredSkills.Contains((int)MissileSkill.MissileLauncherOperation)
            )
        )
        {
            modifiers.Add(
                new(
                    charge.Item.Id,
                    DogmaAttributeId.KineticDamage,
                    DogmaOperation.PostPercent,
                    new DogmaValue.Attribute(ship.Id, DogmaAttributeId.RookieMissileKineticBonus),
                    true
                )
            );
        }
        var activeModules = world.Set<ActiveFittedModule>().Where(a => a.ShipId == ship.Id).ToDictionary(a => a.ModuleId, a => a.ExpiresAt);
        var incoming = world
            .Set<WorldTackle>()
            .Where(t => t.TargetId == ship.Id && t.TargetKind == TackleTargetKind.Ship)
            .AsEnumerable()
            .Select(t => t.Module())
            .ToArray();
        modifiers.AddRange(TackleDefinitions.Modifiers(ship.Id, incoming));
        var gunneryModules = modules.Where(m => m.RequiredSkills.Contains((int)CombatSkill.Gunnery)).Select(m => m.Item.Id).ToArray();
        foreach (var assistance in PilotRemoteAssistanceStore.Active(world, ship.Id))
        {
            modifiers.AddRange(RemoteAssistanceRules.Modifiers(ship.Id, gunneryModules, assistance.Module, assistance.EdgeDistance));
        }
        var npcEwar = NpcEwarStore.Active(world, ship.Id);
        var turrets = modules
            .Where(m =>
                m.GroupId
                    is CombatDefinitions.EnergyWeaponGroup
                        or CombatDefinitions.HybridWeaponGroup
                        or CombatDefinitions.ProjectileWeaponGroup
                || m.Item.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun
            )
            .Select(m => m.Item.Id)
            .ToArray();
        modifiers.AddRange(
            PlayerEwarRules.Modifiers(
                ship.Id,
                turrets,
                charges
                    .Where(c => chargeAttributes[c.Item.TypeId].ContainsKey(DogmaAttributeId.ExplosionRadius))
                    .Select(c => c.Item.Id)
                    .ToArray(),
                PlayerEwarStore.Active(world, ship.Id)
            )
        );
        modifiers.AddRange(
            NpcEwarRules.Modifiers(
                ship.Id,
                turrets,
                npcEwar,
                charges
                    .Where(c => chargeAttributes[c.Item.TypeId].ContainsKey(DogmaAttributeId.ExplosionRadius))
                    .Select(c => c.Item.Id)
                    .ToArray()
            )
        );
        foreach (var module in modules.Where(m => m.Item.Online && incoming.Any(t => t.MwdBlock > 0)))
        {
            if (FittingCatalog.Effects(catalog, module.Item.TypeId).Contains(DogmaEffect.Microwarpdrive))
            {
                modifiers.Add(
                    new(
                        module.Item.Id,
                        DogmaAttributeId.ActivationBlocked,
                        DogmaOperation.Add,
                        new DogmaValue.Constant(incoming.Sum(t => t.MwdBlock)),
                        true
                    )
                );
            }
        }
        foreach (var module in modules)
        {
            if (SpatialModuleCatalog.Supported(catalog, module.Item.TypeId, module.GroupId))
            {
                foreach (var modifier in SpatialModuleCatalog.Modifiers(catalog, module.Item.TypeId))
                {
                    if (
                        modifier.Phase == PassiveEffectPhase.Fitted
                        || module.Item.Online && (modifier.Phase == PassiveEffectPhase.Online || activeModules.ContainsKey(module.Item.Id))
                    )
                    {
                        Add(module.Item.Id, modifier, false);
                    }
                }
                if (module.Item.Online && module.GroupId == SpatialModuleRules.MicroJumpGroup && activeModules.ContainsKey(module.Item.Id))
                {
                    modifiers.Add(
                        new(
                            ship.Id,
                            DogmaAttributeId.SignatureRadius,
                            DogmaOperation.PostPercent,
                            new DogmaValue.Attribute(module.Item.Id, DogmaAttributeId.SignatureRadiusBonusPercent)
                        )
                    );
                }
            }
            if (
                module.Item.Online
                && module.GroupId == AssaultDamageControlRules.Group
                && AssaultDamageControlCatalog.Supported(catalog, module.Item.TypeId, module.GroupId)
            )
            {
                var control = AssaultDamageControlRules.Define(module.Item.Id, module.Item.TypeId, moduleAttributes[module.Item.TypeId]);
                modifiers.AddRange(AssaultDamageControlRules.Modifiers(control, ship.Id, true, activeModules.ContainsKey(module.Item.Id)));
            }
            if (
                module.Item.Online
                && module.GroupId == SignatureSuppressionRules.Group
                && SignatureSuppressionCatalog.Supported(catalog, module.Item.TypeId, module.GroupId)
            )
            {
                foreach (var modifier in SignatureSuppressionCatalog.Modifiers(catalog, module.Item.TypeId))
                {
                    if (modifier.Phase == PassiveEffectPhase.Online || activeModules.ContainsKey(module.Item.Id))
                    {
                        Add(module.Item.Id, modifier, false);
                    }
                }
            }
            if (InventoryDefinitions.IsRigSlot(module.Item.FlagId))
            {
                if (!RigCatalog.Supported(catalog, module.Item.TypeId, module.GroupId))
                {
                    throw new InvalidDataException("Unsupported fitted ship rig.");
                }
                foreach (var modifier in RigCatalog.Modifiers(catalog, module.Item.TypeId))
                {
                    Add(module.Item.Id, modifier, false);
                }
                continue;
            }
            if (
                module.Item.Online
                && module.GroupId == TackleDefinitions.WarpCoreStabilizerGroup
                && TackleCatalog.Supported(catalog, module.Item.TypeId, module.GroupId)
            )
            {
                foreach (var modifier in TackleCatalog.CoreModifiers(catalog, module.Item.TypeId))
                {
                    if (modifier.Phase == PassiveEffectPhase.Online || activeModules.ContainsKey(module.Item.Id))
                    {
                        Add(module.Item.Id, modifier, false);
                    }
                }
            }
            if (
                module.Item.Online
                && activeModules.ContainsKey(module.Item.Id)
                && SupportModuleCatalog.Supported(catalog, module.Item.TypeId, module.GroupId)
            )
            {
                foreach (var modifier in SupportModuleCatalog.Modifiers(catalog, module.Item.TypeId, module.GroupId))
                {
                    Add(module.Item.Id, modifier, false);
                }
            }
            if (RemoteRepairCatalog.Supported(catalog, module.Item.TypeId, module.GroupId))
            {
                foreach (var modifier in RemoteRepairCatalog.Modifiers(catalog, module.Item.TypeId))
                {
                    Add(module.Item.Id, modifier, true);
                }
            }
            if (WeaponCatalog.Supported(catalog, module.Item.TypeId, module.GroupId))
            {
                foreach (var modifier in WeaponCatalog.Modifiers(catalog, module.Item.TypeId))
                {
                    Add(module.Item.Id, modifier, true);
                }
            }
            if (
                module.Item.Online
                && activeModules.ContainsKey(module.Item.Id)
                && HardenerCatalog.Supported(catalog, module.Item.TypeId, module.GroupId)
            )
            {
                foreach (var modifier in HardenerCatalog.Modifiers(catalog, module.Item.TypeId))
                {
                    Add(module.Item.Id, modifier, false);
                }
            }
            if (module.Item.Online && MicrowarpdriveCatalog.Supported(catalog, module.Item.TypeId, module.GroupId))
            {
                foreach (var modifier in PassiveCatalog.Modifiers(catalog, module.Item.TypeId))
                {
                    Add(module.Item.Id, modifier, false);
                }
                if (activeModules.ContainsKey(module.Item.Id))
                {
                    Add(module.Item.Id, MicrowarpdriveCatalog.SignatureModifier, false);
                }
            }
            if (
                !PassiveCatalog.Supported(catalog, module.Item.TypeId, module.GroupId)
                && !Eden.World.Information.InformationCatalog.Supported(catalog, module.Item.TypeId, module.GroupId)
                && !Eden.World.Exploration.ProbeCatalog.Launcher(catalog, module.Item.TypeId, module.GroupId)
            )
            {
                continue;
            }
            foreach (var modifier in PassiveCatalog.Modifiers(catalog, module.Item.TypeId))
            {
                if (modifier.Phase == PassiveEffectPhase.Fitted || module.Item.Online)
                {
                    Add(module.Item.Id, modifier, false);
                }
            }
        }
        foreach (var modifier in HullCatalog.Modifiers(catalog, ship.TypeId))
        {
            Add(ship.Id, modifier, true);
        }
        List<(int Skill, PassiveModifier Modifier)> skillModifiers = [];
        // MiningRules owns work-skill yield. Astrogeology is also a direct barge
        // prerequisite and must not enter the hull graph as a second multiplier.
        var hullSkills = HullCatalog.Skills(catalog, ship.TypeId).Where(skill => !MiningDefinitions.OwnsYieldSkill(skill)).ToHashSet();
        var relevantSkills = PassiveModules
            .Skills.Concat(MissileDefinitions.Skills)
            .Concat(Eden.Gameplay.Exploration.ProbeScanRules.Skills)
            .Concat([(int)MiningSkill.GasHarvesting, (int)MiningSkill.IceHarvesting])
            .Concat([Eden.Gameplay.Exploration.HackingRules.HackingSkill, Eden.Gameplay.Exploration.HackingRules.ArchaeologySkill])
            .Concat(Enum.GetValues<ElectronicWarfareSkill>().Select(s => (int)s))
            .Concat(hullSkills)
            .Concat(Enum.GetValues<RemoteRepairSkill>().Select(s => (int)s))
            .Concat(Enum.GetValues<Eden.Gameplay.Information.InformationSkill>().Select(s => (int)s))
            .Concat(Enum.GetValues<CapacitorEmissionSkill>().Select(s => (int)s))
            .Concat(Enum.GetValues<RiggingSkill>().Select(s => (int)s))
            .Concat([OverloadRules.ThermodynamicsSkill, SpatialModuleRules.MicroJumpSkill, SmartbombRules.EnergyPulseWeaponsSkill])
            .Concat(Enum.GetValues<NaniteRepairSkill>().Select(s => (int)s))
            // Drone work/damage skills remain owned by DroneRules.ApplySkills.
            .Concat([(int)DroneSkill.Drones, (int)DroneSkill.DroneAvionics, (int)DroneSkill.AdvancedDroneAvionics])
            .Concat(
                catalog
                    .Definitions.PublishedSkills.Values.Where(skill => skill.UserCreated && !skill.Deleted)
                    .Select(skill => skill.Definition.TypeId)
            )
            .Distinct()
            .ToArray();
        var trained = world
            .Set<PilotSkill>()
            .Where(s => s.PilotId == pilot.Id && Enumerable.Contains(relevantSkills, s.TypeId))
            .Select(s => s.TypeId)
            .ToHashSet();
        var skillIds = relevantSkills
            .Where(skill => Enumerable.Contains(trained, skill) || Enumerable.Contains(hullSkills, skill))
            .Concat(Enum.GetValues<FittingSkill>().Select(s => (int)s))
            .Concat(Enum.GetValues<DefenseSkill>().Select(s => (int)s))
            .Concat(Enum.GetValues<TargetingSkill>().Select(s => (int)s))
            .Distinct()
            .ToArray();
        var levels = PilotSkillReader.Levels(world, catalog, pilot.Id, skillIds);
        foreach (int skill in relevantSkills.Where(skill => Enumerable.Contains(trained, skill) || Enumerable.Contains(hullSkills, skill)))
        {
            int level = levels.GetValueOrDefault(skill);
            if (level == 0 && !Enumerable.Contains(hullSkills, skill))
            {
                continue;
            }
            var attributes = PilotFittingStore.ReadAttributes(catalog, skill).ToDictionary();
            attributes[DogmaAttributeId.SkillLevel] = level;
            nodes.Add(new(skill, attributes));
            var modifiersForSkill = skill switch
            {
                OverloadRules.ThermodynamicsSkill => OverloadCatalog.ThermodynamicsModifiers(catalog),
                (int)NaniteRepairSkill.Operation or (int)NaniteRepairSkill.Interfacing => NaniteRepairCatalog.SkillModifiers(
                    catalog,
                    skill
                ),
                _ => PassiveCatalog.Modifiers(catalog, skill),
            };
            foreach (var modifier in modifiersForSkill)
            {
                Add(skill, modifier, true);
                if (modifier.Domain != ModifierDomain.Item)
                {
                    skillModifiers.Add((skill, modifier));
                }
            }
        }
        // Local hardener and support markers carry the funded overload interval.
        // Their modifier sources must also reach ship, weapon and drone consumers.
        foreach (
            var module in modules.Where(m => activeModules.GetValueOrDefault(m.Item.Id) > 0 && !Enumerable.Contains(overloads, m.Item.Id))
        )
        {
            if (HardenerDefinitions.Effect(module.GroupId) is null && SupportModuleDefinitions.Effect(module.GroupId) is null)
            {
                continue;
            }
            if (OverloadCatalog.Definition(catalog, module.Item.TypeId) is { } profile)
            {
                modifiers.AddRange(OverloadRules.Modifiers(profile, module.Item.Id, true));
            }
        }
        foreach (var module in modules.Where(m => Enumerable.Contains(overloads, m.Item.Id)))
        {
            var profile = OverloadCatalog.Definition(catalog, module.Item.TypeId);
            if (
                profile is null
                || !module.Item.Online
                || levels.GetValueOrDefault(OverloadRules.ThermodynamicsSkill) < profile.RequiredThermodynamics
                || ModuleConditionPersistence.Read(world, catalog, module.Item).BurnedOut
            )
            {
                throw new InvalidDataException("Overload requires a working online supported module and the earned Thermodynamics level.");
            }
            modifiers.AddRange(OverloadRules.Modifiers(profile, module.Item.Id, true));
        }
        var requested = modifiers
            .Select(m => m.Attribute)
            .Concat([DogmaAttributeId.ModuleRepairRate, DogmaAttributeId.ModuleRepairCostMultiplier])
            .Concat(modifiers.SelectMany(m => DogmaValue.References(m.Source)).Select(v => v.AttributeId))
            .ToHashSet();
        Dictionary<DogmaAttributeId, DogmaAttributeDefinition> definitions = [];
        while (requested.Count > 0)
        {
            var rows = requested
                .Select(id => new FittingAttributeCatalogEntry(id, catalog.Read(CatalogData.Client.Dogmaattributes, (long)id)))
                .Where(entry => entry.Definition is not null)
                .ToArray();
            foreach (var id in requested)
            {
                definitions[id] = new(id, 0);
            }
            requested.Clear();
            foreach (var row in rows)
            {
                var a = row.Definition!;
                var id = row.Id;
                var definition = new DogmaAttributeDefinition(
                    id,
                    a.DefaultValue ?? 0,
                    a.Stackable != 0,
                    a.MaxAttributeID is long max ? (DogmaAttributeId)checked((int)max) : null,
                    a.MinAttributeID is long min ? (DogmaAttributeId)checked((int)min) : null,
                    a.DataType is 10 or 11 or 12
                );
                definitions[id] = definition;
                foreach (var bound in new[] { definition.Maximum, definition.Minimum })
                {
                    if (bound is { } limit && !definitions.ContainsKey(limit))
                    {
                        requested.Add(limit);
                    }
                }
            }
        }
        var graph = new DogmaGraph(nodes, modifiers, definitions);
        var bonuses = skillModifiers
            .Select(s => new FittingSkillBonus(s.Skill, s.Modifier, graph.Read(s.Skill, s.Modifier.SourceAttribute)))
            .ToArray();
        if (revision is not null)
        {
            // Share evaluated values, never the mutable graph or tracked entities.
            // Transaction completion publishes the snapshot; rollback discards it.
            world.StageFittingSnapshot(
                pilot.Id,
                ship.Id,
                revision,
                FittingSnapshot.Capture(graph, pilot.Id, ship.Id, modules, charges, bonuses)
            );
        }
        return new(graph, ship.Id, pilot.Id, modules, bonuses, charges, levels)
        {
            Transient = ownerTypeId is not null || overloadedModules is not null,
            OverloadedModules = overloads,
        };

        void Add(long source, PassiveModifier modifier, bool exempt)
        {
            IEnumerable<long> targets = modifier.Selector switch
            {
                ModifierSelector.Group => locations.Where(m => m.GroupId == modifier.SelectionId).Select(m => m.Item.Id),
                ModifierSelector.RequiredSkill => locations
                    .Where(m => m.RequiredSkills.Contains(modifier.SelectionId!.Value))
                    .Select(m => m.Item.Id),
                ModifierSelector.OwnerRequiredSkill => ownerTargets
                    .Where(m => m.RequiredSkills.Contains(modifier.SelectionId!.Value))
                    .Select(m => m.Item.Id),
                ModifierSelector.Location => modules.Select(m => m.Item.Id),
                _ =>
                [
                    modifier.Domain switch
                    {
                        ModifierDomain.Ship => ship.Id,
                        ModifierDomain.Character => pilot.Id,
                        _ => source,
                    },
                ],
            };
            foreach (long target in targets)
            {
                modifiers.Add(
                    new(target, modifier.Attribute, modifier.Operation, new DogmaValue.Attribute(source, modifier.SourceAttribute), exempt)
                );
            }
        }
    }
}
