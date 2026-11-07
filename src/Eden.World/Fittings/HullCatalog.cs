using System.Collections.Immutable;
using Eden.Data.Catalog;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;

namespace Eden.World.Fittings;

internal static class HullCatalog
{
    private static readonly CatalogDefinition<ImmutableArray<PassiveModifier>> s_modifiers = new("hull-modifiers", LoadModifiers);
    private static readonly CatalogDefinition<ImmutableHashSet<int>> s_skills = new("hull-bonus-skills", LoadSkills);

    internal static bool CanAssemble(CatalogDbContext catalog, ItemType type, int category) =>
        CanAssemble(catalog, type.Id, type.GroupId, category);

    internal static bool CanAssemble(CatalogDbContext catalog, CatalogItemDefinition type, int category) =>
        CanAssemble(catalog, type.Id, type.GroupId, category);

    private static bool CanAssemble(CatalogDbContext catalog, int typeId, int groupId, int category)
    {
        var definition = catalog.Read(CatalogData.Client.Types, (long)typeId);
        // Existing Venture saves and the historical fixtures precede techLevel metadata.
        int techLevel = checked((int)(definition?.TechLevel ?? (typeId == (int)StarterShipType.Venture ? 1 : 0)));
        return TechOneHullRules.CanAssemble(
            new(typeId, (InventoryCategory)category, definition?.Published == 1, techLevel, (HullClass)groupId)
        );
    }

    internal static bool UsesCatalogBonuses(CatalogDbContext catalog, int typeId) =>
        catalog.Read(CatalogData.Client.Types, (long)typeId)?.TechLevel == 1
        && catalog.FittingType(typeId) is { } type
        && CanAssemble(catalog, type, type.CategoryId);

    internal static IReadOnlyList<PassiveModifier> Modifiers(CatalogDbContext catalog, int typeId) =>
        catalog.Definition(s_modifiers, typeId);

    private static ImmutableArray<PassiveModifier> LoadModifiers(CatalogDbContext catalog, int typeId)
    {
        if (!UsesCatalogBonuses(catalog, typeId))
        {
            return [];
        }
        List<PassiveModifier> modifiers = [];
        foreach (var effect in PassiveCatalog.Effects(catalog, typeId))
        {
            foreach (var source in effect.ModifierInfo ?? [])
            {
                var capability = Capability(source);
                if (capability == HullCapability.Unclassified)
                {
                    throw new InvalidDataException($"Unclassified hull effect {effect.EffectID} on type {typeId}.");
                }
                if (!Operational(source))
                {
                    continue;
                }
                if (effect.EffectCategory != (int)PassiveEffectPhase.Fitted)
                {
                    throw new InvalidDataException($"Operational hull effect {effect.EffectID} needs an explicit activation owner.");
                }
                modifiers.Add(
                    PassiveCatalog.Parse(source, effect.EffectCategory!.Value)
                        ?? throw new InvalidDataException($"Unsupported operational hull effect {effect.EffectID} on type {typeId}.")
                );
            }
        }
        return modifiers.ToImmutableArray();
    }

    internal static IReadOnlyList<HullEffectCapability> Capabilities(CatalogDbContext catalog, int typeId) =>
        PassiveCatalog
            .Effects(catalog, typeId)
            .SelectMany(effect =>
                (effect.ModifierInfo ?? []).Select(source => new HullEffectCapability(
                    checked((int)effect.EffectID!.Value),
                    Capability(source),
                    Operational(source)
                ))
            )
            .Distinct()
            .ToArray();

    private static bool Operational(ClientDogmaeffectsModifierInfoItem source) =>
        HullBonusRules.Operational(Capability(source), source.Func == "LocationGroupModifier" ? (int?)source.GroupID : null);

    private static HullCapability Capability(ClientDogmaeffectsModifierInfoItem source)
    {
        var domain = source.Domain switch
        {
            "shipID" => ModifierDomain.Ship,
            "charID" => ModifierDomain.Character,
            "itemID" => ModifierDomain.Item,
            _ => (ModifierDomain?)null,
        };
        var selector = source.Func switch
        {
            "LocationGroupModifier" => ModifierSelector.Group,
            "LocationRequiredSkillModifier" => ModifierSelector.RequiredSkill,
            "OwnerRequiredSkillModifier" => ModifierSelector.OwnerRequiredSkill,
            "ItemModifier" => ModifierSelector.Item,
            _ => (ModifierSelector?)null,
        };
        if (domain is null || selector is null)
        {
            return HullCapability.Unclassified;
        }
        int? selection = selector == ModifierSelector.Group ? (int?)source.GroupID : (int?)source.SkillTypeID;
        return HullBonusRules.Capability(new(domain.Value, selector.Value, selection, (DogmaAttributeId)(source.ModifiedAttributeID ?? 0)));
    }

    internal static IReadOnlySet<int> Skills(CatalogDbContext catalog, int typeId) => catalog.Definition(s_skills, typeId);

    private static ImmutableHashSet<int> LoadSkills(CatalogDbContext catalog, int typeId)
    {
        if (!UsesCatalogBonuses(catalog, typeId))
        {
            return [];
        }
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        return InventoryDefinitions
            .RequiredSkills.Select(pair => attributes.GetValueOrDefault(pair.Skill))
            .Where(value => value > 0)
            .Select(value => checked((int)value))
            .ToImmutableHashSet();
    }
}
