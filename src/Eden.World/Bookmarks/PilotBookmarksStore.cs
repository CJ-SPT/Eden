using Eden.Data.Catalog;
using Eden.Data.World.AccessGroups;
using Eden.Data.World.Bookmarks;
using Eden.Gameplay.AccessGroups;
using Eden.Gameplay.Bookmarks;
using Eden.Infrastructure.Registration;
using Eden.World.AccessGroups;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Bookmarks;

[Injectable]
internal sealed class PilotBookmarksStore(WorldDatabase database, TimeProvider clock) : IPilotBookmarks
{
    public long Revision => database.BookmarkRevision;

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public BookmarkContents Read(long pilotId)
    {
        return WorldActionScope.Inspect<BookmarkContents>(
            database,
            world =>
            {
                CheckPilot(world, pilotId);
                var folders = world
                    .Filter<SavedBookmarkFolder>(f => f.IsPersonal && f.PilotId == pilotId && f.IsKnown)
                    .AsEnumerable()
                    .Concat(
                        world
                            .Filter<SavedBookmarkConnection>(c => c.PilotId == pilotId)
                            .AsEnumerable()
                            .Select(c => world.Set<SavedBookmarkFolder>().Find(c.FolderId))
                            .OfType<SavedBookmarkFolder>()
                            .Where(f => !f.IsPersonal)
                    )
                    .OrderBy(f => f.Id)
                    .AsEnumerable()
                    .Select(f => FolderSnapshot(world, pilotId, f))
                    .Where(f => f.Access != BookmarkAccess.None)
                    .ToArray();
                var active = folders.Where(f => f.IsActive).Select(f => f.Id).ToHashSet();
                long now = Now;
                var rows = world
                    .Filter<SavedBookmark>(b => active.Contains(b.FolderId) && (b.ExpiresAt == null || b.ExpiresAt > now))
                    .OrderBy(b => b.Id)
                    .ToArray();
                var deadlines = rows.Select(b => b.ExpiresAt).Where(t => t > now).ToArray();
                return new(
                    folders,
                    rows.Select(b => Snapshot(b)).ToArray(),
                    world
                        .Filter<SavedBookmarkSubfolder>(s => active.Contains(s.FolderId))
                        .OrderBy(s => s.Id)
                        .AsEnumerable()
                        .Select(s => s.Snapshot())
                        .ToArray(),
                    deadlines.Length > 0 ? deadlines.Min() : null
                );
            }
        );
    }

    public BookmarkFolder Folder(long pilotId, long folderId)
    {
        return WorldActionScope.Inspect<BookmarkFolder>(
            database,
            world =>
            {
                return FolderSnapshot(world, pilotId, RequireFolder(world, pilotId, folderId));
            }
        );
    }

    public BookmarkFolder AddFolder(long pilotId, string name, string description)
    {
        CheckSharedName(name);
        BookmarkRules.CheckDescription(description);
        return Write(
            pilotId,
            world =>
            {
                if (world.Set<SavedBookmarkFolder>().Count(f => f.PilotId == pilotId && f.IsPersonal) >= BookmarkRules.MaximumFolders)
                {
                    throw new InvalidDataException("You have reached the personal location folder limit.");
                }
                CheckKnownLimit(world, pilotId);
                CheckActiveLimit(world, pilotId, null);
                var folder = new SavedBookmarkFolder
                {
                    PilotId = pilotId,
                    Name = name,
                    Description = description,
                };
                world.Add(folder);
                world.SaveChanges();
                return folder.Snapshot();
            }
        );
    }

    public BookmarkFolder AddSharedFolder(long pilotId, string name, string description, BookmarkFolderAccess access)
    {
        CheckSharedName(name);
        BookmarkRules.CheckDescription(description);
        return Write(
            pilotId,
            world =>
            {
                CheckGroups(world, pilotId, access);
                CheckKnownLimit(world, pilotId);
                CheckActiveLimit(world, pilotId, null, false);
                var folder = new SavedBookmarkFolder
                {
                    PilotId = pilotId,
                    Name = name,
                    Description = description,
                    IsPersonal = false,
                    AdminGroupId = access.AdminGroupId,
                    ManageGroupId = access.ManageGroupId,
                    UseGroupId = access.UseGroupId,
                    ViewGroupId = access.ViewGroupId,
                };
                if (Access(world, pilotId, folder) != BookmarkAccess.Admin)
                {
                    throw new InvalidDataException("Choose an administrator list that grants you folder administration.");
                }
                world.Add(folder);
                world.SaveChanges();
                world.Add(
                    new SavedBookmarkConnection
                    {
                        PilotId = pilotId,
                        FolderId = folder.Id,
                        IsActive = true,
                    }
                );
                world.SaveChanges();
                return FolderSnapshot(world, pilotId, folder);
            }
        );
    }

