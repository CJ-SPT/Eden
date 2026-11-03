using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Server.Catalog;

namespace Eden.Server.Persistence;

internal static class ScriptCatalog
{
    private const int AbyssalMetaGroup = 15;

    private enum ScriptEffect
    {
        ScanResolution = 3597,
        TargetingRange = 3598,
        TrackingSpeed = 3599,
        OptimalRange = 3600,
        Falloff = 3686,
        ExplosionRadius = 6128,
        ExplosionVelocity = 6129,
        MissileVelocity = 6130,
        MissileLifetime = 6131,
        SensorStrength = 6488,
    }

    // Build 3662168 fitted script effects: target is the containing module (otherID).
    private static IReadOnlySet<ScriptEffect> Effects(int group) =>
        group switch
        {
            SupportModuleDefinitions.TrackingScriptGroup => new HashSet<ScriptEffect>
            {
                ScriptEffect.TrackingSpeed,
                ScriptEffect.OptimalRange,
                ScriptEffect.Falloff,
                ScriptEffect.ExplosionRadius,
                ScriptEffect.ExplosionVelocity,
            },
            SupportModuleDefinitions.SensorScriptGroup => new HashSet<ScriptEffect>
            {
                ScriptEffect.ScanResolution,
                ScriptEffect.TargetingRange,
                ScriptEffect.SensorStrength,
            },
            SupportModuleDefinitions.GuidanceScriptGroup => new HashSet<ScriptEffect>
            {
                ScriptEffect.ExplosionRadius,
                ScriptEffect.ExplosionVelocity,
                ScriptEffect.MissileVelocity,
                ScriptEffect.MissileLifetime,
            },
            _ => new HashSet<ScriptEffect>(),
        };

    internal static bool Supported(CatalogDbContext catalog, int typeId, int group)
    {
        var expected = Effects(group);
        if (expected.Count == 0)
        {
            return false;
        }
        var type = catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(typeId))?.Read<ClientTypes>();
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        var effects = PassiveCatalog.Effects(catalog, typeId);
        return type?.Published == 1
            && type.MetaGroupID != AbyssalMetaGroup
            && attributes.GetValueOrDefault(DogmaAttributeId.ChargeSize) == SupportModuleDefinitions.ScriptChargeSize
            && FittingCatalog.DefaultEffect(catalog, typeId) is null
            && effects.Count == expected.Count
            && expected.SetEquals(effects.Select(e => (ScriptEffect)checked((int)e.EffectID!.Value)))
            && effects.Count == FittingCatalog.Effects(catalog, typeId).Count
            && effects.All(e =>
                e.EffectCategory == (int)PassiveEffectPhase.Fitted
                && e.ModifierInfo is { Length: > 0 }
                && e.ModifierInfo.All(m =>
                    m.Domain == "otherID"
                    && m.Func == "ItemModifier"
                    && m.Operation == (int)DogmaOperation.PostPercent
                    && m.ModifiedAttributeID is not null
                    && m.ModifyingAttributeID is long source
                    && attributes.TryGetValue((DogmaAttributeId)checked((int)source), out double value)
                    && double.IsFinite(value)
                    && value >= -100
                )
            );
    }

    internal static IReadOnlyList<ScriptModifier> Modifiers(CatalogDbContext catalog, int typeId) =>
        PassiveCatalog
            .Effects(catalog, typeId)
            .SelectMany(e => e.ModifierInfo ?? [])
            .Select(m => new ScriptModifier(
                (DogmaAttributeId)checked((int)m.ModifiedAttributeID!.Value),
                (DogmaAttributeId)checked((int)m.ModifyingAttributeID!.Value),
                (DogmaOperation)m.Operation!.Value
            ))
            .ToArray();
}
