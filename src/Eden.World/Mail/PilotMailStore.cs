using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Mail;
using Eden.Gameplay.Mail;
using Eden.Infrastructure.Registration;
using Eden.World.Characters;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Mail;

internal sealed record MailDeliveryPlan(
    long SenderId,
    long? SentCopyPilotId,
    MailDraft Draft,
    IReadOnlyList<long> Audience,
    long Time,
    bool Automated = false
);

[Injectable]
internal sealed class PilotMailStore(WorldDatabase database, TimeProvider clock) : IPilotMail
{
    private static void Pilot(WorldAction world, long pilot)
    {
        if (!world.PilotExists(pilot))
        {
            throw new InvalidDataException("Unknown mail pilot.");
        }
    }

    private static IQueryable<SavedMailEntry> Visible(WorldAction world, long pilot) =>
        world.Filter<SavedMailEntry>(e => e.PilotId == pilot && !e.Deleted);

    private static SavedMailEntry Entry(WorldAction world, long pilot, long message) =>
        world.Set<SavedMailEntry>().Find(pilot, message) is { Deleted: false } entry
            ? entry
            : throw new InvalidDataException("That message is not in your mailbox.");

    private static void Feed(WorldAction world, SavedMailEntry entry, MailChangeKind kind, Guid origin) =>
        world.Add(
            new SavedMailChange
            {
                PilotId = entry.PilotId,
                MessageId = entry.MessageId,
                Origin = origin,
                Kind = kind,
                Status = entry.Status,
                Labels = entry.Labels,
            }
        );

    private static void LabelFeed(WorldAction world, long pilot, long label, Guid origin) =>
        world.Add(
            new SavedMailChange
            {
                PilotId = pilot,
                LabelId = label,
                Origin = origin,
                Kind = MailChangeKind.Label,
            }
        );

    public long Observe(long pilotId)
    {
        return WorldActionScope.Inspect<long>(
            database,
            world =>
            {
                Pilot(world, pilotId);
                return world.MailHead(pilotId);
            }
        );
    }

