using System.Globalization;
using Eden.Gameplay.Mail;
using Eden.Protocol;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class SavedMailingList
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public MailingListAccess DefaultAccess { get; set; }
    public MailingListRole DefaultRole { get; set; }
    public bool Deleted { get; set; }
    public string? WelcomeTitle { get; set; }
    public string? WelcomeBody { get; set; }

    public MailingListInfo Info(MailingListRole role) => new(Id, new(Key, Name), role);
}

[PrimaryKey(nameof(ListId), nameof(PilotId))]
public sealed class SavedMailingListMember
{
    public long ListId { get; set; }
    public long PilotId { get; set; }
    public MailingListRole Role { get; set; }
}

[PrimaryKey(nameof(ListId), nameof(EntityId))]
public sealed class SavedMailingListAccess
{
    public long ListId { get; set; }
    public long EntityId { get; set; }
    public MailingListAccess Access { get; set; }
}

public sealed class SavedMailingListChange
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long ListId { get; set; }
    public Guid Origin { get; set; }
    public MailingListChangeKind Kind { get; set; }
    public MailingListRole Role { get; set; }
}

[Injectable]
internal sealed class PilotMailingListStore(WorldDatabase database, TimeProvider clock) : IPilotMailingLists
{
    private static SavedMailingList Active(WorldDbContext world, long id) =>
        world.Set<SavedMailingList>().SingleOrDefault(l => l.Id == id && !l.Deleted)
        ?? throw new InvalidDataException("That mailing list does not exist.");

    private static Pilot Pilot(WorldDbContext world, long pilot) =>
        world.Pilots.Find(pilot) ?? throw new InvalidDataException("Unknown mailing-list pilot.");

    private static MailingListRole Role(WorldDbContext world, long pilot, long list) =>
        world.Set<SavedMailingListMember>().Find(list, pilot)?.Role
        ?? throw new InvalidDataException("You are not subscribed to this mailing list.");

    private static SavedMailingList Manage(WorldDbContext world, long pilot, long listId)
    {
        var list = Active(world, listId);
        MailingListRules.Manager(Role(world, pilot, listId));
        return list;
    }

    private static Dictionary<long, MailingListAccess> Access(WorldDbContext world, long list) =>
        world.Set<SavedMailingListAccess>().Where(a => a.ListId == list).ToDictionary(a => a.EntityId, a => a.Access);

    private static void Feed(
        WorldDbContext world,
        long pilot,
        long list,
        MailingListChangeKind kind,
        Guid origin,
        MailingListRole role = MailingListRole.Member
    ) =>
        world.Add(
            new SavedMailingListChange
            {
                PilotId = pilot,
                ListId = list,
                Kind = kind,
                Origin = origin,
                Role = role,
            }
        );

    public IReadOnlyList<MailingListInfo> Joined(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        return (
            from member in world.Set<SavedMailingListMember>()
            join list in world.Set<SavedMailingList>() on member.ListId equals list.Id
            where member.PilotId == pilotId && !list.Deleted
            orderby list.Id
            select new { List = list, member.Role }
        )
            .AsEnumerable()
            .Select(r => r.List.Info(r.Role))
            .ToArray();
    }

    public MailingListInfo? Info(long pilotId, long listId)
    {
        using WorldDbContext world = new(database.Path);
        // Historical mail still names a deleted list. Names and IDs are tombstoned.
        var list = world.Set<SavedMailingList>().Find(listId);
        return list?.Info(world.Set<SavedMailingListMember>().Find(listId, pilotId)?.Role ?? MailingListRole.Member);
    }

