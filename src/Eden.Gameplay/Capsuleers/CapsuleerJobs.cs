namespace Eden.Gameplay.Capsuleers;

public enum CapsuleerAction
{
    Prepare = 1,
    Arrival,
    GateTransfer,
    Lock,
    Collect,
    Trade,
    Retry,
}

public sealed record CapsuleerJob(long PilotId, long Revision, long Sequence, long DueTicks, CapsuleerAction Action)
{
    public CapsuleerJob Next(long dueTicks, CapsuleerAction action)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dueTicks);
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }
        return this with { Revision = checked(Revision + 1), Sequence = checked(Sequence + 1), DueTicks = dueTicks, Action = action };
    }
}

public sealed record CapsuleerTurn(int Completed, int Pending, long? OldestDueTicks, bool BudgetExceeded);

public sealed record CapsuleerQueueStatus(int Due, long? OldestDueTicks);

// This queue is used under the world owner. Callers publish a replacement only after
// its feature transaction commits. A throwing action stays due and can be retried.
public sealed class CapsuleerJobQueue
{
    public const int ActionsPerTurn = 64;
    public static readonly TimeSpan WakeInterval = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan ExecutionBudget = TimeSpan.FromMilliseconds(10);

    private sealed record ScheduledJob(CapsuleerJob Job, CapsuleerRetry? Retry = null)
    {
        internal long DueTicks => Math.Max(Job.DueTicks, Retry?.DueTicks ?? 0);
    }

    private readonly SortedSet<ScheduledJob> _queue = new(
        Comparer<ScheduledJob>.Create(
            (left, right) =>
                (left.DueTicks, left.Job.PilotId, left.Job.Sequence).CompareTo((right.DueTicks, right.Job.PilotId, right.Job.Sequence))
        )
    );
    private Dictionary<long, ScheduledJob> _jobs = [];

    public long? NextDueTicks => _queue.Min?.DueTicks;
    public CapsuleerJob? NextJob => _queue.Min?.Job;

    public void Restore(IEnumerable<CapsuleerJob> jobs)
    {
        var restored = jobs.ToArray();
        if (restored.Select(j => j.PilotId).Distinct().Count() != restored.Length)
        {
            throw new ArgumentException("An agent can have only one current job.", nameof(jobs));
        }
        foreach (var job in restored)
        {
            Validate(job);
        }
        // Rebuilding after a different pilot fails must retain existing retry eligibility.
        var entries = restored.ToDictionary(
            job => job.PilotId,
            job => _jobs.TryGetValue(job.PilotId, out var current) && current.Job == job ? current : new ScheduledJob(job)
        );
        _jobs = entries;
        _queue.Clear();
        foreach (var entry in entries.Values)
        {
            Enqueue(entry);
        }
    }

    public bool Publish(CapsuleerJob job)
    {
        Validate(job);
        if (_jobs.TryGetValue(job.PilotId, out var current) && job.Revision <= current.Job.Revision)
        {
            return false;
        }
        if (current is not null)
        {
            _queue.Remove(current);
        }
        var scheduled = new ScheduledJob(job);
        _jobs[job.PilotId] = scheduled;
        Enqueue(scheduled);
        return true;
    }

    public bool Defer(CapsuleerJob job, long nowTicks)
    {
        if (!_jobs.TryGetValue(job.PilotId, out var current) || current.Job != job)
        {
            return false;
        }
        var deferred = current with { Retry = CapsuleerRetry.Next(current.Retry, nowTicks) };
        _queue.Remove(current);
        _jobs[job.PilotId] = deferred;
        Enqueue(deferred);
        return true;
    }

    public bool Remove(long pilotId)
    {
        if (!_jobs.Remove(pilotId, out var job))
        {
            return false;
        }
        _queue.Remove(job);
        return true;
    }

    public CapsuleerTurn Run(
        long throughTicks,
        Func<CapsuleerJob, CapsuleerJob> execute,
        TimeProvider clock,
        IReadOnlySet<long>? pilots = null,
        bool useCanonicalQueue = false,
        int maximumActions = ActionsPerTurn
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(throughTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumActions);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumActions, ActionsPerTurn);
        long started = clock.GetTimestamp();
        int completed = 0;
        if (_queue.Min is not { } first || first.DueTicks > throughTicks)
        {
            return new(0, _jobs.Count, null, false);
        }
        // The scheduler maintains exact active membership for canonical turns.
        // Other scoped callers select their candidates without changing queue membership.
        var candidates =
            pilots is null || useCanonicalQueue
                ? _queue
                : new SortedSet<ScheduledJob>(pilots.Where(_jobs.ContainsKey).Select(p => _jobs[p]), _queue.Comparer);
        while (candidates.Min is { } scheduled && scheduled.DueTicks <= throughTicks && completed < maximumActions)
        {
            if (completed > 0 && clock.GetElapsedTime(started) >= ExecutionBudget)
            {
                break;
            }
            var job = scheduled.Job;
            var next = execute(job);
            Validate(next);
            if (
                next.PilotId != job.PilotId
                || next.Revision != checked(job.Revision + 1)
                || next.Sequence != checked(job.Sequence + 1)
                || next.DueTicks <= job.DueTicks
            )
            {
                throw new InvalidOperationException("A completed action must advance its own job and deadline once.");
            }
            _queue.Remove(scheduled);
            candidates.Remove(scheduled);
            var replacement = new ScheduledJob(next);
            _jobs[job.PilotId] = replacement;
            if (useCanonicalQueue && pilots is not null && !pilots.Contains(next.PilotId))
            {
                _jobs.Remove(next.PilotId);
            }
            else
            {
                Enqueue(replacement);
            }
            // A witnessed transfer can withdraw its pilot from this turn when
            // the destination is empty. Its next canonical job stays dormant.
            if (pilots is null || pilots.Contains(next.PilotId))
            {
                candidates.Add(replacement);
            }
            completed++;
        }
        long? oldest = candidates.Min is { } pending && pending.DueTicks <= throughTicks ? pending.DueTicks : null;
        return new(completed, _jobs.Count, oldest, clock.GetElapsedTime(started) > ExecutionBudget);
    }

    public CapsuleerQueueStatus Inspect(long throughTicks) =>
        new(
            _queue.TakeWhile(j => j.DueTicks <= throughTicks).Count(),
            _queue.Min is { } first && first.DueTicks <= throughTicks ? first.DueTicks : null
        );

    private void Enqueue(ScheduledJob job) => _queue.Add(job);

    private static void Validate(CapsuleerJob job)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(job.PilotId);
        ArgumentOutOfRangeException.ThrowIfNegative(job.Revision);
        ArgumentOutOfRangeException.ThrowIfNegative(job.Sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(job.DueTicks);
        if (!Enum.IsDefined(job.Action))
        {
            throw new ArgumentOutOfRangeException(nameof(job));
        }
    }
}
