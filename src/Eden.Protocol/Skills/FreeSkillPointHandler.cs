using Eden.Gameplay.Skills;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Skills;

[Injectable(InjectionType.Scoped)]
internal sealed class FreeSkillPointHandler(IPilotFreeSkillPoints points, SkillHandler skills, TimeProvider clock, IPilotSkillReader reader)
    : ISelectedPilotCallHandler
{
    private long? _pilot;
    private long _balance;
    private long _revision;
    private long _nextPoll;
    public bool IsMonitoring => _pilot.HasValue;

    public void Enter(PilotCallContext context)
    {
        if (_pilot != context.Pilot.PilotId)
        {
            _pilot = context.Pilot.PilotId;
            var state = points.Observe(_pilot.Value);
            _balance = state.Points;
            _revision = state.Revision;
            _nextPoll = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        }
    }

    public void Reset() => _pilot = null;

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            Reset();
            return;
        }
        Enter(context);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (now < _nextPoll)
        {
            return;
        }
        var latest = points.Observe(context.Pilot.PilotId);
        if (latest.Revision != _revision)
        {
            // A different session can credit and spend the same amount between
            // polls. Persisted revisions catch that even when the pool is equal.
            RefreshExternal(context);
        }
        else
        {
            _nextPoll = checked(now + TimeSpan.TicksPerSecond);
        }
    }

    public void Notify(PilotCallContext context, long balance, long revision, TrainingState training)
    {
        Enter(context);
        skills.Apply(context, training, true);
        Observe(context, balance, revision, true);
    }

    private FreeSkillPointState RefreshExternal(PilotCallContext context)
    {
        var state = points.Snapshot(context.Pilot.PilotId);
        var ids = reader.ReadSkills(context.Pilot.PilotId).Select(s => s.TypeId).ToArray();
        skills.Apply(context, new(state.Training.Queue, ids), true);
        Observe(context, state.Balance, state.Revision, true);
        return state;
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != "@skills"
            || method
                is not (
                    "GetFreeSkillPoints"
                    or "GetSkillQueueAndFreePoints"
                    or "ApplyFreeSkillPoints"
                    or "GetFreeSkillPointsAppliedToQueue"
                    or "ApplyFreeSkillPointsToQueue"
                    or "GetFreeSkillPointsAppliedToSkills"
                    or "ApplyFreeSkillPointsToSkills"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected free skill point options.");
            }
            Enter(context);
            long pilot = context.Pilot.PilotId;
            if (method == "GetFreeSkillPoints" && args.Length == 0)
            {
                var balance = points.Observe(pilot);
                if (balance.Revision != _revision)
                {
                    return RefreshExternal(context).Balance;
                }
                Observe(context, balance.Points, balance.Revision, false);
                return balance.Points;
            }
            if (method == "GetSkillQueueAndFreePoints" && args.Length == 0)
            {
                var state = points.Snapshot(pilot);
                bool external = state.Revision != _revision;
                var training = external
                    ? new TrainingState(state.Training.Queue, reader.ReadSkills(pilot).Select(s => s.TypeId).ToArray())
                    : state.Training;
                var rows = skills.Apply(context, training, external || training.ChangedSkills.Count > 0);
                Observe(context, state.Balance, state.Revision, external);
                return new SkillQueueReply(rows, state.Balance);
            }
            FreePointRequest request = (method, args) switch
            {
                ("ApplyFreeSkillPoints", [long type, long amount]) when type is > 0 and <= int.MaxValue && amount > 0 =>
                    new FreePointRequest.Single((int)type, amount),
                ("GetFreeSkillPointsAppliedToQueue" or "ApplyFreeSkillPointsToQueue", []) => new FreePointRequest.Queue(),
                ("GetFreeSkillPointsAppliedToSkills" or "ApplyFreeSkillPointsToSkills", [List<object?> levels]) =>
                    new FreePointRequest.Targets(levels.Select(Level).ToArray()),
                _ => throw new InvalidDataException("Invalid free skill point request."),
            };
            if (method.StartsWith("Get", StringComparison.Ordinal))
            {
                var preview = points.Preview(pilot, request);
                return new BlueMap<int, long>(preview.Allocations.ToDictionary(a => a.TypeId, a => a.Spent));
            }
            var result = points.Apply(pilot, request);
            if (result.Allocations.Count > 0 || result.Training.ChangedSkills.Count > 0)
            {
                Notify(context, result.Remaining, result.Revision, result.Training);
            }
            return method == "ApplyFreeSkillPoints" ? result.Remaining : PilotCallResult.None;
        }
        catch (InvalidTrainingQueue error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("This skill point amount is too large.");
        }
    }

    private void Observe(PilotCallContext context, long balance, long revision, bool notify)
    {
        if (notify && balance != _balance)
        {
            context.Notifications.Add(new FreeSkillPointsChanged(balance));
        }
        _balance = balance;
        _revision = revision;
        _nextPoll = checked(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() + TimeSpan.TicksPerSecond);
    }

    private static TrainingTarget Level(object? value) =>
        value is object?[] pair && pair is [long type, long level] && type is > 0 and <= int.MaxValue && level is >= 1 and <= 5
            ? new((int)type, (int)level)
            : throw new InvalidDataException("Choose a skill and level.");
}

internal sealed record FreeSkillPointsChanged(long Balance) : PilotNotification("OnFreeSkillPointsChanged")
{
    public override object?[] Arguments => [Balance];
}
