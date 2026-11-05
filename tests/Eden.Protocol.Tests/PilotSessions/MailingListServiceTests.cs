using Eden.Gameplay.Mail;
using Eden.Protocol.Mail;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.Persistence;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class MailingListServiceTests
{
    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));

    private static Dictionary<string, object?> Fields(object? value) =>
        Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(value).State);

    private static object?[] Post(long list) =>
        [new List<object?>(), list, null, new BlueUnicode("Fleet update"), new BlueUnicode("Undock soon"), false, false];

    [Fact]
    public void NativeCreateJoinWelcomeAndBroadcastUseTheActualListAndMailWireShapes()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock, liveWorld: true);
        using CourierServiceTests.Client owner = new(db, clock, 1000001);
        using CourierServiceTests.Client member = new(db, clock, 1000002);
        Assert.Same(PilotCallResult.Unsupported, owner.Call("Create", ["Fleet List", 1L, 1L, 0L], "mailingListsMgr"));
        owner.Refresh();
        member.Refresh();
        long id = Assert.IsType<long>(Reply(owner.Call("Create", [new BlueUnicode("Fleet  List"), 1L, 1L, 0L], "mailingListsMgr")));
        var joined = Assert.IsType<Dictionary<long, object?>>(Reply(owner.Call("GetJoinedLists", [], "mailingListsMgr")));
        Assert.Equal(true, Fields(joined[id])["isOwner"]);
        Assert.Equal("fleetlist", Fields(joined[id])["name"]);
        Assert.Empty(Assert.IsType<List<object?>>(Reply(owner.Call("GetWelcomeMail", [id], "mailingListsMgr"))));
        Assert.Null(
            Reply(owner.Call("SaveWelcomeMail", [id, new BlueUnicode("Welcome 学習"), new BlueUnicode("Hello")], "mailingListsMgr"))
        );
        var welcome = Fields(Assert.Single(Assert.IsType<List<object?>>(Reply(owner.Call("GetWelcomeMail", [id], "mailingListsMgr")))));
        Assert.Equal("Welcome 学習", welcome["title"]);
        Assert.Equal("Hello", welcome["body"]);
        var subscription = Fields(Reply(member.Call("Join", ["FLEETLIST"], "mailingListsMgr")));
        Assert.Equal(id, subscription["id"]);
        Assert.Equal(false, subscription["isMuted"]);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        member.Advance();
        var automatic = Assert.Single(member.Context.Notifications.OfType<MailSent>());
        var automaticArgs = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(automatic.Arguments)));
        Assert.Equal(8, automaticArgs.Length);
        Assert.Equal(id, automaticArgs[1]);
        Assert.Equal(id, automaticArgs[4]);
        Assert.Equal(32L, automaticArgs[7]);
        long posted = Assert.IsType<long>(Reply(member.Call("SendMail", Post(id), "mailMgr")));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        owner.Advance();
        var broadcast = Assert.Single(owner.Context.Notifications.OfType<MailSent>());
        var args = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(broadcast.Arguments)));
        Assert.Equal(8, args.Length);
        Assert.Equal(posted, args[0]);
        Assert.Empty(Assert.IsType<List<object?>>(args[3]));
        Assert.Equal(id, args[4]);
        var synced = Fields(Reply(owner.Call("SyncMail", [null, 0L], "mailMgr")));
        var header = Fields(Assert.Single(Assert.IsType<List<object?>>(synced["newMail"])));
        Assert.Equal(id.ToString(), BlueMarshal.Text(header["toListID"]));
        Assert.Equal("", BlueMarshal.Text(header["toCharacterIDs"]));
    }

    [Fact]
    public void RolesSelfDemotionAndKicksEmitCallbacksOnlyForKnownMemberships()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        using CourierServiceTests.Client owner = new(db, clock, 1000001);
        using CourierServiceTests.Client member = new(db, clock, 1000002);
        owner.Refresh();
        member.Refresh();
        long id = Assert.IsType<long>(Reply(owner.Call("Create", ["Roles", 1L, 1L, 0d], "mailingListsMgr")));
        Reply(member.Call("Join", ["Roles"], "mailingListsMgr"));
        List<object?> members = [MailingListTestData.Member];
        Assert.Null(Reply(owner.Call("SetMembersOperator", [id, members], "mailingListsMgr")));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        member.Advance();
        Assert.Equal("OnMailingListSetOperator", Assert.Single(member.Context.Notifications.OfType<MailingListRoleChanged>()).Name);
        member.Context.Notifications.Clear();
        Assert.Null(Reply(member.Call("SetMembersClear", [id, members], "mailingListsMgr")));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        member.Advance();
        Assert.Equal("OnMailingListSetClear", Assert.Single(member.Context.Notifications.OfType<MailingListRoleChanged>()).Name);
        Assert.Null(Reply(owner.Call("KickMembers", [id, members], "mailingListsMgr")));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        member.Advance();
        var left = Assert.Single(member.Context.Notifications.OfType<MailingListLeft>());
        Assert.Equal(new object?[] { id, MailingListTestData.Member }, left.Arguments);
        Assert.IsType<PilotCallResult.Failure>(member.Call("Join", ["Roles"], "mailingListsMgr"));
        Assert.Null(Reply(owner.Call("ClearEntityAccess", [id, MailingListTestData.Member], "mailingListsMgr")));
        Assert.Equal(id, Fields(Reply(member.Call("Join", ["Roles"], "mailingListsMgr")))["id"]);
    }

    [Fact]
    public void AccessEditorLookupReturnsNativeOwnerRowsAndSettingsValidateAllArguments()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        using CourierServiceTests.Client owner = new(db, clock, 1000001);
        owner.Refresh();
        var found = Fields(
            Assert.Single(Assert.IsType<List<object?>>(Reply(owner.Call("LookupPCOwners", ["Eden Wing", 2L], "lookupSvc"))))
        );
        Assert.Equal(MailingListTestData.Member, found["ownerID"]);
        Assert.Equal(1L, found["groupID"]);
        Assert.Equal(1373L, found["typeID"]);
        long id = Assert.IsType<long>(Reply(owner.Call("Create", ["Access", 1L, 1L, 0L], "mailingListsMgr")));
        Dictionary<long, object?> access = new() { [MailingListTestData.Member] = 0L };
        Assert.Null(Reply(owner.Call("SetEntitiesAccess", [id, access], "mailingListsMgr")));
        var settings = Fields(Reply(owner.Call("GetSettings", [id], "mailingListsMgr")));
        Assert.Equal(0L, Assert.IsType<Dictionary<long, object?>>(settings["access"])[MailingListTestData.Member]);
        Assert.Equal(0L, settings["mailCost"]);
        Assert.Null(Reply(owner.Call("SetDefaultAccess", [id, 0L, 0L, 0d], "mailingListsMgr")));
        Assert.Equal(0L, Fields(Reply(owner.Call("GetSettings", [id], "mailingListsMgr")))["defaultAccess"]);
        Assert.IsType<PilotCallResult.Failure>(owner.Call("Create", ["Paid", 1L, 1L, 100L], "mailingListsMgr"));
        Assert.IsType<PilotCallResult.Failure>(owner.Call("SetDefaultAccess", [id, 1L, 3L, 0L], "mailingListsMgr"));
        Assert.IsType<PilotCallResult.Failure>(owner.Call("SetMembersMuted", [id, new List<object?> { true }], "mailingListsMgr"));
        Assert.IsType<PilotCallResult.Failure>(
            owner.Call("GetSettings", [id], "mailingListsMgr", new() { ["pilotID"] = MailingListTestData.Member })
        );
        Assert.Null(Reply(owner.Call("Delete", [id], "mailingListsMgr")));
        Assert.Equal("Access", Fields(Reply(owner.Call("GetInfo", [id], "mailingListsMgr")))["displayName"]);
    }

    [Theory]
    [InlineData("SetMembersMuted", 0, "OnMailingListSetMuted")]
    [InlineData("SetMembersOperator", 2, "OnMailingListSetOperator")]
    [InlineData("SetMembersClear", 1, "OnMailingListSetClear")]
    public void NativeRoleAndMemberRepliesHaveThePinnedValues(string method, long role, string eventName)
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        using CourierServiceTests.Client owner = new(db, clock, 1000001);
        using CourierServiceTests.Client member = new(db, clock, 1000002);
        owner.Refresh();
        member.Refresh();
        long id = Assert.IsType<long>(Reply(owner.Call("Create", ["Flags", 1L, 1L, 0L], "mailingListsMgr")));
        Reply(member.Call("Join", ["Flags"], "mailingListsMgr"));
        Assert.Null(Reply(owner.Call(method, [id, new List<object?> { MailingListTestData.Member }], "mailingListsMgr")));
        var members = Assert.IsType<Dictionary<long, object?>>(Reply(owner.Call("GetMembers", [id], "mailingListsMgr")));
        Assert.Equal(3L, members[MailingListTestData.Owner]);
        Assert.Equal(role, members[MailingListTestData.Member]);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        member.Advance();
        var change = Assert.Single(member.Context.Notifications.OfType<MailingListRoleChanged>());
        Assert.Equal(eventName, change.Name);
        Assert.Equal(new object?[] { id }, BlueMarshal.Decode(BlueMarshal.Encode(change.Arguments)));
    }

    [Theory]
    [InlineData("MarkAsReadByList", MailStatus.Read)]
    [InlineData("MarkAsUnreadByList", MailStatus.None)]
    [InlineData("MoveToTrashByList", MailStatus.Trashed)]
    public void NativeListMailboxActionsUseOnlyTheCallersCopy(string method, MailStatus expected)
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var lists = MailingListTestData.Store(db);
        long id = lists.Create(MailingListTestData.Owner, "Select", MailingListTestData.Open, Guid.NewGuid());
        lists.Join(MailingListTestData.Member, "Select", Guid.NewGuid());
        long message = MailTestData.Store(db).Send(MailingListTestData.Owner, MailingListTestData.Broadcast(id), Guid.NewGuid());
        if (method == "MarkAsUnreadByList")
        {
            MailTestData.Store(db).Change(MailingListTestData.Member, new MailSelection.List(id), MailOperation.Read, 0, Guid.NewGuid());
        }
        using CourierServiceTests.Client member = new(db, clock, 1000002);
        member.Refresh();
        Assert.Null(Reply(member.Call(method, [id], "mailMgr")));
        Assert.Equal(expected, Assert.Single(MailTestData.Store(db).Sync(MailingListTestData.Member, null, 0).Status).Status);
        Assert.Equal(message, Assert.Single(MailTestData.Store(db).Sync(MailingListTestData.Owner, null, 0).New).Id);
        Assert.Equal(MailStatus.None, Assert.Single(MailTestData.Store(db).Sync(MailingListTestData.Owner, null, 0).Status).Status);
    }

    [Fact]
    public void UnknownExternalJoinDoesNotCauseARoleCallbackBeforeCacheRefreshAndDeletionRemovesTheMembership()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var lists = MailingListTestData.Store(db);
        using CourierServiceTests.Client observer = new(db, clock, 1000002);
        observer.Refresh();
        long id = lists.Create(MailingListTestData.Owner, "External", MailingListTestData.Open, Guid.NewGuid());
        lists.Join(MailingListTestData.Member, "External", Guid.NewGuid());
        lists.ChangeMembers(MailingListTestData.Owner, id, [MailingListTestData.Member], MailingListRole.Operator, Guid.NewGuid());
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        observer.Advance();
        Assert.Empty(observer.Context.Notifications.OfType<MailingListRoleChanged>());
        Assert.True(
            Fields(Assert.IsType<Dictionary<long, object?>>(Reply(observer.Call("GetJoinedLists", [], "mailingListsMgr")))[id])[
                "isOperator"
            ]
                is true
        );
        lists.ChangeMembers(MailingListTestData.Owner, id, [MailingListTestData.Member], MailingListRole.Muted, Guid.NewGuid());
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        observer.Advance();
        Assert.Single(observer.Context.Notifications.OfType<MailingListRoleChanged>());
        lists.Delete(MailingListTestData.Owner, id, Guid.NewGuid());
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        observer.Advance();
        Assert.Equal(id, Assert.Single(observer.Context.Notifications.OfType<MailingListDeleted>()).ListId);
    }
}
