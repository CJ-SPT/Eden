namespace Eden.Gameplay.Agents;

public sealed record MissionStructureProgress(long Id, bool Required, bool CompletesMission, bool Destroyed);

public static class MissionStructureRules
{
    public const int LargeCollidableStructureGroup = 319;

    // Build 3662168 group for the destructible Habitat used by Avenge a Fallen Comrade.
    public const int DeadspaceOverseerStructureGroup = 494;
    public const int RecoveryContainerType = 23;
    public const int RecoveryContainerGroup = 12;

    public static bool IsDestructibleGroup(int groupId) => groupId is LargeCollidableStructureGroup or DeadspaceOverseerStructureGroup;

    public static bool Complete(bool enemiesComplete, IReadOnlyList<MissionStructureProgress> structures) =>
        structures.Any(s => s.Destroyed && s.CompletesMission) || enemiesComplete && structures.All(s => !s.Required || s.Destroyed);
}
