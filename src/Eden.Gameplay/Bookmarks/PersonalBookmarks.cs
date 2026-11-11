using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Bookmarks;

public enum BookmarkExpiry
{
    Never = 0,
    FourHours = 1,
    TwoDays = 2,
    OneDay = 3,
}

public sealed record BookmarkFolder(long Id, long OwnerId, string Name, string Description, bool IsActive, bool IsKnown);

public sealed record BookmarkSubfolder(long Id, long FolderId, string Name);

public sealed record BookmarkTarget(long? ItemId, int TypeId, int LocationId, SpacePosition Position);

public sealed record BookmarkText(string Name, string Note);

public sealed record PersonalBookmark(
    long Id,
    long FolderId,
    long? SubfolderId,
    long CreatorId,
    BookmarkTarget Target,
    BookmarkText Text,
    long CreatedAt,
    long? ExpiresAt
);

public sealed record BookmarkContents(
    IReadOnlyList<BookmarkFolder> Folders,
    IReadOnlyList<PersonalBookmark> Bookmarks,
    IReadOnlyList<BookmarkSubfolder> Subfolders
);

public sealed record BookmarkFolderContents(
    BookmarkFolder Folder,
    IReadOnlyList<PersonalBookmark> Bookmarks,
    IReadOnlyList<BookmarkSubfolder> Subfolders
);

public sealed record BookmarkEdit(long Id, long FolderId, long NewFolderId, long? SubfolderId, BookmarkText Text, bool CancelExpiry);

public interface IPilotBookmarks
{
    BookmarkContents Read(long pilotId);
    BookmarkFolder Folder(long pilotId, long folderId);
    BookmarkFolder AddFolder(long pilotId, string name, string description);
    void UpdateFolder(long pilotId, long folderId, string name, string description);
    void DeleteFolder(long pilotId, long folderId);
    BookmarkFolderContents SetFolderState(long pilotId, long folderId, bool active, bool known = true);
    IReadOnlyList<BookmarkFolder> SearchFolders(long pilotId);
    BookmarkSubfolder AddSubfolder(long pilotId, long folderId, string name);
    void UpdateSubfolder(long pilotId, long folderId, long subfolderId, string name);
    IReadOnlyList<long> DeleteSubfolder(long pilotId, long folderId, long subfolderId);
    PersonalBookmark CreateStatic(long pilotId, long itemId, long folderId, long? subfolderId, BookmarkText text, BookmarkExpiry expiry);
    PersonalBookmark Create(
        long pilotId,
        BookmarkTarget target,
        long folderId,
        long? subfolderId,
        BookmarkText text,
        BookmarkExpiry expiry
    );
    PersonalBookmark Find(long pilotId, long bookmarkId);
    void Update(long pilotId, BookmarkEdit edit);
    IReadOnlyList<long> Delete(long pilotId, long folderId, IReadOnlyList<long> bookmarkIds);
    IReadOnlyList<PersonalBookmark> MoveOrCopy(
        long pilotId,
        long sourceFolderId,
        long folderId,
        long? subfolderId,
        IReadOnlyList<long> bookmarkIds,
        bool copy
    );
}

public static class BookmarkRules
{
    // Build 3662168 appConst and bookmarkConst; personal access level is 1.
    public const int MaximumNameLength = 100;
    public const int MaximumDescriptionLength = 3900;
    public const int MaximumBookmarksPerFolder = 3000;
    public const int MaximumFolders = 30;
    public const int MaximumActiveFolders = 5;
    public const int SolarSystemTypeId = 5;
    public const int RegionTypeId = 3;
    public const int ConstellationTypeId = 4;

    public static void CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumNameLength || name.Contains('\0'))
        {
            throw new InvalidDataException($"Locations and folders need a name of at most {MaximumNameLength} characters.");
        }
    }

    public static void CheckDescription(string description)
    {
        if (description.Length > MaximumDescriptionLength || description.Contains('\0'))
        {
            throw new InvalidDataException($"Location notes must contain at most {MaximumDescriptionLength} characters and no nulls.");
        }
    }

    // Pinned catalog object IDs fit Int32. Larger IDs belong to generated world objects;
    // their saved position remains usable after the object disappears or is replaced.
    public static BookmarkTarget DurableTarget(BookmarkTarget target) =>
        target.ItemId is > int.MaxValue ? target with { ItemId = null, TypeId = SolarSystemTypeId } : target;

    public static void CheckTarget(BookmarkTarget target)
    {
        if (
            target.ItemId <= 0
            || target.TypeId <= 0
            || target.LocationId <= 0
            || !double.IsFinite(target.Position.X)
            || !double.IsFinite(target.Position.Y)
            || !double.IsFinite(target.Position.Z)
        )
        {
            throw new InvalidDataException("Choose a valid location.");
        }
    }

    public static void CheckBatch(IReadOnlyList<long> ids)
    {
        if (ids.Count is < 1 or > MaximumBookmarksPerFolder || ids.Any(id => id <= 0))
        {
            throw new InvalidDataException("Choose a valid list of personal locations.");
        }
    }

    public static long? ExpiryTime(BookmarkExpiry expiry, long now) =>
        expiry switch
        {
            BookmarkExpiry.Never => null,
            BookmarkExpiry.FourHours => checked(now + TimeSpan.FromHours(4).Ticks),
            BookmarkExpiry.TwoDays => checked(now + TimeSpan.FromDays(2).Ticks),
            BookmarkExpiry.OneDay => checked(now + TimeSpan.FromDays(1).Ticks),
            _ => throw new InvalidDataException("Choose a valid location expiry."),
        };
}