    public BookmarkAccess UpdateSharedFolder(long pilotId, long folderId, string name, string description, BookmarkFolderAccess access)
    {
        CheckSharedName(name);
        BookmarkRules.CheckDescription(description);
        return Write(
            pilotId,
            world =>
            {
                var folder = RequireFolder(world, pilotId, folderId, BookmarkAccess.Admin);
                if (folder.IsPersonal)
                {
                    throw new InvalidDataException("Personal folders cannot use access lists.");
                }
                CheckGroups(world, pilotId, access);
                folder.Name = name;
                folder.Description = description;
                folder.AdminGroupId = access.AdminGroupId;
                folder.ManageGroupId = access.ManageGroupId;
                folder.UseGroupId = access.UseGroupId;
                folder.ViewGroupId = access.ViewGroupId;
                world.SaveChanges();
                PruneUnavailableConnections(world, folderId: folderId);
                return Access(world, pilotId, folder);
            }
        );
    }

    private static void CheckSharedName(string name)
    {
        BookmarkRules.CheckName(name);
        if (name.Length > BookmarkRules.MaximumFolderNameLength)
        {
            throw new InvalidDataException("Folder names must contain at most 40 characters.");
        }
    }

    private static void CheckGroups(WorldAction world, long pilotId, BookmarkFolderAccess access)
    {
        if (access.AdminGroupId is null)
        {
            throw new InvalidDataException("Shared folders need an administrator access list.");
        }
        foreach (long id in new[] { access.AdminGroupId, access.ManageGroupId, access.UseGroupId, access.ViewGroupId }.OfType<long>())
        {
            if (!AccessGroupRules.CanManage(AccessGroupStore.Role(world, pilotId, id)))
            {
                throw new InvalidDataException("Choose access lists you administer or manage.");
            }
        }
    }

    public void UpdateFolder(long pilotId, long folderId, string name, string description)
    {
        CheckSharedName(name);
        BookmarkRules.CheckDescription(description);
        Write(
            pilotId,
            world =>
            {
                var folder = RequireFolder(world, pilotId, folderId, BookmarkAccess.Admin);
                folder.Name = name;
                folder.Description = description;
                return true;
            }
        );
    }

    public void DeleteFolder(long pilotId, long folderId) =>
        Write(
            pilotId,
            world =>
            {
                var folder = RequireFolder(world, pilotId, folderId, BookmarkAccess.Admin);
                world.RemoveRange(world.Filter<SavedBookmark>(b => b.FolderId == folderId).ToArray());
                world.SaveChanges();
                world.RemoveRange(world.Filter<SavedBookmarkSubfolder>(s => s.FolderId == folderId).ToArray());
                world.SaveChanges();
                world.RemoveRange(world.Filter<SavedBookmarkConnection>(c => c.FolderId == folderId).ToArray());
                world.SaveChanges();
                world.Remove(folder);
                return true;
            }
        );

    public BookmarkFolderContents SetFolderState(long pilotId, long folderId, bool active, bool known = true) =>
        Write(
            pilotId,
            world =>
            {
                var folder = RequireFolder(world, pilotId, folderId);
                var before = FolderSnapshot(world, pilotId, folder);
                if (active && known && !(before.IsActive && before.IsKnown))
                {
                    CheckActiveLimit(world, pilotId, folderId, folder.IsPersonal);
                }
                if (known && !before.IsKnown)
                {
                    CheckKnownLimit(world, pilotId);
                }
                if (folder.IsPersonal)
                {
                    folder.IsActive = active && known;
                    folder.IsKnown = known;
                }
                else
                {
                    var connection = world.Set<SavedBookmarkConnection>().Find(pilotId, folderId);
                    if (!known && connection is not null)
                    {
                        world.Remove(connection);
                    }
                    else if (known)
                    {
                        if (connection is null)
                        {
                            world.Add(
                                new SavedBookmarkConnection
                                {
                                    PilotId = pilotId,
                                    FolderId = folderId,
                                    IsActive = active,
                                }
                            );
                        }
                        else
                        {
                            connection.IsActive = active;
                        }
                    }
                    world.SaveChanges();
                }
                long now = Now;
                return new BookmarkFolderContents(
                    FolderSnapshot(world, pilotId, folder),
                    active && known
                        ? world
                            .Filter<SavedBookmark>(b => b.FolderId == folderId && (b.ExpiresAt == null || b.ExpiresAt > now))
                            .OrderBy(b => b.Id)
                            .AsEnumerable()
                            .Select(b => Snapshot(b))
                            .ToArray()
                        : [],
                    active && known
                        ? world
                            .Filter<SavedBookmarkSubfolder>(s => s.FolderId == folderId)
                            .OrderBy(s => s.Id)
                            .AsEnumerable()
                            .Select(s => s.Snapshot())
                            .ToArray()
                        : []
                );
            }
        );

