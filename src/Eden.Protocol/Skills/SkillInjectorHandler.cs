using Eden.Gameplay.Docking;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Skills;

[Injectable(InjectionType.Scoped)]
internal sealed class SkillInjectorHandler(IPilotSkillInjectors injectors, FreeSkillPointHandler points, TimeProvider clock)
    : ISelectedPilotCallHandler
{
    private long? _pilot;
    private SkillInjectorStatus? _observed;
    private long _announcedDeadline;
    private long _nextPoll;
    public bool IsMonitoring => _pilot.HasValue;

    public void Enter(PilotCallContext context)
    {
        points.Enter(context);
        if (_pilot != context.Pilot.PilotId)
        {
            _pilot = context.Pilot.PilotId;
            _observed = injectors.Read(_pilot.Value);
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            _announcedDeadline = _observed.NextAlphaInjection <= now ? _observed.NextAlphaInjection : 0;
            _nextPoll = now;
        }
    }

    public void Reset()
    {
        _pilot = null;
        _observed = null;
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            Reset();
            return;
        }
        Enter(context);
        if (clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() >= _nextPoll)
        {
            Refresh(context);
        }
    }

    public SkillInjectorStatus Refresh(PilotCallContext context)
    {
        Enter(context);
        var status = injectors.Read(context.Pilot.PilotId);
        Observe(context, status);
        return status;
    }

    private void Observe(PilotCallContext context, SkillInjectorStatus status)
    {
        int before = _observed!.AvailableUses;
        int after = status.AvailableUses;
        if (after > before)
        {
            context.Notifications.Add(new NonDiminishingInjectionsAdded(after - before));
        }
        else if (after < before)
        {
            context.Notifications.Add(
                status.Revision == _observed.Revision
                    ? new NonDiminishingInjectionsRemoved()
                    : new NonDiminishingInjectionsUsed(before - after)
            );
        }
        else if (status.Revision != _observed.Revision)
        {
            // A grant and spend can cancel between polls. Reset the native cache
            // rather than adding a delta to a possibly stale counter.
            context.Notifications.Add(new NonDiminishingInjectionsRemoved());
            if (after > 0)
            {
                context.Notifications.Add(new NonDiminishingInjectionsAdded(after));
            }
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (status.NextAlphaInjection > 0 && status.NextAlphaInjection <= now && status.NextAlphaInjection != _announcedDeadline)
        {
            if (context.Pilot.Account.CloneGrade == AccountCloneGrade.Alpha)
            {
                context.Notifications.Add(new AlphaInjectionAvailable());
            }
            _announcedDeadline = status.NextAlphaInjection;
        }
        _observed = status;
        _nextPoll = checked(now + TimeSpan.TicksPerSecond);
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool owned =
            (service, method)
            is
                ("nonDiminishingInjectionMgr", "GetAvailableNonDiminishingInjections")
                or
                ("alphaInjectorMgr", "GetNextAvailableInjection")
                or
                (
                    "@skills",
                    "GetSkillPoints"
                        or "GetDiminishedSpFromInjectors"
                        or "CheckInjectionConstraints"
                        or "InjectSkillpoints"
                        or "SplitSkillInjector"
                        or "CombineSkillInjector"
                );
        if (!owned)
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            Enter(context);
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected skill injector arguments.");
            }
            long pilot = context.Pilot.PilotId;
            if (service == "nonDiminishingInjectionMgr" && args.Length == 0)
            {
                return Refresh(context).AvailableUses;
            }
            if (service == "alphaInjectorMgr" && args.Length == 0)
            {
                return Refresh(context).NextAlphaInjection;
            }
            if (service == "@skills" && method == "GetSkillPoints" && args.Length == 0)
            {
                return injectors.AllocatedPoints(pilot);
            }
            if (
                service == "@skills"
                && method == "GetDiminishedSpFromInjectors"
                && args is [long type, long previewCount, long cachedUses]
                && type is > 0 and <= int.MaxValue
                && cachedUses >= 0
            )
            {
                // The client supplies its cached allowance count; only saved,
                // unexpired server allowances authorize an undiminished award.
                return injectors.Preview(pilot, (int)type, Quantity(previewCount));
            }
            if (service == "@skills" && args is [long id, long quantity] && id > 0)
            {
                int count = Quantity(quantity);
                switch (method)
                {
                    case "CheckInjectionConstraints":
                        injectors.Check(pilot, id, count);
                        return PilotCallResult.None;
                    case "InjectSkillpoints":
                        var result = injectors.Inject(pilot, id, count);
                        foreach (var change in result.Changes)
                        {
                            InventoryChangesWire.NotifyItemChange(context, change);
                        }
                        points.Notify(context, result.Points.Remaining, result.Points.Revision, result.Points.Training);
                        Observe(context, result.Status);
                        return PilotCallResult.None;
                    case "SplitSkillInjector" or "CombineSkillInjector":
                        foreach (var change in injectors.Convert(pilot, id, count, method == "SplitSkillInjector"))
                        {
                            InventoryChangesWire.NotifyItemChange(context, change);
                        }
                        return PilotCallResult.None;
                }
            }
            throw new InvalidDataException("Invalid skill injector request.");
        }
        catch (InvalidSkillInjection error)
        {
            return new PilotCallResult.NamedFailure(error.Message, new SkillInjectionErrorValues(error.TypeId, error.Limit));
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
            return new PilotCallResult.NamedFailure("InconsistentInjectorQuantity", new SkillInjectionErrorValues(0, null));
        }
    }

    private static int Quantity(long quantity) =>
        quantity is > 0 and <= int.MaxValue ? (int)quantity : throw new InvalidSkillInjection("InconsistentInjectorQuantity");
}

internal sealed record SkillInjectionErrorValues(int TypeId, long? Limit) : BlueModel
{
    public override object ToBlue() =>
        Limit is long maximum
            ? new Dictionary<string, object?> { ["typeID"] = TypeId, ["limit"] = maximum }
            : new Dictionary<string, object?>();
}

internal sealed record NonDiminishingInjectionsAdded(int Amount) : PilotNotification("OnNonDiminishingInjectionsAdded")
{
    public override object?[] Arguments => [Amount];
}

internal sealed record NonDiminishingInjectionsUsed(int Amount) : PilotNotification("OnNonDiminishingInjectionsUsed")
{
    public override object?[] Arguments => [Amount];
}

internal sealed record NonDiminishingInjectionsRemoved() : PilotNotification("OnNonDiminishingInjectionsRemoved")
{
    public override object?[] Arguments => [];
}

internal sealed record AlphaInjectionAvailable() : PilotNotification("OnAlphaInjectionAvailable")
{
    public override object?[] Arguments => [];
}
