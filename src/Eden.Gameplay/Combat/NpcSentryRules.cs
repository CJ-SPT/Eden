using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Combat;

public static class NpcSentryRules
{
    // Build 3662168 faction sentries, in the Sentry Gun group.
    public const int Group = 99;

    public static int? Type(NpcEmpireFaction faction) =>
        faction switch
        {
            NpcEmpireFaction.Amarr => 1194,
            NpcEmpireFaction.Caldari => 3740,
            NpcEmpireFaction.Gallente => 3742,
            NpcEmpireFaction.Minmatar => 3743,
            _ => null,
        };

    public static double GridRange(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        Math.Min(NpcEncounterDefinitions.VisibilityRange, attributes.GetValueOrDefault(DogmaAttributeId.EntityAttackRange));
}