    public MailChangeBatch Changes(long pilotId, long cursor)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("read.mailChanges");
        return WorldActionScope.Inspect<MailChangeBatch>(
            database,
            world =>
            {
                if (cursor >= world.MailHead(pilotId))
                {
                    return new(cursor, false, []);
                }
                var rows = world
                    .Filter<SavedMailChange>(c => c.PilotId == pilotId && c.Id > cursor)
                    .OrderBy(c => c.Id)
                    .Take(MailRules.FeedBatch + 1)
                    .ToArray();
                List<MailChange> changes = [];
                foreach (var row in rows.Take(MailRules.FeedBatch))
                {
                    cursor = row.Id;
                    MailHeader? header = null;
                    MailLabel? label = null;
                    if (
                        row.Kind is MailChangeKind.Sent or MailChangeKind.State or MailChangeKind.Trashed or MailChangeKind.Restored
                        && world.Set<SavedMailEntry>().Find(pilotId, row.MessageId) is not { Deleted: false }
                    )
                    {
                        // Native trash/restore callbacks index the header directly. Coalesce
                        // a permanently deleted message to its safe deletion event instead
                        // of reviving it or updating a header that was never delivered.
                        continue;
                    }
                    if (row.Kind == MailChangeKind.Sent)
                    {
                        header = world.Set<SavedMail>().Find(row.MessageId)!.Header();
                    }
                    if (row.Kind == MailChangeKind.Label)
                    {
                        label = world.Set<SavedMailLabel>().Find(pilotId, row.LabelId)?.Snapshot();
                        if (label is null)
                        {
                            continue;
                        }
                    }
                    changes.Add(new(row.Id, row.Origin, row.Kind, new(row.MessageId, row.Status, row.Labels), header, label));
                }
                return new MailChangeBatch(cursor, rows.Length > MailRules.FeedBatch, changes);
            }
        );
    }

    public MailSnapshot Sync(long pilotId, long? first, long last)
    {
        if (first <= 0 || last < 0 || (first.HasValue && first > last))
        {
            throw new InvalidDataException("Invalid mail synchronization range.");
        }
        return WorldActionScope.Inspect<MailSnapshot>(
            database,
            world =>
            {
                Pilot(world, pilotId);
                var states = Visible(world, pilotId).OrderBy(e => e.MessageId).AsEnumerable().Select(e => e.Snapshot()).ToArray();
                var headers = states
                    .Select(s => s.Id)
                    .Where(id => id > last || (first.HasValue && id < first))
                    .Select(id => world.Set<SavedMail>().Find(id)!.Header())
                    .ToArray();
                return new MailSnapshot(
                    headers.Where(h => h.Id > last).ToArray(),
                    headers.Where(h => first.HasValue && h.Id < first).ToArray(),
                    states
                );
            }
        );
    }

    public IReadOnlyList<MailHeader> Headers(long pilotId, IReadOnlyList<long> ids)
    {
        return WorldActionScope.Inspect<IReadOnlyList<MailHeader>>(
            database,
            world =>
            {
                return ids.Distinct()
                    .Order()
                    .Where(id => world.Set<SavedMailEntry>().Find(pilotId, id) is { Deleted: false })
                    .Select(id => world.Set<SavedMail>().Find(id)!.Header())
                    .ToArray();
            }
        );
    }

    public string Body(long pilotId, long messageId, bool markRead, Guid origin)
    {
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
        var entry = Entry(world, pilotId, messageId);
        if (markRead && !entry.Status.HasFlag(MailStatus.Read))
        {
            entry.Status |= MailStatus.Read;
            Feed(world, entry, MailChangeKind.State, origin);
            world.SaveChanges();
        }
        string body = world.Set<SavedMail>().Find(messageId)!.Body;
        transaction.Commit();
        return body;
    }

    public long Send(long pilotId, MailDraft draft, Guid origin)
    {
        MailRules.Draft(draft);
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
        Pilot(world, pilotId);
        if (draft.Recipients.Any(id => !world.PilotExists(id)))
        {
            throw new InvalidDataException("Mail can be sent only to local Eden pilots.");
        }
        if (draft.Recipients.Any(id => world.Set<SavedBlockedContact>().Find(id, pilotId) is not null))
        {
            throw new InvalidDataException("A recipient is blocking mail from you.");
        }
        HashSet<long> audience = draft.Recipients.ToHashSet();
        if (draft.ListId is long listId)
        {
            audience.UnionWith(PilotMailingListStore.Audience(world, pilotId, listId));
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        long cutoff = checked(now - TimeSpan.TicksPerMinute);
        if (world.Filter<SavedMail>(m => m.SenderId == pilotId && m.SentAt > cutoff).Count() >= MailRules.MaximumPerMinute)
        {
            throw new InvalidDataException("You may send at most five messages per minute.");
        }
        long? sourceId = draft.ReplyTo ?? draft.ForwardedFrom;
        if (sourceId.HasValue)
        {
            var source = Entry(world, pilotId, sourceId.Value);
            source.Status |= draft.ReplyTo.HasValue ? MailStatus.Replied : MailStatus.Forwarded;
        }
        long id = Deliver(world, new(pilotId, pilotId, draft, audience.Order().ToArray(), now), origin);
        transaction.Commit();
        return id;
    }

    internal static long Deliver(WorldAction world, MailDeliveryPlan plan, Guid origin)
    {
        var mail = world
            .Add(
                new SavedMail
                {
                    SenderId = plan.SenderId,
                    SentAt = plan.Time,
                    ListId = plan.Draft.ListId,
                    Subject = plan.Draft.Subject,
                    Body = plan.Draft.Body,
                    RecipientsJson = JsonSerializer.Serialize(plan.Draft.Recipients),
                }
            )
            .Entity;
        world.SaveChanges();
        var owners = plan.SentCopyPilotId is long sender ? plan.Audience.Append(sender) : plan.Audience;
        foreach (long owner in owners.Distinct())
        {
            var initial = MailRules.InitialState(mail.Id, owner, plan.SentCopyPilotId, plan.Draft, plan.Automated);
            var entry = world
                .Add(
                    new SavedMailEntry
                    {
                        PilotId = owner,
                        MessageId = mail.Id,
                        Status = initial.Status,
                        Labels = initial.Labels,
                    }
                )
                .Entity;
            Feed(world, entry, MailChangeKind.Sent, origin);
        }
        world.SaveChanges();
        return mail.Id;
    }

    private static SavedMailEntry[] Select(WorldAction world, long pilot, MailSelection selection)
    {
        var query = Visible(world, pilot);
        switch (selection)
        {
            case MailSelection.Ids ids:
                if (ids.Values.Any(id => id <= 0) || ids.Values.Distinct().Count() != ids.Values.Count)
                {
                    throw new InvalidDataException("Choose distinct message IDs.");
                }
                var found = query.Where(e => ids.Values.Contains(e.MessageId)).ToArray();
                if (found.Length != ids.Values.Count)
                {
                    throw new InvalidDataException("A selected message is not in your mailbox.");
                }
                return found;
            case MailSelection.All:
                return query.Where(e => (e.Status & MailStatus.Trashed) == 0).ToArray();
            case MailSelection.Trash:
                return query.Where(e => (e.Status & MailStatus.Trashed) != 0).ToArray();
            case MailSelection.List list when list.Id > 0:
                return query
                    .Where(e => (e.Status & MailStatus.Trashed) == 0)
                    .AsEnumerable()
                    .Where(e => world.Set<SavedMail>().Find(e.MessageId)?.ListId == list.Id)
                    .ToArray();
            case MailSelection.Label label:
                CheckMask(world, pilot, label.Mask);
                return query.Where(e => (e.Labels & label.Mask) != 0 && (e.Status & MailStatus.Trashed) == 0).ToArray();
            default:
                throw new InvalidDataException("Invalid mailbox selection.");
        }
    }

    private static void CheckMask(WorldAction world, long pilot, long mask)
    {
        long allowed = MailRules.SystemLabels;
        foreach (long id in world.Filter<SavedMailLabel>(l => l.PilotId == pilot).Select(l => l.Id))
        {
            allowed |= id;
        }
        if (mask < 0 || (mask & ~allowed) != 0)
        {
            throw new InvalidDataException("Select labels belonging to your mailbox.");
        }
    }

    public void Change(long pilotId, MailSelection selection, MailOperation operation, long labels, Guid origin)
    {
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
        Pilot(world, pilotId);
        if (operation is MailOperation.AssignLabels or MailOperation.RemoveLabels or MailOperation.ReplaceLabels)
        {
            CheckMask(world, pilotId, labels);
            if (
                operation is MailOperation.AssignLabels or MailOperation.ReplaceLabels
                && (labels & (long)(SystemMailLabel.Corporation | SystemMailLabel.Alliance)) != 0
            )
            {
                throw new InvalidDataException("Organization mail labels cannot be assigned to personal messages.");
            }
        }
        foreach (var entry in Select(world, pilotId, selection))
        {
            var before = entry.Snapshot();
            var after = MailRules.Change(before, operation, labels);
            bool deleting = operation == MailOperation.Delete;
            if (after == before && !deleting)
            {
                continue;
            }
            entry.Status = after.Status;
            entry.Labels = after.Labels;
            entry.Deleted = deleting;
            Feed(
                world,
                entry,
                operation switch
                {
                    MailOperation.Trash => MailChangeKind.Trashed,
                    MailOperation.Restore => MailChangeKind.Restored,
                    MailOperation.Delete => MailChangeKind.Deleted,
                    _ => MailChangeKind.State,
                },
                origin
            );
        }
        world.SaveChanges();
        transaction.Commit();
    }

    public IReadOnlyList<MailLabel> Labels(long pilotId)
    {
        return WorldActionScope.Inspect<IReadOnlyList<MailLabel>>(
            database,
            world =>
            {
                Pilot(world, pilotId);
                return world
                    .Filter<SavedMailLabel>(l => l.PilotId == pilotId)
                    .OrderBy(l => l.Id)
                    .AsEnumerable()
                    .Select(l => l.Snapshot())
                    .ToArray();
            }
        );
    }

    public long CreateLabel(long pilotId, string name, int? color, Guid origin)
    {
        MailRules.Label(name, color);
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
        Pilot(world, pilotId);
        var existing = world.Filter<SavedMailLabel>(l => l.PilotId == pilotId).ToArray();
        if (existing.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("A mail label with that name already exists.");
        }
        long id = MailRules.FirstCustomLabel;
        while (existing.Any(l => l.Id == id))
        {
            id <<= 1;
        }
        if (id > MailRules.MaximumMask)
        {
            throw new InvalidDataException("You may create at most 25 mail labels.");
        }
        world.Add(
            new SavedMailLabel
            {
                PilotId = pilotId,
                Id = id,
                Name = name,
                Color = color,
            }
        );
        LabelFeed(world, pilotId, id, origin);
        world.SaveChanges();
        transaction.Commit();
        return id;
    }

    public void EditLabel(long pilotId, long labelId, string? name, int? color, Guid origin)
    {
        MailRules.LabelId(labelId);
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
        Pilot(world, pilotId);
        var label = world.Set<SavedMailLabel>().Find(pilotId, labelId);
        if (labelId < MailRules.FirstCustomLabel)
        {
            if (name is not null)
            {
                throw new InvalidDataException("System mail labels cannot be renamed.");
            }
            label ??= world.Add(new SavedMailLabel { PilotId = pilotId, Id = labelId }).Entity;
        }
        if (label is null)
        {
            throw new InvalidDataException("Unknown mail label.");
        }
        if (name is not null)
        {
            MailRules.Label(name, color);
            if (
                world
                    .Filter<SavedMailLabel>(l => l.PilotId == pilotId && l.Id != labelId)
                    .AsEnumerable()
                    .Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            )
            {
                throw new InvalidDataException("A mail label with that name already exists.");
            }
            label.Name = name;
        }
        if (color < 0)
        {
            throw new InvalidDataException("Invalid label color.");
        }
        if (color.HasValue)
        {
            label.Color = color;
        }
        LabelFeed(world, pilotId, labelId, origin);
        world.SaveChanges();
        transaction.Commit();
    }

    public void DeleteLabel(long pilotId, long labelId, Guid origin)
    {
        MailRules.LabelId(labelId);
        if (labelId < MailRules.FirstCustomLabel)
        {
            throw new InvalidDataException("System mail labels cannot be deleted.");
        }
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
        var label = world.Set<SavedMailLabel>().Find(pilotId, labelId) ?? throw new InvalidDataException("Unknown mail label.");
        foreach (var entry in world.Filter<SavedMailEntry>(e => e.PilotId == pilotId && (e.Labels & labelId) != 0))
        {
            entry.Labels &= ~labelId;
            if (!entry.Deleted)
            {
                Feed(world, entry, MailChangeKind.State, origin);
            }
        }
        world.Remove(label);
        world.SaveChanges();
        transaction.Commit();
    }
}
