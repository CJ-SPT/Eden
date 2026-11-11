namespace Eden.Gameplay.Capsuleers;

public sealed record CapsuleerBlockingReason(string Reason, int Agents);

public sealed record CapsuleerReturnProgress(int SystemId, long EntryTicks, DateTimeOffset SampledAt, int Bots, long MaximumSkippedTicks);

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
    public int CoarseAgents { get; init; }
    public int CoarseSystems { get; init; }
    public int CombatPinnedSystems { get; init; }
    public long RemoteAgeTicks { get; init; }
    public long SkippedRemoteTicks { get; init; }
    public IReadOnlyList<CapsuleerReturnProgress> RecentReturns { get; init; } = [];
    public long BackgroundBudgetOverruns { get; init; }
    public int PendingPreparationSystems { get; init; }
    public TimeSpan LastUpdate { get; init; }
    public TimeSpan LastPresentation { get; init; }
    public TimeSpan LastSimulation => LastUpdate > LastPresentation ? LastUpdate - LastPresentation : TimeSpan.Zero;
    public long BudgetOverruns { get; init; }
    public TimeSpan MaximumTurnOverrun { get; init; }

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
