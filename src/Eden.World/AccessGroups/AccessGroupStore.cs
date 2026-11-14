using Eden.Data.World.AccessGroups;
using Eden.Data.World.Bookmarks;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Gameplay.AccessGroups;
using Eden.Gameplay.Bookmarks;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.AccessGroups;

[Injectable]
internal sealed class AccessGroupStore(WorldDatabase database, TimeProvider clock) : IPilotAccessGroups
{
    public long Revision => database.AccessGroupRevision;

    public IReadOnlyList<AccessGroup> Read(long pilotId) => ReadContents(pilotId).Select(c => c.Group).ToArray();

    public IReadOnlyList<AccessGroupContents> ReadContents(long pilotId)
    {
        return WorldActionScope.Inspect<IReadOnlyList<AccessGroupContents>>(
            database,
            world =>
            {
                long corporation = PilotCorporation(world, pilotId);
                var members = world
                    .Set<SavedAccessGroupMember>()
                    .OrderBy(m => m.GroupId)
                    .ThenBy(m => m.OwnerId)
                    .AsEnumerable()
                    .ToLookup(m => m.GroupId, m => new AccessGroupMember(m.OwnerId, m.Role));
                List<AccessGroupContents> result = [];
                foreach (var group in world.Set<SavedAccessGroup>().OrderBy(g => g.Id))
                {
                    var rows = members[group.Id].ToArray();
                    var role = AccessGroupRules.Resolve(rows, pilotId, corporation);
                    if (AccessGroupRules.CanManage(role))
                    {
                        result.Add(
                            new(
                                new(
                                    group.Id,
                                    group.CreatorId,
                                    group.Name,
                                    group.Description,
                                    role,
                                    rows.Where(m => m.Role == AccessGroupRole.Admin).Select(m => m.OwnerId).ToArray()
                                ),
                                rows
                            )
                        );
                    }
                }
                return result;
            }
        );
    }

    public AccessGroup? Find(long pilotId, long groupId)
    {
        return WorldActionScope.Inspect<AccessGroup?>(
            database,
            world =>
            {
                if (world.Set<SavedAccessGroup>().Find(groupId) is not { } group)
                {
                    return null;
                }
                var result = Snapshot(world, pilotId, group);
                return AccessGroupRules.CanManage(result.Role) ? result : null;
            }
        );
    }

    public IReadOnlyList<AccessGroupMember> Members(long pilotId, long groupId)
    {
        return WorldActionScope.Inspect<IReadOnlyList<AccessGroupMember>>(
            database,
            world =>
            {
                Require(world, pilotId, groupId);
                return MemberRows(world, groupId);
            }
        );
    }

    public IReadOnlyList<AccessGroupLog> Logs(long pilotId, long groupId)
    {
        return WorldActionScope.Inspect<IReadOnlyList<AccessGroupLog>>(
            database,
            world =>
            {
                Require(world, pilotId, groupId);
                return world
                    .Filter<SavedAccessGroupLog>(l => l.GroupId == groupId)
                    .OrderByDescending(l => l.Id)
                    .Take(AccessGroupRules.MaximumLogEntries)
                    .AsEnumerable()
                    .Select(l => l.Snapshot())
                    .ToArray();
            }
        );
    }

    public AccessGroup Create(long pilotId, string name, string description)
    {
        BookmarkRules.CheckName(name);
        BookmarkRules.CheckDescription(description);
        return Write(world =>
        {
            PilotCorporation(world, pilotId);
            if (
                world
                    .Set<SavedAccessGroupMember>()
                    .Count(m => m.OwnerId == pilotId && (m.Role == AccessGroupRole.Admin || m.Role == AccessGroupRole.Manager))
                >= AccessGroupRules.MaximumManagedGroups
            )
            {
                throw new InvalidDataException("You have reached the access list limit.");
            }
            var group = new SavedAccessGroup
            {
                CreatorId = pilotId,
                Name = name,
                Description = description,
            };
            world.Add(group);
            world.SaveChanges();
            world.Add(
                new SavedAccessGroupMember
                {
                    GroupId = group.Id,
                    OwnerId = pilotId,
                    Role = AccessGroupRole.Admin,
                }
            );
            Log(world, pilotId, group.Id, AccessGroupChange.Created);
            world.SaveChanges();
            return Snapshot(world, pilotId, group);
        });
    }

