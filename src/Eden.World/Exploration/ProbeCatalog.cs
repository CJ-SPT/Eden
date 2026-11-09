using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Fittings;

namespace Eden.World.Exploration;

internal static class ProbeCatalog
{
    private const int AbyssalMetaGroup = 15;
    private const int ProbeLaunchingEffect = 3793;

    internal static bool Launcher(CatalogDbContext catalog, int typeId, int group)
    {
        if (group != ProbeScanRules.LauncherGroup)
        {
            return false;
        }
        var type = catalog.Read(CatalogData.Client.Types, (long)typeId);
        var effects = PassiveCatalog.Effects(catalog, typeId);
        return type?.Published == 1
            && type.MetaGroupID != AbyssalMetaGroup
            && FittingCatalog.DefaultEffect(catalog, typeId) == DogmaEffect.UseMissiles
            && effects.Any(e => e.EffectID == (int)DogmaEffect.HighPower)
            && effects.Any(e => e.EffectID == (int)DogmaEffect.Online)
            && effects.Count == FittingCatalog.Effects(catalog, typeId).Count
            && effects.All(e =>
                e.EffectID is (int)DogmaEffect.Online or (int)DogmaEffect.HighPower or (int)DogmaEffect.UseMissiles
                || e.EffectCategory is 0 or 4
                    && (e.ModifierInfo ?? []).All(m => PassiveCatalog.Parse(m, e.EffectCategory.Value) is not null)
            );
    }

    internal static bool Probe(CatalogDbContext catalog, int typeId, int group)
    {
        if (
            group != ProbeScanRules.ProbeGroup
            || catalog.Read(CatalogData.Client.Types, (long)typeId)?.Published != 1
            || FittingCatalog.DefaultEffect(catalog, typeId) != (DogmaEffect)ProbeLaunchingEffect
        )
        {
            return false;
        }
        var a = catalog.FittingAttributes(typeId);
        return a.GetValueOrDefault(DogmaAttributeId.ProbeStrength) > 0
            && a.GetValueOrDefault(DogmaAttributeId.ProbeBaseRange) > 0
            && a.GetValueOrDefault(DogmaAttributeId.ProbeRangeFactor) > 1;
    }

    internal static bool Compatible(
        IReadOnlyDictionary<DogmaAttributeId, double> launcher,
        IReadOnlyDictionary<DogmaAttributeId, double> probe
    ) =>
        launcher.GetValueOrDefault(DogmaAttributeId.ChargeGroup1) == ProbeScanRules.ProbeGroup
        && launcher.GetValueOrDefault(DogmaAttributeId.ChargeSize) >= probe.GetValueOrDefault(DogmaAttributeId.ChargeSize);

    internal static ProbeProfile FittedProfile(CatalogDbContext catalog, FittingCalculation fitting, long target, int typeId, int hullType)
    {
        var attributes = catalog.FittingAttributes(typeId);
        var required = InventoryDefinitions
            .RequiredSkills.Select(p => attributes.GetValueOrDefault(p.Skill))
            .Where(v => v > 0)
            .Select(v => checked((int)v))
            .ToHashSet();
        var ship = Evaluate(Modifiers(HullCatalog.Modifiers(catalog, hullType), fitting.ShipId, true));
        var modules = fitting
            .Modules.Where(m => m.Item.Online || InventoryDefinitions.IsRigSlot(m.Item.FlagId))
            .Where(m =>
                m.GroupId is ProbeScanRules.LauncherGroup or (int)PassiveModuleGroup.ScanPinpointingAndRangefinding
                || InventoryDefinitions.IsRigSlot(m.Item.FlagId)
            )
            .SelectMany(m =>
                Modifiers(
                    InventoryDefinitions.IsRigSlot(m.Item.FlagId)
                        ? RigCatalog.Modifiers(catalog, m.Item.TypeId)
                        : PassiveCatalog.Modifiers(catalog, m.Item.TypeId),
                    m.Item.Id,
                    false
                )
            );
        var equipment = Evaluate(modules);
        return Profile(fitting.Graph.Attributes(target)) with
        {
            Bonuses = new(ship.Strength, ship.Deviation, equipment.Strength, equipment.Deviation),
        };

        IEnumerable<DogmaModifier> Modifiers(IReadOnlyList<PassiveModifier> modifiers, long source, bool exempt) =>
            modifiers
                .Where(m =>
                    m.Selector == ModifierSelector.OwnerRequiredSkill
                    && m.SelectionId is int skill
                    && required.Contains(skill)
                    && m.Attribute is DogmaAttributeId.ProbeStrength or DogmaAttributeId.ProbeDeviation
                )
                .Select(m => new DogmaModifier(
                    0,
                    m.Attribute,
                    m.Operation,
                    new DogmaValue.Constant(fitting.Graph.Read(source, m.SourceAttribute)),
                    exempt
                ));

        static (double Strength, double Deviation) Evaluate(IEnumerable<DogmaModifier> applied)
        {
            DogmaGraph graph = new(
                [
                    new(
                        0,
                        new Dictionary<DogmaAttributeId, double>
                        {
                            [DogmaAttributeId.ProbeStrength] = 1,
                            [DogmaAttributeId.ProbeDeviation] = 1,
                        }
                    ),
                ],
                applied.ToArray(),
                new Dictionary<DogmaAttributeId, DogmaAttributeDefinition>
                {
                    [DogmaAttributeId.ProbeStrength] = new(DogmaAttributeId.ProbeStrength, 1, false),
                    [DogmaAttributeId.ProbeDeviation] = new(DogmaAttributeId.ProbeDeviation, 1, false),
                }
            );
            return ((graph.Read(0, DogmaAttributeId.ProbeStrength) - 1) * 100, (graph.Read(0, DogmaAttributeId.ProbeDeviation) - 1) * 100);
        }
    }

    internal static ProbeProfile Profile(IReadOnlyDictionary<DogmaAttributeId, double> a) =>
        new(
            a.GetValueOrDefault(DogmaAttributeId.ProbeStrength),
            a.GetValueOrDefault(DogmaAttributeId.ProbeBaseRange),
            a.GetValueOrDefault(DogmaAttributeId.ProbeRangeFactor),
            a.GetValueOrDefault(DogmaAttributeId.ProbeDeviation),
            a.GetValueOrDefault(DogmaAttributeId.MissileFlightTime)
        );
}
