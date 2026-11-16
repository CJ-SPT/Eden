using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;

namespace Eden.World.Runtime.Economy;

internal sealed class MissileMotionHistory
{
    private sealed record Sample(long At, SpacePosition Position, SpacePosition Velocity);

    private readonly SortedList<long, Sample> _samples = [];
    private int _flights;
    private long _expires;
    private long _longestFlight;

    internal void AddFlight(EconomyWorldMissile row)
    {
        _flights++;
        _expires = Math.Max(_expires, row.ExpiresAt);
        _longestFlight = Math.Max(_longestFlight, row.ExpiresAt - row.LaunchedAt);
    }

    internal bool RemoveFlight() => --_flights == 0;

    internal void Observe(long now, SpacePosition position, SpacePosition velocity)
    {
        if (now > _expires)
        {
            return;
        }
        _samples[now] = new(now, position, velocity);
        while (_samples.Count > 0 && _samples.Keys[0] < now - _longestFlight - TimeSpan.TicksPerSecond)
        {
            _samples.RemoveAt(0);
        }
    }

    internal sealed record FlightStep(WorldMissile Row, MissilePayload Payload, long Time);

    internal static IEnumerable<FlightStep> DueSteps(WorldMissile[] rows, long now, Func<WorldMissile, MissilePayload> advance)
    {
        // Keep one predicted step per due flight. A later launch can hit before an
        // older one, and an overdue surviving flight can owe more than one step.
        PriorityQueue<FlightStep, (long Time, long Id)> pending = new();
        foreach (var row in rows)
        {
            Schedule(row);
        }
        while (pending.TryDequeue(out var step, out _))
        {
            if (step.Row.Status == MissileFlightStatus.Flying)
            {
                yield return step;
                Schedule(step.Row);
            }
        }

        void Schedule(WorldMissile row)
        {
            if (row.Status != MissileFlightStatus.Flying || now < Math.Min(row.UpdatedAt + TimeSpan.TicksPerSecond, row.ExpiresAt))
            {
                return;
            }
            var next = advance(row);
            long time = checked(row.LaunchedAt + (long)Math.Round(next.Flight.ElapsedSeconds * TimeSpan.TicksPerSecond));
            pending.Enqueue(new(row, next, time), (time, row.Id));
        }
    }

    internal static MissilePayload Step(
        WorldAction world,
        WorldMissile row,
        MissilePayload payload,
        MissileTarget? current,
        long observedAt
    )
    {
        if (current is null || payload.Flight.Orphaned)
        {
            return payload with { Flight = MissileFlightRules.Tick(payload.Flight, null), Target = null };
        }
        long from = row.UpdatedAt;
        long end = Math.Min(from + TimeSpan.TicksPerSecond, row.ExpiresAt);
        MissileTarget first = payload.Target ?? At(current, observedAt, from);
        List<MissileMotionChange> changes = [];
        long previous = from;
        var history = (world as EconomyTransaction)?.MissileHistory(row.TargetId);
        if (history is not null)
        {
            foreach (var sample in history._samples.Values)
            {
                if (sample.At > from && sample.At <= observedAt && sample.At - TimeSpan.TicksPerSecond < end)
                {
                    Include(new(sample.Position, sample.Velocity, row.TargetRadius), sample.At);
                }
            }
        }
        if (observedAt > previous && observedAt - TimeSpan.TicksPerSecond < end)
        {
            Include(current, observedAt);
        }
        var flight = MissileFlightRules.Tick(payload.Flight, first, changes, out var target);
        return payload with { Flight = flight, Target = target };

        // A Destiny velocity describes its own one-second motion frame. Preserve the
        // last paid flight boundary across gaps and use each recorded turn only in its frame.
        void Include(MissileTarget measured, long at)
        {
            long start = Math.Max(previous, at - TimeSpan.TicksPerSecond);
            var target = At(measured, at, start);
            if (start == from)
            {
                first = target;
            }
            else if (start < end)
            {
                changes.Add(new((start - from) / (double)TimeSpan.TicksPerSecond, target));
            }
            previous = at;
        }

        static MissileTarget At(MissileTarget measured, long at, long time) =>
            measured with
            {
                Position = measured.Position + measured.Velocity * ((time - at) / (double)TimeSpan.TicksPerSecond),
            };
    }
}

internal sealed partial class EconomyTransaction
{
    internal MissileMotionHistory? MissileHistory(long id) => store.MissileHistory(id);
}