    public void Delete(long pilotId, long groupId) =>
        Write(world =>
        {
            var group = Require(world, pilotId, groupId, true);
            // Clear grants before pruning connections. Losing the only admin list
            // can intentionally make a shared folder inaccessible, as self-demotion can.
            world.RemoveRange(world.Filter<SavedAccessGroupMember>(m => m.GroupId == groupId).ToArray());
            world.RemoveRange(world.Filter<SavedAccessGroupLog>(l => l.GroupId == groupId).ToArray());
            foreach (
                var folder in world
                    .Filter<SavedBookmarkFolder>(f => f.AdminGroupId == groupId)
                    .AsEnumerable()
                    .Concat(world.Filter<SavedBookmarkFolder>(f => f.ManageGroupId == groupId))
                    .Concat(world.Filter<SavedBookmarkFolder>(f => f.UseGroupId == groupId))
                    .Concat(world.Filter<SavedBookmarkFolder>(f => f.ViewGroupId == groupId))
                    .DistinctBy(f => f.Id)
                    .ToArray()
            )
            {
                if (folder.AdminGroupId == groupId)
                {
                    folder.AdminGroupId = null;
                }
                if (folder.ManageGroupId == groupId)
                {
                    folder.ManageGroupId = null;
                }
                if (folder.UseGroupId == groupId)
                {
                    folder.UseGroupId = null;
                }
                if (folder.ViewGroupId == groupId)
                {
                    folder.ViewGroupId = null;
                }
            }
            world.Remove(group);
            return true;
        });

    public void Update(long pilotId, long groupId, string? name, string? description)
    {
        if (name is not null)
        {
            BookmarkRules.CheckName(name);
        }
        if (description is not null)
        {
            BookmarkRules.CheckDescription(description);
        }
        Write(world =>
        {
            var group = Require(world, pilotId, groupId, true);
            if (name is not null && name != group.Name)
            {
                Log(world, pilotId, groupId, AccessGroupChange.NameChanged, oldText: group.Name, newText: name);
            }
            if (description is not null && description != group.Description)
            {
                Log(world, pilotId, groupId, AccessGroupChange.DescriptionChanged, oldText: group.Description, newText: description);
            }
            group.Name = name ?? group.Name;
            group.Description = description ?? group.Description;
            return true;
        });
    }

    public void ChangeMembers(long pilotId, long groupId, IReadOnlyList<AccessGroupMember> members, bool remove = false)
    {
        if (members.Count is < 1 or > AccessGroupRules.MaximumBatch || members.Select(m => m.OwnerId).Distinct().Count() != members.Count)
        {
            throw new InvalidDataException("Choose a unique list of at most 10000 members.");
        }
        Write(world =>
        {
            Require(world, pilotId, groupId, mutate: true);
            var actor = Role(world, pilotId, groupId);
            var current = world.Filter<SavedAccessGroupMember>(m => m.GroupId == groupId).ToDictionary(m => m.OwnerId);
            var ids = members.Select(m => m.OwnerId).ToHashSet();
            var characters = ids.Where(world.PilotExists).ToHashSet();
            var corporations = world.Filter<SavedCorporation>(c => ids.Contains((long)c.Id)).Select(c => (long)c.Id).ToHashSet();
            var managed = world
                .Filter<SavedAccessGroupMember>(m =>
                    ids.Contains(m.OwnerId) && (m.Role == AccessGroupRole.Admin || m.Role == AccessGroupRole.Manager)
                )
                .GroupBy(m => m.OwnerId)
                .Select(g => new { Owner = g.Key, Count = g.Count() })
                .ToDictionary(g => g.Owner, g => g.Count);
            int added = members.Count(m => !current.ContainsKey(m.OwnerId));
            if (!remove && current.Count + added > AccessGroupRules.MaximumMembers)
            {
                throw new InvalidDataException("An access list may contain at most 10000 entries.");
            }
            foreach (var member in members)
            {
                bool character = characters.Contains(member.OwnerId);
                bool known =
                    character
                    || member.OwnerId == 0
                    || corporations.Contains(member.OwnerId)
                    || member.OwnerId <= int.MaxValue && database.Catalog.NpcCorporations.ContainsKey((int)member.OwnerId);
                if (!known)
                {
                    throw new InvalidDataException("Choose known characters or corporations, or Everyone.");
                }
                var row = current.GetValueOrDefault(member.OwnerId);
                AccessGroupRules.CheckChange(actor, pilotId, member, row?.Role ?? AccessGroupRole.None, character, remove);
                if (
                    !remove
                    && AccessGroupRules.CanManage(member.Role)
                    && !AccessGroupRules.CanManage(row?.Role ?? AccessGroupRole.None)
                    && managed.GetValueOrDefault(member.OwnerId) >= AccessGroupRules.MaximumManagedGroups
                )
                {
                    throw new InvalidDataException("This character already manages 1000 access lists.");
                }
                if (remove && row is not null || !remove && row?.Role != member.Role)
                {
                    Log(
                        world,
                        pilotId,
                        groupId,
                        remove ? AccessGroupChange.MemberRemoved
                            : row is null ? AccessGroupChange.MemberAdded
                            : AccessGroupChange.MemberRoleChanged,
                        member.OwnerId,
                        row?.Role ?? AccessGroupRole.None,
                        remove ? AccessGroupRole.None : member.Role
                    );
                }
                if (remove)
                {
                    if (row is not null)
                    {
                        world.Remove(row);
                    }
                }
                else if (row is not null)
                {
                    row.Role = member.Role;
                }
                else
                {
                    world.Add(
                        new SavedAccessGroupMember
                        {
                            GroupId = groupId,
                            OwnerId = member.OwnerId,
                            Role = member.Role,
                        }
                    );
                }
            }
            return true;
        });
    }

