using Eden.Gameplay.Bookmarks;
using Microsoft.EntityFrameworkCore;

namespace Eden.Data.World.Bookmarks;

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
