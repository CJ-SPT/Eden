using Eden.Data.World.Characters;
using Eden.Data.World.Mail;
using Eden.Gameplay.Notifications;
using Eden.Infrastructure.Registration;
using Eden.World.Characters;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Mail;

[Injectable(InjectionType.Singleton)]
internal sealed class InboxChangeSignal
{
    private long _revision;
    public long Revision => Interlocked.Read(ref _revision);

    public void Publish() => Interlocked.Increment(ref _revision);
}

[Injectable]
internal sealed class PilotInboxStore(WorldDatabase database, InboxChangeSignal signal) : IPilotInbox
{
    public long Revision => signal.Revision;

    internal static void AddContact(WorldAction world, long sender, long receiver, bool added, double level, string message, long time)
    {
        InboxRules.CheckMessage(message);
        if (
            world.Set<SavedBlockedContact>().Find(receiver, sender) is not null
            || world.Set<SavedBlockedContact>().Find(sender, receiver) is not null
        )
        {
            throw new InvalidDataException("A blocked pilot cannot receive this contact notification.");
        }
        world.Add(
            new SavedInboxNotification
            {
                Type = added ? InboxNotificationType.ContactAdded : InboxNotificationType.ContactEdited,
                SenderId = sender,
                ReceiverId = receiver,
                Created = time,
                ContactLevel = level,
                Message = message,
            }
        );
    }

    private static void CheckPilot(WorldAction world, long id)
    {
        if (!world.PilotExists(id))
        {
            throw new InvalidDataException("Unknown notification recipient.");
        }
    }

    private static IQueryable<SavedInboxNotification> Visible(WorldAction world, long pilot, InboxGroup? group = null)
    {
        InboxRules.CheckGroup(group);
        var rows = world.Filter<SavedInboxNotification>(n => n.ReceiverId == pilot && !n.Deleted);
        return group switch
        {
            null => rows,
            InboxGroup.Unread => rows.Where(n => !n.Processed),
            InboxGroup.Contacts => rows.Where(n =>
                n.Type == InboxNotificationType.ContactAdded || n.Type == InboxNotificationType.ContactEdited
            ),
            // Other notification families have no server producers yet.
            _ => rows.Where(n => false),
        };
    }

    public long Observe(long pilotId)
    {
        return WorldActionScope.Inspect<long>(
            database,
            world =>
            {
                CheckPilot(world, pilotId);
                return world.Filter<SavedInboxNotification>(n => n.ReceiverId == pilotId).Select(n => (long?)n.Id).Max() ?? 0;
            }
        );
    }

    public InboxChanges Changes(long pilotId, long after)
    {
        if (after < 0)
        {
            throw new InvalidDataException("Invalid notification delivery cursor.");
        }
        return WorldActionScope.Inspect<InboxChanges>(
            database,
            world =>
            {
                CheckPilot(world, pilotId);
                // Include tombstones/read rows in the cursor stream so they cannot be
                // reannounced as new unread notifications after a client mutation.
                var rows = world
                    .Filter<SavedInboxNotification>(n => n.ReceiverId == pilotId && n.Id > after)
                    .OrderBy(n => n.Id)
                    .Take(InboxRules.DeliveryBatchSize + 1)
                    .AsEnumerable()
                    .Select(n => n.Snapshot())
                    .ToArray();
                var batch = rows.Take(InboxRules.DeliveryBatchSize).ToArray();
                return new InboxChanges(batch.LastOrDefault()?.Id ?? after, batch, rows.Length > batch.Length);
            }
        );
    }

    public IReadOnlyList<InboxNotification> Read(long pilotId, InboxQuery query)
    {
        if (query.AfterId < 0)
        {
            throw new InvalidDataException("Invalid notification history cutoff.");
        }
        return WorldActionScope.Inspect<IReadOnlyList<InboxNotification>>(
            database,
            world =>
            {
                CheckPilot(world, pilotId);
                var rows = Visible(world, pilotId, query.Group).Where(n => n.Id > query.AfterId);
                if (query.UnreadOnly)
                {
                    rows = rows.Where(n => !n.Processed);
                }
                return rows.OrderByDescending(n => n.Created)
                    .ThenByDescending(n => n.Id)
                    .AsEnumerable()
                    .Select(n => n.Snapshot())
                    .ToArray();
            }
        );
    }

    private void Selected(long pilot, IReadOnlyList<long> ids, bool delete)
    {
        InboxRules.CheckIds(ids);
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
        CheckPilot(world, pilot);
        var rows = world.Filter<SavedInboxNotification>(n => n.ReceiverId == pilot && ids.Contains(n.Id)).ToArray();
        if (rows.Length != ids.Distinct().Count())
        {
            throw new InvalidDataException("Select notifications belonging to your selected pilot.");
        }
        foreach (var row in rows)
        {
            if (delete)
            {
                row.Deleted = true;
            }
            else if (!row.Deleted)
            {
                row.Processed = true;
            }
        }
        world.SaveChanges();
        transaction.Commit();
    }

    public void MarkRead(long pilotId, IReadOnlyList<long> ids) => Selected(pilotId, ids, false);

    public void Delete(long pilotId, IReadOnlyList<long> ids) => Selected(pilotId, ids, true);

    private void Group(long pilot, InboxGroup? group, bool delete)
    {
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
        CheckPilot(world, pilot);
        foreach (var row in Visible(world, pilot, group))
        {
            if (delete)
            {
                row.Deleted = true;
            }
            else
            {
                row.Processed = true;
            }
        }
        transaction.Commit();
    }

    public void MarkGroupRead(long pilotId, InboxGroup? group) => Group(pilotId, group, false);

    public void DeleteGroup(long pilotId, InboxGroup? group) => Group(pilotId, group, true);
}
