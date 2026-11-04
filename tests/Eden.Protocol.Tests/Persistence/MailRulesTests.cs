using Eden.Gameplay.Mail;

namespace Eden.Protocol.Tests.Persistence;

public sealed class MailRulesTests
{
    private static MailDraft Valid => new([90000002], "Subject", "Message", null, null);

    [Theory]
    [InlineData(150, 8000, true)]
    [InlineData(151, 8000, false)]
    [InlineData(150, 8001, false)]
    public void PinnedSubjectAndPlainBodyLimitsAreEnforced(int subject, int body, bool accepted)
    {
        var draft = Valid with { Subject = new('s', subject), Body = new('b', body) };
        if (accepted)
        {
            MailRules.Draft(draft);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => MailRules.Draft(draft));
        }
    }

    [Fact]
    public void RichTextIsPreservedAndBothTaggedAndPlainLimitsApply()
    {
        MailRules.Draft(Valid with { Body = "<b>" + new string('x', 8000) + "</b>" });
        Assert.Throws<InvalidDataException>(() => MailRules.Draft(Valid with { Body = "<b>" + new string('x', 8001) + "</b>" }));
        string body = "<" + new string('a', 9998) + ">";
        MailRules.Draft(Valid with { Body = body });
        Assert.Throws<InvalidDataException>(() => MailRules.Draft(Valid with { Body = body + "a" }));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public void RecipientLimitsMatchThePinnedClient(int count, bool accepted)
    {
        var draft = Valid with { Recipients = Enumerable.Range(1, count).Select(i => (long)i).ToArray() };
        Assert.Equal(accepted, Record.Exception(() => MailRules.Draft(draft)) is null);
    }

    [Fact]
    public void DuplicateRecipientsInvalidSourcesAndNulTextReject()
    {
        foreach (
            var draft in new[]
            {
                Valid with
                {
                    Recipients = [1, 1],
                },
                Valid with
                {
                    Recipients = [-1],
                },
                Valid with
                {
                    ReplyTo = -1,
                },
                Valid with
                {
                    ForwardedFrom = 0,
                },
                Valid with
                {
                    ReplyTo = 1,
                    ForwardedFrom = 2,
                },
                Valid with
                {
                    Subject = " ",
                },
                Valid with
                {
                    Subject = "a\0",
                },
                Valid with
                {
                    Body = "a\0",
                },
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => MailRules.Draft(draft));
        }
    }

    [Fact]
    public void LabelsUseSingleBitsAndThePinnedNameLimit()
    {
        MailRules.Label(new('a', 40), null);
        MailRules.LabelId(1L << 28);
        Assert.Throws<InvalidDataException>(() => MailRules.Label(new('a', 41), null));
        Assert.Throws<InvalidDataException>(() => MailRules.Label("Name", -1));
        foreach (long id in new long[] { 0, -1, 3, 1L << 29 })
        {
            Assert.Throws<InvalidDataException>(() => MailRules.LabelId(id));
        }
    }
}
