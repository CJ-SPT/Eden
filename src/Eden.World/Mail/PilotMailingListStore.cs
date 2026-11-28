using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Alliances;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Mail;
using Eden.Gameplay.Mail;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Mail;

[Injectable]
internal sealed class PilotMailingListStore(WorldDatabase database, TimeProvider clock) : IPilotMailingLists
{
    private static SavedMailingList Active(WorldAction world, long id) =>
        world.Set<SavedMailingList>().SingleOrDefault(l => l.Id == id && !l.Deleted)
        ?? throw new InvalidDataException("That mailing list does not exist.");

    private static Pilot Pilot(WorldAction world, long pilot) =>
        world.Pilots.Find(pilot) ?? throw new InvalidDataException("Unknown mailing-list pilot.");

    private static MailingListRole Role(WorldAction world, long pilot, long list) =>
        world.Set<SavedMailingListMember>().Find(list, pilot)?.Role
        ?? throw new InvalidDataException("You are not subscribed to this mailing list.");

    private static SavedMailingList Manage(WorldAction world, long pilot, long listId)
    {
        var list = Active(world, listId);
        MailingListRules.Manager(Role(world, pilot, listId));
        if (!CanSubscribe(world, list, Pilot(world, pilot), MailingListAccess.Allowed, Access(world, listId)))
        {
            throw new InvalidDataException("You are blocked from this mailing list.");
        }
        return list;
    }

    private static Dictionary<long, MailingListAccess> Access(WorldAction world, long list) =>
        world.Filter<SavedMailingListAccess>(a => a.ListId == list).ToDictionary(a => a.EntityId, a => a.Access);

    private static long? Alliance(WorldAction world, int corporationId) =>
        world is EconomyTransaction
        && world.Set<SavedAllianceMember>().Find(corporationId) is { } member
        && world.Set<SavedAlliance>().Find(member.AllianceId) is { ClosedAt: null }
            ? member.AllianceId
            : null;

    private static bool CanSubscribe(
        WorldAction world,
        SavedMailingList list,
        Pilot pilot,
        MailingListAccess fallback,
        IReadOnlyDictionary<long, MailingListAccess> rules
    ) =>
        pilot.Id == list.OwnerId
        || MailingListRules.Access(pilot.Id, pilot.CorporationId, fallback, rules, Alliance(world, pilot.CorporationId))
            == MailingListAccess.Allowed;

    private static void Feed(
        WorldAction world,
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
        return WorldActionScope.Inspect<IReadOnlyList<MailingListInfo>>(
            database,
            world =>
            {
                return world
                    .Filter<SavedMailingListMember>(m => m.PilotId == pilotId)
                    .OrderBy(m => m.ListId)
                    .AsEnumerable()
                    .Select(m => (List: world.Set<SavedMailingList>().Find(m.ListId), m.Role))
                    .Where(m => m.List is { Deleted: false })
                    .Select(m => m.List!.Info(m.Role))
                    .ToArray();
            }
        );
    }

    public MailingListInfo? Info(long pilotId, long listId)
    {
        return WorldActionScope.Inspect<MailingListInfo?>(
            database,
            world =>
            {
                // Historical mail still names a deleted list. Names and IDs are tombstoned.
                var list = world.Set<SavedMailingList>().Find(listId);
                return list?.Info(world.Set<SavedMailingListMember>().Find(listId, pilotId)?.Role ?? MailingListRole.Member);
            }
        );
    }

