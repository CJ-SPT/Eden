using System.Globalization;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Server.Catalog;

namespace Eden.Server.Persistence;

internal static class PassiveCatalog
{
    private const int TechOne = 1;
    private const int TechOneMetaGroup = 1;
    private const int SkillLevelEffect = 132;

    // Admit a profile only when every fitted/online effect can be evaluated.
    internal static bool Supported(CatalogDbContext catalog, int typeId, int groupId)
    {
        if (!PassiveModules.Groups.Contains(groupId))
        {
            return false;
        }
        var type = catalog.Set<SourceRecord>().Find("client/types", Key(typeId))?.Read<ClientTypes>();
        if (
            type?.Published != 1
            || type.MetaGroupID != TechOneMetaGroup
            || catalog.Set<TypeAttribute>().Find(typeId, (int)DogmaAttributeId.TechLevel)?.Value != TechOne
        )
        {
            return false;
        }
        var effects = Effects(catalog, typeId);
        return effects.Count > 0
            && effects.Count == FittingCatalog.Effects(catalog, typeId).Count
            && effects.All(e =>
                e.EffectID == (int)DogmaEffect.Online
                || (
                    e.EffectCategory is (int)PassiveEffectPhase.Fitted or (int)PassiveEffectPhase.Online
                    && (e.ModifierInfo ?? []).All(m => Parse(m, e.EffectCategory!.Value) is not null)
                )
            );
    }

    internal static IReadOnlyList<ClientDogmaeffects> Effects(CatalogDbContext catalog, int typeId) =>
        FittingCatalog
            .Effects(catalog, typeId)
            .Select(id => catalog.Set<SourceRecord>().Find("client/dogmaeffects", Key((int)id))?.Read<ClientDogmaeffects>())
            .Where(e => e is not null)
            .Cast<ClientDogmaeffects>()
            .ToArray();

    internal static IReadOnlyList<PassiveModifier> Modifiers(CatalogDbContext catalog, int typeId) =>
        Effects(catalog, typeId)
            .Where(e =>
                e.EffectID != SkillLevelEffect && e.EffectCategory is (int)PassiveEffectPhase.Fitted or (int)PassiveEffectPhase.Online
            )
            .SelectMany(e =>
                (e.ModifierInfo ?? []).Select(m =>
                    Parse(m, e.EffectCategory!.Value) ?? throw new InvalidDataException($"Unsupported passive modifier on type {typeId}.")
                )
            )
            .ToArray();

    internal static PassiveModifier? Parse(ClientDogmaeffectsModifierInfoItem modifier, long phase)
    {
        ModifierDomain? domain = modifier.Domain switch
        {
            "itemID" => ModifierDomain.Item,
            "shipID" => ModifierDomain.Ship,
            "charID" => ModifierDomain.Character,
            _ => null,
        };
        ModifierSelector? selector = modifier.Func switch
        {
            "ItemModifier" => ModifierSelector.Item,
            "LocationGroupModifier" => ModifierSelector.Group,
            "LocationRequiredSkillModifier" => ModifierSelector.RequiredSkill,
            _ => null,
        };
        if (
            domain is null
            || selector is null
            || modifier.ModifiedAttributeID is null
            || modifier.ModifyingAttributeID is null
            || modifier.Operation is not (0 or 2 or 4 or 6)
        )
        {
            return null;
        }
        int? selection = selector == ModifierSelector.Group ? (int?)modifier.GroupID : (int?)modifier.SkillTypeID;
        if (selector != ModifierSelector.Item && (domain != ModifierDomain.Ship || selection is null))
        {
            return null;
        }
        return new(
            domain.Value,
            selector.Value,
            selection,
            (DogmaAttributeId)checked((int)modifier.ModifiedAttributeID.Value),
            (DogmaAttributeId)checked((int)modifier.ModifyingAttributeID.Value),
            (DogmaOperation)modifier.Operation.Value,
            (PassiveEffectPhase)phase
        );
    }

    internal static string Key(int id) => id.ToString(CultureInfo.InvariantCulture);
}
