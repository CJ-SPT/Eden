using System.Collections.Immutable;
using System.Globalization;
using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Combat.Weapons;

namespace Eden.World.Fittings;

internal static class PassiveCatalog
{
    private static readonly CatalogDefinition<ImmutableArray<ClientDogmaeffects>> s_effects = new("passive-effects", LoadEffects);
    private static readonly CatalogDefinition<ImmutableArray<PassiveModifier>> s_modifiers = new("passive-modifiers", LoadModifiers);
    private const int AbyssalMetaGroup = 15;
    private const int SkillLevelEffect = 132;

    // Admit a profile only when every fitted/online effect can be evaluated.
    internal static bool Supported(CatalogDbContext catalog, int typeId, int groupId)
    {
        if (!PassiveModules.Groups.Contains(groupId))
        {
            return false;
        }
        var type = catalog.Read(CatalogData.Client.Types, (long)typeId);
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        if (type?.Published != 1 || type.MetaGroupID == AbyssalMetaGroup || InventoryDefinitions.RequiresSpecificShip(attributes))
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
        catalog.Definition(s_effects, typeId);

    private static ImmutableArray<ClientDogmaeffects> LoadEffects(CatalogDbContext catalog, int typeId) =>
        // Preserve the original insertion order: assignment modifiers are ordered.
        (catalog.Read(CatalogData.Client.Typedogma, (long)typeId)?.DogmaEffects ?? [])
            .Select(effect => checked((int)effect.EffectID!.Value))
            .Distinct()
            .Select(id => catalog.Read(CatalogData.Client.Dogmaeffects, (long)id))
            .Where(e => e is not null)
            .Cast<ClientDogmaeffects>()
            .ToImmutableArray();

    internal static IReadOnlyList<PassiveModifier> Modifiers(CatalogDbContext catalog, int typeId) =>
        catalog.Definition(s_modifiers, typeId);

    private static ImmutableArray<PassiveModifier> LoadModifiers(CatalogDbContext catalog, int typeId) =>
        Effects(catalog, typeId)
            .Where(e =>
                e.EffectID != SkillLevelEffect && e.EffectCategory is (int)PassiveEffectPhase.Fitted or (int)PassiveEffectPhase.Online
            )
            .SelectMany(e =>
                MissileSkillCatalog.LegacyModifiers(typeId, e)
                ?? (e.ModifierInfo ?? []).Select(m =>
                    Parse(m, e.EffectCategory!.Value) ?? throw new InvalidDataException($"Unsupported passive modifier on type {typeId}.")
                )
            )
            .ToImmutableArray();

    internal static PassiveModifier? Parse(ClientDogmaeffectsModifierInfoItem modifier, long phase, bool rig = false, bool hull = false)
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
            "OwnerRequiredSkillModifier" => ModifierSelector.OwnerRequiredSkill,
            _ => null,
        };
        if (
            domain is null
            || selector is null
            || modifier.ModifiedAttributeID is null
            || modifier.ModifyingAttributeID is null
            || (
                modifier.Operation is not (0 or 2 or 4 or 6)
                && !(
                    modifier.Operation == (int)DogmaOperation.PostAssignment
                    && (
                        rig
                        || hull
                            && modifier.Func == "LocationGroupModifier"
                            && modifier.Domain == "shipID"
                            && (
                                modifier.ModifiedAttributeID is (int)DogmaAttributeId.MaxGroupFitted or (int)DogmaAttributeId.MaxGroupOnline
                                || modifier.ModifiedAttributeID == (int)DogmaAttributeId.NosOverride
                                    && modifier.ModifyingAttributeID == (int)DogmaAttributeId.NosOverride
                                    && modifier.GroupID == CapacitorWarfareRules.NosferatuGroup
                            )
                    )
                )
            )
        )
        {
            return null;
        }
        int? selection = selector == ModifierSelector.Group ? (int?)modifier.GroupID : (int?)modifier.SkillTypeID;
        if (
            selector == ModifierSelector.OwnerRequiredSkill
            && (
                domain != ModifierDomain.Character
                || selection is not int ownerSkill
                || (
                    !rig
                    && !MissileDefinitions.SupportsOwnerSkill(ownerSkill)
                    && !(
                        ownerSkill
                            is Eden.Gameplay.Exploration.ProbeScanRules.AstrometricsSkill
                                or Eden.Gameplay.Exploration.ProbeScanRules.ScienceSkill
                        && modifier.ModifiedAttributeID is (int)DogmaAttributeId.ProbeStrength or (int)DogmaAttributeId.ProbeDeviation
                    )
                    && !(hull && DroneRules.SupportsOwnerSkill(ownerSkill))
                    && !DroneEquipmentRules.SupportsOwnerModifier(ownerSkill, (DogmaAttributeId)modifier.ModifiedAttributeID.Value)
                )
            )
        )
        {
            return null;
        }
        if (
            selector is not (ModifierSelector.Item or ModifierSelector.OwnerRequiredSkill)
            && (domain != ModifierDomain.Ship || selection is null)
        )
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
