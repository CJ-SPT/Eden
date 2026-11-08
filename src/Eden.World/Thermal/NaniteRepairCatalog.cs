using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Thermal;

internal sealed record NaniteRepairPrice(long ComputedBasePrice, double PasteBasePrice);

internal static class NaniteRepairCatalog
{
    private const int StandardSkillEffect = 132;
    private const int OperationLevelEffect = 3522;
    private const int OperationCostEffect = 3521;
    private const int InterfacingLevelEffect = 3524;
    private const int InterfacingRateEffect = 3723;
    private const double MinimumOperationBonusPerLevel = -20;
    private static readonly CatalogDefinition<NaniteRepairPrice> s_prices = new("nanite-repair-price", ReadPrice);
    private static readonly CatalogDefinition<IReadOnlyList<PassiveModifier>> s_skills = new("nanite-repair-skill", ReadSkill);

    internal static NaniteRepairPrice Price(CatalogDbContext catalog, int type) => catalog.Definition(s_prices, type);

    internal static IReadOnlyList<PassiveModifier> SkillModifiers(CatalogDbContext catalog, int skill) =>
        catalog.Definition(s_skills, skill);

    private static NaniteRepairPrice ReadPrice(CatalogDbContext catalog, int type)
    {
        var source = catalog.Read(CatalogData.Client.Types, (long)type);
        if (
            source?.BasePrice is not double basePrice
            || source.PortionSize is not long portionSize
            || FittingCatalog.Definition(catalog, type) is not { Rack: not ModuleRack.Rig }
        )
        {
            throw new InvalidDataException("Nanite repair pricing requires a supported ship module.");
        }
        var rows = catalog.Read(CatalogData.Client.Typematerials, (long)type)?.Materials ?? [];
        var materials = rows.Select(row =>
            {
                var material = row.MaterialTypeID is long id ? catalog.Read(CatalogData.Client.Types, id) : null;
                if (
                    material?.BasePrice is not double price
                    || material.PortionSize is not long portion
                    || row.Quantity is not long quantity
                )
                {
                    throw new InvalidDataException("Missing nanite repair material price.");
                }
                return new NaniteMaterialValue(price, portion, quantity);
            })
            .ToArray();
        long computed = NaniteRepairRules.ComputedBasePrice(basePrice, portionSize, materials);
        double paste = catalog.Read(CatalogData.Client.Types, (long)NaniteRepairRules.PasteType)?.BasePrice ?? 0;
        if (!double.IsFinite(paste) || paste <= 0)
        {
            throw new InvalidDataException("Missing nanite repair paste base price.");
        }
        return new(computed, paste);
    }

    private static IReadOnlyList<PassiveModifier> ReadSkill(CatalogDbContext catalog, int skill)
    {
        var (scaleId, applyId, source, target) = (NaniteRepairSkill)skill switch
        {
            NaniteRepairSkill.Operation => (
                OperationLevelEffect,
                OperationCostEffect,
                DogmaAttributeId.NaniteRepairCostBonus,
                DogmaAttributeId.ModuleRepairCostMultiplier
            ),
            NaniteRepairSkill.Interfacing => (
                InterfacingLevelEffect,
                InterfacingRateEffect,
                DogmaAttributeId.NaniteRepairRateBonus,
                DogmaAttributeId.ModuleRepairRate
            ),
            _ => throw new InvalidDataException("Unknown nanite repair skill."),
        };
        var effects = PassiveCatalog.Effects(catalog, skill);
        if (effects.Count != 3 || effects.Select(e => e.EffectID).Distinct().Count() != effects.Count)
        {
            throw new InvalidDataException("Unverified nanite repair skill effects.");
        }
        var scale = effects.SingleOrDefault(e => e.EffectID == scaleId);
        var apply = effects.SingleOrDefault(e => e.EffectID == applyId);
        if (
            !catalog.FittingAttributes(skill).TryGetValue(source, out double bonus)
            || effects.SingleOrDefault(e => e.EffectID == StandardSkillEffect)?.EffectCategory != (int)PassiveEffectPhase.Fitted
            || !Matches(scale, "itemID", source, DogmaAttributeId.SkillLevel, DogmaOperation.PreMultiply)
            || !Matches(apply, "charID", target, source, DogmaOperation.PostPercent)
            || !double.IsFinite(bonus)
            || (skill == (int)NaniteRepairSkill.Operation ? bonus < MinimumOperationBonusPerLevel || bonus > 0 : bonus < 0)
        )
        {
            throw new InvalidDataException("Unverified nanite repair skill program.");
        }
        return PassiveCatalog.Modifiers(catalog, skill);
    }

    private static bool Matches(
        ClientDogmaeffects? effect,
        string domain,
        DogmaAttributeId target,
        DogmaAttributeId source,
        DogmaOperation operation
    ) =>
        effect?.EffectCategory == (int)PassiveEffectPhase.Fitted
        && effect.ModifierInfo is { Length: 1 } rows
        && rows[0].Domain == domain
        && rows[0].Func == "ItemModifier"
        && rows[0].ModifiedAttributeID == (long)target
        && rows[0].ModifyingAttributeID == (long)source
        && rows[0].Operation == (int)operation;
}
