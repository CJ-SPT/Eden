using Eden.Gameplay.Bookmarks;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Bookmarks;

[Injectable(InjectionType.Scoped)]
internal sealed class BookmarkMonitor(IPilotBookmarks bookmarks, TimeProvider clock)
{
    private BookmarkContents? _before;
    private long _revision;
    private long _pilot;
    private int _corporation;
    public bool IsMonitoring => _before is not null;

    public void Reset() => _before = null;

    public BookmarkContents Read(PilotCallContext context)
    {
        _pilot = context.Pilot.PilotId;
        _corporation = context.Pilot.CorpId;
        _revision = bookmarks.Revision;
        _before = bookmarks.Read(_pilot);
        return _before;
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected || context.Pilot.PilotId != _pilot)
        {
            Reset();
        }
        if (_before is not { } before)
        {
            return;
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (_revision == bookmarks.Revision && _corporation == context.Pilot.CorpId && !(before.RefreshAt <= now))
        {
            return;
        }
        var after = Read(context);
        long[] lost = before.Folders.Select(f => f.Id).Except(after.Folders.Select(f => f.Id)).ToArray();
        if (lost.Length > 0)
        {
            context.Notifications.Add(new BookmarkAccessLostNotification(lost));
        }
        var levels = after
            .Folders.Where(f => before.Folders.Any(old => old.Id == f.Id && old.Access != f.Access))
            .ToDictionary(f => f.Id, f => (int)f.Access);
        if (levels.Count > 0)
        {
            context.Notifications.Add(new BookmarkAccessChangedNotification(levels));
        }
        List<BookmarkFolderUpdate> updates = [];
        foreach (var folder in after.Folders.Where(f => f.IsActive))
        {
            if (before.Folders.SingleOrDefault(f => f.Id == folder.Id) is not { IsActive: true } oldFolder)
            {
                continue;
            }
            long id = folder.Id;
            if (folder.Name != oldFolder.Name || folder.Description != oldFolder.Description)
            {
                updates.Add(new(id, "folderUpdated", new object?[] { new BlueUnicode(folder.Name), new BlueUnicode(folder.Description) }));
            }
            var oldBookmarks = before.Bookmarks.Where(b => b.FolderId == id).ToDictionary(b => b.Id);
            var current = after.Bookmarks.Where(b => b.FolderId == id).ToArray();
            var removed = oldBookmarks.Keys.Except(current.Select(b => b.Id)).ToArray();
            if (removed.Length > 0)
            {
                updates.Add(new(id, "bookmarksRemoved", new BlueList<long>(removed)));
            }
            var added = current.Where(b => !oldBookmarks.ContainsKey(b.Id)).Select(b => new BookmarkReply(b)).ToArray();
            if (added.Length > 0)
            {
                updates.Add(new(id, "bookmarksAdded", new BlueList<BookmarkReply>(added)));
            }
            var changed = current
                .Where(b => oldBookmarks.TryGetValue(b.Id, out var old) && old != b)
                .Select(b => new BookmarkReply(b))
                .ToArray();
            if (changed.Length > 0)
            {
                updates.Add(new(id, "bookmarksUpdated", new BlueList<BookmarkReply>(changed)));
            }
            var oldSubs = before.Subfolders.Where(s => s.FolderId == id).ToDictionary(s => s.Id);
            var subs = after.Subfolders.Where(s => s.FolderId == id).ToArray();
            foreach (long removedId in oldSubs.Keys.Except(subs.Select(s => s.Id)))
            {
                updates.Add(new(id, "subfolderRemoved", removedId));
            }
            foreach (var sub in subs)
            {
                if (!oldSubs.TryGetValue(sub.Id, out var old))
                {
                    updates.Add(new(id, "subfolderAdded", new BookmarkSubfolderReply(sub)));
                }
                else if (sub != old)
                {
                    updates.Add(new(id, "subfolderUpdated", new object?[] { sub.Id, new BlueUnicode(sub.Name) }));
                }
            }
        }
        if (updates.Count > 0)
        {
            context.Notifications.Add(new BookmarkUpdatesNotification(updates));
        }
    }
}

// Heterogeneous update arguments are confined to this explicit native wire adapter.
internal sealed record BookmarkFolderUpdate(long FolderId, string Kind, object? Value) : BlueModel
{
    public override object ToBlue() => new object?[] { new object?[] { FolderId }, Kind, Value };
}

internal sealed record BookmarkUpdatesNotification(IReadOnlyList<BookmarkFolderUpdate> Updates)
    : PilotNotification("OnSharedBookmarksFolderUpdated")
{
    public override object?[] Arguments => [new BlueList<BookmarkFolderUpdate>(Updates)];
}

internal sealed record BookmarkAccessLostNotification(IReadOnlyList<long> FolderIds)
    : PilotNotification("OnSharedBookmarksFoldersAccessLost")
{
    public override object?[] Arguments => [new BlueList<long>(FolderIds)];
}

internal sealed record BookmarkAccessChangedNotification(IReadOnlyDictionary<long, int> Levels)
    : PilotNotification("OnSharedBookmarksFoldersAccessChanged")
{
    public override object?[] Arguments => [new BlueMap<long, int>(Levels)];
}
