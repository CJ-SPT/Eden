using Eden.Data.World.Characters;
using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Gameplay.Notifications;
using Eden.Protocol.Notifications;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class ContactNotificationCostTests
{
    private const long Sender = MailTestData.Sender;
    private const long Recipient = MailTestData.Recipient;
    private const int OtherCorporation = 1000035;

    [Fact]
    public async Task NativePaidContactAddCommitsNotificationWalletAndProgressOnceAcrossReopen()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock, liveWorld: true);
        var live = Prepare(db, 2_500);
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        var inbox = db.Services.GetRequiredService<IPilotInbox>();
        long notificationId;
        using (CourierServiceTests.Client sender = new(db, clock, 1000001))
        using (CourierServiceTests.Client recipient = new(db, clock, 1000002))
        {
            sender.Refresh();
            recipient.Refresh();
            Assert.Null(Reply(recipient.Call("SetContactCost", [40L], "account")));
            Quote(Change(sender, add: true, standing: 5), 40);
            Assert.IsType<PilotCallResult.Failure>(Change(sender, add: true, standing: 5, approval: 40));
            Assert.Empty(contacts.Read(Sender).Addresses);
            Assert.Empty(inbox.Read(Recipient, new()));
            Assert.Equal(0, TaskCount(live));
            Assert.Equal(25d, Reply(sender.Call("GetCashBalance", [], "account")));
            Assert.Empty(Journal(db.Services));

            using (var action = live.Economy.Begin(new(pilots: [Sender])))
            {
                action.Set<PilotWallet>().Find(Sender)!.Cents = 10_000;
                action.Commit();
            }
            Assert.Null(Reply(Change(sender, add: true, standing: 5, approval: 40)));
            Assert.IsType<PilotCallResult.Failure>(Change(sender, add: true, standing: 5, approval: 40));
            Assert.Equal(1, TaskCount(live));
            Assert.Equal(new PersonalContact(Recipient, 5, true, 0), Assert.Single(contacts.Read(Sender).Addresses));
            recipient.Advance();
            var delivered = Assert.Single(recipient.Context.Notifications.OfType<InboxNotificationReceived>()).Notification;
            Assert.Equal(InboxNotificationType.ContactAdded, delivered.Type);
            Assert.Equal(new ContactNotificationContent(5, "Étoile 学習"), delivered.Content);
            notificationId = delivered.Id;
            Assert.Equal(60d, Reply(sender.Call("GetCashBalance", [], "account")));
        }
        await live.FlushAsync();
        db.Services.Dispose();
        using var reopened = ServerServices.Build(new(db.Path), clock, liveWorld: true);
        Assert.Equal(notificationId, Assert.Single(reopened.GetRequiredService<IPilotInbox>().Read(Recipient, new())).Id);
        Assert.Equal(1, TaskCount(reopened.GetRequiredService<LiveWorld>()));
        Assert.Equal(5, Assert.Single(reopened.GetRequiredService<IPilotContacts>().Read(Sender).Addresses).Standing);
        var payment = Assert.Single(Journal(reopened));
        Assert.Equal(WalletEntryType.Cspa, payment.EntryType);
        Assert.Equal(Recipient, payment.ReferenceId);
        Assert.Equal(-4_000L, payment.AmountCents);
        Assert.Equal(6_000L, payment.BalanceCents);
    }

    [Fact]
    public void NativeContactEditRequotesAndPreservesTheLatestStandingOnRejection()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock, liveWorld: true);
        Prepare(db, 10_000);
        using CourierServiceTests.Client sender = new(db, clock, 1000001);
        using CourierServiceTests.Client recipient = new(db, clock, 1000002);
        sender.Refresh();
        recipient.Refresh();
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        var inbox = db.Services.GetRequiredService<IPilotInbox>();
        Assert.Null(Reply(recipient.Call("SetContactCost", [20L], "account")));
        Assert.IsType<PilotCallResult.Failure>(Change(sender, add: true, standing: 0, notify: false, approval: 20));
        Assert.Null(Reply(Change(sender, add: true, standing: 0, notify: false)));
        Quote(Change(sender, add: false, standing: 10), 20);
        Assert.Null(Reply(Change(sender, add: false, standing: -5, notify: false)));
        Assert.Null(Reply(recipient.Call("SetContactCost", [45L], "account")));
        Quote(Change(sender, add: false, standing: 10, approval: 20), 45);
        Assert.Null(Reply(recipient.Call("BlockOwners", [new List<object?> { Sender }], "charMgr")));
        Assert.IsType<PilotCallResult.Failure>(Change(sender, add: false, standing: 10, approval: 45));
        Assert.Equal(-5, Assert.Single(contacts.Read(Sender).Addresses).Standing);
        Assert.Equal(100d, Reply(sender.Call("GetCashBalance", [], "account")));
        Assert.Empty(inbox.Read(Recipient, new()));
        Assert.Empty(Journal(db.Services));

        Assert.Null(Reply(recipient.Call("UnblockOwners", [new List<object?> { Sender }], "charMgr")));
        Assert.Null(Reply(Change(sender, add: false, standing: 10, approval: 45)));
        Assert.Equal(10, Assert.Single(contacts.Read(Sender).Addresses).Standing);
        Assert.Equal(InboxNotificationType.ContactEdited, Assert.Single(inbox.Read(Recipient, new())).Type);
        Assert.Equal(-4_500L, Assert.Single(Journal(db.Services)).AmountCents);

        Assert.Null(Reply(recipient.Call("SetContactCost", [null], "account")));
        Assert.IsType<PilotCallResult.Failure>(Change(sender, add: false, standing: -10));
        contacts.Add(Recipient, Sender, 0, false);
        Assert.Null(Reply(Change(sender, add: false, standing: -10)));
        Assert.Equal(-10, Assert.Single(contacts.Read(Sender).Addresses).Standing);
        Assert.Equal(2, inbox.Read(Recipient, new()).Count);
        Assert.Single(Journal(db.Services));
    }

    private static LiveWorld Prepare(TestWorld db, long balance)
    {
        var live = db.Services.GetRequiredService<LiveWorld>();
        using var world = live.Economy.Begin(new(pilots: [Sender, Recipient]));
        world.Pilots.Find(Recipient)!.CorporationId = OtherCorporation;
        world.Add(new PilotWallet { PilotId = Sender, Cents = balance });
        world.Commit();
        return live;
    }

    private static long TaskCount(LiveWorld live) =>
        live.Economy.Inspect(world => world.Set<SavedTaskCounter>().Find(Sender, TaskEvents.ContactAdded)?.Count ?? 0);

    private static IReadOnlyList<CorporationWalletEntry> Journal(IServiceProvider services) =>
        services.GetRequiredService<ICorporationWallet>().Journal(Sender, false, (int)WalletAccountKey.Cash);

    private static PilotCallResult Change(
        CourierServiceTests.Client client,
        bool add,
        double standing,
        bool notify = true,
        long? approval = null
    ) =>
        client.Call(
            add ? "AddContact" : "EditContact",
            [Recipient, standing, true, notify, new BlueUnicode("Étoile 学習")],
            "charMgr",
            approval is long cost ? new() { ["approvedCost"] = cost } : null
        );

    private static void Quote(PilotCallResult result, long cost)
    {
        var quote = Assert.IsType<PilotCallResult.NamedFailure>(result);
        Assert.Equal("ContactCostNotApproved", quote.Name);
        var values = BlueRead.Fields(BlueMarshal.Decode(BlueMarshal.Encode(quote.Values)));
        Assert.Equal(cost, values["totalCost"]);
        Assert.EndsWith(" ISK", BlueMarshal.Text(values["totalCostISK"]));
    }

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));
}
