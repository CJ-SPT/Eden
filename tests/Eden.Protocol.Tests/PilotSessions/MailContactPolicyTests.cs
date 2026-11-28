using Eden.Data.World.Characters;
using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Mail;
using Eden.Gameplay.Market;
using Eden.Protocol.Mail;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class MailContactPolicyTests
{
    private const long Sender = MailTestData.Sender;
    private const long Recipient = MailTestData.Recipient;
    private const int OtherCorporation = 1000035;

    [Fact]
    public async Task NativePaidMailRequotesAndCommitsDeliveryWithTheWalletCharge()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock, liveWorld: true);
        var live = db.Services.GetRequiredService<LiveWorld>();
        using (var world = live.Economy.Begin(new(pilots: [Sender, Recipient])))
        {
            world.Pilots.Find(Recipient)!.CorporationId = OtherCorporation;
            world.Add(new PilotWallet { PilotId = Sender, Cents = 10_000 });
            world.Commit();
        }
        long delivered;
        using (CourierServiceTests.Client sender = new(db, clock, 1000001))
        using (CourierServiceTests.Client recipient = new(db, clock, 1000002))
        {
            Assert.Same(PilotCallResult.Unsupported, recipient.Call("GetDefaultContactCost", [], "account"));
            sender.Refresh();
            recipient.Refresh();
            Assert.Equal(0L, Reply(recipient.Call("GetDefaultContactCost", [], "account")));
            Assert.IsType<PilotCallResult.Failure>(recipient.Call("SetContactCost", [1_000_001L], "account"));
            Assert.IsType<PilotCallResult.Failure>(recipient.Call("SetContactCost", [-1L], "account"));
            Assert.Null(Reply(recipient.Call("SetContactCost", [40L], "account")));
            Quote(Send(sender), 40);
            Assert.Null(Reply(recipient.Call("SetContactCost", [75L], "account")));
            Quote(Send(sender, 40L), 75);
            Assert.Equal(100d, Reply(sender.Call("GetCashBalance", [], "account")));
            Assert.Empty(MailTestData.Store(db).Sync(Recipient, null, 0).New);

            SetBalance(5_000);
            Assert.IsType<PilotCallResult.Failure>(Send(sender, 75L));
            Assert.Equal(50d, Reply(sender.Call("GetCashBalance", [], "account")));
            Assert.Empty(MailTestData.Store(db).Sync(Recipient, null, 0).New);
            Assert.Empty(Journal(sender));

            SetBalance(10_000);
            delivered = Assert.IsType<long>(Reply(Send(sender, 75L)));
            Assert.Equal(25d, Reply(sender.Call("GetCashBalance", [], "account")));
            var journal = BlueRead.Fields(Assert.IsType<BlueInstance>(Assert.Single(Journal(sender))).State);
            Assert.Equal((long)WalletEntryType.Cspa, journal["entryTypeID"]);
            Assert.Equal(-75d, journal["amount"]);
            Assert.Equal(25d, journal["balance"]);
            Assert.Equal(Sender, journal["ownerID1"]);
            Assert.Equal((long)CombatDefinitions.ConcordCorporation, journal["ownerID2"]);
            Assert.Equal(Recipient, journal["referenceID"]);
            Assert.Empty(Journal(recipient));
            clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            recipient.Advance();
            Assert.Equal(delivered, Assert.Single(recipient.Context.Notifications.OfType<MailSent>()).Header.Id);
        }
        await live.FlushAsync();
        db.Services.Dispose();
        using var reopened = ServerServices.Build(new(db.Path), clock, liveWorld: true);
        Assert.Equal(75L, reopened.GetRequiredService<IMailContactPolicy>().ContactCost(Recipient));
        Assert.Equal(2_500L, reopened.GetRequiredService<IPilotWalletReader>().Balance(Sender));
        Assert.Equal(delivered, Assert.Single(reopened.GetRequiredService<IPilotMail>().Sync(Recipient, null, 0).New).Id);

        void SetBalance(long cents)
        {
            using var action = live.Economy.Begin(new(pilots: [Sender]));
            action.Set<PilotWallet>().Find(Sender)!.Cents = cents;
            action.Commit();
        }
    }

    [Fact]
    public void BlockUnknownMailExemptsCorporationAndRecipientContactsWhileExplicitBlocksWin()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock, liveWorld: true);
        using CourierServiceTests.Client sender = new(db, clock, 1000001);
        using CourierServiceTests.Client recipient = new(db, clock, 1000002);
        sender.Refresh();
        recipient.Refresh();
        Assert.Null(Reply(recipient.Call("SetContactCost", [null], "account")));
        Assert.Null(Reply(recipient.Call("GetDefaultContactCost", [], "account")));
        Assert.IsType<long>(Reply(Send(sender)));

        var live = db.Services.GetRequiredService<LiveWorld>();
        using (var action = live.Economy.Begin(new(pilots: [Recipient])))
        {
            action.Pilots.Find(Recipient)!.CorporationId = OtherCorporation;
            action.Commit();
        }
        Assert.IsType<PilotCallResult.Failure>(Send(sender));
        using (var action = live.Economy.Begin(new(pilots: [Recipient])))
        {
            action.Add(new SavedContact { PilotId = Recipient, ContactId = Sender });
            action.Commit();
        }
        Assert.IsType<long>(Reply(Send(sender)));
        using (var action = live.Economy.Begin(new(pilots: [Recipient])))
        {
            action.Add(new SavedBlockedContact { PilotId = Recipient, ContactId = Sender });
            action.Commit();
        }
        Assert.IsType<PilotCallResult.Failure>(Send(sender));
        Assert.Null(Reply(recipient.Call("SetContactCost", [0L], "account")));
        Assert.Equal(0L, Reply(recipient.Call("GetDefaultContactCost", [], "account")));
        Assert.IsType<PilotCallResult.Failure>(Send(sender));
        Assert.Equal(2, MailTestData.Store(db).Sync(Recipient, null, 0).New.Count);
        Assert.Empty(Journal(sender));
    }

    [Fact]
    public void BiomassedRecipientCannotReceivePaidRepliesToHistoricalMail()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock, liveWorld: true);
        var live = db.Services.GetRequiredService<LiveWorld>();
        using (var world = live.Economy.Begin(new(pilots: [Sender, Recipient])))
        {
            world.Pilots.Find(Recipient)!.CorporationId = OtherCorporation;
            world.Add(new PilotWallet { PilotId = Sender, Cents = 10_000 });
            world.Commit();
        }
        var mail = MailTestData.Store(db);
        long source = mail.Send(Recipient, MailTestData.Draft with { Recipients = [Sender] }, Guid.NewGuid());
        db.Services.GetRequiredService<IMailContactPolicy>().SetContactCost(Recipient, 75);
        var deletion = db.Services.GetRequiredService<ICharacterDeletion>();
        deletion.Prepare(1000002, Recipient);
        clock.AdvanceWallTime(CharacterDeletionRules.PreparationDelay);
        deletion.Delete(1000002, Recipient);

        var reply = MailTestData.Draft with { ReplyTo = source, ApprovedContactCostIsk = 75 };
        Assert.Throws<InvalidDataException>(() => mail.Send(Sender, reply, Guid.NewGuid()));

        Assert.Equal(10_000, db.Services.GetRequiredService<IPilotWalletReader>().Balance(Sender));
        Assert.Equal(MailStatus.None, Assert.Single(mail.Sync(Sender, null, 0).Status).Status);
        Assert.Equal(source, Assert.Single(mail.Sync(Recipient, null, 0).New).Id);
        Assert.Equal(MailTestData.Draft.Body, mail.Body(Sender, source, false, Guid.NewGuid()));
    }

    private static List<object?> Journal(CourierServiceTests.Client client) =>
        Assert.IsType<List<object?>>(Reply(client.Call("GetTransactions", [1000L, null, null, false], "account")));

    private static PilotCallResult Send(CourierServiceTests.Client client, long? approval = null) =>
        client.Call(
            "SendMail",
            [new List<object?> { Recipient }, null, null, "CSPA test", "Message", false, false],
            "mailMgr",
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
