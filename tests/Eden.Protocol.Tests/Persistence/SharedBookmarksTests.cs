using Eden.Data.World.Bookmarks;
using Eden.Gameplay.AccessGroups;
using Eden.Gameplay.Bookmarks;
using Eden.Server;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class SharedBookmarksTests
{
    private const long Pilot = 90000001;
    private const long Wing = 90000002;

    [Fact]
    public void BuiltInCorporationListsAreVisibleAssignableImmutableAndStableAcrossRestart()
    {
        using TestWorld db = new();
        var groups = db.Services.GetRequiredService<IPilotAccessGroups>();
        var store = db.Services.GetRequiredService<IPilotBookmarks>();
        var builtin = Assert.Single(groups.Read(Pilot));
        Assert.Equal(AccessGroupRole.Admin, builtin.Role);
        Assert.Equal(AccessGroupRole.Admin, Assert.Single(groups.Members(Pilot, builtin.Id)).Role);
        Assert.Equal(builtin.CreatorId, Assert.Single(builtin.Admins));
        Assert.Throws<InvalidDataException>(() => groups.Update(Pilot, builtin.Id, "Changed", null));
        Assert.Throws<InvalidDataException>(() => groups.Delete(Pilot, builtin.Id));
        Assert.Throws<InvalidDataException>(() => groups.ChangeMembers(Pilot, builtin.Id, [new(Pilot, AccessGroupRole.Member)]));
        var folder = store.AddSharedFolder(Pilot, "Corporation", "", new(builtin.Id, null, null, null));
        Assert.Equal(BookmarkAccess.Admin, folder.Access);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        Assert.Equal(builtin.Id, Assert.Single(reopened.GetRequiredService<IPilotAccessGroups>().Read(Pilot)).Id);
        Assert.Equal(folder, reopened.GetRequiredService<IPilotBookmarks>().Folder(Pilot, folder.Id));
    }

    [Fact]
    public void SharedFoldersKeepConnectionsPrivateAndRevokeNavigationImmediatelyAcrossRestart()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        var groups = db.Services.GetRequiredService<IPilotAccessGroups>();
        var bookmarks = db.Services.GetRequiredService<IPilotBookmarks>();
        var admin = groups.Create(Pilot, "Administrators", "Private");
        var view = groups.Create(Pilot, "Viewers", "");
        groups.ChangeMembers(Pilot, view.Id, [new(Wing, AccessGroupRole.Member)]);
        var folder = bookmarks.AddSharedFolder(Pilot, "Shared", "Positions", new(admin.Id, null, null, view.Id));
        Assert.Empty(bookmarks.Read(Wing).Folders);
        Assert.Equal(BookmarkAccess.View, bookmarks.Folder(Wing, folder.Id).Access);
        Assert.False(bookmarks.Folder(Wing, folder.Id).IsKnown);
        bookmarks.SetFolderState(Wing, folder.Id, true);
        var bookmark = bookmarks.CreateStatic(Pilot, 60003760, folder.Id, null, new("Home", ""), BookmarkExpiry.Never);
        Assert.Single(bookmarks.Read(Wing).Bookmarks);
        Assert.Throws<InvalidDataException>(() => bookmarks.Find(Wing, bookmark.Id));
        clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        Assert.Equal(bookmark, bookmarks.Find(Wing, bookmark.Id));
        bookmarks.SetFolderState(Wing, folder.Id, false);
        Assert.True(bookmarks.Folder(Pilot, folder.Id).IsActive);
        Assert.Empty(bookmarks.Read(Wing).Bookmarks);
        bookmarks.SetFolderState(Wing, folder.Id, true);
        using (var reopened = ServerServices.Build(new(db.Path), clock))
        {
            Assert.Equal(bookmark, reopened.GetRequiredService<IPilotBookmarks>().Find(Wing, bookmark.Id));
        }
        groups.ChangeMembers(Pilot, view.Id, [new(Wing, AccessGroupRole.Excluded)]);
        Assert.Empty(bookmarks.Read(Wing).Folders);
        Assert.Throws<InvalidDataException>(() => bookmarks.Find(Wing, bookmark.Id));
        Assert.Throws<InvalidDataException>(() => bookmarks.SetFolderState(Wing, folder.Id, true));
        Assert.Equal(bookmark, bookmarks.Find(Pilot, bookmark.Id));
    }

    [Fact]
    public void UseRightsEditOwnBookmarksAndManagersEditOthersWithAtomicBatchRejection()
    {
        using TestWorld db = new();
        var groups = db.Services.GetRequiredService<IPilotAccessGroups>();
        var store = db.Services.GetRequiredService<IPilotBookmarks>();
        var admin = groups.Create(Pilot, "Admins", "");
        var users = groups.Create(Pilot, "Users", "");
        groups.ChangeMembers(Pilot, users.Id, [new(Wing, AccessGroupRole.Member)]);
        var folder = store.AddSharedFolder(Pilot, "Shared", "", new(admin.Id, null, users.Id, null));
        store.SetFolderState(Wing, folder.Id, true);
        var own = store.CreateStatic(Wing, 60003760, folder.Id, null, new("Own", ""), BookmarkExpiry.Never);
        var other = store.CreateStatic(Pilot, 60003760, folder.Id, null, new("Other", ""), BookmarkExpiry.Never);
        Assert.Throws<InvalidDataException>(() => store.Delete(Wing, folder.Id, [own.Id, other.Id]));
        Assert.Equal(2, store.Read(Wing).Bookmarks.Count);
        store.Update(Wing, new(own.Id, folder.Id, folder.Id, null, new("Changed", ""), false));
        Assert.Throws<InvalidDataException>(() => store.Update(Wing, new(other.Id, folder.Id, folder.Id, null, new("Bad", ""), false)));
        var personal = store.AddFolder(Wing, "Copies", "");
        var copy = Assert.Single(store.MoveOrCopy(Wing, folder.Id, personal.Id, null, [other.Id], true));
        Assert.Equal(Wing, copy.CreatorId);
        Assert.Throws<InvalidDataException>(() => store.MoveOrCopy(Wing, folder.Id, personal.Id, null, [other.Id], false));
        Assert.Throws<InvalidDataException>(() => store.UpdateFolder(Wing, folder.Id, "Bad", ""));
        Assert.Throws<InvalidDataException>(() => store.DeleteFolder(Wing, folder.Id));
        store.UpdateSharedFolder(Pilot, folder.Id, "Shared", "", new(admin.Id, users.Id, null, null));
        Assert.Equal(BookmarkAccess.Manage, store.Folder(Wing, folder.Id).Access);
        Assert.Equal(2, store.Delete(Wing, folder.Id, [own.Id, other.Id]).Count);
        Assert.DoesNotContain(store.Read(Wing).Bookmarks, b => b.FolderId == folder.Id);
        Assert.Single(store.Read(Wing).Bookmarks);
    }

    [Fact]
    public void AccessListManagersCannotPromoteOrModifyOtherManagersAndCreatorHasNoOverride()
    {
        using TestWorld db = new();
        var groups = db.Services.GetRequiredService<IPilotAccessGroups>();
        var group = groups.Create(Pilot, "Permissions", "");
        groups.ChangeMembers(Pilot, group.Id, [new(Wing, AccessGroupRole.Manager)]);
        Assert.Throws<InvalidDataException>(() => groups.ChangeMembers(Wing, group.Id, [new(Pilot, AccessGroupRole.Member)]));
        Assert.Throws<InvalidDataException>(() => groups.ChangeMembers(Wing, group.Id, [new(Wing, AccessGroupRole.Admin)]));
        Assert.Throws<InvalidDataException>(() => groups.Update(Wing, group.Id, "Bad", null));
        Assert.Throws<InvalidDataException>(() => groups.Delete(Wing, group.Id));
        groups.ChangeMembers(Wing, group.Id, [new(0, AccessGroupRole.Member)]);
        Assert.Throws<InvalidDataException>(() => groups.ChangeMembers(Pilot, group.Id, [new(0, AccessGroupRole.Excluded)]));
        groups.ChangeMembers(Pilot, group.Id, [new(Pilot, AccessGroupRole.Excluded)]);
        Assert.Null(groups.Find(Pilot, group.Id));
        Assert.Throws<InvalidDataException>(() => groups.Members(Pilot, group.Id));
        Assert.Throws<InvalidDataException>(() => groups.Delete(Pilot, group.Id));
        groups.ChangeMembers(Wing, group.Id, [new(Wing, AccessGroupRole.Member)]);
        Assert.DoesNotContain(groups.Read(Wing), g => g.Id == group.Id);
    }

    [Fact]
    public void PersonalExceptionsOverrideCorporationAndEveryoneAndBlockedNeverGrantsAdministration()
    {
        AccessGroupMember[] members =
        [
            new(0, AccessGroupRole.Member),
            new(1000009, AccessGroupRole.Member),
            new(Wing, AccessGroupRole.Excluded),
        ];
        Assert.Equal(AccessGroupRole.Excluded, AccessGroupRules.Resolve(members, Wing, 1000009));
        Assert.Equal(AccessGroupRole.Member, AccessGroupRules.Resolve(members, Pilot, 1000009));
        Assert.False(AccessGroupRules.CanManage(AccessGroupRole.Excluded));
        Assert.False(AccessGroupRules.GrantsAccess(AccessGroupRole.Excluded));
    }

    [Fact]
    public void DeletingAnAccessListClearsFolderGrantsWithoutDeletingSavedLocations()
    {
        using TestWorld db = new();
        var groups = db.Services.GetRequiredService<IPilotAccessGroups>();
        var bookmarks = db.Services.GetRequiredService<IPilotBookmarks>();
        var admin = groups.Create(Pilot, "Admins", "");
        var viewers = groups.Create(Pilot, "Viewers", "");
        groups.ChangeMembers(Pilot, viewers.Id, [new(Wing, AccessGroupRole.Member)]);
        var folder = bookmarks.AddSharedFolder(Pilot, "Shared", "", new(admin.Id, null, null, viewers.Id));
        var bookmark = bookmarks.CreateStatic(Pilot, 60003760, folder.Id, null, new("Home", ""), BookmarkExpiry.Never);
        groups.Delete(Pilot, viewers.Id);
        Assert.Throws<InvalidDataException>(() => bookmarks.Folder(Wing, folder.Id));
        Assert.Null(bookmarks.Folder(Pilot, folder.Id).Groups!.ViewGroupId);
        Assert.Equal(bookmark, bookmarks.Find(Pilot, bookmark.Id));
        bookmarks.DeleteFolder(Pilot, folder.Id);
        using WorldDbContext world = new(db.Path);
        Assert.Empty(world.Set<SavedBookmarkConnection>());
        Assert.Empty(world.Set<SavedBookmark>());
    }

    [Fact]
    public void SharedLimitsIncludeActiveFoldersSubfoldersCapacityAndExpiryReclamation()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        var groups = db.Services.GetRequiredService<IPilotAccessGroups>();
        var store = db.Services.GetRequiredService<IPilotBookmarks>();
        var admin = groups.Create(Pilot, "Admins", "");
        BookmarkFolderAccess access = new(admin.Id, null, null, null);
        var folder = store.AddSharedFolder(Pilot, new string('f', BookmarkRules.MaximumFolderNameLength), "", access);
        Assert.Throws<InvalidDataException>(() => store.AddSharedFolder(Pilot, new string('f', 41), "", access));
        for (int i = 1; i < BookmarkRules.MaximumActiveSharedFolders; i++)
        {
            store.AddSharedFolder(Pilot, $"Shared {i}", "", access);
        }
        Assert.Throws<InvalidDataException>(() => store.AddSharedFolder(Pilot, "Too many active", "", access));
        store.SetFolderState(Pilot, folder.Id, false);
        var fourth = store.AddSharedFolder(Pilot, "Fourth", "", access);
        Assert.Throws<InvalidDataException>(() => store.SetFolderState(Pilot, folder.Id, true));
        store.SetFolderState(Pilot, fourth.Id, false);
        store.SetFolderState(Pilot, folder.Id, true);
        for (int i = 0; i < BookmarkRules.MaximumSubfolders; i++)
        {
            store.AddSubfolder(Pilot, folder.Id, $"Subfolder {i}");
        }
        Assert.Throws<InvalidDataException>(() => store.AddSubfolder(Pilot, folder.Id, "Too many"));
        var bookmark = store.CreateStatic(Pilot, 60003760, folder.Id, null, new("Home", ""), BookmarkExpiry.FourHours);
        using (WorldDbContext world = new(db.Path))
        {
            // Populate the exact boundary without issuing 499 independent feature operations.
            world.Database.ExecuteSql(
                $"""
                WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n<499)
                INSERT INTO WorldBookmark(FolderId,CreatorId,ItemId,TypeId,LocationId,X,Y,Z,Name,Note,CreatedAt,ExpiresAt)
                SELECT FolderId,CreatorId,ItemId,TypeId,LocationId,X,Y,Z,Name,Note,CreatedAt,ExpiresAt
                FROM WorldBookmark,numbers WHERE Id={bookmark.Id}
                """
            );
        }
        Assert.Equal(BookmarkRules.MaximumSharedBookmarks, store.Read(Pilot).Bookmarks.Count);
        Assert.Throws<InvalidDataException>(() =>
            store.CreateStatic(Pilot, 60003760, folder.Id, null, new("Full", ""), BookmarkExpiry.Never)
        );
        clock.AdvanceWallTime(TimeSpan.FromHours(4));
        store.CreateStatic(Pilot, 60003760, folder.Id, null, new("Reclaimed", ""), BookmarkExpiry.Never);
        Assert.Single(store.Read(Pilot).Bookmarks);
    }

    [Fact]
    public void Version144MigrationPreservesPersonalBookmarksAndBackfillsSubfolderCreators()
    {
        using TestWorld db = new();
        var store = db.Services.GetRequiredService<IPilotBookmarks>();
        var folder = store.AddFolder(Pilot, "Existing", "Notes");
        var subfolder = store.AddSubfolder(Pilot, folder.Id, "Existing subfolder");
        var bookmark = store.CreateStatic(Pilot, 60003760, folder.Id, subfolder.Id, new("Home", "Keep"), BookmarkExpiry.Never);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.OpenConnection();
            world.Database.ExecuteSqlRaw(
                """
                PRAGMA foreign_keys=OFF;
                DROP TABLE WorldBookmarkConnection;
                DROP TABLE WorldAccessGroupMember;
                DROP TABLE WorldAccessGroupLog;
                CREATE TABLE LegacyBookmarkFolder(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Name TEXT NOT NULL, Description TEXT NOT NULL, IsActive INTEGER NOT NULL, IsKnown INTEGER NOT NULL);
                INSERT INTO LegacyBookmarkFolder SELECT Id,PilotId,Name,Description,IsActive,IsKnown FROM WorldBookmarkFolder;
                DROP TABLE WorldBookmarkFolder;
                ALTER TABLE LegacyBookmarkFolder RENAME TO WorldBookmarkFolder;
                CREATE INDEX IX_WorldBookmarkFolder_PilotId ON WorldBookmarkFolder(PilotId);
                ALTER TABLE WorldBookmarkSubfolder DROP COLUMN CreatorId;
                DROP TABLE WorldAccessGroup;
                PRAGMA user_version=144;
                PRAGMA foreign_keys=ON;
                """
            );
        }
        WorldInitialization.Open(db.Path);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var saved = reopened.GetRequiredService<IPilotBookmarks>();
        Assert.Equal(folder, Assert.Single(saved.Read(Pilot).Folders));
        Assert.Equal(subfolder, Assert.Single(saved.Read(Pilot).Subfolders));
        Assert.Equal(bookmark, saved.Find(Pilot, bookmark.Id));
        Assert.True(saved.AddFolder(Pilot, "New", "").Id > folder.Id);
        using WorldDbContext check = new(db.Path);
        Assert.Empty(check.Database.SqlQueryRaw<string>("SELECT 'invalid' AS Value FROM pragma_foreign_key_check"));
    }

    [Fact]
    public void FailedMembershipBatchRollsBackRowsAuditAndNotificationsAndRevokedConnectionsRequireReconnect()
    {
        using TestWorld db = new();
        var groups = db.Services.GetRequiredService<IPilotAccessGroups>();
        var store = db.Services.GetRequiredService<IPilotBookmarks>();
        var admin = groups.Create(Pilot, "Admins", "");
        var viewers = groups.Create(Pilot, "Viewers", "");
        long revision = groups.Revision;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER RejectAccessMember BEFORE INSERT ON WorldAccessGroupMember
                WHEN NEW.OwnerId=90000002 BEGIN SELECT RAISE(ABORT,'membership failure'); END;
                """
            );
        }
        Assert.Throws<DbUpdateException>(() =>
            groups.ChangeMembers(Pilot, viewers.Id, [new(0, AccessGroupRole.Member), new(Wing, AccessGroupRole.Member)])
        );
        Assert.Single(groups.Members(Pilot, viewers.Id));
        Assert.Single(groups.Logs(Pilot, viewers.Id));
        Assert.Equal(revision, groups.Revision);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectAccessMember");
        }
        groups.ChangeMembers(Pilot, viewers.Id, [new(Wing, AccessGroupRole.Member)]);
        var folder = store.AddSharedFolder(Pilot, "Shared", "", new(admin.Id, null, null, viewers.Id));
        store.SetFolderState(Wing, folder.Id, true);
        groups.ChangeMembers(Pilot, viewers.Id, [new(Wing, AccessGroupRole.Excluded)]);
        groups.ChangeMembers(Pilot, viewers.Id, [new(Wing, AccessGroupRole.Member)]);
        Assert.Empty(store.Read(Wing).Folders);
        Assert.False(store.Folder(Wing, folder.Id).IsKnown);
        Assert.True(store.SetFolderState(Wing, folder.Id, true).Folder.IsKnown);
    }
}