    public long Create(long pilotId, string name, MailingListDefaults defaults, Guid origin)
    {
        var normalized = MailingListRules.Name(name);
        MailingListRules.Defaults(defaults);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        _ = Pilot(world, pilotId);
        if (world.Set<SavedMailingList>().Any(l => l.Key == normalized.Key && !l.Deleted))
        {
            throw new InvalidDataException("That mailing list name is already in use.");
        }
        var list = world
            .Add(
                new SavedMailingList
                {
                    OwnerId = pilotId,
                    Key = normalized.Key,
                    Name = normalized.Display,
                    DefaultAccess = defaults.Access,
                    DefaultRole = defaults.Role,
                }
            )
            .Entity;
        world.SaveChanges();
        world.Add(
            new SavedMailingListMember
            {
                ListId = list.Id,
                PilotId = pilotId,
                Role = MailingListRole.Owner,
            }
        );
        Feed(world, pilotId, list.Id, MailingListChangeKind.Joined, origin, MailingListRole.Owner);
        world.SaveChanges();
        transaction.Commit();
        return list.Id;
    }

    public MailingListInfo Join(long pilotId, string name, Guid origin)
    {
        var normalized = MailingListRules.Name(name);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var pilot = Pilot(world, pilotId);
        var list =
            world.Set<SavedMailingList>().SingleOrDefault(l => l.Key == normalized.Key && !l.Deleted)
            ?? throw new InvalidDataException("That mailing list does not exist.");
        var existing = world.Set<SavedMailingListMember>().Find(list.Id, pilotId);
        if (existing is not null)
        {
            return list.Info(existing.Role);
        }
        if (MailingListRules.Access(pilotId, pilot.CorporationId, list.DefaultAccess, Access(world, list.Id)) != MailingListAccess.Allowed)
        {
            throw new InvalidDataException("You are blocked from joining this mailing list.");
        }
        MailingListRules.Capacity(world.Set<SavedMailingListMember>().Count(m => m.ListId == list.Id));
        world.Add(
            new SavedMailingListMember
            {
                ListId = list.Id,
                PilotId = pilotId,
                Role = list.DefaultRole,
            }
        );
        Feed(world, pilotId, list.Id, MailingListChangeKind.Joined, origin, list.DefaultRole);
        if (list.WelcomeTitle is not null)
        {
            WelcomeDelivery(world, list, [pilotId], [pilotId], origin);
        }
        world.SaveChanges();
        transaction.Commit();
        return list.Info(list.DefaultRole);
    }

