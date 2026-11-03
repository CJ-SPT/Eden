using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Skills;

[Injectable]
internal sealed class SkillHandler(
    IPilotSkillReader skills,
    IPilotTraining training,
    IPilotSkillBooks books,
    TimeProvider clock,
    PilotBrain brain,
    PropulsionHandler propulsion
) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        long pilotId = context.Pilot.PilotId;
        try
        {
            return (service, method) switch
            {
                ("skillMgr2", "GetMySkillHandler") => context.Reference("@skills"),
                ("dogmaIM", "InjectSkillIntoBrain") => Inject(context, args),
                ("@skills", "GetSkills" or "GetAllSkills") when args.Length == 0 => SkillRows(skills.ReadSkills(pilotId)),
                ("expertSystemMgr", "GetMyExpertSystems") => BlueEmpty.IndexedDictionary,
                ("@skills", "GetAttributes") => new BlueMap<DogmaAttributeId, int>(PilotProfile.TrainingAttributes()),
                ("@skills", "GetFreeSkillPoints") => 0,
                ("@skills", "GetSkillHistory") => History(pilotId, args),
                ("@skills", "GetSkillQueueAndFreePoints") => new SkillQueueReply(Advance(context)),
                ("@skills", "SaveNewQueue") => Save(context, args, keywords),
                ("@skills", "AbortTraining") when args.Length == 0 => Pause(context),
                ("@skills", "GetBoosters" or "GetImplants") => BlueEmpty.IndexedDictionary,
                ("@skills", "CheckAndSendNotifications") => PilotCallResult.None,
                _ => PilotCallResult.Unsupported,
            };
        }
        catch (InvalidTrainingQueue error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private PilotCallResult Inject(PilotCallContext context, object?[] args)
    {
        if (args is not [List<object?> items] || items.Any(i => i is not long))
        {
            return PilotCallResult.Unsupported;
        }
        var result = books.Inject(context.Pilot.PilotId, items.Cast<long>().ToArray());
        foreach (var change in result.Changes)
        {
            InventoryHandler.NotifyItemChange(context, change);
        }
        var updates = SkillRows(skills.ReadSkills(context.Pilot.PilotId).Where(s => result.TypeIds.Contains(s.TypeId)));
        context.Notifications.Add(new ServerSkillsChanged(updates, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()));
        return PilotCallResult.None;
    }

    public IReadOnlyList<SkillQueueRow> Advance(PilotCallContext context)
    {
        var state = training.Advance(context.Pilot.PilotId);
        return Apply(context, state, state.ChangedSkills.Count > 0);
    }

    private PilotCallResult Pause(PilotCallContext context)
    {
        Apply(context, training.Pause(context.Pilot.PilotId), true);
        context.Notifications.Add(new SkillQueuePaused());
        return PilotCallResult.None;
    }

    private PilotCallResult Save(PilotCallContext context, object?[] args, Dictionary<string, object?>? keywords)
    {
        if (SkillQueueRequest.Read(args, keywords) is not { } request)
        {
            return PilotCallResult.Unsupported;
        }
        Apply(context, training.Save(context.Pilot.PilotId, request.Targets, request.Activate), true);
        return PilotCallResult.None;
    }

    private IReadOnlyList<SkillQueueRow> Apply(PilotCallContext context, TrainingState state, bool notify)
    {
        context.NextSkillCompletion = state.Queue.FirstOrDefault()?.EndTime;
        if (state.ChangedSkills.Count > 0)
        {
            propulsion.RefreshNavigation(context);
            var updates = SkillRows(skills.ReadSkills(context.Pilot.PilotId).Where(s => state.ChangedSkills.Contains(s.TypeId)));
            context.Notifications.Add(new ServerSkillsChanged(updates, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()));
            context.Notifications.Add(new ServerBrainUpdated(brain.Update(context.Pilot.PilotId, context.Pilot.ShipId)));
        }
        var rows = state.Queue.Select((entry, index) => new SkillQueueRow(index, entry)).ToArray();
        if (notify)
        {
            context.Notifications.Add(new SkillQueueSaved(rows));
        }
        return rows;
    }

    private BlueList<SkillHistoryRow> History(long pilotId, object?[] args)
    {
        int count = args switch
        {
            [] or [null] => int.MaxValue,
            [long n] when n is >= 0 and <= int.MaxValue => (int)n,
            _ => throw new InvalidTrainingQueue("Invalid history limit"),
        };
        return new BlueList<SkillHistoryRow>(training.History(pilotId, count).Select(entry => new SkillHistoryRow(entry)).ToArray());
    }

    private static BlueMap<int, TrainedSkillReply> SkillRows(IEnumerable<TrainedSkill> source) =>
        new(source.ToDictionary(skill => skill.TypeId, skill => new TrainedSkillReply(skill)));
}