    public long Create(long pilotId, string name, MailingListDefaults defaults, Guid origin)
    {
        var normalized = MailingListRules.Name(name);
        MailingListRules.Defaults(defaults);
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
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
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
        var pilot = Pilot(world, pilotId);
        var list =
            world.Set<SavedMailingList>().SingleOrDefault(l => l.Key == normalized.Key && !l.Deleted)
            ?? throw new InvalidDataException("That mailing list does not exist.");
        var existing = world.Set<SavedMailingListMember>().Find(list.Id, pilotId);
        var fallback = existing is null ? list.DefaultAccess : MailingListAccess.Allowed;
        if (!CanSubscribe(world, list, pilot, fallback, Access(world, list.Id)))
        {
            throw new InvalidDataException("You are blocked from joining this mailing list.");
        }
        if (existing is not null)
        {
            return list.Info(existing.Role);
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
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
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
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
        var list = Active(world, listId);
        if (list.OwnerId != pilotId)
        {
            throw new InvalidDataException("Only the mailing-list owner can delete it.");
        }
        Tombstone(world, list, origin);
        transaction.Commit();
    }

    private static void Tombstone(WorldAction world, SavedMailingList list, Guid origin)
    {
        foreach (var member in world.Filter<SavedMailingListMember>(m => m.ListId == list.Id).ToArray())
        {
            Feed(world, member.PilotId, list.Id, MailingListChangeKind.Deleted, origin);
            world.Remove(member);
        }
        list.Deleted = true;
    }

    internal static void RetirePilot(WorldAction world, long pilotId)
    {
        var lists = world.Filter<SavedMailingList>(list => list.OwnerId == pilotId && !list.Deleted).ToArray();
        var ownedIds = lists.Select(list => list.Id).ToHashSet();
        foreach (var list in lists)
        {
            Tombstone(world, list, Guid.Empty);
        }
        world.RemoveRange(
            world.Filter<SavedMailingListMember>(member => member.PilotId == pilotId && !ownedIds.Contains(member.ListId)).ToArray()
        );
    }

    internal static void ReconcileRetiredPilots(WorldDbContext world)
    {
        var retired = world.Set<SavedCharacterDeletion>().Where(row => row.DeletedAt != null).Select(row => row.PilotId);
        var retiredLists = world.Set<SavedMailingList>().Where(list => !list.Deleted && retired.Contains(list.OwnerId));
        using var transaction = world.Database.BeginTransaction();
        world
            .Set<SavedMailingListMember>()
            .Where(member => retired.Contains(member.PilotId) || retiredLists.Any(list => list.Id == member.ListId))
            .ExecuteDelete();
        retiredLists.ExecuteUpdate(setters => setters.SetProperty(list => list.Deleted, true));
        transaction.Commit();
    }

    public IReadOnlyDictionary<long, MailingListRole> Members(long pilotId, long listId)
    {
        return WorldActionScope.Inspect<IReadOnlyDictionary<long, MailingListRole>>(
            database,
            world =>
            {
                _ = Manage(world, pilotId, listId);
                return world.Filter<SavedMailingListMember>(m => m.ListId == listId).ToDictionary(m => m.PilotId, m => m.Role);
            }
        );
    }

    public void ChangeMembers(long pilotId, long listId, IReadOnlyList<long> members, MailingListRole? role, Guid origin)
    {
        MailingListRules.Members(members);
        if (role.HasValue && role is not (MailingListRole.Muted or MailingListRole.Member or MailingListRole.Operator))
        {
            throw new InvalidDataException("Choose a member, muted or operator role.");
        }
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
        var list = Manage(world, pilotId, listId);
        var selected = world.Filter<SavedMailingListMember>(m => m.ListId == listId && members.Contains(m.PilotId)).ToArray();
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

    private static void Block(WorldAction world, long list, long entity)
    {
        var entry =
            world.Set<SavedMailingListAccess>().Find(list, entity)
            ?? world.Add(new SavedMailingListAccess { ListId = list, EntityId = entity }).Entity;
        entry.Access = MailingListAccess.Blocked;
    }

    public MailingListSettings Settings(long pilotId, long listId)
    {
        return WorldActionScope.Inspect<MailingListSettings>(
            database,
            world =>
            {
                var list = Manage(world, pilotId, listId);
                return new MailingListSettings(new(list.DefaultAccess, list.DefaultRole), Access(world, listId));
            }
        );
    }

    public void SetDefaults(long pilotId, long listId, MailingListDefaults defaults)
    {
        MailingListRules.Defaults(defaults);
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
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
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var list = Manage(world, pilotId, listId);
        foreach (var (entity, value) in access)
        {
            if (entity == list.OwnerId && value == MailingListAccess.Blocked)
            {
                throw new InvalidDataException("The list owner cannot be blocked.");
            }
            bool organization =
                world is EconomyTransaction
                && entity <= int.MaxValue
                && (
                    world.Set<SavedCorporation>().Find((int)entity) is { ClosedAt: null }
                    || world.Set<SavedAlliance>().Find((int)entity) is { ClosedAt: null }
                );
            if (!organization && world.Pilots.Find(entity) is null && catalog.Read(CatalogData.Client.Npccorporations, entity) is null)
            {
                throw new InvalidDataException("Choose a local character, corporation or alliance.");
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
        RemoveBlockedMembers(world, list);
        world.SaveChanges();
        transaction.Commit();
    }

    private static void RemoveBlockedMembers(WorldAction world, SavedMailingList list)
    {
        var rules = Access(world, list.Id);
        var members = world.Filter<SavedMailingListMember>(m => m.ListId == list.Id).ToArray();
        foreach (var member in members)
        {
            if (!CanSubscribe(world, list, Pilot(world, member.PilotId), MailingListAccess.Allowed, rules))
            {
                world.Remove(member);
                Feed(world, member.PilotId, list.Id, MailingListChangeKind.Left, Guid.Empty);
            }
        }
    }

    public void ClearAccess(long pilotId, long listId, long entityId)
    {
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
        var list = Manage(world, pilotId, listId);
        var entry = world.Set<SavedMailingListAccess>().Find(listId, entityId);
        if (entry is not null)
        {
            world.Remove(entry);
            world.SaveChanges();
            RemoveBlockedMembers(world, list);
            world.SaveChanges();
        }
        transaction.Commit();
    }

    public MailingListWelcome? Welcome(long pilotId, long listId)
    {
        return WorldActionScope.Inspect<MailingListWelcome?>(
            database,
            world =>
            {
                var list = Manage(world, pilotId, listId);
                return list.WelcomeTitle is null ? null : new(list.WelcomeTitle, list.WelcomeBody ?? "");
            }
        );
    }

    public void SaveWelcome(long pilotId, long listId, MailingListWelcome? welcome, bool send, Guid origin)
    {
        if (welcome is not null)
        {
            MailRules.Draft(new([pilotId], welcome.Title, welcome.Body, null, null));
        }
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
        var list = Manage(world, pilotId, listId);
        list.WelcomeTitle = welcome?.Title;
        list.WelcomeBody = welcome?.Body;
        if (send && welcome is not null)
        {
            var audience = Subscribers(world, list, Access(world, listId)).ToArray();
            WelcomeDelivery(world, list, [], audience, origin);
        }
        world.SaveChanges();
        transaction.Commit();
    }

    private void WelcomeDelivery(
        WorldAction world,
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

    internal static IReadOnlyList<long> Audience(WorldAction world, long pilotId, long listId)
    {
        var list = Active(world, listId);
        if (Role(world, pilotId, listId) == MailingListRole.Muted)
        {
            throw new InvalidDataException("Muted members cannot post to this mailing list.");
        }
        var rules = Access(world, listId);
        var sender = Pilot(world, pilotId);
        if (!CanSubscribe(world, list, sender, MailingListAccess.Allowed, rules))
        {
            throw new InvalidDataException("You are blocked from this mailing list.");
        }
        var blocked = world.Filter<SavedBlockedContact>(b => b.ContactId == pilotId).Select(b => b.PilotId).ToHashSet();
        return Subscribers(world, list, rules).Where(id => !blocked.Contains(id)).ToArray();
    }

    private static IEnumerable<long> Subscribers(
        WorldAction world,
        SavedMailingList list,
        IReadOnlyDictionary<long, MailingListAccess> rules
    ) =>
        world
            .Filter<SavedMailingListMember>(m => m.ListId == list.Id)
            .AsEnumerable()
            .Select(m => Pilot(world, m.PilotId))
            .Where(p => CanSubscribe(world, list, p, MailingListAccess.Allowed, rules))
            .Select(p => p.Id);

    public long Observe(long pilotId)
    {
        return WorldActionScope.Inspect<long>(
            database,
            world =>
            {
                return world.MailingListHead(pilotId);
            }
        );
    }

    public MailingListChanges Changes(long pilotId, long cursor)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("read.listChanges");
        if (LiveWorld.Find(database.Path) is { } live && !live.Economy.HasMailingListChanges(pilotId, cursor))
        {
            return new(cursor, false, []);
        }
        return WorldActionScope.Inspect<MailingListChanges>(
            database,
            world =>
            {
                if (cursor >= world.MailingListHead(pilotId))
                {
                    return new(cursor, false, []);
                }
                var rows = world
                    .Filter<SavedMailingListChange>(c => c.PilotId == pilotId && c.Id > cursor)
                    .OrderBy(c => c.Id)
                    .Take(MailRules.FeedBatch + 1)
                    .ToArray();
                var items = rows.Take(MailRules.FeedBatch)
                    .Select(c => new MailingListChange(c.Id, c.ListId, c.Kind, c.Role, c.Origin))
                    .ToArray();
                return new MailingListChanges(items.LastOrDefault()?.Id ?? cursor, rows.Length > MailRules.FeedBatch, items);
            }
        );
    }
}
