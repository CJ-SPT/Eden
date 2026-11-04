using Eden.Gameplay.Mail;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.Persistence;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class MailComposerDefaultsTests
{
    // Build 3662168 NewNewMessage uses False, not None, for both defaults.
    private static object?[] Draft(object? reply, object? forward) =>
        [new List<object?> { MailTestData.Recipient }, null, null, "Hello pilot 2", "A new message", reply, forward];

    [Theory]
    [InlineData(false, false)]
    [InlineData(null, null)]
    [InlineData(0L, 0L)]
    [InlineData(false, null)]
    [InlineData(null, false)]
    public void AbsentReplyAndForwardSourcesDeliverExactlyOneOrdinaryMessage(object? reply, object? forward)
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        client.Refresh();
        var success = Assert.IsType<PilotCallResult.Success>(client.Call("SendMail", Draft(reply, forward), "mailMgr"));
        long id = Assert.IsType<long>(BlueMarshal.Decode(BlueMarshal.Encode(success.Reply)));
        var mail = MailTestData.Store(db);
        Assert.Equal(id, Assert.Single(mail.Sync(MailTestData.Sender, null, 0).New).Id);
        Assert.Equal(id, Assert.Single(mail.Sync(MailTestData.Recipient, null, 0).New).Id);
        Assert.Equal(MailStatus.Read, Assert.Single(mail.Sync(MailTestData.Sender, null, 0).Status).Status);
        Assert.Equal(MailStatus.None, Assert.Single(mail.Sync(MailTestData.Recipient, null, 0).Status).Status);
        Assert.Equal("A new message", mail.Body(MailTestData.Recipient, id, false, Guid.NewGuid()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    public void TrueIsNotAnIdentifierAndRejectsWithoutDelivery(int position)
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        client.Refresh();
        var args = Draft(null, null);
        args[position] = true;
        Assert.IsType<PilotCallResult.Failure>(client.Call("SendMail", args, "mailMgr"));
        Assert.Empty(MailTestData.Store(db).Sync(MailTestData.Sender, null, 0).New);
        Assert.Empty(MailTestData.Store(db).Sync(MailTestData.Recipient, null, 0).New);
    }
}
