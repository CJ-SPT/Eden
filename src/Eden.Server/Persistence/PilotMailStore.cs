using System.Text.Json;
using Eden.Gameplay.Mail;
using Eden.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class SavedMail
{
    public long Id { get; set; }

    // Historical headers may outlive their sender; mailbox ownership has its own FK.
    public long SenderId { get; set; }
    public long SentAt { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string RecipientsJson { get; set; } = "[]";

    public MailHeader Header() => new(Id, SenderId, SentAt, JsonSerializer.Deserialize<long[]>(RecipientsJson)!, Subject);
}

[PrimaryKey(nameof(PilotId), nameof(MessageId))]
public sealed class SavedMailEntry
{
    public long PilotId { get; set; }
    public long MessageId { get; set; }
    public MailStatus Status { get; set; }
    public long Labels { get; set; }
    public bool Deleted { get; set; }

    public MailEntryState Snapshot() => new(MessageId, Status, Labels);
}

[PrimaryKey(nameof(PilotId), nameof(Id))]
public sealed class SavedMailLabel
{
    public long PilotId { get; set; }
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int? Color { get; set; }

    public MailLabel Snapshot() => new(Id, Name, Color);
}

public sealed class SavedMailChange
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long MessageId { get; set; }
    public Guid Origin { get; set; }
    public MailChangeKind Kind { get; set; }
    public MailStatus Status { get; set; }
    public long Labels { get; set; }
    public long LabelId { get; set; }
}

[Injectable]
internal sealed class PilotMailStore(WorldDatabase database, TimeProvider clock) : IPilotMail
{
    private static void Pilot(WorldDbContext world, long pilot)
    {
        if (!world.Pilots.Any(p => p.Id == pilot))
        {
            throw new InvalidDataException("Unknown mail pilot.");
        }
    }

    private static IQueryable<SavedMailEntry> Visible(WorldDbContext world, long pilot) =>
        world.Set<SavedMailEntry>().Where(e => e.PilotId == pilot && !e.Deleted);

    private static SavedMailEntry Entry(WorldDbContext world, long pilot, long message) =>
        Visible(world, pilot).SingleOrDefault(e => e.MessageId == message)
        ?? throw new InvalidDataException("That message is not in your mailbox.");

    private static void Feed(WorldDbContext world, SavedMailEntry entry, MailChangeKind kind, Guid origin) =>
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

