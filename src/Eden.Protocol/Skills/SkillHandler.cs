using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Characters;
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
    PropulsionHandler propulsion,
    FittingOperations fittingOperations,
    IPilotCharacterSheet sheet,
    IPilotJumpCloneSkills cloneSkills,
    IPilotExpertSystems? expertSystems = null
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
                ("skillMgr2", "GetMySkillHandler") => context.Reference("@skills", PilotBindingLifetime.Pilot),
                ("dogmaIM", "InjectSkillIntoBrain") => Inject(context, args),
                ("@skills", "GetSkills" or "GetAllSkills") when args.Length == 0 => Current(context, pilotId),
                ("@skills", "GetAttributes") when args.Length == 0 => new BlueMap<DogmaAttributeId, int>(
                    sheet.ReadEffectiveAttributes(pilotId).ToDogma()
                ),
                ("@skills", "GetRespecInfo") when args.Length == 0 => new NeuralRemapReply(sheet.ReadAttributes(pilotId)),
                ("@skills", "RespecCharacter") => Remap(context, CharacterAttributesWire.Read(args)),
                ("@skills", "GetSkillHistory") => History(pilotId, args),
                // The Ship Tree consumes optional (typeID, pointChange) highlights separately from current skills.
                // Eden does not retain a per-pilot last-viewed skill snapshot, so there are no highlight deltas to report.
                ("@skills", "GetSkillChangesForISIS") when args.Length == 0 && keywords is not { Count: > 0 } => BlueEmpty.List,
                ("@skills", "SaveNewQueue") => Save(context, args, keywords),
                ("@skills", "AbortTraining") when args.Length == 0 => Pause(context),
                ("@skills", "CheckAndSendNotifications") => PilotCallResult.None,
                _ => PilotCallResult.Unsupported,
            };
        }
        catch (InvalidCharacterSheet error)
        {
            return new PilotCallResult.NamedFailure(error.Message, new RemapErrorValues(error.NextTime));
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

    private PilotCallResult Remap(PilotCallContext context, CharacterAttributes attributes)
    {
        var result = sheet.Remap(context.Pilot.PilotId, attributes);
        if (result.Changed)
        {
            long pilotId = context.Pilot.PilotId;
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            context.Notifications.Add(
                new ModuleAttributesChanged(
                    sheet
                        .ReadEffectiveAttributes(pilotId)
                        .ToDogma()
                        .Select(a => new ModuleAttributeChange(pilotId, new DogmaItemKey.Item(pilotId), a.Key, now, a.Value))
                        .ToArray()
                )
            );
            context.Notifications.Add(new RespecInfoChanged());
        }
        Apply(context, result.Training, result.Changed || result.Training.ChangedSkills.Count > 0);
        return PilotCallResult.None;
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
            InventoryChangesWire.NotifyItemChange(context, change);
        }
        var updates = SkillRows(
            skills.ReadSkills(context.Pilot.PilotId).Where(s => result.TypeIds.Contains(s.TypeId)),
            context.ExpertSystems?.Skills.Where(grant => result.TypeIds.Contains(grant.TypeId))
        );
        context.ReportedSkillTypes.UnionWith(updates.Entries.Keys);
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

    internal IReadOnlyList<SkillQueueRow> Apply(PilotCallContext context, TrainingState state, bool notify, bool removed = false)
    {
        context.NextSkillCompletion = state.Queue.FirstOrDefault()?.EndTime;
        if (state.ChangedSkills.Count > 0)
        {
            propulsion.RefreshNavigation(context);
            var updates = SkillRows(
                skills.ReadSkills(context.Pilot.PilotId).Where(s => state.ChangedSkills.Contains(s.TypeId)),
                context.ExpertSystems?.Skills.Where(grant => state.ChangedSkills.Contains(grant.TypeId))
            );
            long changedAt = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            context.ReportedSkillTypes.UnionWith(updates.Entries.Keys);
            if (updates.Entries.Count > 0)
            {
                context.Notifications.Add(
                    removed ? new ServerSkillsRemoved(updates, changedAt) : new ServerSkillsChanged(updates, changedAt)
                );
            }
            RefreshSkillEffects(context, state.ChangedSkills, changedAt);
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

    private void RefreshSkillEffects(PilotCallContext context, IEnumerable<int> changedSkills, long now)
    {
        long pilotId = context.Pilot.PilotId;
        context.Notifications.Add(new ServerBrainUpdated(brain.Update(pilotId, context.Pilot.ShipId, context.ExpertSystems?.Skills)));
        if (changedSkills.Any(skill => Enum.IsDefined((InfomorphSkill)skill)))
        {
            var clones = cloneSkills.ReadCloneSkills(pilotId);
            var item = new DogmaItemKey.Item(pilotId);
            context.Notifications.Add(
                new ModuleAttributesChanged([
                    new(pilotId, item, DogmaAttributeId.MaxJumpClones, now, clones.Capacity),
                    new(pilotId, item, DogmaAttributeId.CloneJumpCooldown, now, clones.CooldownHours),
                ])
            );
        }
        fittingOperations.NotifyShipAttributes(context);
    }

    internal void ApplyExpertSystems(PilotCallContext context, ExpertSystemChange change)
    {
        var before = change.Before.Skills.ToDictionary(grant => grant.TypeId);
        var after = change.After.Skills.ToDictionary(grant => grant.TypeId);
        int[] affected = before
            .Keys.Union(after.Keys)
            .Where(type => before.GetValueOrDefault(type) != after.GetValueOrDefault(type))
            .ToArray();
        if (affected.Length == 0)
        {
            return;
        }

        var trained = skills.ReadSkills(context.Pilot.PilotId).ToDictionary(skill => skill.TypeId);
        Dictionary<int, TrainedSkillReply> changed = [];
        Dictionary<int, TrainedSkillReply> removed = [];
        foreach (int type in affected)
        {
            var actual = trained.GetValueOrDefault(type);
            var previous = Project(actual, before.GetValueOrDefault(type));
            var current = Project(actual, after.GetValueOrDefault(type));
            if (previous == current)
            {
                continue;
            }
            if (current is null && previous is not null)
            {
                removed[type] = new(new TrainedSkill(type, -1, previous.Rank));
            }
            else if (current is not null)
            {
                changed[type] = current;
            }
        }

        if (changed.Count == 0 && removed.Count == 0)
        {
            return;
        }

        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (changed.Count > 0)
        {
            context.ReportedSkillTypes.UnionWith(changed.Keys);
            context.Notifications.Add(new ServerSkillsChanged(new(changed), now));
        }
        if (removed.Count > 0)
        {
            context.ReportedSkillTypes.ExceptWith(removed.Keys);
            context.Notifications.Add(new ServerSkillsRemoved(new(removed), now));
        }

        propulsion.RefreshNavigation(context);
        RefreshSkillEffects(context, changed.Keys.Concat(removed.Keys), now);
    }

    private static TrainedSkillReply? Project(TrainedSkill? skill, ExpertVirtualSkill? grant) =>
        skill is not null ? new(skill, grant?.Level)
        : grant is not null ? new(grant)
        : null;

    internal static BlueMap<int, TrainedSkillReply> SkillRows(
        IEnumerable<TrainedSkill> source,
        IEnumerable<ExpertVirtualSkill>? grants = null
    )
    {
        var rented = grants?.ToDictionary(grant => grant.TypeId);
        var rows = source.ToDictionary(
            skill => skill.TypeId,
            skill => new TrainedSkillReply(skill, rented?.GetValueOrDefault(skill.TypeId)?.Level)
        );
        if (rented is not null)
        {
            foreach (var grant in rented.Values)
            {
                rows.TryAdd(grant.TypeId, new TrainedSkillReply(grant));
            }
        }
        return new(rows);
    }

    private BlueMap<int, TrainedSkillReply> Current(PilotCallContext context, long pilot)
    {
        if (context.ExpertSystems is null && expertSystems is not null)
        {
            context.ObserveExpertSystems(expertSystems.Read(pilot));
        }
        var rows = SkillRows(skills.ReadSkills(pilot), context.ExpertSystems?.Skills);
        context.ReportedSkillTypes.Clear();
        context.ReportedSkillTypes.UnionWith(rows.Entries.Keys);
        return rows;
    }

    internal void ReportDeleted(PilotCallContext context, IReadOnlyList<int> deleted)
    {
        // Build 3662168 skillsvc removes entries with negative trainedSkillPoints and no virtual level.
        var activeRentals = context.ExpertSystems?.Skills.Select(grant => grant.TypeId).ToHashSet();
        var missing = deleted
            .Where(type => activeRentals?.Contains(type) != true && context.ReportedSkillTypes.Remove(type))
            .Select(type => new TrainedSkill(type, -1, 1))
            .ToArray();
        if (missing.Length > 0)
        {
            context.Notifications.Add(new ServerSkillsRemoved(SkillRows(missing), clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()));
        }
    }
}
