using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Protocol;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class SavedContact
{
    public long PilotId { get; set; }
    public long ContactId { get; set; }
    public double Standing { get; set; }
    public bool InWatchlist { get; set; }
    public long LabelMask { get; set; }
}

public sealed class SavedAgentContact
{
    public long PilotId { get; set; }
    public long AgentId { get; set; }
}

public sealed class SavedBlockedContact
{
    public long PilotId { get; set; }
    public long ContactId { get; set; }
}

public sealed class SavedContactLabel
{
    public long PilotId { get; set; }
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int? Color { get; set; }
}

[Injectable]
internal sealed class PilotContactsStore(WorldDatabase database, INpcDirectory npcs, InboxChangeSignal inbox, TimeProvider? clock = null)
    : IPilotContacts,
        ICharacterDirectory
{
    public int? Corporation(long characterId)
    {
        using WorldDbContext world = new(database.Path);
        return world.Pilots.Where(p => p.Id == characterId).Select(p => (int?)p.CorporationId).SingleOrDefault()
            ?? npcs.Character(characterId)?.CorporationId;
    }

    public IReadOnlyList<CharacterIdentity> Read(IReadOnlyList<long> ids)
    {
        using WorldDbContext world = new(database.Path);
        return world
            .Pilots.Where(p => ids.Contains(p.Id))
            .OrderBy(p => p.Id)
            .Select(p => new CharacterIdentity(p.Id, p.Name, p.GenderId))
            .AsEnumerable()
            .Concat(npcs.Characters(ids).Select(npc => npc.Identity))
            .DistinctBy(character => character.Id)
            .OrderBy(character => character.Id)
            .ToArray();
    }

    public PersonalContacts Read(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        CheckPilot(world, pilotId);
        return new(
            world
                .Set<SavedContact>()
                .Where(c => c.PilotId == pilotId)
                .OrderBy(c => c.ContactId)
                .Select(c => new PersonalContact(c.ContactId, c.Standing, c.InWatchlist, c.LabelMask))
                .AsEnumerable()
                .Concat(
                    world
                        .Set<SavedAgentContact>()
                        .Where(c => c.PilotId == pilotId)
                        .Select(c => c.AgentId)
                        .AsEnumerable()
                        .Where(id => npcs.Agent(id) is not null)
                        .Select(id => new PersonalContact(id, 0, false, 0))
                )
                .OrderBy(c => c.ContactId)
                .ToArray(),
            world.Set<SavedBlockedContact>().Where(c => c.PilotId == pilotId).OrderBy(c => c.ContactId).Select(c => c.ContactId).ToArray()
        );
    }

    public void Add(long pilotId, long contactId, double standing, bool inWatchlist, string? notification = null) =>
        SaveContact(pilotId, contactId, standing, inWatchlist, true, notification);

    public void AddAgent(long pilotId, long agentId) =>
        Write(
            pilotId,
            world =>
            {
                if (npcs.Agent(agentId) is null)
                {
                    throw new InvalidDataException("Choose a known active agent.");
                }
                if (world.Set<SavedAgentContact>().Find(pilotId, agentId) is not null)
                {
                    return;
                }
                world.Add(new SavedAgentContact { PilotId = pilotId, AgentId = agentId });
                using CatalogDbContext catalog = new(database.CatalogPath);
                PilotTaskTrackingStore.Record(
                    world,
                    catalog,
                    pilotId,
                    (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                    TaskEvents.ContactAdded
                );
            }
        );

    public void Edit(long pilotId, long contactId, double standing, bool inWatchlist, string? notification = null) =>
        SaveContact(pilotId, contactId, standing, inWatchlist, false, notification);

    private void SaveContact(long pilotId, long contactId, double standing, bool inWatchlist, bool add, string? notification)
    {
        ContactRules.CheckStanding(standing);
        Write(
            pilotId,
            world =>
            {
                CheckTargets(world, pilotId, [contactId]);
                var contact = world.Set<SavedContact>().Find(pilotId, contactId);
                if (add && contact is not null || !add && contact is null)
                {
                    throw new InvalidDataException(add ? "This pilot is already a contact." : "This pilot is not a contact.");
                }
                if (contact is null)
                {
                    contact = new SavedContact { PilotId = pilotId, ContactId = contactId };
                    world.Add(contact);
                    using CatalogDbContext catalog = new(database.CatalogPath);
                    PilotTaskTrackingStore.Record(
                        world,
                        catalog,
                        pilotId,
                        (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                        TaskEvents.ContactAdded
                    );
                }
                contact.Standing = standing;
                contact.InWatchlist = inWatchlist;
                if (notification is not null)
                {
                    PilotInboxStore.AddContact(
                        world,
                        pilotId,
                        contactId,
                        add,
                        standing,
                        notification,
                        (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc()
                    );
                }
            },
            publishInbox: notification is not null
        );
    }

    public void EditStanding(long pilotId, IReadOnlyList<long> contacts, double standing)
    {
        ContactRules.CheckStanding(standing);
        Write(
            pilotId,
            world =>
            {
                foreach (var contact in RequireContacts(world, pilotId, contacts))
                {
                    contact.Standing = standing;
                }
            }
        );
    }

    public void Delete(long pilotId, IReadOnlyList<long> contacts) =>
        Write(
            pilotId,
            world =>
            {
                ContactRules.CheckContacts(pilotId, contacts);
                var known = world.Pilots.Where(p => contacts.Contains(p.Id)).Select(p => p.Id).ToHashSet();
                known.UnionWith(
                    world.Set<SavedAgentContact>().Where(c => c.PilotId == pilotId && contacts.Contains(c.AgentId)).Select(c => c.AgentId)
                );
                if (contacts.Any(id => !known.Contains(id) && npcs.Agent(id) is null))
                {
                    throw new InvalidDataException("Choose known pilots or agents to remove.");
                }
                world.Set<SavedContact>().Where(c => c.PilotId == pilotId && contacts.Contains(c.ContactId)).ExecuteDelete();
                world.Set<SavedAgentContact>().Where(c => c.PilotId == pilotId && contacts.Contains(c.AgentId)).ExecuteDelete();
            }
        );

    public void SetBlocked(long pilotId, IReadOnlyList<long> contacts, bool blocked) =>
        Write(
            pilotId,
            world =>
            {
                CheckTargets(world, pilotId, contacts);
                foreach (long id in contacts.Distinct())
                {
                    var current = world.Set<SavedBlockedContact>().Find(pilotId, id);
                    if (blocked && current is null)
                    {
                        world.Add(new SavedBlockedContact { PilotId = pilotId, ContactId = id });
                    }
                    else if (!blocked && current is not null)
                    {
                        world.Remove(current);
                    }
                }
            }
        );

    public IReadOnlyList<ContactLabel> Labels(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        CheckPilot(world, pilotId);
        return world
            .Set<SavedContactLabel>()
            .Where(l => l.PilotId == pilotId)
            .OrderBy(l => l.Id)
            .Select(l => new ContactLabel(l.Id, l.Name, l.Color))
            .ToArray();
    }

    public long CreateLabel(long pilotId, string name, int? color)
    {
        ContactRules.CheckLabelName(name);
        ContactRules.CheckLabelColor(color);
        long id = 0;
        Write(
            pilotId,
            world =>
            {
                CheckUniqueName(world, pilotId, name, null);
                var used = world.Set<SavedContactLabel>().Where(l => l.PilotId == pilotId).Select(l => l.Id).ToHashSet();
                id = Enumerable.Range(0, ContactRules.MaximumLabels).Select(bit => 1L << bit).FirstOrDefault(bit => !used.Contains(bit));
                if (id == 0)
                {
                    throw new InvalidDataException("No more contact labels are available.");
                }
                world.Add(
                    new SavedContactLabel
                    {
                        PilotId = pilotId,
                        Id = id,
                        Name = name,
                        Color = color,
                    }
                );
            }
        );
        return id;
    }

    public void EditLabel(long pilotId, long labelId, string? name, int? color)
    {
        ContactRules.CheckLabelId(labelId);
        ContactRules.CheckLabelColor(color);
        if (name is not null)
        {
            ContactRules.CheckLabelName(name);
        }
        if (name is null && color is null)
        {
            throw new InvalidDataException("Choose a label name or color to change.");
        }
        Write(
            pilotId,
            world =>
            {
                var label =
                    world.Set<SavedContactLabel>().Find(pilotId, labelId) ?? throw new InvalidDataException("Unknown contact label.");
                if (name is not null)
                {
                    CheckUniqueName(world, pilotId, name, labelId);
                    label.Name = name;
                }
                if (color is not null)
                {
                    label.Color = color;
                }
            }
        );
    }

    public void DeleteLabel(long pilotId, long labelId)
    {
        ContactRules.CheckLabelId(labelId);
        Write(
            pilotId,
            world =>
            {
                var label =
                    world.Set<SavedContactLabel>().Find(pilotId, labelId) ?? throw new InvalidDataException("Unknown contact label.");
                foreach (var contact in world.Set<SavedContact>().Where(c => c.PilotId == pilotId && (c.LabelMask & labelId) != 0))
                {
                    contact.LabelMask &= ~labelId;
                }
                world.Remove(label);
            }
        );
    }

    public void SetLabels(long pilotId, IReadOnlyList<long> contacts, long mask, bool assign) =>
        Write(
            pilotId,
            world =>
            {
                long available = world
                    .Set<SavedContactLabel>()
                    .Where(l => l.PilotId == pilotId)
                    .Select(l => l.Id)
                    .AsEnumerable()
                    .Aggregate(0L, (all, id) => all | id);
                if (mask <= 0 || (mask & ~available) != 0)
                {
                    throw new InvalidDataException("Choose existing contact labels.");
                }
                foreach (var contact in RequireContacts(world, pilotId, contacts))
                {
                    contact.LabelMask = assign ? contact.LabelMask | mask : contact.LabelMask & ~mask;
                }
            }
        );

    private void Write(long pilotId, Action<WorldDbContext> operation, bool publishInbox = false)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        CheckPilot(world, pilotId);
        operation(world);
        world.SaveChanges();
        transaction.Commit();
        if (publishInbox)
        {
            inbox.Publish();
        }
    }

    private static SavedContact[] RequireContacts(WorldDbContext world, long pilotId, IReadOnlyList<long> contacts)
    {
        CheckTargets(world, pilotId, contacts);
        var saved = world.Set<SavedContact>().Where(c => c.PilotId == pilotId && contacts.Contains(c.ContactId)).ToArray();
        if (saved.Length != contacts.Distinct().Count())
        {
            throw new InvalidDataException("Choose existing personal contacts.");
        }
        return saved;
    }

    private static void CheckTargets(WorldDbContext world, long pilotId, IReadOnlyList<long> contacts)
    {
        ContactRules.CheckContacts(pilotId, contacts);
        if (world.Pilots.Count(p => contacts.Contains(p.Id)) != contacts.Distinct().Count())
        {
            throw new InvalidDataException("Choose known Eden pilots as contacts.");
        }
    }

    private static void CheckPilot(WorldDbContext world, long pilotId)
    {
        if (!world.Pilots.Any(p => p.Id == pilotId))
        {
            throw new InvalidDataException("Unknown character.");
        }
    }

    private static void CheckUniqueName(WorldDbContext world, long pilotId, string name, long? except)
    {
        if (world.Set<SavedContactLabel>().Any(l => l.PilotId == pilotId && l.Id != except && l.Name == name))
        {
            throw new InvalidDataException("A contact label already has this name.");
        }
    }
}
