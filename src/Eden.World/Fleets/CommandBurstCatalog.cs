using System.Collections.Immutable;
using System.Globalization;
using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Fleets;

internal static class CommandBurstCatalog
{
    private const int CpuEffect = 5264;
    private const int ChargeEffect = 6737;
    private static readonly CatalogTypeRule s_modules = new("command-burst-modules", ValidateModule);
    private static readonly CatalogTypeRule s_charges = new("command-burst-charges", ValidateCharge);
    private static readonly CatalogDefinition<CommandBurstCollection> s_collections = new("command-burst-collections", LoadCollection);

    internal static readonly int[] Skills =
    [
        3348,
        3349,
        3350,
        3351,
        3352,
        3354,
        11569,
        11572,
        11574,
        20494,
        20495,
        22536,
        22552,
        24764,
        89610,
        89611,
    ];

    // Effect 6737 assigns each collection ID to the launcher and multiplies its corresponding strength.
    private static readonly (DogmaAttributeId Id, DogmaAttributeId Strength, DogmaAttributeId Multiplier)[] s_slots =
    [
        ((DogmaAttributeId)2468, (DogmaAttributeId)2469, (DogmaAttributeId)2596),
        ((DogmaAttributeId)2470, (DogmaAttributeId)2471, (DogmaAttributeId)2597),
        ((DogmaAttributeId)2472, (DogmaAttributeId)2473, (DogmaAttributeId)2598),
        ((DogmaAttributeId)2536, (DogmaAttributeId)2537, (DogmaAttributeId)2599),
    ];

    internal static bool Module(CatalogDbContext catalog, int type, int group) => s_modules.Supports(catalog, type, group);

    internal static bool Charge(CatalogDbContext catalog, int type, int group) => s_charges.Supports(catalog, type, group);

    internal static CommandBurstCollection Collection(CatalogDbContext catalog, int id) => catalog.Definition(s_collections, id);

    private static bool ValidateModule(CatalogDbContext catalog, int type, int group)
    {
        if (
            group != CommandBurstRules.Group
            || catalog.Read(CatalogData.Client.Types, (long)type)?.Published != 1
            || FittingCatalog.DefaultEffect(catalog, type) is not { } effect
            || !CommandBurstRules.IsBurst(effect)
            || !FittingCatalog.Effects(catalog, type).SetEquals([DogmaEffect.HighPower, DogmaEffect.Online, (DogmaEffect)CpuEffect, effect])
            || catalog.Read(CatalogData.Client.Dogmaeffects, (long)effect)
                is not {
                    EffectCategory: (int)PassiveEffectPhase.Active,
                    IsOffensive: 0,
                    IsWarpSafe: 1,
                    DisallowAutoRepeat: 0,
                    RangeAttributeID: (int)DogmaAttributeId.MaxRange,
                    DurationAttributeID: (int)DogmaAttributeId.Duration,
                    DischargeAttributeID: (int)DogmaAttributeId.CapacitorNeed,
                }
        )
        {
            return false;
        }
        var attributes = catalog.FittingAttributes(type);
        try
        {
            _ = CommandBurstRules.Define(0, type, effect, attributes);
            return CommandBurstRules.Compatible(
                    group,
                    checked((int)attributes.GetValueOrDefault(DogmaAttributeId.ChargeGroup1)),
                    attributes
                ) && InventoryDefinitions.RequiresSpecificShip(attributes);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool ValidateCharge(CatalogDbContext catalog, int type, int group)
    {
        if (
            !CommandBurstRules.IsCharge(group)
            || catalog.Read(CatalogData.Client.Types, (long)type)?.Published != 1
            || !FittingCatalog.Effects(catalog, type).SetEquals([(DogmaEffect)ChargeEffect])
        )
        {
            return false;
        }
        try
        {
            var a = catalog.FittingAttributes(type);
            var values = s_slots.Where(slot => a.GetValueOrDefault(slot.Id) > 0).ToArray();
            foreach (var slot in values)
            {
                _ = Collection(catalog, checked((int)a[slot.Id]));
            }
            return values.Length > 0 && values.All(slot => double.IsFinite(a.GetValueOrDefault(slot.Multiplier)));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal static IEnumerable<DogmaModifier> ChargeModifiers(long module, long charge)
    {
        foreach (var (id, strength, multiplier) in s_slots)
        {
            yield return new(module, id, DogmaOperation.PreAssignment, new DogmaValue.Attribute(charge, id), true);
            yield return new(module, strength, DogmaOperation.PostMultiply, new DogmaValue.Attribute(charge, multiplier), true);
        }
    }

    internal static CommandBurstValue[] Values(DogmaGraph graph, long module) =>
        s_slots
            .Select(slot => new CommandBurstValue(checked((int)graph.Read(module, slot.Id)), graph.Read(module, slot.Strength)))
            .Where(value => value.CollectionId > 0)
            .ToArray();

    private static CommandBurstCollection LoadCollection(CatalogDbContext catalog, int id)
    {
        var row =
            catalog.Read(CatalogData.Client.Dbuffcollections, id.ToString(CultureInfo.InvariantCulture))
            ?? throw new InvalidDataException($"Missing command burst collection {id}.");
        DogmaOperation operation = row.OperationName switch
        {
            "PostPercent" => DogmaOperation.PostPercent,
            "ModAdd" => DogmaOperation.Add,
            "PostAssignment" => DogmaOperation.PostAssignment,
            _ => throw new InvalidDataException($"Unsupported command burst operation {row.OperationName}."),
        };
        bool minimum = row.AggregateMode switch
        {
            "Minimum" => true,
            "Maximum" => false,
            _ => throw new InvalidDataException($"Unsupported command burst aggregation {row.AggregateMode}."),
        };
        List<CommandBurstModifier> modifiers = [];
        modifiers.AddRange(
            (row.ItemModifiers ?? []).Select(m => new CommandBurstModifier(
                (DogmaAttributeId)m.DogmaAttributeID!.Value,
                ModifierSelector.Item
            ))
        );
        modifiers.AddRange(
            (row.LocationModifiers ?? []).Select(m => new CommandBurstModifier(
                (DogmaAttributeId)m.DogmaAttributeID!.Value,
                ModifierSelector.Location
            ))
        );
        modifiers.AddRange(
            (row.LocationGroupModifiers ?? []).Select(m => new CommandBurstModifier(
                (DogmaAttributeId)m.DogmaAttributeID!.Value,
                ModifierSelector.Group,
                (int)m.GroupID!.Value
            ))
        );
        modifiers.AddRange(
            (row.LocationRequiredSkillModifiers ?? []).Select(m => new CommandBurstModifier(
                (DogmaAttributeId)m.DogmaAttributeID!.Value,
                ModifierSelector.RequiredSkill,
                (int)m.SkillID!.Value
            ))
        );
        return new(id, minimum, operation, modifiers.ToImmutableArray());
    }
}