    public IReadOnlyList<BookmarkFolder> SearchFolders(long pilotId)
    {
        return WorldActionScope.Inspect<IReadOnlyList<BookmarkFolder>>(
            database,
            world =>
            {
                CheckPilot(world, pilotId);
                return world
                    .Set<SavedBookmarkFolder>()
                    .OrderBy(f => f.Id)
                    .AsEnumerable()
                    .Select(f => FolderSnapshot(world, pilotId, f))
                    .Where(f => f.Access is BookmarkAccess.Personal or BookmarkAccess.Admin)
                    .ToArray();
            }
        );
    }

    public BookmarkSubfolder AddSubfolder(long pilotId, long folderId, string name)
    {
        CheckSharedName(name);
        return Write(
            pilotId,
            world =>
            {
                RequireFolder(world, pilotId, folderId, BookmarkAccess.Use);
                if (world.Set<SavedBookmarkSubfolder>().Count(s => s.FolderId == folderId) >= BookmarkRules.MaximumSubfolders)
                {
                    throw new InvalidDataException("A folder may contain at most 30 subfolders.");
                }
                var subfolder = new SavedBookmarkSubfolder
                {
                    FolderId = folderId,
                    Name = name,
                    CreatorId = pilotId,
                };
                world.Add(subfolder);
                world.SaveChanges();
                return subfolder.Snapshot();
            }
        );
    }

    public void UpdateSubfolder(long pilotId, long folderId, long subfolderId, string name)
    {
        CheckSharedName(name);
        Write(
            pilotId,
            world =>
            {
                var subfolder = RequireSubfolder(world, folderId, subfolderId);
                RequireOwnOrManage(world, pilotId, folderId, subfolder.CreatorId);
                subfolder.Name = name;
                return true;
            }
        );
    }

    public IReadOnlyList<long> DeleteSubfolder(long pilotId, long folderId, long subfolderId) =>
        Write(
            pilotId,
            world =>
            {
                var subfolder = RequireSubfolder(world, folderId, subfolderId);
                RequireOwnOrManage(world, pilotId, folderId, subfolder.CreatorId);
                foreach (var creator in world.Filter<SavedBookmark>(b => b.SubfolderId == subfolderId).Select(b => b.CreatorId).Distinct())
                {
                    RequireOwnOrManage(world, pilotId, folderId, creator);
                }
                var ids = world.Filter<SavedBookmark>(b => b.SubfolderId == subfolderId).Select(b => b.Id).ToArray();
                world.RemoveRange(world.Filter<SavedBookmark>(b => b.SubfolderId == subfolderId).ToArray());
                world.SaveChanges();
                world.Remove(subfolder);
                return (IReadOnlyList<long>)ids;
            }
        );

