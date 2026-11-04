using Eden.Gameplay.Mail;
using Eden.Protocol.Mail;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class MailArrivalContractTests
{
    [Theory]
    [InlineData(MailStatus.None)]
    [InlineData(MailStatus.Read)]
    [InlineData(MailStatus.Automated)]
    [InlineData(MailStatus.Read | MailStatus.Automated)]
    public void ArrivalMatchesThePinnedEightArgumentCallbackWithoutALabelMask(MailStatus status)
    {
        const long sentAt = 134067744000000000;
        MailHeader header = new(17, 90000001, sentAt, [90000002], "New mail 学習");
        MailSent notification = new(header, new(header.Id, status, 31));
        // Build 3662168 mailSvc.OnMailSent has self plus these eight arguments.
        // Its labelMask variable is calculated inside the callback, not received.
        object?[] expected =
        [
            17L,
            90000001L,
            sentAt,
            new List<object?> { 90000002L },
            null,
            null,
            new BlueUnicode(header.Subject),
            (int)status,
        ];
        Assert.Equal("OnMailSent", notification.Name);
        Assert.Equal(BlueMarshal.Encode(expected), BlueMarshal.Encode(notification.Arguments));
        var decoded = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(notification.Arguments)));
        Assert.Equal(8, decoded.Length);
        Assert.Equal((long)status, decoded[7]);
    }
}
