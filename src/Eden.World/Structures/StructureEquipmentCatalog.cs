using System.Collections.Immutable;
using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Structures;

internal static class StructureEquipmentCatalog
{
    private static readonly CatalogDefinition<StructureEquipmentProfile> s_profiles = new("structure-equipment", Load);
    private static readonly CatalogDefinition<DogmaAttributeDefinition> s_attributes = new("structure-fitting-attribute", LoadAttribute);
    private static readonly CatalogDefinition<IReadOnlyList<PassiveModifier>> s_hullModifiers = new(
        "structure-hull-modifiers",
        LoadModifiers
    );

    internal static StructureEquipmentProfile Require(CatalogDbContext catalog, int typeId) => catalog.Definition(s_profiles, typeId);

    internal static IReadOnlyList<PassiveModifier> HullModifiers(CatalogDbContext catalog, int typeId) =>
        catalog.Definition(s_hullModifiers, typeId);

    private static StructureEquipmentProfile Load(CatalogDbContext catalog, int typeId)
    {
        var type = catalog.RequireItem(typeId);
        var native = catalog.Read(CatalogData.Client.Types, (long)typeId);
        if (type.CategoryId != StructureRules.ServiceCategoryId || native?.Published != 1)
        {
            throw new InvalidDataException("Choose published Upwell structure equipment.");
        }
        var attributes = catalog.FittingAttributes(typeId);
        var effects = FittingCatalog.Effects(catalog, typeId);
        bool service = StructureCatalog.ReadService(catalog, typeId) is not null;
        ModuleRack rack =
            service ? ModuleRack.High
            : effects.Contains(DogmaEffect.HighPower) ? ModuleRack.High
            : effects.Contains(DogmaEffect.MediumPower) ? ModuleRack.Medium
            : effects.Contains(DogmaEffect.LowPower) ? ModuleRack.Low
            : effects.Contains(DogmaEffect.RigSlot) ? ModuleRack.Rig
            : throw new InvalidDataException("The structure equipment has no fitting rack.");
        var fitting = new ModuleFittingDefinition(
            rack,
            effects.Contains(DogmaEffect.TurretFitted),
            effects.Contains(DogmaEffect.LauncherFitted),
            0,
            0
        );
        var modifiers = LoadModifiers(catalog, typeId);
        if (
            modifiers.Any(modifier =>
                modifier.Domain == ModifierDomain.Character || modifier.Selector == ModifierSelector.OwnerRequiredSkill
            )
        )
        {
            throw new InvalidDataException("This structure equipment requires an unsupported owner modifier.");
        }
        return new(
            typeId,
            type.GroupId,
            fitting,
            ShipFittingRestrictions.Read(attributes),
            service ? new(null, null, null) : FittingModuleLimits.Read(attributes),
            attributes,
            modifiers,
            InventoryDefinitions
                .RequiredSkills.Select(pair => checked((int)attributes.GetValueOrDefault(pair.Skill)))
                .Where(id => id > 0)
                .ToImmutableHashSet(),
            checked((int)(native.VariationParentTypeID ?? typeId)),
            service
        );
    }

    private static IReadOnlyList<PassiveModifier> LoadModifiers(CatalogDbContext catalog, int typeId) =>
        PassiveCatalog
            .Effects(catalog, typeId)
            .Where(effect => effect.EffectCategory is (int)PassiveEffectPhase.Fitted or (int)PassiveEffectPhase.Online)
            .SelectMany(effect =>
                (effect.ModifierInfo ?? []).Select(modifier =>
                {
                    // Pinned structureID is the owning structure node. Structure grading rigs also use operator -1.
                    bool assignment = modifier.Operation == (int)DogmaOperation.PreAssignment;
                    var normalized = new ClientDogmaeffectsModifierInfoItem
                    {
                        Domain = modifier.Domain == "structureID" ? "shipID" : modifier.Domain,
                        Operation = assignment ? (int)DogmaOperation.PostAssignment : modifier.Operation,
                        EffectID = modifier.EffectID,
                        Func = modifier.Func,
                        GroupID = modifier.GroupID,
                        ModifiedAttributeID = modifier.ModifiedAttributeID,
                        ModifyingAttributeID = modifier.ModifyingAttributeID,
                        SkillTypeID = modifier.SkillTypeID,
                    };
                    var parsed =
                        PassiveCatalog.Parse(normalized, effect.EffectCategory!.Value, rig: true)
                        ?? throw new InvalidDataException($"Unsupported passive structure modifier on type {typeId}.");
                    return assignment ? parsed with { Operation = DogmaOperation.PreAssignment } : parsed;
                })
            )
            .ToImmutableArray();

    internal static IReadOnlyDictionary<DogmaAttributeId, DogmaAttributeDefinition> Definitions(
        CatalogDbContext catalog,
        IReadOnlyDictionary<DogmaAttributeId, double> hull,
        IReadOnlyList<StructureFittedEquipment> equipment,
        IReadOnlyList<PassiveModifier> hullModifiers,
        IReadOnlyList<StructureLoadedCharge>? charges = null
    )
    {
        Dictionary<DogmaAttributeId, DogmaAttributeDefinition> result = [];
        Queue<DogmaAttributeId> pending = new(
            hull.Keys.Concat(equipment.SelectMany(item => item.Profile.Attributes.Keys))
                .Concat(
                    equipment.SelectMany(item =>
                        item.Profile.Modifiers.SelectMany(modifier => new[] { modifier.Attribute, modifier.SourceAttribute })
                    )
                )
                .Concat(hullModifiers.SelectMany(modifier => new[] { modifier.Attribute, modifier.SourceAttribute }))
                .Concat((charges ?? []).SelectMany(charge => charge.Profile.Attributes.Keys))
                .Concat(
                    (charges ?? []).SelectMany(charge =>
                        charge.Profile.Modifiers.SelectMany(modifier => new[] { modifier.Attribute, modifier.SourceAttribute })
                    )
                )
                .Distinct()
        );
        while (pending.TryDequeue(out var id))
        {
            if (result.ContainsKey(id))
            {
                continue;
            }
            var definition = catalog.Definition(s_attributes, (int)id);
            result.Add(id, definition);
            if (definition.Minimum is { } minimum)
            {
                pending.Enqueue(minimum);
            }
            if (definition.Maximum is { } maximum)
            {
                pending.Enqueue(maximum);
            }
        }
        return result;
    }

    private static DogmaAttributeDefinition LoadAttribute(CatalogDbContext catalog, int id)
    {
        var definition = catalog.Read(CatalogData.Client.Dogmaattributes, (long)id);
        return definition is null
            ? new((DogmaAttributeId)id, 0)
            : new(
                (DogmaAttributeId)id,
                definition.DefaultValue ?? 0,
                definition.Stackable != 0,
                definition.MaxAttributeID is long maximum ? (DogmaAttributeId)checked((int)maximum) : null,
                definition.MinAttributeID is long minimum ? (DogmaAttributeId)checked((int)minimum) : null,
                definition.DataType is 10 or 11 or 12
            );
    }
}
