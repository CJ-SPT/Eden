using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;

namespace Eden.World.Fittings;

internal static class HullCatalog
{
    internal static bool CanAssemble(CatalogDbContext catalog, ItemType type, int category)
    {
        var definition = catalog.FindSource("client/types", PassiveCatalog.Key(type.Id))?.Read<ClientTypes>();
        // Existing Venture saves and the historical fixtures precede techLevel metadata.
        int techLevel = checked((int)(definition?.TechLevel ?? (type.Id == (int)StarterShipType.Venture ? 1 : 0)));
        return TechOneHullRules.CanAssemble(
            new(type.Id, (InventoryCategory)category, definition?.Published == 1, techLevel, (HullClass)type.GroupId)
        );
    }

    internal static bool UsesCatalogBonuses(CatalogDbContext catalog, int typeId) =>
        catalog.FindSource("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>().TechLevel == 1
        && catalog.Set<ItemType>().Find(typeId) is { } type
        && catalog.Set<ItemGroup>().Find(type.GroupId) is { } group
        && CanAssemble(catalog, type, group.CategoryId);

    internal static IReadOnlyList<PassiveModifier> Modifiers(CatalogDbContext catalog, int typeId)
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
        return modifiers;
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

    internal static IReadOnlySet<int> Skills(CatalogDbContext catalog, int typeId)
    {
        if (!UsesCatalogBonuses(catalog, typeId))
        {
            return new HashSet<int>();
        }
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        return InventoryDefinitions
            .RequiredSkills.Select(pair => attributes.GetValueOrDefault(pair.Skill))
            .Where(value => value > 0)
            .Select(value => checked((int)value))
            .ToHashSet();
    }
}
