namespace Eden.Gameplay.Exploration;

// Scene identities come from talecommon.scenetypes in client build 3662168.
public enum IncursionScene
{
    Headquarters = 1,
    Assault = 2,
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
    IReadOnlyList<IncursionSystem> Systems,
    bool HasFinalEncounter = false
);

public sealed record IncursionPendingLoyalty(long TaleId, long Amount);

public sealed record IncursionAgencyUpdate(long TaleId, long LastUpdated, double Influence, bool HasFinalEncounter, bool IsActive);

public interface IIncursionDirectory
{
    IncursionAgencyUpdate? AgencyUpdate => null;
    IReadOnlyList<IncursionFocus> Read(long now);
    IncursionPendingLoyalty? PendingLoyalty(long pilotId, long now);
}
