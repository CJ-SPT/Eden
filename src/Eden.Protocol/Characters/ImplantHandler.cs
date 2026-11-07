using Eden.Gameplay.Characters;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.Characters;

[Injectable(InjectionType.Scoped)]
internal sealed class ImplantHandler(
    IPilotImplants implants,
    IPilotCharacterSheet sheet,
    IPilotTraining training,
    IPilotSkillReader reader,
    SkillHandler skills,
    TimeProvider clock
) : ISelectedPilotCallHandler
{
    private long? _pilot;
    private ImplantSnapshot? _observed;
    private long _nextPoll;
    public bool IsMonitoring => _pilot.HasValue;

    public void Enter(PilotCallContext context)
    {
        if (_pilot != context.Pilot.PilotId)
        {
            _pilot = context.Pilot.PilotId;
            _observed = implants.Read(_pilot.Value);
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

    internal ImplantSnapshot Refresh(PilotCallContext context)
    {
        Enter(context);
        var latest = implants.Read(context.Pilot.PilotId);
        if (
            latest.Revision != _observed!.Revision
            || latest.Attributes != _observed.Attributes
            || !latest.Implants.SequenceEqual(_observed.Implants)
        )
        {
            var state = training.Advance(context.Pilot.PilotId);
            // Clone switches or another session may already have materialized SP.
            // Refresh persisted skills as well as the rebased training timestamps.
            Notify(context, latest, new(state.Queue, reader.ReadSkills(context.Pilot.PilotId).Select(s => s.TypeId).ToArray()));
        }
        else
        {
            Observe(latest);
        }
        return latest;
    }

    private void Observe(ImplantSnapshot snapshot)
    {
        _observed = snapshot;
        _nextPoll = checked(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() + TimeSpan.TicksPerSecond);
    }

    private void Notify(PilotCallContext context, ImplantSnapshot snapshot, TrainingState state)
    {
        skills.Apply(context, state, true);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        long pilot = context.Pilot.PilotId;
        context.Notifications.Add(
            new ModuleAttributesChanged(
                sheet
                    .ReadEffectiveAttributes(pilot)
                    .ToDogma()
                    .Select(a => new ModuleAttributeChange(pilot, new DogmaItemKey.Item(pilot), a.Key, now, a.Value))
                    .ToArray()
            )
        );
        context.Notifications.Add(new ServerImplantsChanged());
        Observe(snapshot);
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool read = service == "@skills" && method == "GetImplants";
        if (!read && !(service == "dogmaIM" && method is "InjectImplant" or "DestroyImplant"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            Enter(context);
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected implant arguments.");
            }
            if (read && args.Length == 0)
            {
                return new BlueMap<long, ImplantInfo>(Refresh(context).Implants.ToDictionary(i => i.ItemId, i => new ImplantInfo(i)));
            }
            if (!read && args is [long id] && id > 0)
            {
                var result =
                    method == "InjectImplant" ? implants.Install(context.Pilot.PilotId, id) : implants.Destroy(context.Pilot.PilotId, id);
                foreach (var change in result.Changes)
                {
                    InventoryChangesWire.NotifyItemChange(context, change);
                }
                Notify(context, result.Snapshot, result.Training);
                return PilotCallResult.None;
            }
            throw new InvalidDataException("Invalid implant request.");
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (InvalidTrainingQueue error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record ImplantInfo(InstalledImplant Implant) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?> { ["itemID"] = Implant.ItemId, ["typeID"] = Implant.Profile.TypeId }
        );
}

internal sealed record ServerImplantsChanged() : PilotNotification("OnServerImplantsChanged")
{
    public override object?[] Arguments => [];
}
