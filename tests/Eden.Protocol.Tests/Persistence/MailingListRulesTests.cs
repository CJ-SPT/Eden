using Eden.Gameplay.Mail;

namespace Eden.Protocol.Tests.Persistence;

public sealed class MailingListRulesTests
{
    [Theory]
    [InlineData("Team Blue", "teamblue", "Team Blue")]
    [InlineData("Team\u180EBlue", "teamblue", "Team\u180EBlue")]
    [InlineData(" Team   Blue ", "teamblue", " Team Blue ")]
    [InlineData("prefix\\Team Blue", "teamblue", "prefix\\Team Blue")]
    [InlineData("Straße", "straße", "Straße")]
    [InlineData("Σςσ", "σσσ", "Σςσ")]
    [InlineData("ﬀ Fleet", "ﬀfleet", "ﬀ Fleet")]
    [InlineData("🚀 Fleet", "🚀fleet", "🚀 Fleet")]
    public void NamesUseThePinnedWhitespaceSuffixAndUnicodeCaseFold(string input, string key, string display) =>
        Assert.Equal(new MailingListName(key, display), MailingListRules.Name(input));

    [Theory]
    [InlineData(0, false)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    public void NameLengthUsesThePinnedBoundary(int length, bool accepted) =>
        Assert.Equal(accepted, Record.Exception(() => MailingListRules.Name(new('a', length))) is null);

    [Fact]
    public void NameAndRoleValidationCannotGrantOwnershipOrAcceptEmptyKeys()
    {
        foreach (string name in new[] { " ", "prefix\\", "a\0b" })
        {
            Assert.Throws<InvalidDataException>(() => MailingListRules.Name(name));
        }
        Assert.Throws<InvalidDataException>(() => MailingListRules.Defaults(new(MailingListAccess.Allowed, MailingListRole.Owner)));
        Assert.Throws<InvalidDataException>(() => MailingListRules.Defaults(new(MailingListAccess.Allowed, MailingListRole.Operator)));
        Assert.Throws<InvalidDataException>(() => MailingListRules.Manager(MailingListRole.Member));
        Assert.Throws<InvalidDataException>(() => MailingListRules.Manager(MailingListRole.Muted));
    }

    [Fact]
    public void CapacityAndUpdateLimitsHaveExactBoundaries()
    {
        MailingListRules.Capacity(2999);
        Assert.Throws<InvalidDataException>(() => MailingListRules.Capacity(3000));
        MailingListRules.Members(Enumerable.Range(1, 1000).Select(i => (long)i).ToArray());
        Assert.Throws<InvalidDataException>(() => MailingListRules.Members(Enumerable.Range(1, 1001).Select(i => (long)i).ToArray()));
        Assert.Throws<InvalidDataException>(() => MailingListRules.Members([1, 1]));
    }

    [Fact]
    public void CharacterAccessOverridesCorporationAndFallback()
    {
        Dictionary<long, MailingListAccess> rules = new() { [1000044] = MailingListAccess.Blocked, [90000001] = MailingListAccess.Allowed };
        Assert.Equal(MailingListAccess.Allowed, MailingListRules.Access(90000001, 1000044, MailingListAccess.Blocked, rules));
        Assert.Equal(MailingListAccess.Blocked, MailingListRules.Access(90000002, 1000044, MailingListAccess.Allowed, rules));
        Assert.Equal(MailingListAccess.Allowed, MailingListRules.Access(90000002, 1000045, MailingListAccess.Allowed, rules));
    }

    [Theory]
    [InlineData(false, false, MailStatus.Read, 2)]
    [InlineData(true, false, MailStatus.None, 3)]
    [InlineData(false, true, MailStatus.None, 2)]
    public void InitialOutgoingAndReceivedCopiesMatchNativeReadAndLabelRules(bool self, bool list, MailStatus status, long labels)
    {
        MailDraft draft = new(self ? [90000001] : [90000002], "Subject", "Body", null, null) { ListId = list ? 1 : null };
        var state = MailRules.InitialState(17, 90000001, 90000001, draft, false);
        Assert.Equal(status, state.Status);
        Assert.Equal(labels, state.Labels);
    }
}
