using System.Globalization;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal sealed record FittingModule(OwnedItem Item, int GroupId, IReadOnlySet<int> RequiredSkills);

internal sealed record FittingCalculation(
    DogmaGraph Graph,
    long ShipId,
    long PilotId,
    IReadOnlyList<FittingModule> Modules,
    IReadOnlyList<FittingSkillBonus> SkillBonuses
)
{
    internal static FittingCalculation Read(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, Ship? ship = null)
    {
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
            .Select(i => new FittingModule(
                i,
                catalog.Set<ItemType>().Find(i.TypeId)!.GroupId,
                InventoryDefinitions
                    .RequiredSkills.Select(p => moduleAttributes[i.TypeId].GetValueOrDefault(p.Skill))
                    .Where(v => v > 0)
                    .Select(v => checked((int)v))
                    .ToHashSet()
            ))
            .ToArray();
        List<DogmaNode> nodes = [new(ship.Id, hullAttributes), new(pilot.Id, new Dictionary<DogmaAttributeId, double>())];
        nodes.AddRange(items.Select(i => new DogmaNode(i.Id, moduleAttributes[i.TypeId])));
        List<DogmaModifier> modifiers = [];
        var activeModules = world.Set<ActiveFittedModule>().Where(a => a.ShipId == ship.Id).Select(a => a.ModuleId).ToHashSet();
        foreach (var module in modules)
        {
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
        var relevantSkills = PassiveModules.Skills.ToArray();
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
            .Concat(modifiers.Select(m => m.Source).OfType<DogmaValue.Attribute>().Select(v => v.AttributeId))
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
        return new(graph, ship.Id, pilot.Id, modules, bonuses);

        void Add(long source, PassiveModifier modifier, bool exempt)
        {
            IEnumerable<long> targets = modifier.Selector switch
            {
                ModifierSelector.Group => modules.Where(m => m.GroupId == modifier.SelectionId).Select(m => m.Item.Id),
                ModifierSelector.RequiredSkill => modules
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
