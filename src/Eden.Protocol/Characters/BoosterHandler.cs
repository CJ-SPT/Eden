using Eden.Gameplay.Characters;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.Characters;

[Injectable(InjectionType.Scoped)]
internal sealed class BoosterHandler(
    IPilotBoosters boosters,
    IPilotTraining training,
    IPilotSkillReader reader,
    SkillHandler skills,
    SkillInjectorHandler injectors,
    TimeProvider clock
) : ISelectedPilotCallHandler
{
    private long? _pilot;
    private BoosterSnapshot? _observed;
    private long _nextPoll;
    public bool IsMonitoring => _pilot.HasValue;

    public void Enter(PilotCallContext context)
    {
        injectors.Enter(context);
        if (_pilot != context.Pilot.PilotId)
        {
            _pilot = context.Pilot.PilotId;
            _observed = boosters.Read(_pilot.Value);
            _nextPoll = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
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

    private BoosterSnapshot Refresh(PilotCallContext context)
    {
        Enter(context);
        var latest = boosters.Read(context.Pilot.PilotId);
        if (latest.Revision != _observed!.Revision || !latest.Boosters.SequenceEqual(_observed.Boosters))
        {
            var state = training.Advance(context.Pilot.PilotId);
            Notify(context, latest, new(state.Queue, reader.ReadSkills(context.Pilot.PilotId).Select(s => s.TypeId).ToArray()));
        }
        else
        {
            Observe(latest);
        }
        return latest;
    }

    private void Observe(BoosterSnapshot state)
    {
        _observed = state;
        _nextPoll = checked(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() + TimeSpan.TicksPerSecond);
    }

    private void Notify(PilotCallContext context, BoosterSnapshot state, TrainingState queue)
    {
        skills.Apply(context, queue, true);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        long pilot = context.Pilot.PilotId;
        context.Notifications.Add(
            new ModuleAttributesChanged(
                state
                    .Attributes.ToDogma()
                    .Select(a => new ModuleAttributeChange(pilot, new DogmaItemKey.Item(pilot), a.Key, now, a.Value))
                    .ToArray()
            )
        );
        context.Notifications.Add(new ServerBoostersChanged());
        Observe(state);
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool read = service == "@skills" && method == "GetBoosters";
        if (!read && (service != "dogmaIM" || method != "UseBooster"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            Enter(context);
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected booster arguments.");
            }
            if (read && args.Length == 0)
            {
                return new BlueMap<long, BoosterInfo>(Refresh(context).Boosters.ToDictionary(b => b.ItemId, b => new BoosterInfo(b)));
            }
            if (!read && args is [long id, long location] && id > 0 && location > 0)
            {
                var result = boosters.Consume(context.Pilot.PilotId, id, location);
                InventoryChangesWire.NotifyItemChange(context, result.Change);
                Notify(context, result.Snapshot, result.Training);
                injectors.Refresh(context);
                return PilotCallResult.None;
            }
            throw new InvalidDataException("Invalid booster request.");
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (InvalidTrainingQueue error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("The booster lifetime is outside the supported range.");
        }
    }
}

internal sealed record BoosterInfo(ActiveBooster Booster) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["boosterID"] = Booster.ItemId,
                ["itemID"] = Booster.ItemId,
                ["boosterTypeID"] = Booster.TypeId,
                ["boosterSlot"] = Booster.Slot,
                ["expiryTime"] = Booster.ExpiresAt,
            }
        );
}

internal sealed record ServerBoostersChanged() : PilotNotification("OnServerBoostersChanged")
{
    public override object?[] Arguments => [];
}
