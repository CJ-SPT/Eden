using System.IO.Compression;
using System.Text;
using Eden.Gameplay.Mail;
using Eden.Protocol.Mail;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.Persistence;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class MailServiceTests
{
    private static object?[] Draft(long recipient = MailTestData.Recipient) =>
        [new List<object?> { recipient }, null, null, new BlueUnicode("Étoile 学習"), new BlueUnicode(MailTestData.Draft.Body), null, null];

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));

    private static Dictionary<string, object?> Fields(object? value) =>
        Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(value).State);

    [Fact]
    public void NativeSyncHeadersCompressedBodyAndDeliveryWorkAcrossSessionsWithoutWriterEcho()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        using CourierServiceTests.Client sender = new(db, clock, 1000001);
        using CourierServiceTests.Client recipient = new(db, clock, 1000002);
        using CourierServiceTests.Client observer = new(db, clock, 1000001);
        Assert.Same(PilotCallResult.Unsupported, sender.Call("SyncMail", [null, 0L], "mailMgr"));
        sender.Refresh();
        recipient.Refresh();
        observer.Refresh();
        sender.Context.Notifications.Clear();
        recipient.Context.Notifications.Clear();
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(Reply(sender.Call("GetJoinedLists", [], "mailingListsMgr"))));
        var found = Assert.IsType<List<object?>>(
            Reply(
                sender.Call(
                    "QuickQuery",
                    [new BlueUnicode("Eden Wing"), new List<object?> { 2L }],
                    "search",
                    new()
                    {
                        ["exact"] = 2L,
                        ["hideNPC"] = 1L,
                        ["onlyAltName"] = false,
                    }
                )
            )
        );
        long recipientId = Assert.IsType<long>(Assert.Single(found));
        long id = Assert.IsType<long>(Reply(sender.Call("SendMail", Draft(recipientId), "mailMgr")));
        Assert.Empty(sender.Context.Notifications.OfType<MailSent>());
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        recipient.Advance();
        observer.Advance();
        var delivery = Assert.Single(recipient.Context.Notifications.OfType<MailSent>());
        Assert.Equal(id, delivery.Header.Id);
        var eventArgs = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(delivery.Arguments)));
        Assert.Equal(id, eventArgs[0]);
        Assert.Equal(new List<object?> { MailTestData.Recipient }, eventArgs[3]);
        Assert.Equal("Étoile 学習", eventArgs[6]);
        Assert.Single(observer.Context.Notifications.OfType<MailSent>());
        var snapshot = Fields(Reply(recipient.Call("SyncMail", [null, 0L], "mailMgr")));
        var header = Fields(Assert.Single(Assert.IsType<List<object?>>(snapshot["newMail"])));
        Assert.Equal(id, header["messageID"]);
        Assert.Equal(MailTestData.Recipient.ToString(), BlueMarshal.Text(header["toCharacterIDs"]));
        Assert.Null(header["toListID"]);
        Assert.Equal("Étoile 学習", header["title"]);
        Assert.Single(Assert.IsType<List<object?>>(snapshot["mailStatus"]));
        var compressed = Assert.IsType<byte[]>(Reply(recipient.Call("GetBody", [id, true], "mailMgr")));
        using ZLibStream body = new(new MemoryStream(compressed), CompressionMode.Decompress);
        using StreamReader text = new(body, Encoding.UTF8);
        Assert.Equal(MailTestData.Draft.Body, text.ReadToEnd());
        Assert.Equal(MailStatus.Read, Assert.Single(MailTestData.Store(db).Sync(MailTestData.Recipient, null, 0).Status).Status);
        recipient.Context.Notifications.Clear();
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        recipient.Advance();
        Assert.Empty(recipient.Context.Notifications.OfType<MailSent>());
    }

    [Fact]
    public void NativeLabelAndMailboxOperationsReturnNoneAndRefreshTheOtherSelectedSession()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        using CourierServiceTests.Client writer = new(db, clock, 1000002);
        using CourierServiceTests.Client observer = new(db, clock, 1000002);
        writer.Refresh();
        observer.Refresh();
        long id = MailTestData.Store(db).Send(MailTestData.Sender, MailTestData.Draft, Guid.NewGuid());
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        writer.Advance();
        observer.Advance();
        writer.Context.Notifications.Clear();
        observer.Context.Notifications.Clear();
        long label = Assert.IsType<long>(Reply(writer.Call("CreateLabel", [new BlueUnicode("Mining"), 7L], "mailMgr")));
        List<object?> ids = [id];
        Assert.Null(Reply(writer.Call("AssignLabels", [ids, label], "mailMgr")));
        Assert.Null(Reply(writer.Call("MarkAsRead", [ids], "mailMgr")));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        observer.Advance();
        Assert.Single(observer.Context.Notifications.OfType<MailLabelChanged>());
        Assert.Equal(2, observer.Context.Notifications.OfType<MailStateChanged>().Count());
        Assert.Equal(label | 1, observer.Context.Notifications.OfType<MailStateChanged>().Last().State.Labels);
        Assert.Null(Reply(writer.Call("MoveToTrash", [ids], "mailMgr")));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        observer.Advance();
        Assert.Single(observer.Context.Notifications.OfType<MailTrashed>());
        Assert.Null(Reply(writer.Call("MoveAllFromTrash", [], "mailMgr")));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        observer.Advance();
        Assert.Null(Reply(writer.Call("MoveToTrashByLabel", [label], "mailMgr")));
        Assert.Null(Reply(writer.Call("EmptyTrash", [], "mailMgr")));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        observer.Advance();
        Assert.Single(observer.Context.Notifications.OfType<MailRestored>());
        Assert.Single(observer.Context.Notifications.OfType<MailDeleted>());
        Assert.Empty(MailTestData.Store(db).Sync(MailTestData.Recipient, null, 0).Status);
        Assert.Single(MailTestData.Store(db).Sync(MailTestData.Sender, null, 0).Status);
    }

    [Fact]
    public void GroupSendsMalformedSelectionsSpoofedOwnersAndPaidApprovalsCannotMutateMail()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        client.Context.Select(db.Store.FindPilot(1000001)!);
        var grouped = Draft();
        grouped[1] = 123L;
        Assert.IsType<PilotCallResult.Failure>(client.Call("SendMail", grouped, "mailMgr"));
        grouped[1] = null;
        grouped[2] = 1000044L;
        Assert.IsType<PilotCallResult.Failure>(client.Call("SendMail", grouped, "mailMgr"));
        Assert.IsType<PilotCallResult.Failure>(client.Call("SendMail", Draft(), "mailMgr", new() { ["pilotID"] = MailTestData.Recipient }));
        Assert.IsType<PilotCallResult.Failure>(client.Call("SendMail", Draft(), "mailMgr", new() { ["approvedCost"] = 1L }));
        Assert.IsType<PilotCallResult.Failure>(client.Call("AssignLabels", [new List<object?> { -1L }, 16L], "mailMgr"));
        Assert.Empty(MailTestData.Store(db).Sync(MailTestData.Sender, null, 0).Status);
        Assert.IsType<long>(Reply(client.Call("SendMail", Draft(), "mailMgr", new() { ["approvedCost"] = 0L })));
    }

    [Theory]
    [InlineData("MarkAsRead")]
    [InlineData("MarkAllAsRead")]
    [InlineData("MarkAsReadByLabel")]
    [InlineData("MarkAsUnread")]
    [InlineData("MarkAllAsUnread")]
    [InlineData("MarkAsUnreadByLabel")]
    [InlineData("MoveToTrash")]
    [InlineData("MoveAllToTrash")]
    [InlineData("MoveToTrashByLabel")]
    [InlineData("MoveFromTrash")]
    [InlineData("MoveAllFromTrash")]
    [InlineData("DeleteMail")]
    [InlineData("EmptyTrash")]
    [InlineData("AssignLabels")]
    [InlineData("RemoveLabels")]
    [InlineData("ReplaceLabels")]
    public void NativeBulkActionsPersistTheirExpectedFlagsAndLabels(string method)
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var store = MailTestData.Store(db);
        long id = store.Send(MailTestData.Sender, MailTestData.Draft, Guid.NewGuid());
        long label = store.CreateLabel(MailTestData.Recipient, "Bulk", null, Guid.NewGuid());
        var selection = new MailSelection.Ids([id]);
        if (method != "AssignLabels")
        {
            store.Change(MailTestData.Recipient, selection, MailOperation.AssignLabels, label, Guid.NewGuid());
        }
        if (method.Contains("Unread", StringComparison.Ordinal))
        {
            store.Change(MailTestData.Recipient, selection, MailOperation.Read, 0, Guid.NewGuid());
        }
        if (method is "MoveFromTrash" or "MoveAllFromTrash" or "DeleteMail" or "EmptyTrash")
        {
            store.Change(MailTestData.Recipient, selection, MailOperation.Trash, 0, Guid.NewGuid());
        }
        using CourierServiceTests.Client client = new(db, clock, 1000002);
        client.Refresh();
        object?[] args = method switch
        {
            "AssignLabels" or "RemoveLabels" or "ReplaceLabels" => [new List<object?> { id }, label],
            _ when method.EndsWith("ByLabel", StringComparison.Ordinal) => [label],
            _ when method.Contains("All", StringComparison.Ordinal) || method == "EmptyTrash" => [],
            _ => [new List<object?> { id }],
        };
        Assert.Null(Reply(client.Call(method, args, "mailMgr")));
        var rows = store.Sync(MailTestData.Recipient, null, 0).Status;
        if (method is "DeleteMail" or "EmptyTrash")
        {
            Assert.Empty(rows);
        }
        else
        {
            var expectedStatus = method switch
            {
                "MarkAsRead" or "MarkAllAsRead" or "MarkAsReadByLabel" => MailStatus.Read,
                "MoveToTrash" or "MoveAllToTrash" or "MoveToTrashByLabel" => MailStatus.Trashed,
                _ => MailStatus.None,
            };
            var row = Assert.Single(rows);
            Assert.Equal(expectedStatus, row.Status);
            Assert.Equal(
                method == "ReplaceLabels" ? label
                    : method == "RemoveLabels" ? 1
                    : label | 1,
                row.Labels
            );
        }
        Assert.Equal(MailStatus.Read, Assert.Single(store.Sync(MailTestData.Sender, null, 0).Status).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeReplyAndForwardArgumentsSetOnlyTheCallersSourceFlag(bool forward)
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var store = MailTestData.Store(db);
        long source = store.Send(MailTestData.Sender, MailTestData.Draft, Guid.NewGuid());
        using CourierServiceTests.Client client = new(db, clock, 1000002);
        client.Refresh();
        var args = Draft(MailTestData.Sender);
        args[forward ? 6 : 5] = source;
        Assert.IsType<long>(Reply(client.Call("SendMail", args, "mailMgr")));
        var expected = forward ? MailStatus.Forwarded : MailStatus.Replied;
        Assert.Equal(expected, store.Sync(MailTestData.Recipient, null, 0).Status.Single(s => s.Id == source).Status);
        Assert.Equal(MailStatus.Read, store.Sync(MailTestData.Sender, null, 0).Status.Single(s => s.Id == source).Status);
    }
}
