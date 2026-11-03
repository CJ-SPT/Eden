using System.Data;
using System.Globalization;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Skills;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal sealed record FittingLocation(OwnedItem Item, int GroupId, IReadOnlySet<int> RequiredSkills);

internal sealed record FittingCalculation(
    DogmaGraph Graph,
    long ShipId,
    long PilotId,
    IReadOnlyList<FittingLocation> Modules,
    IReadOnlyList<FittingSkillBonus> SkillBonuses,
    IReadOnlyList<FittingLocation> Charges
)
{
    internal static FittingCalculation Read(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, Ship? ship = null)
    {
        // A fitting graph reads many static rows. Keep this nonpooled connection
        // open for its owning context instead of reopening SQLite for every query.
        if (catalog.Database.GetDbConnection().State != ConnectionState.Open)
        {
            catalog.Database.OpenConnection();
        }
        ship ??= pilot.ActiveShip;
        var hull = catalog.Set<ItemType>().Find(ship.TypeId)!;
        var hullAttributes = PilotFittingStore.ReadAttributes(catalog, ship.TypeId).ToDictionary();
        hullAttributes[DogmaAttributeId.Mass] = hull.Mass ?? 0;
        hullAttributes[DogmaAttributeId.CargoCapacity] = hull.Capacity ?? 0;
        var items = world
            .Set<OwnedItem>()
            .Where(i =>
                i.OwnerId == pilot.Id
                && i.LocationId == ship.Id
                && i.FlagId >= (int)InventoryFlag.LowSlot0
                && i.FlagId <= (int)InventoryFlag.HighSlot7
            )
            .ToArray();
        var moduleAttributes = items
            .Select(i => i.TypeId)
            .Distinct()
            .ToDictionary(id => id, id => PilotFittingStore.ReadAttributes(catalog, id));
        var modules = items
            .Select(i => new FittingLocation(
                i,
                catalog.Set<ItemType>().Find(i.TypeId)!.GroupId,
                InventoryDefinitions
                    .RequiredSkills.Select(p => moduleAttributes[i.TypeId].GetValueOrDefault(p.Skill))
                    .Where(v => v > 0)
                    .Select(v => checked((int)v))
                    .ToHashSet()
            ))
            .ToArray();
        var moduleIds = items.Select(i => i.Id).ToArray();
        var loaded = world.Set<OwnedItem>().Where(i => i.OwnerId == pilot.Id && moduleIds.Contains(i.LocationId)).ToArray();
        var chargeAttributes = loaded
            .Select(i => i.TypeId)
            .Distinct()
            .ToDictionary(id => id, id => PilotFittingStore.ReadAttributes(catalog, id));
        var charges = loaded
            .Select(i => new FittingLocation(
                i,
                catalog.Set<ItemType>().Find(i.TypeId)!.GroupId,
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
            new(pilot.Id, new Dictionary<DogmaAttributeId, double> { [DogmaAttributeId.MissileDamageMultiplier] = 1 }),
        ];
        nodes.AddRange(loaded.Select(i => new DogmaNode(i.Id, chargeAttributes[i.TypeId])));
        nodes.AddRange(items.Select(i => new DogmaNode(i.Id, moduleAttributes[i.TypeId])));
        List<DogmaModifier> modifiers = [];
        foreach (var charge in charges.Where(c => SupportModuleDefinitions.IsScript(c.GroupId)))
        {
            var parent = modules.Single(m => m.Item.Id == charge.Item.LocationId);
            if (
                !SupportModuleCatalog.Supported(catalog, parent.Item.TypeId, parent.GroupId)
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
        foreach (var role in FittingRules.RookieResistanceModifiers)
        {
            hullAttributes.TryAdd(role.Resonance, 1);
            modifiers.Add(new(ship.Id, role.Resonance, DogmaOperation.PostPercent, new DogmaValue.Attribute(ship.Id, role.Bonus), true));
        }
        // Build 3662168 effect 5000 applies the Ibis role to pilot-owned
        // missile ammunition, independently of launcher damage upgrades.
        foreach (
            var charge in charges.Where(c =>
                MissileDefinitions.ChargeGroups.Contains(c.GroupId) && c.RequiredSkills.Contains((int)MissileSkill.MissileLauncherOperation)
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
        var activeModules = world.Set<ActiveFittedModule>().Where(a => a.ShipId == ship.Id).Select(a => a.ModuleId).ToHashSet();
        var incoming = world
            .Set<WorldTackle>()
            .Where(t => t.TargetId == ship.Id && t.TargetKind == TackleTargetKind.Ship)
            .AsEnumerable()
            .Select(t => t.Module())
            .ToArray();
        modifiers.AddRange(TackleDefinitions.Modifiers(ship.Id, incoming));
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
            if (
                module.Item.Online
                && module.GroupId == TackleDefinitions.WarpCoreStabilizerGroup
                && TackleCatalog.Supported(catalog, module.Item.TypeId, module.GroupId)
            )
            {
                foreach (var modifier in TackleCatalog.CoreModifiers(catalog, module.Item.TypeId))
                {
                    if (modifier.Phase == PassiveEffectPhase.Online || activeModules.Contains(module.Item.Id))
                    {
                        Add(module.Item.Id, modifier, false);
                    }
                }
            }
            if (
                module.Item.Online
                && activeModules.Contains(module.Item.Id)
                && SupportModuleCatalog.Supported(catalog, module.Item.TypeId, module.GroupId)
            )
            {
                foreach (var modifier in SupportModuleCatalog.Modifiers(catalog, module.Item.TypeId, module.GroupId))
                {
                    Add(module.Item.Id, modifier, false);
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
                && activeModules.Contains(module.Item.Id)
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
                if (activeModules.Contains(module.Item.Id))
                {
                    Add(module.Item.Id, MicrowarpdriveCatalog.SignatureModifier, false);
                }
            }
            if (!PassiveCatalog.Supported(catalog, module.Item.TypeId, module.GroupId))
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
        List<(int Skill, PassiveModifier Modifier)> skillModifiers = [];
        var relevantSkills = PassiveModules.Skills.Concat(MissileDefinitions.Skills).Distinct().ToArray();
        var trained = world
            .Set<PilotSkill>()
            .Where(s => s.PilotId == pilot.Id && relevantSkills.Contains(s.TypeId))
            .Select(s => s.TypeId)
            .ToHashSet();
        foreach (int skill in relevantSkills.Where(trained.Contains))
        {
            int level = PilotSkillReader.Level(world, catalog, pilot.Id, skill);
            if (level == 0)
            {
                continue;
            }
            var attributes = PilotFittingStore.ReadAttributes(catalog, skill).ToDictionary();
            attributes[DogmaAttributeId.SkillLevel] = level;
            nodes.Add(new(skill, attributes));
            foreach (var modifier in PassiveCatalog.Modifiers(catalog, skill))
            {
                Add(skill, modifier, true);
                if (modifier.Domain != ModifierDomain.Item)
                {
                    skillModifiers.Add((skill, modifier));
                }
            }
        }
        var requested = modifiers
            .Select(m => m.Attribute)
            .Concat(modifiers.SelectMany(m => DogmaValue.References(m.Source)).Select(v => v.AttributeId))
            .ToHashSet();
        Dictionary<DogmaAttributeId, DogmaAttributeDefinition> definitions = [];
        while (requested.Count > 0)
        {
            var keys = requested.Select(id => PassiveCatalog.Key((int)id)).ToArray();
            var rows = catalog
                .Set<SourceRecord>()
                .Where(r => r.Dataset == "client/dogmaattributes" && keys.Contains(r.Key))
                .AsEnumerable()
                .Select(r =>
                    (Id: (DogmaAttributeId)int.Parse(r.Key, CultureInfo.InvariantCulture), Definition: r.Read<ClientDogmaattributes>())
                )
                .ToArray();
            foreach (var id in requested)
            {
                definitions[id] = new(id, 0);
            }
            requested.Clear();
            foreach (var row in rows)
            {
                var a = row.Definition;
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
        return new(graph, ship.Id, pilot.Id, modules, bonuses, charges);

        void Add(long source, PassiveModifier modifier, bool exempt)
        {
            IEnumerable<long> targets = modifier.Selector switch
            {
                ModifierSelector.Group => locations.Where(m => m.GroupId == modifier.SelectionId).Select(m => m.Item.Id),
                ModifierSelector.RequiredSkill or ModifierSelector.OwnerRequiredSkill => locations
                    .Where(m => m.RequiredSkills.Contains(modifier.SelectionId!.Value))
                    .Select(m => m.Item.Id),
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
