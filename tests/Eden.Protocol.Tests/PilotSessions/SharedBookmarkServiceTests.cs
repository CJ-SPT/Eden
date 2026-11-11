using Eden.Gameplay.AccessGroups;
using Eden.Gameplay.Bookmarks;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.AccessGroups;
using Eden.Protocol.Bookmarks;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class SharedBookmarkServiceTests
{
    private const long Pilot = 90000001;
    private const long Wing = 90000002;

    [Fact]
    public void NativeAccessListFolderAndMembershipCallsPreserveShapesAndRejectForgedPermissions()
    {
        using TestWorld db = new();
        using CorporationTestClient actor = new(db, 1000001);
        using CorporationTestClient reader = new(db, 1000002);
        var created = Fields(Reply(actor, "ownerGroupManager", "Create", ["Fleet", "Description"]));
        long group = Assert.IsType<long>(created["groupID"]);
        Assert.Equal(Pilot, created["creatorID"]);
        Assert.Equal(2L, created["membershipType"]);
        Assert.Equal(Pilot, Assert.Single(Assert.IsType<List<object?>>(created["admins"])));
        Assert.Empty(
            Assert.IsType<List<object?>>(
                Reply(actor, "ownerGroupManager", "AddMembers", [group, new Dictionary<long, object?> { [Wing] = 0L }])
            )
        );
        var folder = Fields(Reply(actor, "accessGroupBookmarkMgr", "AddFolder", [false, "Fleet", "", group, null, null, group]));
        long folderId = Assert.IsType<long>(folder["folderID"]);
        Assert.Equal(false, folder["isPersonal"]);
        Assert.Equal(5L, folder["accessLevel"]);
        var info = Fields(Reply(reader, "accessGroupBookmarkMgr", "GetFolderInfo", [folderId]));
        Assert.Equal(5L, info["accessLevel"]);
        // Folder administration is granted by membership in the admin list. It does
        // not grant permission to inspect or edit the list's membership itself.
        Assert.Null(Reply(reader, "ownerGroupManager", "GetGroup", [group]));
        Assert.IsType<PilotCallResult.Failure>(reader.CallService("ownerGroupManager", "GetMembers", [group]));
        Assert.IsType<PilotCallResult.Failure>(
            reader.CallService("ownerGroupManager", "AddMembers", [group, new Dictionary<long, object?> { [Wing] = 2L }])
        );
        var connected = BlueRead.Tuple(Reply(reader, "accessGroupBookmarkMgr", "AddToKnownFolders", [folderId, true]), 3);
        Assert.IsType<List<object?>>(connected[1]);
        Assert.Equal(folderId, Fields(connected[0])["folderID"]);
        Assert.Equal(
            (long)reader.Context.Pilot.CorpId,
            Fields(Assert.Single(Assert.IsType<List<object?>>(Reply(reader, "ownerGroupManager", "GetMyGroups", []))))["creatorID"]
        );
        Reply(actor, "ownerGroupManager", "UpdateMembershipTypes", [group, new List<object?> { Wing }, 1L]);
        Assert.Equal(1L, Fields(Reply(reader, "ownerGroupManager", "GetGroup", [group]))["membershipType"]);
        Assert.Equal(2, Assert.IsType<List<object?>>(Reply(reader, "ownerGroupManager", "GetMembers", [group])).Count);
        Assert.IsType<Dictionary<long, object?>>(
            Reply(reader, "ownerGroupManager", "GetMembersForMultipleGroups", [new List<object?> { group }])
        );
        Reply(actor, "ownerGroupManager", "DeleteMembers", [group, new List<object?> { Wing }]);
        Assert.IsType<PilotCallResult.Failure>(reader.CallService("accessGroupBookmarkMgr", "GetFolderInfo", [folderId]));
        var logs = db.Services.GetRequiredService<IPilotAccessGroups>().Logs(Pilot, group);
        Assert.Contains(logs, l => l.Change == AccessGroupChange.MemberAdded && l.OwnerId == Wing && l.NewRole == AccessGroupRole.Member);
        Assert.Contains(
            logs,
            l => l.Change == AccessGroupChange.MemberRemoved && l.OwnerId == Wing && l.OldRole == AccessGroupRole.Manager
        );
        // The inert decoder intentionally does not decode outbound DBRow reductions.
        var logCall = Assert.IsType<PilotCallResult.Success>(actor.CallService("ownerGroupManager", "GetGroupLogs", [group]));
        Assert.NotEmpty(Assert.IsType<BlueList<AccessGroupLogReply>>(logCall.Reply).Items);
    }

    [Fact]
    public void PrimedSharedFolderUpdatesIncludeNativeRowsSubfoldersExpiryAndAccessRevocationWithZeroIdleSql()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        using CorporationTestClient actor = new(db, 1000001);
        using CorporationTestClient reader = new(db, 1000002);
        var groups = db.Services.GetRequiredService<IPilotAccessGroups>();
        var store = db.Services.GetRequiredService<IPilotBookmarks>();
        var admin = groups.Create(Pilot, "Admins", "");
        var users = groups.Create(Pilot, "Viewers", "");
        groups.ChangeMembers(Pilot, users.Id, [new(Wing, AccessGroupRole.Member)]);
        var folder = store.AddSharedFolder(Pilot, "Fleet", "", new(admin.Id, null, null, users.Id));
        store.SetFolderState(Wing, folder.Id, true);
        Reply(reader, "accessGroupBookmarkMgr", "GetMyActiveBookmarks", []);
        var monitor = reader.Services.GetRequiredService<BookmarkMonitor>();
        reader.Context.Notifications.Clear();
        AssertIdle();
        var sub = store.AddSubfolder(Pilot, folder.Id, "Sites");
        var bookmark = store.CreateStatic(Pilot, 60003760, folder.Id, sub.Id, new("Home", ""), BookmarkExpiry.FourHours);
        monitor.Advance(reader.Context);
        var added = Assert.IsType<BookmarkUpdatesNotification>(Assert.Single(reader.Context.Notifications));
        var wire = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(added.Arguments)), 1);
        var updates = Assert.IsType<List<object?>>(wire[0]);
        var bmUpdate = BlueRead.Tuple(updates[0], 3);
        Assert.Equal(folder.Id, BlueRead.Tuple(bmUpdate[0], 1)[0]);
        Assert.Equal("bookmarksAdded", BlueMarshal.Text(bmUpdate[1]));
        Assert.Equal(bookmark.Id, Fields(Assert.Single(Assert.IsType<List<object?>>(bmUpdate[2])))["bookmarkID"]);
        Assert.Equal("subfolderAdded", BlueMarshal.Text(BlueRead.Tuple(updates[1], 3)[1]));
        reader.Context.Notifications.Clear();
        AssertIdle();
        store.UpdateSharedFolder(Pilot, folder.Id, "Renamed", "Notes", new(admin.Id, null, users.Id, null));
        store.UpdateSubfolder(Pilot, folder.Id, sub.Id, "Travel");
        store.Update(Pilot, new(bookmark.Id, folder.Id, folder.Id, sub.Id, new("Changed", ""), false));
        monitor.Advance(reader.Context);
        Assert.Contains(reader.Context.Notifications, n => n is BookmarkAccessChangedNotification);
        var changed = Assert.Single(reader.Context.Notifications.OfType<BookmarkUpdatesNotification>());
        Assert.Contains(changed.Updates, u => u.Kind == "bookmarksUpdated");
        Assert.Contains(changed.Updates, u => u.Kind == "folderUpdated");
        Assert.Contains(changed.Updates, u => u.Kind == "subfolderUpdated");
        reader.Context.Notifications.Clear();
        clock.AdvanceWallTime(TimeSpan.FromHours(4));
        monitor.Advance(reader.Context);
        Assert.Equal(
            "bookmarksRemoved",
            Assert.Single(Assert.IsType<BookmarkUpdatesNotification>(Assert.Single(reader.Context.Notifications)).Updates).Kind
        );
        reader.Context.Notifications.Clear();
        groups.ChangeMembers(Pilot, users.Id, [new(Wing, AccessGroupRole.Excluded)]);
        monitor.Advance(reader.Context);
        Assert.Equal(
            folder.Id,
            Assert.Single(Assert.IsType<BookmarkAccessLostNotification>(Assert.Single(reader.Context.Notifications)).FolderIds)
        );
        reader.Context.Notifications.Clear();
        AssertIdle();

        void AssertIdle()
        {
            using var measured = SessionPerformance.Begin("shared bookmark idle", _ => { }, force: true)!;
            for (int i = 0; i < 10; i++)
            {
                monitor.Advance(reader.Context);
            }
            Assert.Equal(0, measured.Commands);
            Assert.Equal(0, measured.Connections);
            Assert.Equal(0, measured.FittingCalculations);
            Assert.Empty(reader.Context.Notifications);
        }
    }

    [Fact]
    public void AccessListMonitorInvalidatesNamesRolesAndDeletionWithoutIdleQueries()
    {
        using TestWorld db = new();
        using CorporationTestClient client = new(db, 1000002);
        var groups = db.Services.GetRequiredService<IPilotAccessGroups>();
        var group = groups.Create(Pilot, "Team", "");
        groups.ChangeMembers(Pilot, group.Id, [new(Wing, AccessGroupRole.Manager)]);
        Reply(client, "ownerGroupManager", "GetMyGroups", []);
        var monitor = client.Services.GetRequiredService<AccessGroupMonitor>();
        using (var measured = SessionPerformance.Begin("access list idle", _ => { }, force: true)!)
        {
            for (int i = 0; i < 10; i++)
            {
                monitor.Advance(client.Context);
            }
            Assert.Equal(0, measured.Commands);
            Assert.Equal(0, measured.Connections);
        }
        groups.Update(Pilot, group.Id, "Renamed", "Changed");
        monitor.Advance(client.Context);
        var updated = Assert.IsType<AccessGroupsUpdatedNotification>(Assert.Single(client.Context.Notifications));
        var map = Assert.IsType<Dictionary<long, object?>>(BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(updated.Arguments)), 1)[0]);
        Assert.Equal(new object?[] { 5L, 6L }, Assert.IsType<List<object?>>(map[group.Id]));
        client.Context.Notifications.Clear();
        groups.Delete(Pilot, group.Id);
        monitor.Advance(client.Context);
        Assert.Contains(
            (int)AccessGroupChange.Deleted,
            Assert.IsType<AccessGroupsUpdatedNotification>(Assert.Single(client.Context.Notifications)).Changes[group.Id].Items
        );
    }

    [Fact]
    public void CorporationGrantsUseSavedMembershipAndLeavingRevokesPrimedFoldersBeforeTheSessionRefreshes()
    {
        using TestWorld db = new();
        int corporation = CorporationAdministrationTests.CreateAndJoin(db);
        using CorporationTestClient client = new(db, 1000002);
        var groups = db.Services.GetRequiredService<IPilotAccessGroups>();
        var store = db.Services.GetRequiredService<IPilotBookmarks>();
        var admin = groups.Create(Pilot, "Admins", "");
        var corp = groups.Create(Pilot, "Corporation", "");
        groups.ChangeMembers(Pilot, corp.Id, [new(corporation, AccessGroupRole.Member)]);
        Assert.Throws<InvalidDataException>(() => groups.ChangeMembers(Pilot, corp.Id, [new(corporation, AccessGroupRole.Admin)]));
        var folder = store.AddSharedFolder(Pilot, "Corporation", "", new(admin.Id, null, null, corp.Id));
        store.SetFolderState(Wing, folder.Id, true);
        Reply(client, "accessGroupBookmarkMgr", "GetMyActiveBookmarks", []);
        var monitor = client.Services.GetRequiredService<BookmarkMonitor>();
        db.Services.GetRequiredService<ICorporationMembership>().Remove(Wing, Wing);
        Assert.Equal(corporation, client.Context.Pilot.CorpId);
        monitor.Advance(client.Context);
        Assert.Equal(
            folder.Id,
            Assert.Single(Assert.IsType<BookmarkAccessLostNotification>(Assert.Single(client.Context.Notifications)).FolderIds)
        );
        Assert.Throws<InvalidDataException>(() => store.Folder(Wing, folder.Id));
        Assert.Empty(store.Read(Wing).Folders);
        Assert.True(store.Folder(Pilot, folder.Id).IsKnown);
    }

    private static object? Reply(CorporationTestClient client, string service, string method, object?[] args) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(client.CallService(service, method, args)).Reply));

    private static Dictionary<string, object?> Fields(object? value) => BlueRead.Fields(Assert.IsType<BlueInstance>(value).State);
}
