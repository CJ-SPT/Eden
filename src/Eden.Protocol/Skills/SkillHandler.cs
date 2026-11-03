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
    public object? Call(
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
                ("expertSystemMgr", "GetMyExpertSystems") => new Dictionary<long, object?>(),
                ("@skills", "GetAttributes") => new BlueMap<DogmaAttributeId, int>(PilotProfile.TrainingAttributes()),
                ("@skills", "GetFreeSkillPoints") => 0,
                ("@skills", "GetSkillHistory") => History(pilotId, args),
                ("@skills", "GetSkillQueueAndFreePoints") => new object?[] { Advance(context), 0 },
                ("@skills", "SaveNewQueue") => Save(context, args, keywords),
                ("@skills", "AbortTraining") when args.Length == 0 => Pause(context),
                ("@skills", "GetBoosters" or "GetImplants") => new Dictionary<long, object?>(),
                ("@skills", "CheckAndSendNotifications") => null,
                _ => PilotCallRouter.Unsupported,
            };
        }
        catch (InvalidTrainingQueue error)
        {
            return new PilotCallRouter.Error(error.Message);
        }
        catch (InvalidDataException error)
        {
            return new PilotCallRouter.Error(error.Message);
        }
    }

    private object? Inject(PilotCallContext context, object?[] args)
    {
        if (args is not [List<object?> items] || items.Any(i => i is not long))
        {
            return PilotCallRouter.Unsupported;
        }
        var result = books.Inject(context.Pilot.PilotId, items.Cast<long>().ToArray());
        foreach (var change in result.Changes)
        {
            InventoryHandler.NotifyItemChange(context, change);
        }
        var updates = SkillRows(skills.ReadSkills(context.Pilot.PilotId).Where(s => result.TypeIds.Contains(s.TypeId)));
        context.Notifications.Add(("OnServerSkillsChanged", [updates, null, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()]));
        return null;
    }

    public List<object?> Advance(PilotCallContext context)
    {
        var state = training.Advance(context.Pilot.PilotId);
        return Apply(context, state, state.ChangedSkills.Count > 0);
    }

    private object? Pause(PilotCallContext context)
    {
        Apply(context, training.Pause(context.Pilot.PilotId), true);
        context.Notifications.Add(("OnSkillQueuePausedServer", []));
        return null;
    }

    private object? Save(PilotCallContext context, object?[] args, Dictionary<string, object?>? keywords)
    {
        if (args.Length != 1)
        {
            return PilotCallRouter.Unsupported;
        }
        // Python's empty dictionary does not carry a key type on the wire.
        var entries = args[0] switch
        {
            Dictionary<long, object?> map => map,
            Dictionary<string, object?> { Count: 0 } => [],
            _ => throw new InvalidTrainingQueue("QueueInvalidPosition"),
        };
        if (entries.Count > 150)
        {
            throw new InvalidTrainingQueue("QueueTooManySkills");
        }
        List<TrainingTarget> targets = [];
        for (int i = 0; i < entries.Count; i++)
        {
            if (
                entries.GetValueOrDefault(i) is not object?[] pair
                || pair is not [long typeId, long level]
                || typeId is < 1 or > int.MaxValue
                || level is < 1 or > 5
            )
            {
                throw new InvalidTrainingQueue("QueueInvalidPosition");
            }
            targets.Add(new((int)typeId, (int)level));
        }
        bool activate = keywords?.GetValueOrDefault("activate") switch
        {
            true or 1L => true,
            null or false or 0L => false,
            _ => throw new InvalidTrainingQueue("Invalid activation flag"),
        };
        Apply(context, training.Save(context.Pilot.PilotId, targets, activate), true);
        return null;
    }

    private List<object?> Apply(PilotCallContext context, TrainingState state, bool notify)
    {
        context.NextSkillCompletion = state.Queue.FirstOrDefault()?.EndTime;
        if (state.ChangedSkills.Count > 0)
        {
            propulsion.RefreshNavigation(context);
            var updates = SkillRows(skills.ReadSkills(context.Pilot.PilotId).Where(s => state.ChangedSkills.Contains(s.TypeId)));
            context.Notifications.Add(("OnServerSkillsChanged", [updates, null, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()]));
            context.Notifications.Add(("OnServerBrainUpdated", [brain.Update(context.Pilot.PilotId, context.Pilot.ShipId)]));
        }
        var rows = state
            .Queue.Select(
                (e, index) =>
                    (object?)
                        new BlueInstance(
                            "utillib.KeyVal",
                            new Dictionary<string, object?>
                            {
                                ["queuePosition"] = index,
                                ["trainingStartTime"] = e.StartTime,
                                ["trainingEndTime"] = e.EndTime,
                                ["trainingTypeID"] = e.Target.TypeId,
                                ["trainingToLevel"] = e.Target.Level,
                                ["trainingStartSP"] = e.StartPoints,
                                ["trainingDestinationSP"] = e.EndPoints,
                            }
                        )
            )
            .ToList();
        if (notify)
        {
            context.Notifications.Add(("OnNewSkillQueueSaved", [rows]));
        }
        return rows;
    }

    private object History(long pilotId, object?[] args)
    {
        int count = args switch
        {
            [] or [null] => int.MaxValue,
            [long n] when n is >= 0 and <= int.MaxValue => (int)n,
            _ => throw new InvalidTrainingQueue("Invalid history limit"),
        };
        return training
            .History(pilotId, count)
            .Select(e =>
                (object?)
                    new BlueInstance(
                        "utillib.KeyVal",
                        new Dictionary<string, object?>
                        {
                            ["logDate"] = e.Time,
                            ["eventTypeID"] = e.EventId,
                            ["skillTypeID"] = e.TypeId,
                            ["absolutePoints"] = e.Points,
                        }
                    )
            )
            .ToList();
    }

    private static Dictionary<long, object?> SkillRows(IEnumerable<TrainedSkill> source) =>
        source.ToDictionary(
            s => (long)s.TypeId,
            s =>
                (object?)
                    new BlueConstructor(
                        "characterskills.common.character_skill_entry.CharacterSkillEntry",
                        [s.TypeId, s.Level, s.Points, s.Rank, null]
                    )
        );
}
