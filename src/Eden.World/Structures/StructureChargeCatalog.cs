using System.Collections.Immutable;
using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Structures;

internal static class StructureChargeCatalog
{
    private static readonly CatalogDefinition<StructureChargeProfile> s_profiles = new("structure-charge", Load);

    internal static StructureChargeProfile Require(CatalogDbContext catalog, int typeId) => catalog.Definition(s_profiles, typeId);

    private static StructureChargeProfile Load(CatalogDbContext catalog, int typeId)
    {
        var type = catalog.RequireItem(typeId);
        if (type.CategoryId != (int)InventoryCategory.Charge || catalog.Read(CatalogData.Client.Types, (long)typeId)?.Published != 1)
        {
            throw new InvalidDataException("Choose published ammunition or a module script.");
        }
        var attributes = catalog.FittingAttributes(typeId);
        List<StructureChargeModifier> modifiers = [];
        foreach (
            var effect in PassiveCatalog
                .Effects(catalog, typeId)
                .Where(effect => effect.EffectCategory is (int)PassiveEffectPhase.Fitted or (int)PassiveEffectPhase.Online)
        )
        {
            foreach (var modifier in effect.ModifierInfo ?? [])
            {
                if (
                    modifier.Func != "ItemModifier"
                    || modifier.ModifiedAttributeID is not long target
                    || modifier.ModifyingAttributeID is not long source
                    || modifier.Operation is not long operation
                    || !Enum.IsDefined((DogmaOperation)checked((int)operation))
                )
                {
                    throw new InvalidDataException($"Unsupported structure charge modifier on type {typeId}.");
                }
                var domain = modifier.Domain switch
                {
                    "itemID" => StructureChargeTarget.Charge,
                    "otherID" => StructureChargeTarget.Module,
                    "shipID" or "structureID" => StructureChargeTarget.Structure,
                    _ => throw new InvalidDataException($"Unsupported structure charge domain on type {typeId}."),
                };
                modifiers.Add(
                    new(
                        domain,
                        (DogmaAttributeId)checked((int)target),
                        (DogmaAttributeId)checked((int)source),
                        (DogmaOperation)checked((int)operation),
                        (PassiveEffectPhase)checked((int)effect.EffectCategory!.Value)
                    )
                );
            }
        }
        return new(
            typeId,
            type.GroupId,
            attributes,
            modifiers.ToImmutableArray(),
            InventoryDefinitions
                .RequiredSkills.Select(pair => checked((int)attributes.GetValueOrDefault(pair.Skill)))
                .Where(id => id > 0)
                .ToImmutableHashSet()
        );
    }
}