    private static void LabelFeed(WorldDbContext world, long pilot, long label, Guid origin) =>
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
        using WorldDbContext world = new(database.Path);
        Pilot(world, pilotId);
        return world.Set<SavedMailChange>().Where(c => c.PilotId == pilotId).Max(c => (long?)c.Id) ?? 0;
    }

    public MailChangeBatch Changes(long pilotId, long cursor)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var rows = world
            .Set<SavedMailChange>()
            .Where(c => c.PilotId == pilotId && c.Id > cursor)
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
                && !Visible(world, pilotId).Any(e => e.MessageId == row.MessageId)
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
        return new(cursor, rows.Length > MailRules.FeedBatch, changes);
    }

    public MailSnapshot Sync(long pilotId, long? first, long last)
    {
        if (first <= 0 || last < 0 || (first.HasValue && first > last))
        {
            throw new InvalidDataException("Invalid mail synchronization range.");
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        Pilot(world, pilotId);
        var states = Visible(world, pilotId).OrderBy(e => e.MessageId).AsEnumerable().Select(e => e.Snapshot()).ToArray();
        var ids = states.Select(s => s.Id).ToArray();
        var headers = world
            .Set<SavedMail>()
            .Where(m => ids.Contains(m.Id) && (m.Id > last || (first.HasValue && m.Id < first)))
            .OrderBy(m => m.Id)
            .AsEnumerable()
            .Select(m => m.Header())
            .ToArray();
        return new(headers.Where(h => h.Id > last).ToArray(), headers.Where(h => first.HasValue && h.Id < first).ToArray(), states);
    }

    public IReadOnlyList<MailHeader> Headers(long pilotId, IReadOnlyList<long> ids)
    {
        using WorldDbContext world = new(database.Path);
        var visible = Visible(world, pilotId).Where(e => ids.Contains(e.MessageId)).Select(e => e.MessageId);
        return world
            .Set<SavedMail>()
            .Where(m => visible.Contains(m.Id))
            .OrderBy(m => m.Id)
            .AsEnumerable()
            .Select(m => m.Header())
            .ToArray();
    }

    public string Body(long pilotId, long messageId, bool markRead, Guid origin)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
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
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        Pilot(world, pilotId);
        if (world.Pilots.Count(p => draft.Recipients.Contains(p.Id)) != draft.Recipients.Count)
        {
            throw new InvalidDataException("Mail can be sent only to local Eden pilots.");
        }
        if (world.Set<SavedBlockedContact>().Any(b => draft.Recipients.Contains(b.PilotId) && b.ContactId == pilotId))
        {
            throw new InvalidDataException("A recipient is blocking mail from you.");
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        long cutoff = checked(now - TimeSpan.TicksPerMinute);
        if (world.Set<SavedMail>().Count(m => m.SenderId == pilotId && m.SentAt > cutoff) >= MailRules.MaximumPerMinute)
        {
            throw new InvalidDataException("You may send at most five messages per minute.");
        }
        long? sourceId = draft.ReplyTo ?? draft.ForwardedFrom;
        if (sourceId.HasValue)
        {
            var source = Entry(world, pilotId, sourceId.Value);
            source.Status |= draft.ReplyTo.HasValue ? MailStatus.Replied : MailStatus.Forwarded;
        }
        var mail = world
            .Add(
                new SavedMail
                {
                    SenderId = pilotId,
                    SentAt = now,
                    Subject = draft.Subject,
                    Body = draft.Body,
                    RecipientsJson = JsonSerializer.Serialize(draft.Recipients),
                }
            )
            .Entity;
        world.SaveChanges();
        foreach (long owner in draft.Recipients.Append(pilotId).Distinct())
        {
            var entry = world
                .Add(
                    new SavedMailEntry
                    {
                        PilotId = owner,
                        MessageId = mail.Id,
                        Status = owner == pilotId ? MailStatus.Read : MailStatus.None,
                        Labels =
                            (owner == pilotId ? (long)SystemMailLabel.Sent : 0)
                            | (draft.Recipients.Contains(owner) ? (long)SystemMailLabel.Inbox : 0),
                    }
                )
                .Entity;
            Feed(world, entry, MailChangeKind.Sent, origin);
        }
        world.SaveChanges();
        transaction.Commit();
        return mail.Id;
    }

    private static SavedMailEntry[] Select(WorldDbContext world, long pilot, MailSelection selection)
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
            case MailSelection.Label label:
                CheckMask(world, pilot, label.Mask);
                return query.Where(e => (e.Labels & label.Mask) != 0 && (e.Status & MailStatus.Trashed) == 0).ToArray();
            default:
                throw new InvalidDataException("Invalid mailbox selection.");
        }
    }

    private static void CheckMask(WorldDbContext world, long pilot, long mask)
    {
        long allowed = MailRules.SystemLabels;
        foreach (long id in world.Set<SavedMailLabel>().Where(l => l.PilotId == pilot).Select(l => l.Id))
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
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
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
        using WorldDbContext world = new(database.Path);
        Pilot(world, pilotId);
        return world
            .Set<SavedMailLabel>()
            .Where(l => l.PilotId == pilotId)
            .OrderBy(l => l.Id)
            .AsEnumerable()
            .Select(l => l.Snapshot())
            .ToArray();
    }

    public long CreateLabel(long pilotId, string name, int? color, Guid origin)
    {
        MailRules.Label(name, color);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        Pilot(world, pilotId);
        var existing = world.Set<SavedMailLabel>().Where(l => l.PilotId == pilotId).ToArray();
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
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
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
                    .Set<SavedMailLabel>()
                    .Where(l => l.PilotId == pilotId && l.Id != labelId)
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
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var label = world.Set<SavedMailLabel>().Find(pilotId, labelId) ?? throw new InvalidDataException("Unknown mail label.");
        foreach (var entry in world.Set<SavedMailEntry>().Where(e => e.PilotId == pilotId && (e.Labels & labelId) != 0))
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
