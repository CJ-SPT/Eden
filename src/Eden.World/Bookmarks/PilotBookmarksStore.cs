using Eden.Gameplay.Bookmarks;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Bookmarks;

public sealed class SavedBookmarkFolder
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool IsKnown { get; set; } = true;

    public BookmarkFolder Snapshot() => new(Id, PilotId, Name, Description, IsActive, IsKnown);
}

public sealed class SavedBookmarkSubfolder
{
    public long Id { get; set; }
    public long FolderId { get; set; }
    public string Name { get; set; } = "";

    public BookmarkSubfolder Snapshot() => new(Id, FolderId, Name);
}

public sealed class SavedBookmark
{
    public long Id { get; set; }
    public long FolderId { get; set; }
    public long? SubfolderId { get; set; }
    public long CreatorId { get; set; }
    public long? ItemId { get; set; }
    public int TypeId { get; set; }
    public int LocationId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
    public long CreatedAt { get; set; }
    public long? ExpiresAt { get; set; }

    public PersonalBookmark Snapshot() =>
        new(Id, FolderId, SubfolderId, CreatorId, new(ItemId, TypeId, LocationId, new(X, Y, Z)), new(Name, Note), CreatedAt, ExpiresAt);
}

[Injectable]
internal sealed class PilotBookmarksStore(WorldDatabase database, TimeProvider clock) : IPilotBookmarks
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public BookmarkContents Read(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        CheckPilot(world, pilotId);
        var folders = world.Set<SavedBookmarkFolder>().Where(f => f.PilotId == pilotId && f.IsKnown).OrderBy(f => f.Id).ToArray();
        var active = folders.Where(f => f.IsActive).Select(f => f.Id).ToArray();
        long now = Now;
        return new(
            folders.Select(f => f.Snapshot()).ToArray(),
            world
                .Set<SavedBookmark>()
                .Where(b => active.Contains(b.FolderId) && (b.ExpiresAt == null || b.ExpiresAt > now))
                .OrderBy(b => b.Id)
                .AsEnumerable()
                .Select(b => b.Snapshot())
                .ToArray(),
            world
                .Set<SavedBookmarkSubfolder>()
                .Where(s => active.Contains(s.FolderId))
                .OrderBy(s => s.Id)
                .AsEnumerable()
                .Select(s => s.Snapshot())
                .ToArray()
        );
    }

    public BookmarkFolder Folder(long pilotId, long folderId)
    {
        using WorldDbContext world = new(database.Path);
        return RequireFolder(world, pilotId, folderId).Snapshot();
    }

    public BookmarkFolder AddFolder(long pilotId, string name, string description)
    {
        BookmarkRules.CheckName(name);
        BookmarkRules.CheckDescription(description);
        return Write(
            pilotId,
            world =>
            {
                if (world.Set<SavedBookmarkFolder>().Count(f => f.PilotId == pilotId) >= BookmarkRules.MaximumFolders)
                {
                    throw new InvalidDataException("You have reached the personal location folder limit.");
                }
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

    public void UpdateFolder(long pilotId, long folderId, string name, string description)
    {
        BookmarkRules.CheckName(name);
        BookmarkRules.CheckDescription(description);
        Write(
            pilotId,
            world =>
            {
                var folder = RequireFolder(world, pilotId, folderId);
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
                var folder = RequireFolder(world, pilotId, folderId);
                world.Set<SavedBookmark>().Where(b => b.FolderId == folderId).ExecuteDelete();
                world.Set<SavedBookmarkSubfolder>().Where(s => s.FolderId == folderId).ExecuteDelete();
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
                if (active && known && !(folder.IsActive && folder.IsKnown))
                {
                    CheckActiveLimit(world, pilotId, folderId);
                }
                folder.IsActive = active && known;
                folder.IsKnown = known;
                long now = Now;
                return new BookmarkFolderContents(
                    folder.Snapshot(),
                    active && known
                        ? world
                            .Set<SavedBookmark>()
                            .Where(b => b.FolderId == folderId && (b.ExpiresAt == null || b.ExpiresAt > now))
                            .OrderBy(b => b.Id)
                            .AsEnumerable()
                            .Select(b => b.Snapshot())
                            .ToArray()
                        : [],
                    active && known
                        ? world
                            .Set<SavedBookmarkSubfolder>()
                            .Where(s => s.FolderId == folderId)
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
        using WorldDbContext world = new(database.Path);
        CheckPilot(world, pilotId);
        return world
            .Set<SavedBookmarkFolder>()
            .Where(f => f.PilotId == pilotId)
            .OrderBy(f => f.Id)
            .AsEnumerable()
            .Select(f => f.Snapshot())
            .ToArray();
    }

    public BookmarkSubfolder AddSubfolder(long pilotId, long folderId, string name)
    {
        BookmarkRules.CheckName(name);
        return Write(
            pilotId,
            world =>
            {
                RequireFolder(world, pilotId, folderId);
                var subfolder = new SavedBookmarkSubfolder { FolderId = folderId, Name = name };
                world.Add(subfolder);
                world.SaveChanges();
                return subfolder.Snapshot();
            }
        );
    }

    public void UpdateSubfolder(long pilotId, long folderId, long subfolderId, string name)
    {
        BookmarkRules.CheckName(name);
        Write(
            pilotId,
            world =>
            {
                RequireFolder(world, pilotId, folderId);
                RequireSubfolder(world, folderId, subfolderId).Name = name;
                return true;
            }
        );
    }

    public IReadOnlyList<long> DeleteSubfolder(long pilotId, long folderId, long subfolderId) =>
        Write(
            pilotId,
            world =>
            {
                RequireFolder(world, pilotId, folderId);
                var subfolder = RequireSubfolder(world, folderId, subfolderId);
                var ids = world.Set<SavedBookmark>().Where(b => b.SubfolderId == subfolderId).Select(b => b.Id).ToArray();
                world.Set<SavedBookmark>().Where(b => b.SubfolderId == subfolderId).ExecuteDelete();
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
        if (catalog.Set<Station>().Find(checked((int)itemId)) is { } station)
        {
            target = new(itemId, station.TypeId, station.SolarSystemId, new(station.X, station.Y, station.Z));
        }
        else if (catalog.Set<Celestial>().Find(checked((int)itemId)) is { } body)
        {
            target = new(itemId, body.TypeId, body.SolarSystemId, new(body.X, body.Y, body.Z));
        }
        else if (catalog.Set<SolarSystem>().Find(checked((int)itemId)) is not null)
        {
            target = new(itemId, BookmarkRules.SolarSystemTypeId, checked((int)itemId), new(0, 0, 0));
        }
        else if (catalog.Set<Constellation>().Find((int)itemId) is not null)
        {
            target = new(itemId, BookmarkRules.ConstellationTypeId, (int)itemId, new(0, 0, 0));
        }
        else if (catalog.Set<Region>().Find((int)itemId) is not null)
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
                return bookmark.Snapshot();
            }
        );
    }

    public PersonalBookmark Find(long pilotId, long bookmarkId)
    {
        using WorldDbContext world = new(database.Path);
        var bookmark = world.Set<SavedBookmark>().Find(bookmarkId) ?? throw new InvalidDataException("Unknown personal location.");
        var folder = RequireFolder(world, pilotId, bookmark.FolderId);
        if (!folder.IsActive || !folder.IsKnown || bookmark.ExpiresAt <= Now)
        {
            throw new InvalidDataException("This personal location is inactive or expired.");
        }
        return bookmark.Snapshot();
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
                return (IReadOnlyList<PersonalBookmark>)changed.Select(b => b.Snapshot()).ToArray();
            }
        );

    private T Write<T>(long pilotId, Func<WorldDbContext, T> operation)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        CheckPilot(world, pilotId);
        T result = operation(world);
        world.SaveChanges();
        transaction.Commit();
        return result;
    }

    private static void CheckPilot(WorldDbContext world, long pilotId)
    {
        if (!world.Pilots.Any(p => p.Id == pilotId))
        {
            throw new InvalidDataException("Unknown character.");
        }
    }

    private static SavedBookmarkFolder RequireFolder(WorldDbContext world, long pilotId, long folderId) =>
        world.Set<SavedBookmarkFolder>().SingleOrDefault(f => f.Id == folderId && f.PilotId == pilotId)
        ?? throw new InvalidDataException("Unknown personal location folder.");

    private static SavedBookmarkSubfolder RequireSubfolder(WorldDbContext world, long folderId, long subfolderId) =>
        world.Set<SavedBookmarkSubfolder>().SingleOrDefault(s => s.Id == subfolderId && s.FolderId == folderId)
        ?? throw new InvalidDataException("Unknown location subfolder.");

    private static void RequireDestination(WorldDbContext world, long pilotId, long folderId, long? subfolderId)
    {
        RequireFolder(world, pilotId, folderId);
        if (subfolderId is { } id)
        {
            RequireSubfolder(world, folderId, id);
        }
    }

    private SavedBookmark[] RequireBookmarks(WorldDbContext world, long pilotId, long folderId, IReadOnlyList<long> ids)
    {
        BookmarkRules.CheckBatch(ids);
        RequireFolder(world, pilotId, folderId);
        long now = Now;
        var bookmarks = world
            .Set<SavedBookmark>()
            .Where(b => b.FolderId == folderId && ids.Contains(b.Id) && (b.ExpiresAt == null || b.ExpiresAt > now))
            .OrderBy(b => b.Id)
            .ToArray();
        if (bookmarks.Length != ids.Distinct().Count())
        {
            throw new InvalidDataException("Choose existing, unexpired personal locations in that folder.");
        }
        return bookmarks;
    }

    private static void CheckActiveLimit(WorldDbContext world, long pilotId, long? except)
    {
        if (
            world.Set<SavedBookmarkFolder>().Count(f => f.PilotId == pilotId && f.IsActive && f.IsKnown && f.Id != except)
            >= BookmarkRules.MaximumActiveFolders
        )
        {
            throw new InvalidDataException("At most five personal location folders can be active.");
        }
    }

    private static void CheckCapacity(WorldDbContext world, long folderId, int added, long now)
    {
        world.Set<SavedBookmark>().Where(b => b.FolderId == folderId && b.ExpiresAt <= now).ExecuteDelete();
        if (world.Set<SavedBookmark>().Count(b => b.FolderId == folderId) + added > BookmarkRules.MaximumBookmarksPerFolder)
        {
            throw new InvalidDataException("This personal location folder is full.");
        }
    }
}