    public void Leave(long pilotId, long listId, Guid origin)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var list = Active(world, listId);
        if (list.OwnerId == pilotId)
        {
            throw new InvalidDataException("The owner must delete the list instead of leaving it.");
        }
        var member = world.Set<SavedMailingListMember>().Find(listId, pilotId);
        if (member is not null)
        {
            world.Remove(member);
            Feed(world, pilotId, listId, MailingListChangeKind.Left, origin);
            world.SaveChanges();
        }
        transaction.Commit();
    }

    public void Delete(long pilotId, long listId, Guid origin)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var list = Active(world, listId);
        if (list.OwnerId != pilotId)
        {
            throw new InvalidDataException("Only the mailing-list owner can delete it.");
        }
        foreach (var member in world.Set<SavedMailingListMember>().Where(m => m.ListId == listId))
        {
            Feed(world, member.PilotId, listId, MailingListChangeKind.Deleted, origin);
            world.Remove(member);
        }
        list.Deleted = true;
        world.SaveChanges();
        transaction.Commit();
    }

    public IReadOnlyDictionary<long, MailingListRole> Members(long pilotId, long listId)
    {
        using WorldDbContext world = new(database.Path);
        _ = Manage(world, pilotId, listId);
        return world.Set<SavedMailingListMember>().Where(m => m.ListId == listId).ToDictionary(m => m.PilotId, m => m.Role);
    }

    public void ChangeMembers(long pilotId, long listId, IReadOnlyList<long> members, MailingListRole? role, Guid origin)
    {
        MailingListRules.Members(members);
        if (role.HasValue && role is not (MailingListRole.Muted or MailingListRole.Member or MailingListRole.Operator))
        {
            throw new InvalidDataException("Choose a member, muted or operator role.");
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var list = Manage(world, pilotId, listId);
        var selected = world.Set<SavedMailingListMember>().Where(m => m.ListId == listId && members.Contains(m.PilotId)).ToArray();
        if (members.Contains(list.OwnerId) || selected.Length != members.Count)
        {
            throw new InvalidDataException("Choose current members other than the owner.");
        }
        foreach (var member in selected)
        {
            if (role is MailingListRole next)
            {
                member.Role = next;
                Feed(world, member.PilotId, listId, MailingListChangeKind.Role, origin, next);
            }
            else
            {
                Block(world, listId, member.PilotId);
                world.Remove(member);
                // Kick callbacks must also reach an operator who kicks themself.
                Feed(world, member.PilotId, listId, MailingListChangeKind.Left, Guid.Empty);
            }
        }
        world.SaveChanges();
        transaction.Commit();
    }

    private static void Block(WorldDbContext world, long list, long entity)
    {
        var entry =
            world.Set<SavedMailingListAccess>().Find(list, entity)
            ?? world.Add(new SavedMailingListAccess { ListId = list, EntityId = entity }).Entity;
        entry.Access = MailingListAccess.Blocked;
    }

    public MailingListSettings Settings(long pilotId, long listId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var list = Manage(world, pilotId, listId);
        return new(new(list.DefaultAccess, list.DefaultRole), Access(world, listId));
    }

    public void SetDefaults(long pilotId, long listId, MailingListDefaults defaults)
    {
        MailingListRules.Defaults(defaults);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var list = Manage(world, pilotId, listId);
        // Closing public enrollment does not evict existing subscribers or mute them.
        list.DefaultAccess = defaults.Access;
        list.DefaultRole = defaults.Role;
        world.SaveChanges();
        transaction.Commit();
    }

    public void SetAccess(long pilotId, long listId, IReadOnlyDictionary<long, MailingListAccess> access, Guid origin)
    {
        MailingListRules.Members(access.Keys.ToArray());
        if (access.Values.Any(value => !Enum.IsDefined(value)))
        {
            throw new InvalidDataException("Invalid mailing-list access.");
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var list = Manage(world, pilotId, listId);
        foreach (var (entity, value) in access)
        {
            if (entity == list.OwnerId && value == MailingListAccess.Blocked)
            {
                throw new InvalidDataException("The list owner cannot be blocked.");
            }
            if (
                !world.Pilots.Any(p => p.Id == entity)
                && catalog.Set<SourceRecord>().Find("client/npccorporations", entity.ToString(CultureInfo.InvariantCulture)) is null
            )
            {
                throw new InvalidDataException("Choose a local character or a catalog corporation.");
            }
        }
        foreach (var (entity, value) in access)
        {
            var entry =
                world.Set<SavedMailingListAccess>().Find(listId, entity)
                ?? world.Add(new SavedMailingListAccess { ListId = listId, EntityId = entity }).Entity;
            entry.Access = value;
        }
        world.SaveChanges();
        var rules = Access(world, listId);
        var members = (
            from member in world.Set<SavedMailingListMember>()
            join pilot in world.Pilots on member.PilotId equals pilot.Id
            where member.ListId == listId && member.PilotId != list.OwnerId
            select new { Member = member, pilot.CorporationId }
        ).ToArray();
        foreach (
            var row in members.Where(m =>
                MailingListRules.Access(m.Member.PilotId, m.CorporationId, MailingListAccess.Allowed, rules) == MailingListAccess.Blocked
            )
        )
        {
            world.Remove(row.Member);
            Feed(world, row.Member.PilotId, listId, MailingListChangeKind.Left, Guid.Empty);
        }
        world.SaveChanges();
        transaction.Commit();
    }

    public void ClearAccess(long pilotId, long listId, long entityId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        _ = Manage(world, pilotId, listId);
        var entry = world.Set<SavedMailingListAccess>().Find(listId, entityId);
        if (entry is not null)
        {
            world.Remove(entry);
            world.SaveChanges();
        }
        transaction.Commit();
    }

    public MailingListWelcome? Welcome(long pilotId, long listId)
    {
        using WorldDbContext world = new(database.Path);
        var list = Manage(world, pilotId, listId);
        return list.WelcomeTitle is null ? null : new(list.WelcomeTitle, list.WelcomeBody ?? "");
    }

    public void SaveWelcome(long pilotId, long listId, MailingListWelcome? welcome, bool send, Guid origin)
    {
        if (welcome is not null)
        {
            MailRules.Draft(new([pilotId], welcome.Title, welcome.Body, null, null));
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var list = Manage(world, pilotId, listId);
        list.WelcomeTitle = welcome?.Title;
        list.WelcomeBody = welcome?.Body;
        if (send && welcome is not null)
        {
            var audience = world.Set<SavedMailingListMember>().Where(m => m.ListId == listId).Select(m => m.PilotId).ToArray();
            WelcomeDelivery(world, list, [], audience, origin);
        }
        world.SaveChanges();
        transaction.Commit();
    }

    private void WelcomeDelivery(
        WorldDbContext world,
        SavedMailingList list,
        IReadOnlyList<long> direct,
        IReadOnlyList<long> audience,
        Guid origin
    ) =>
        PilotMailStore.Deliver(
            world,
            new(
                list.Id,
                null,
                new MailDraft(direct, list.WelcomeTitle!, list.WelcomeBody ?? "", null, null) { ListId = list.Id },
                audience,
                clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                Automated: true
            ),
            origin
        );

    internal static IReadOnlyList<long> Audience(WorldDbContext world, long pilotId, long listId)
    {
        var list = Active(world, listId);
        if (Role(world, pilotId, listId) == MailingListRole.Muted)
        {
            throw new InvalidDataException("Muted members cannot post to this mailing list.");
        }
        var rules = Access(world, listId);
        var sender = Pilot(world, pilotId);
        if (
            pilotId != list.OwnerId
            && MailingListRules.Access(pilotId, sender.CorporationId, MailingListAccess.Allowed, rules) == MailingListAccess.Blocked
        )
        {
            throw new InvalidDataException("You are blocked from this mailing list.");
        }
        var blocked = world.Set<SavedBlockedContact>().Where(b => b.ContactId == pilotId).Select(b => b.PilotId).ToHashSet();
        return (
            from member in world.Set<SavedMailingListMember>()
            join pilot in world.Pilots on member.PilotId equals pilot.Id
            where member.ListId == listId
            select new { pilot.Id, pilot.CorporationId }
        )
            .AsEnumerable()
            .Where(p =>
                !blocked.Contains(p.Id)
                && (
                    p.Id == list.OwnerId
                    || MailingListRules.Access(p.Id, p.CorporationId, MailingListAccess.Allowed, rules) == MailingListAccess.Allowed
                )
            )
            .Select(p => p.Id)
            .ToArray();
    }

    public long Observe(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        return world.Set<SavedMailingListChange>().Where(c => c.PilotId == pilotId).Max(c => (long?)c.Id) ?? 0;
    }

    public MailingListChanges Changes(long pilotId, long cursor)
    {
        using var phase = Eden.Protocol.PilotSessions.SessionPerformance.Phase("read.listChanges");
        if (LiveReadModels.Find(database.Path) is { } resident && !resident.HasListChanges(pilotId, cursor))
        {
            return new(cursor, false, []);
        }
        using WorldDbContext world = new(database.Path);
        var rows = world
            .Set<SavedMailingListChange>()
            .Where(c => c.PilotId == pilotId && c.Id > cursor)
            .OrderBy(c => c.Id)
            .Take(MailRules.FeedBatch + 1)
            .ToArray();
        var items = rows.Take(MailRules.FeedBatch).Select(c => new MailingListChange(c.Id, c.ListId, c.Kind, c.Role, c.Origin)).ToArray();
        return new(items.LastOrDefault()?.Id ?? cursor, rows.Length > MailRules.FeedBatch, items);
    }
}