    public PersonalBookmark CreateStatic(
        long pilotId,
        long itemId,
        long folderId,
        long? subfolderId,
        BookmarkText text,
        BookmarkExpiry expiry
    )
    {
        if (itemId is <= 0 or > int.MaxValue)
        {
            throw new InvalidDataException("Choose a known static location.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        BookmarkTarget target;
        if (catalog.FindStation(itemId) is { } station)
        {
            target = new(itemId, station.TypeId, station.SolarSystemId, new(station.X, station.Y, station.Z));
        }
        else if (catalog.FindCelestial(itemId) is { } body)
        {
            target = new(itemId, body.TypeId, body.SolarSystemId, new(body.X, body.Y, body.Z));
        }
        else if (catalog.FindSystem((int)itemId) is not null)
        {
            target = new(itemId, BookmarkRules.SolarSystemTypeId, checked((int)itemId), new(0, 0, 0));
        }
        else if (catalog.Definitions.Constellations.ContainsKey((int)itemId))
        {
            target = new(itemId, BookmarkRules.ConstellationTypeId, (int)itemId, new(0, 0, 0));
        }
        else if (catalog.Definitions.Regions.ContainsKey((int)itemId))
        {
            target = new(itemId, BookmarkRules.RegionTypeId, (int)itemId, new(0, 0, 0));
        }
        else
        {
            throw new InvalidDataException("Choose a known station, celestial or solar system.");
        }
        return Create(pilotId, target, folderId, subfolderId, text, expiry);
    }

    public PersonalBookmark Create(
        long pilotId,
        BookmarkTarget target,
        long folderId,
        long? subfolderId,
        BookmarkText text,
        BookmarkExpiry expiry
    )
    {
        BookmarkRules.CheckTarget(target);
        target = BookmarkRules.DurableTarget(target);
        BookmarkRules.CheckName(text.Name);
        BookmarkRules.CheckDescription(text.Note);
        long now = Now;
        long? end = BookmarkRules.ExpiryTime(expiry, now);
        return Write(
            pilotId,
            world =>
            {
                RequireDestination(world, pilotId, folderId, subfolderId);
                CheckCapacity(world, folderId, 1, now);
                var bookmark = new SavedBookmark
                {
                    FolderId = folderId,
                    SubfolderId = subfolderId,
                    CreatorId = pilotId,
                    ItemId = target.ItemId,
                    TypeId = target.TypeId,
                    LocationId = target.LocationId,
                    X = target.Position.X,
                    Y = target.Position.Y,
                    Z = target.Position.Z,
                    Name = text.Name,
                    Note = text.Note,
                    CreatedAt = now,
                    ExpiresAt = end,
                };
                world.Add(bookmark);
                world.SaveChanges();
                return Snapshot(bookmark);
            }
        );
    }

    public PersonalBookmark Find(long pilotId, long bookmarkId)
    {
        return WorldActionScope.Inspect<PersonalBookmark>(
            database,
            world =>
            {
                var bookmark = world.Set<SavedBookmark>().Find(bookmarkId) ?? throw new InvalidDataException("Unknown location.");
                var folder = FolderSnapshot(world, pilotId, RequireFolder(world, pilotId, bookmark.FolderId));
                if (!folder.IsActive || !folder.IsKnown || bookmark.ExpiresAt <= Now || !Visible(world, pilotId, bookmark, Now))
                {
                    throw new InvalidDataException("This location is inactive, expired or not yet available.");
                }
                return Snapshot(bookmark);
            }
        );
    }

    public void Update(long pilotId, BookmarkEdit edit)
    {
        BookmarkRules.CheckName(edit.Text.Name);
        BookmarkRules.CheckDescription(edit.Text.Note);
        Write(
            pilotId,
            world =>
            {
                var bookmark = RequireBookmarks(world, pilotId, edit.FolderId, [edit.Id]).Single();
                RequireOwnOrManage(world, pilotId, edit.FolderId, bookmark.CreatorId);
                RequireDestination(world, pilotId, edit.NewFolderId, edit.SubfolderId);
                if (edit.NewFolderId != edit.FolderId)
                {
                    CheckCapacity(world, edit.NewFolderId, 1, Now);
                }
                bookmark.FolderId = edit.NewFolderId;
                bookmark.SubfolderId = edit.SubfolderId;
                bookmark.Name = edit.Text.Name;
                bookmark.Note = edit.Text.Note;
                if (edit.CancelExpiry)
                {
                    bookmark.ExpiresAt = null;
                }
                return true;
            }
        );
    }

    public IReadOnlyList<long> Delete(long pilotId, long folderId, IReadOnlyList<long> bookmarkIds) =>
        Write(
            pilotId,
            world =>
            {
                var bookmarks = RequireBookmarks(world, pilotId, folderId, bookmarkIds);
                foreach (var bookmark in bookmarks)
                {
                    RequireOwnOrManage(world, pilotId, folderId, bookmark.CreatorId);
                }
                world.RemoveRange(bookmarks);
                return (IReadOnlyList<long>)bookmarks.Select(b => b.Id).ToArray();
            }
        );

    public IReadOnlyList<PersonalBookmark> MoveOrCopy(
        long pilotId,
        long sourceFolderId,
        long folderId,
        long? subfolderId,
        IReadOnlyList<long> bookmarkIds,
        bool copy
    ) =>
        Write(
            pilotId,
            world =>
            {
                var bookmarks = RequireBookmarks(world, pilotId, sourceFolderId, bookmarkIds);
                foreach (var bookmark in bookmarks)
                {
                    if (copy)
                    {
                        RequireFolder(world, pilotId, sourceFolderId, BookmarkAccess.Use);
                    }
                    else
                    {
                        RequireOwnOrManage(world, pilotId, sourceFolderId, bookmark.CreatorId);
                    }
                }
                RequireDestination(world, pilotId, folderId, subfolderId);
                if (copy || sourceFolderId != folderId)
                {
                    CheckCapacity(world, folderId, bookmarks.Length, Now);
                }
                var changed = bookmarks
                    .Select(bookmark =>
                    {
                        if (copy)
                        {
                            bookmark = new SavedBookmark
                            {
                                CreatorId = pilotId,
                                ItemId = bookmark.ItemId,
                                TypeId = bookmark.TypeId,
                                LocationId = bookmark.LocationId,
                                X = bookmark.X,
                                Y = bookmark.Y,
                                Z = bookmark.Z,
                                Name = bookmark.Name,
                                Note = bookmark.Note,
                                CreatedAt = Now,
                                ExpiresAt = bookmark.ExpiresAt,
                            };
                            world.Add(bookmark);
                        }
                        bookmark.FolderId = folderId;
                        bookmark.SubfolderId = subfolderId;
                        return bookmark;
                    })
                    .ToArray();
                world.SaveChanges();
                return (IReadOnlyList<PersonalBookmark>)changed.Select(b => Snapshot(b)).ToArray();
            }
        );

    private T Write<T>(long pilotId, Func<WorldAction, T> operation)
    {
        using var transaction = new WorldActionScope(database, TransactionParticipants.None);
        var world = transaction.World;
        CheckPilot(world, pilotId);
        T result = operation(world);
        world.SaveChanges();
        world.AfterCommit(database.NotifyBookmarksChanged);
        transaction.Commit();
        return result;
    }

    private static PersonalBookmark Snapshot(SavedBookmark row)
    {
        var bookmark = row.Snapshot();
        return bookmark with { Target = BookmarkRules.DurableTarget(bookmark.Target) };
    }

    private static void CheckPilot(WorldAction world, long pilotId)
    {
        if (!world.PilotExists(pilotId))
        {
            throw new InvalidDataException("Unknown character.");
        }
    }

    private static SavedBookmarkFolder RequireFolder(
        WorldAction world,
        long pilotId,
        long folderId,
        BookmarkAccess required = BookmarkAccess.View
    )
    {
        CheckPilot(world, pilotId);
        var folder = world.Set<SavedBookmarkFolder>().Find(folderId) ?? throw new InvalidDataException("Unknown location folder.");
        var access = Access(world, pilotId, folder);
        if (access != BookmarkAccess.Personal && access < required)
        {
            throw new InvalidDataException("You do not have permission to use this location folder.");
        }
        return folder;
    }

    private static void RequireOwnOrManage(WorldAction world, long pilotId, long folderId, long creatorId) =>
        RequireFolder(world, pilotId, folderId, creatorId == pilotId ? BookmarkAccess.Use : BookmarkAccess.Manage);

    private static BookmarkAccess Access(WorldAction world, long pilotId, SavedBookmarkFolder folder)
    {
        if (folder.IsPersonal)
        {
            return folder.PilotId == pilotId ? BookmarkAccess.Personal : BookmarkAccess.None;
        }
        foreach (
            var grant in new[]
            {
                (folder.AdminGroupId, BookmarkAccess.Admin),
                (folder.ManageGroupId, BookmarkAccess.Manage),
                (folder.UseGroupId, BookmarkAccess.Use),
                (folder.ViewGroupId, BookmarkAccess.View),
            }
        )
        {
            if (grant.Item1 is { } id && AccessGroupRules.GrantsAccess(AccessGroupStore.Role(world, pilotId, id)))
            {
                return grant.Item2;
            }
        }
        return BookmarkAccess.None;
    }

    private static BookmarkFolder FolderSnapshot(WorldAction world, long pilotId, SavedBookmarkFolder folder)
    {
        if (folder.IsPersonal)
        {
            return folder.PilotId == pilotId ? folder.Snapshot() : folder.Snapshot() with { Access = BookmarkAccess.None };
        }
        var connection = world.Set<SavedBookmarkConnection>().Find(pilotId, folder.Id);
        return new(
            folder.Id,
            folder.PilotId,
            folder.Name,
            folder.Description,
            connection?.IsActive == true,
            connection is not null,
            false,
            Access(world, pilotId, folder),
            new(folder.AdminGroupId, folder.ManageGroupId, folder.UseGroupId, folder.ViewGroupId)
        );
    }

    private static bool Visible(WorldAction world, long pilotId, SavedBookmark bookmark, long now) =>
        bookmark.CreatorId == pilotId
        || bookmark.CreatedAt <= now - BookmarkRules.SharedDelay
        || world.Set<SavedBookmarkFolder>().Find(bookmark.FolderId)!.IsPersonal;

    private static SavedBookmarkSubfolder RequireSubfolder(WorldAction world, long folderId, long subfolderId) =>
        world.Set<SavedBookmarkSubfolder>().SingleOrDefault(s => s.Id == subfolderId && s.FolderId == folderId)
        ?? throw new InvalidDataException("Unknown location subfolder.");

    private static void RequireDestination(WorldAction world, long pilotId, long folderId, long? subfolderId)
    {
        RequireFolder(world, pilotId, folderId, BookmarkAccess.Use);
        if (subfolderId is { } id)
        {
            RequireSubfolder(world, folderId, id);
        }
    }

    private SavedBookmark[] RequireBookmarks(WorldAction world, long pilotId, long folderId, IReadOnlyList<long> ids)
    {
        BookmarkRules.CheckBatch(ids);
        RequireFolder(world, pilotId, folderId);
        long now = Now;
        var bookmarks = world
            .Filter<SavedBookmark>(b => b.FolderId == folderId && ids.Contains(b.Id) && (b.ExpiresAt == null || b.ExpiresAt > now))
            .OrderBy(b => b.Id)
            .ToArray();
        if (bookmarks.Length != ids.Distinct().Count())
        {
            throw new InvalidDataException("Choose existing, unexpired locations in that folder.");
        }
        return bookmarks;
    }

    private static void CheckActiveLimit(WorldAction world, long pilotId, long? except, bool personal = true)
    {
        int count = personal
            ? world.Set<SavedBookmarkFolder>().Count(f => f.PilotId == pilotId && f.IsPersonal && f.IsActive && f.IsKnown && f.Id != except)
            : world.Set<SavedBookmarkConnection>().Count(c => c.PilotId == pilotId && c.IsActive && c.FolderId != except);
        if (count >= (personal ? BookmarkRules.MaximumActiveFolders : BookmarkRules.MaximumActiveSharedFolders))
        {
            throw new InvalidDataException("At most five personal and three shared location folders can be active.");
        }
    }

    internal static void PruneUnavailableConnections(WorldDbContext world, long? pilotId = null, long? folderId = null) =>
        PruneUnavailableConnections(new SqlWorldAction(world), pilotId, folderId);

    internal static void PruneUnavailableConnections(WorldAction world, long? pilotId = null, long? folderId = null)
    {
        var connections =
            pilotId is long pilot ? world.Filter<SavedBookmarkConnection>(c => c.PilotId == pilot)
            : folderId is long id ? world.Filter<SavedBookmarkConnection>(c => c.FolderId == id)
            : world.Set<SavedBookmarkConnection>();
        foreach (var connection in connections.Where(c => folderId == null || c.FolderId == folderId).ToArray())
        {
            var folder = world.Set<SavedBookmarkFolder>().Find(connection.FolderId);
            if (folder is null || Access(world, connection.PilotId, folder) == BookmarkAccess.None)
            {
                world.Remove(connection);
            }
        }
    }

    private static void CheckKnownLimit(WorldAction world, long pilotId)
    {
        int count =
            world.Set<SavedBookmarkFolder>().Count(f => f.IsPersonal && f.PilotId == pilotId && f.IsKnown)
            + world.Set<SavedBookmarkConnection>().Count(c => c.PilotId == pilotId);
        if (count >= BookmarkRules.MaximumFolders)
        {
            throw new InvalidDataException("At most 30 location folders can be connected.");
        }
    }

    private static void CheckCapacity(WorldAction world, long folderId, int added, long now)
    {
        world.RemoveRange(world.Filter<SavedBookmark>(b => b.FolderId == folderId && b.ExpiresAt <= now).ToArray());
        world.SaveChanges();
        if (
            world.Set<SavedBookmark>().Count(b => b.FolderId == folderId) + added
            > (
                world.Set<SavedBookmarkFolder>().Find(folderId)!.IsPersonal
                    ? BookmarkRules.MaximumBookmarksPerFolder
                    : BookmarkRules.MaximumSharedBookmarks
            )
        )
        {
            throw new InvalidDataException("This location folder is full.");
        }
    }
}
