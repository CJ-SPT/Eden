namespace Eden.Gameplay.Capsuleers;

public sealed record CapsuleerBlockingReason(string Reason, int Agents);

public sealed record CapsuleerRuntimeSummary(
    CapsuleerPopulationSummary Population,
    int KnownSystems,
    int ObservedSystems,
    int Jobs,
    int DueJobs,
    long OldestDelayTicks,
    long CompletedActions,
    long Failures,
    TimeSpan LastTurn,
    IReadOnlyList<CapsuleerBlockingReason> Blocked,
    string? LastFailure
)
{
    public int ScheduledAgents { get; init; }
    public TimeSpan LastUpdate { get; init; }
    public TimeSpan LastPresentation { get; init; }

    public static CapsuleerRuntimeSummary Empty { get; } = new(new(0, 0, 0, 0, 0), 0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, [], null);
}

public sealed record CapsuleerUpdateTiming(TimeSpan Total, TimeSpan Presentation);

public interface ICapsuleerUpdateDiagnostics
{
    void RecordUpdate(CapsuleerUpdateTiming timing);
}

public interface ICapsuleerDiagnostics
{
    CapsuleerRuntimeSummary Read();
}
