namespace Eden.Gameplay.Exploration;

// Scene identities come from talecommon.scenetypes in client build 3662168.
public enum IncursionScene
{
    Vanguard = 3,
    Staging = 4,
}

public sealed record IncursionSystem(int SystemId, IncursionScene SceneType);

public sealed record IncursionFocus(
    long TaleId,
    int StagingSystemId,
    int ConstellationId,
    long LastUpdated,
    long EndsAt,
    double Influence,
    IReadOnlyList<IncursionSystem> Systems
);

public sealed record IncursionPendingLoyalty(long TaleId, long Amount);

public interface IIncursionDirectory
{
    IReadOnlyList<IncursionFocus> Read(long now);
    IncursionPendingLoyalty? PendingLoyalty(long pilotId, long now);
}