    internal static AccessGroupRole Role(WorldAction world, long pilotId, long groupId) =>
        AccessGroupRules.Resolve(MemberRows(world, groupId), pilotId, PilotCorporation(world, pilotId));

    internal static long PilotCorporation(WorldAction world, long pilotId) =>
        world.Filter<Pilot>(p => p.Id == pilotId).Select(p => (long?)p.CorporationId).SingleOrDefault()
        ?? throw new InvalidDataException("Unknown character.");

    private static AccessGroupMember[] MemberRows(WorldAction world, long groupId) =>
        world
            .Filter<SavedAccessGroupMember>(m => m.GroupId == groupId)
            .OrderBy(m => m.OwnerId)
            .Select(m => new AccessGroupMember(m.OwnerId, m.Role))
            .ToArray();

    private static AccessGroup Snapshot(WorldAction world, long pilotId, SavedAccessGroup group) =>
        new(
            group.Id,
            group.CreatorId,
            group.Name,
            group.Description,
            Role(world, pilotId, group.Id),
            MemberRows(world, group.Id).Where(m => m.Role == AccessGroupRole.Admin).Select(m => m.OwnerId).ToArray()
        );

    private static SavedAccessGroup Require(WorldAction world, long pilotId, long id, bool admin = false, bool mutate = false)
    {
        var group = world.Set<SavedAccessGroup>().Find(id) ?? throw new InvalidDataException("Unknown access list.");
        if (group.IsCorporation && (admin || mutate))
        {
            throw new InvalidDataException("Built-in corporation access lists cannot be changed.");
        }
        var role = Role(world, pilotId, id);
        if (admin ? role != AccessGroupRole.Admin : !AccessGroupRules.CanManage(role))
        {
            throw new InvalidDataException("You do not have permission to manage this access list.");
        }
        return group;
    }

    private void Log(
        WorldAction world,
        long actor,
        long group,
        AccessGroupChange change,
        long? owner = null,
        AccessGroupRole? oldRole = null,
        AccessGroupRole? newRole = null,
        string? oldText = null,
        string? newText = null
    ) =>
        world.Add(
            new SavedAccessGroupLog
            {
                GroupId = group,
                ActorId = actor,
                Time = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                Change = change,
                OwnerId = owner,
                OldRole = oldRole,
                NewRole = newRole,
                OldText = oldText,
                NewText = newText,
            }
        );

    private T Write<T>(Func<WorldAction, T> operation)
    {
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
        T result = operation(world);
        world.SaveChanges();
        Eden.World.Bookmarks.PilotBookmarksStore.PruneUnavailableConnections(world);
        world.SaveChanges();
        world.AfterCommit(database.NotifyAccessGroupsChanged);
        transaction.Commit();
        return result;
    }
}
