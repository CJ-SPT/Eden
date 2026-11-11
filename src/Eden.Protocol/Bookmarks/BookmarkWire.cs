using Eden.Gameplay.Bookmarks;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Bookmarks;

internal sealed record BookmarkFolderReply(BookmarkFolder Folder) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["folderID"] = Folder.Id,
                ["folderName"] = new BlueUnicode(Folder.Name),
                ["description"] = new BlueUnicode(Folder.Description),
                ["isPersonal"] = Folder.IsPersonal,
                ["creatorID"] = Folder.OwnerId,
                ["ownerID"] = Folder.OwnerId,
                ["isActive"] = Folder.IsActive,
                ["accessLevel"] = (int)Folder.Access,
                ["adminGroupID"] = Folder.Groups?.AdminGroupId,
                ["manageGroupID"] = Folder.Groups?.ManageGroupId,
                ["useGroupID"] = Folder.Groups?.UseGroupId,
                ["viewGroupID"] = Folder.Groups?.ViewGroupId,
            }
        );
}

internal sealed record BookmarkSubfolderReply(BookmarkSubfolder Subfolder) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["subfolderID"] = Subfolder.Id,
                ["folderID"] = Subfolder.FolderId,
                ["subfolderName"] = new BlueUnicode(Subfolder.Name),
                ["creatorID"] = Subfolder.CreatorId,
            }
        );
}

internal sealed record BookmarkReply(PersonalBookmark Bookmark) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["bookmarkID"] = Bookmark.Id,
                ["folderID"] = Bookmark.FolderId,
                ["subfolderID"] = Bookmark.SubfolderId,
                ["creatorID"] = Bookmark.CreatorId,
                ["itemID"] = Bookmark.Target.ItemId,
                ["typeID"] = Bookmark.Target.TypeId,
                ["locationID"] = Bookmark.Target.LocationId,
                ["memo"] = new BlueUnicode(Bookmark.Text.Name),
                ["note"] = new BlueUnicode(Bookmark.Text.Note),
                ["flag"] = null,
                ["created"] = Bookmark.CreatedAt,
                ["expiry"] = Bookmark.ExpiresAt,
                ["x"] = Bookmark.Target.Position.X,
                ["y"] = Bookmark.Target.Position.Y,
                ["z"] = Bookmark.Target.Position.Z,
            }
        );
}

internal sealed record BookmarkCreationReply(PersonalBookmark Bookmark) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Bookmark.Id,
            Bookmark.Target.ItemId,
            Bookmark.Target.TypeId,
            Bookmark.Target.Position.X,
            Bookmark.Target.Position.Y,
            Bookmark.Target.Position.Z,
            Bookmark.Target.LocationId,
            Bookmark.ExpiresAt,
        };
}

internal sealed record BookmarkContentsReply(BookmarkContents Contents) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            new BlueList<BookmarkFolderReply>(Contents.Folders.Select(f => new BookmarkFolderReply(f)).ToArray()),
            new BlueList<BookmarkReply>(Contents.Bookmarks.Select(b => new BookmarkReply(b)).ToArray()),
            new BlueList<BookmarkSubfolderReply>(Contents.Subfolders.Select(s => new BookmarkSubfolderReply(s)).ToArray()),
        };
}

internal sealed record BookmarkFolderContentsReply(BookmarkFolderContents Contents) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            new BookmarkFolderReply(Contents.Folder),
            new BlueList<BookmarkReply>(Contents.Bookmarks.Select(b => new BookmarkReply(b)).ToArray()),
            new BlueList<BookmarkSubfolderReply>(Contents.Subfolders.Select(s => new BookmarkSubfolderReply(s)).ToArray()),
        };
}

internal sealed record BookmarkMoveReply(IReadOnlyList<PersonalBookmark> Bookmarks, bool Copy) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Copy
                ? Bookmarks.ToDictionary(b => b.Id, b => (object?)new BookmarkReply(b))
                : (object)new BlueList<BookmarkReply>(Bookmarks.Select(b => new BookmarkReply(b)).ToArray()),
            null,
        };
}
