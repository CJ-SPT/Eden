using Eden.Gameplay.Characters;
using Eden.Gameplay.Notifications;
using Eden.Protocol.Notifications;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Mail;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class InboxServiceTests
{
    private const long Sender = 90000001;
    private const long Recipient = 90000002;

    [Fact]
    public void NativeContactNotificationsHistoryCutoffAndReadDeleteOperationsUseSelectedPilotState()
    {
        using Fixture f = new();
        Assert.Same(PilotCallResult.Unsupported, f.Guest.Call("notificationMgr", "GetAllNotifications", []));
        f.Select();
        Assert.Null(Reply(f.Owner.Call("charMgr", "AddContact", [Recipient, 5d, true, true, new BlueUnicode("Étoile 学習 🚀")])));
        var row = Assert.Single(Rows(f.Guest.Call("notificationMgr", "GetAllNotifications", [], new() { ["fromID"] = null })));
        long first = Assert.IsType<long>(row["notificationID"]);
        Assert.Equal(89L, row["typeID"]);
        Assert.Equal(Sender, row["senderID"]);
        Assert.Equal(Recipient, row["receiverID"]);
        Assert.Equal(false, row["processed"]);
        Assert.Equal(f.Clock.Now, row["created"]);
        var data = BlueRead.Fields(row["data"]);
        Assert.Equal(5d, data["level"]);
        Assert.Equal("Étoile 学習 🚀", data["message"]);
        Assert.Empty(Rows(f.Guest.Call("notificationMgr", "GetAllNotifications", [], new() { ["fromID"] = first })));
        Assert.Single(Rows(f.Guest.Call("notificationMgr", "GetByGroupID", [9L])));
        Assert.Single(Rows(f.Guest.Call("notificationMgr", "GetUnprocessed", [])));
        Assert.Null(Reply(f.Guest.Call("notificationMgr", "MarkAsProcessed", [new List<object?> { first }])));
        Assert.Empty(Rows(f.Guest.Call("notificationMgr", "GetUnprocessed", [])));
        Assert.Null(Reply(f.Owner.Call("charMgr", "EditContact", [Recipient, -5d, false, true, "Changed"])));
        f.Guest.Advance();
        var received = Assert.Single(f.Guest.Context.Notifications.OfType<InboxNotificationReceived>());
        var args = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(received.Arguments)), 5);
        Assert.Equal(90L, args[1]);
        Assert.Equal(Sender, args[2]);
        Assert.Equal(f.Clock.Now, args[3]);
        Assert.Equal(-5d, BlueRead.Fields(args[4])["level"]);
        Assert.Equal("Changed", BlueRead.Fields(args[4])["message"]);
        Assert.Null(Reply(f.Guest.Call("notificationMgr", "MarkGroupAsProcessed", [9L])));
        Assert.Null(Reply(f.Guest.Call("notificationMgr", "MarkAllAsProcessed", [])));
        Assert.Empty(Rows(f.Guest.Call("notificationMgr", "GetByGroupID", [0L])));
        Assert.Null(Reply(f.Guest.Call("notificationMgr", "DeleteNotifications", [new List<object?> { first }])));
        Assert.Single(Rows(f.Guest.Call("notificationMgr", "GetAllNotifications", [])));
        Assert.Null(Reply(f.Guest.Call("notificationMgr", "DeleteGroupNotifications", [9L])));
        Assert.Empty(Rows(f.Guest.Call("notificationMgr", "GetAllNotifications", [])));
        Assert.Null(Reply(f.Owner.Call("charMgr", "EditContact", [Recipient, 0d, false, true, null])));
        Assert.Null(Reply(f.Guest.Call("notificationMgr", "DeleteAllNotifications", [])));
        Assert.Empty(Rows(f.Guest.Call("notificationMgr", "GetAllNotifications", [])));
    }

    [Fact]
    public void FetchReadOrDeleteBeforeTheTickCannotDuplicateOrResurrectAnUnreadPush()
    {
        using Fixture f = new();
        f.Select();
        var contacts = f.Db.Services.GetRequiredService<IPilotContacts>();
        var inbox = f.Db.Services.GetRequiredService<IPilotInbox>();
        contacts.Add(Sender, Recipient, 5, false, "Fetched before tick");
        Assert.Single(Rows(f.Guest.Call("notificationMgr", "GetUnprocessed", [])));
        Assert.DoesNotContain(f.Guest.Context.Notifications, n => n is InboxNotificationReceived);
        contacts.Edit(Sender, Recipient, 0, false, "Read before tick");
        inbox.MarkRead(Recipient, [inbox.Read(Recipient, new())[0].Id]);
        f.Guest.Advance();
        Assert.DoesNotContain(f.Guest.Context.Notifications, n => n is InboxNotificationReceived);
        contacts.Edit(Sender, Recipient, -5, false, "Deleted before tick");
        inbox.Delete(Recipient, [inbox.Read(Recipient, new())[0].Id]);
        f.Guest.Advance();
        Assert.DoesNotContain(f.Guest.Context.Notifications, n => n is InboxNotificationReceived);
        contacts.Edit(Sender, Recipient, 10, false, "Deliver once");
        // A filtered fetch must not swallow notifications absent from its reply.
        Assert.Empty(Rows(f.Guest.Call("notificationMgr", "GetByGroupID", [11L])));
        var delivered = Assert.Single(f.Guest.Context.Notifications.OfType<InboxNotificationReceived>());
        Assert.Equal("Deliver once", delivered.Notification.Content.Message);
        f.Guest.Context.Notifications.Clear();
        f.Guest.Advance();
        f.Guest.Advance();
        Assert.DoesNotContain(f.Guest.Context.Notifications, n => n is InboxNotificationReceived);
        f.Guest.Inbox.Reset();
        Assert.False(f.Guest.Inbox.IsMonitoring);
        f.Guest.Inbox.Enter(f.Guest.Context);
        f.Guest.Advance();
        Assert.DoesNotContain(f.Guest.Context.Notifications, n => n is InboxNotificationReceived);
        Assert.Empty(inbox.Read(Sender, new()));
    }

    [Fact]
    public void ADeliveryBatchContinuesOnTheNextTickWithoutAnotherPublication()
    {
        using Fixture f = new();
        f.Select();
        using (WorldDbContext world = new(f.Db.Path))
        {
            for (int i = 0; i <= InboxRules.DeliveryBatchSize; i++)
            {
                world.Add(
                    new SavedInboxNotification
                    {
                        Type = InboxNotificationType.ContactAdded,
                        SenderId = Sender,
                        ReceiverId = Recipient,
                        Created = f.Clock.Now,
                        ContactLevel = 5,
                        Message = $"Message {i}",
                    }
                );
            }
            world.SaveChanges();
        }
        f.Db.Services.GetRequiredService<InboxChangeSignal>().Publish();
        f.Guest.Advance();
        var first = f.Guest.Context.Notifications.OfType<InboxNotificationReceived>().ToArray();
        Assert.Equal(InboxRules.DeliveryBatchSize, first.Length);
        Assert.Equal(first.OrderBy(n => n.Notification.Id), first);
        f.Guest.Context.Notifications.Clear();
        f.Guest.Advance();
        Assert.Equal(
            $"Message {InboxRules.DeliveryBatchSize}",
            Assert.Single(f.Guest.Context.Notifications.OfType<InboxNotificationReceived>()).Notification.Content.Message
        );
        f.Guest.Context.Notifications.Clear();
        f.Guest.Advance();
        Assert.DoesNotContain(f.Guest.Context.Notifications, n => n is InboxNotificationReceived);
        f.Owner.Advance();
        Assert.DoesNotContain(f.Owner.Context.Notifications, n => n is InboxNotificationReceived);
        var inbox = f.Db.Services.GetRequiredService<IPilotInbox>();
        var selected = inbox.Read(Recipient, new()).Take(InboxRules.MaximumUpdated).Select(n => n.Id).ToArray();
        inbox.MarkRead(Recipient, selected);
        Assert.Equal(InboxRules.MaximumUpdated, inbox.Read(Recipient, new()).Count(n => n.Processed));
        inbox.Delete(Recipient, selected);
        Assert.Equal(InboxRules.DeliveryBatchSize + 1 - InboxRules.MaximumUpdated, inbox.Read(Recipient, new()).Count);
    }

    [Fact]
    public void InvalidAndForgedWireRequestsCannotReadOrChangeAnotherPilotsInbox()
    {
        using Fixture f = new();
        f.Select();
        var contacts = f.Db.Services.GetRequiredService<IPilotContacts>();
        contacts.Add(Sender, Recipient, 0, false, "To guest");
        contacts.Add(Recipient, Sender, 0, false, "To owner");
        var inbox = f.Db.Services.GetRequiredService<IPilotInbox>();
        long owned = inbox.Read(Recipient, new())[0].Id;
        long foreign = inbox.Read(Sender, new())[0].Id;
        foreach (
            var (method, args) in new (string, object?[])[]
            {
                ("GetAllNotifications", [-1L]),
                ("GetAllNotifications", [1L, 2L]),
                ("GetUnprocessed", [Sender]),
                ("GetByGroupID", [13L]),
                ("GetByGroupID", [long.MaxValue]),
                ("MarkAsProcessed", [new List<object?> { owned, foreign }]),
                ("DeleteNotifications", [new List<object?> { owned, foreign }]),
                ("DeleteNotifications", [new object?[] { owned }]),
                ("MarkAllAsProcessed", [Sender]),
                ("DeleteAllNotifications", [Sender]),
                ("MarkGroupAsProcessed", [13L]),
                ("DeleteGroupNotifications", [double.NaN]),
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(f.Guest.Call("notificationMgr", method, args));
        }
        Assert.IsType<PilotCallResult.Failure>(f.Guest.Call("notificationMgr", "GetAllNotifications", [], new() { ["pilotID"] = Sender }));
        Assert.IsType<PilotCallResult.Failure>(f.Guest.Call("notificationMgr", "GetAllNotifications", [owned], new() { ["fromID"] = 0L }));
        Assert.Same(PilotCallResult.Unsupported, f.Guest.Call("notificationMgr", "CreateNotification", []));
        Assert.False(Assert.Single(inbox.Read(Recipient, new())).Processed);
        Assert.False(Assert.Single(inbox.Read(Sender, new())).Processed);
    }

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));

    private static Dictionary<string, object?>[] Rows(PilotCallResult result) =>
        Assert.IsType<List<object?>>(Reply(result)).Select(value => BlueRead.Fields(Assert.IsType<BlueInstance>(value).State)).ToArray();

    private sealed class Fixture : IDisposable
    {
        public TestClock Clock { get; } = new();
        public TestWorld Db { get; }
        public Client Owner { get; }
        public Client Guest { get; }

        public Fixture()
        {
            Db = new(clock: Clock);
            Owner = new(Db, Clock, 1000001);
            Guest = new(Db, Clock, 1000002);
        }

        public void Select()
        {
            Owner.Select();
            Guest.Select();
            Owner.Context.Notifications.Clear();
            Guest.Context.Notifications.Clear();
        }

        public void Dispose()
        {
            Owner.Dispose();
            Guest.Dispose();
            Db.Dispose();
        }
    }

    private sealed class Client(TestWorld db, TestClock clock, long account) : IDisposable
    {
        private readonly IServiceScope _scope = db.Services.CreateScope();
        public PilotCallContext Context { get; } =
            new(
                account,
                db.Store.ReadAccount(account),
                db.Store.ListPilots(account),
                p => new(db.Store, p, clock, new(), db.Objects),
                account,
                SignedSettings.Empty
            );
        public InboxHandler Inbox => _scope.ServiceProvider.GetRequiredService<InboxHandler>();

        public void Select()
        {
            Context.Select(db.Store.FindPilot(account)!);
            _scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().Enter(Context);
        }

        public void Advance() => _scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().Advance(Context);

        public PilotCallResult Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
        {
            var result = _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);
            if (Context.Selected)
            {
                Advance();
            }
            return result;
        }

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
        }
    }
}
