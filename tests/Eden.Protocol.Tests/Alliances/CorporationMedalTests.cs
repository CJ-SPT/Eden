using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Alliances.AllianceCreationTests;

namespace Eden.Protocol.Tests.Alliances;

public sealed class CorporationMedalTests
{
    [Fact]
    public async Task NativeMedalsChargeTheSelectedDivisionAndPersistVisibilityAcrossCheckpoint()
    {
        using TestWorld world = new(liveWorld: true);
        int corporation = Corporation(world);
        const int division = CorporationWalletRules.MasterKey + 1;
        const long fee = CorporationMedalRules.FeeCents;
        AllianceBillingTests.Fund(world, corporation, 3 * fee, division);
        var wallet = world.Services.GetRequiredService<ICorporationWallet>();
        wallet.SelectDivision(Ceo, division);
        var membership = world.Services.GetRequiredService<ICorporationMembership>();
        var invite = membership.Invite(Ceo, Other);
        membership.Update(Other, new(invite.Id, Other, corporation, "", CorporationApplicationStatus.Joined, "", invite.AppliedAt));
        long medalId;
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            List<object?> graphics = [new List<object?> { 1L, "amarr.1_1", 0xffaabbccL }, new List<object?> { 2L, "caldari.1_2", -1L }];
            Assert.IsType<PilotCallResult.Failure>(
                Call(member, "CreateMedal", ["Fleet Service", "Recognition for fleet service.", graphics])
            );
            var quote = Assert.IsType<PilotCallResult.NamedFailure>(
                Call(ceo, "CreateMedal", ["Fleet Service", "Recognition for fleet service.", graphics])
            );
            Assert.Equal("ConfirmCreatingMedal", quote.Name);
            Assert.Equal(3 * fee, wallet.Balance(Ceo, division));
            medalId = Assert.IsType<long>(
                RoundTrip(Reply(ceo, "CreateMedal", ["Fleet Service", "Recognition for fleet service.", graphics, true]))
            );
            Assert.Equal(2 * fee, wallet.Balance(Ceo, division));
            Assert.Single(ceo.Context.Notifications.OfType<CorporationDecorationAdded>());
            var reply = Assert.IsType<CorporationMedalCatalogueReply>(Reply(ceo, "GetAllCorpMedals", [(long)corporation]));
            Assert.Equal(medalId, Assert.Single(reply.Entries).Medal.Id);
            Assert.NotEmpty(BlueMarshal.Encode(reply));
            var details = Assert.IsType<CorporationMedalDetailsReply>(Reply(ceo, "GetMedalDetails", [medalId]));
            Assert.Equal(unchecked((int)0xffaabbcc), details.Details!.Medal.Design.Graphics[0].Color);

            List<object?> recipients = [Other];
            Assert.Equal(
                "ConfirmGivingMedal",
                Assert.IsType<PilotCallResult.NamedFailure>(Call(ceo, "GiveMedalToCharacters", [medalId, recipients, "Scouting"])).Name
            );
            Assert.Equal(2 * fee, wallet.Balance(Ceo, division));
            Assert.Null(RoundTrip(Reply(ceo, "GiveMedalToCharacters", [medalId, recipients, "Scouting", true])));
            Assert.Null(RoundTrip(Reply(ceo, "GiveMedalToCharacters", [medalId, recipients, "Logistics", true])));
            Assert.Equal(
                "NotEnoughMoney",
                Assert
                    .IsType<PilotCallResult.NamedFailure>(Call(ceo, "GiveMedalToCharacters", [medalId, recipients, "A third award", true]))
                    .Name
            );
            Assert.Equal(0, wallet.Balance(Ceo, division));
            Assert.Equal(0, wallet.Balance(Ceo, CorporationWalletRules.MasterKey));
            Assert.Equal(2, Assert.IsType<CorporationMedalRecipientsReply>(Reply(ceo, "GetRecipientsOfMedal", [medalId])).Awards.Count);

            var own = Assert.IsType<ReceivedMedalsReply>(Reply(member, "GetMedalsReceived", [Other]));
            Assert.Equal(2, own.Medals.Count);
            Assert.All(own.Medals, entry => Assert.Equal(CorporationMedalStatus.Private, entry.Award.Status));
            Assert.IsNotType<ReceivedMedalsReply>(Reply(ceo, "GetMedalsReceived", [Other]));
            var publish = new Dictionary<long, object?> { [medalId] = 3L };
            Assert.IsType<PilotCallResult.Failure>(Call(ceo, "SetMedalStatus", [publish]));
            Assert.Null(RoundTrip(Reply(member, "SetMedalStatus", [publish])));
            var visible = Assert.IsType<ReceivedMedalsReply>(Reply(ceo, "GetMedalsReceived", [Other]));
            Assert.Equal(2, visible.Medals.Count);
            var rowsets = BlueRead.Tuple(RoundTrip(visible), 2);
            Assert.Equal(2, Lines(rowsets[0]).Count);
            Assert.Equal(2, Lines(rowsets[1]).Count);
            Assert.Collection(
                wallet
                    .Journal(Ceo, true, division)
                    .Where(row => row.EntryType is WalletEntryType.MedalCreation or WalletEntryType.MedalIssuing),
                row => Assert.Equal(WalletEntryType.MedalIssuing, row.EntryType),
                row => Assert.Equal(WalletEntryType.MedalIssuing, row.EntryType),
                row => Assert.Equal(WalletEntryType.MedalCreation, row.EntryType)
            );
        }

        await world.Services.GetRequiredService<LiveWorld>().FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: true);
        var medals = reopened.GetRequiredService<ICorporationMedals>();
        Assert.Equal(2, medals.Received(Ceo, Other).Count);
        Assert.Equal(2, medals.Details(medalId)!.RecipientCount);
        medals.SetStatus(Other, new Dictionary<long, CorporationMedalStatus> { [medalId] = CorporationMedalStatus.Deleted });
        Assert.Empty(medals.Received(Other, Other));
        Assert.Throws<InvalidDataException>(() =>
            medals.SetStatus(Other, new Dictionary<long, CorporationMedalStatus> { [medalId] = CorporationMedalStatus.Public })
        );
        Assert.Equal(2, medals.Recipients(Ceo, medalId).Count);
    }

    private static PilotCallResult Call(CorporationTestClient client, string method, object?[] arguments) =>
        client.CallService("corporationSvc", method, arguments);

    private static object? Reply(CorporationTestClient client, string method, object?[] arguments) =>
        Assert.IsType<PilotCallResult.Success>(Call(client, method, arguments)).Reply;

    private static object? RoundTrip(object? value) => BlueMarshal.Decode(BlueMarshal.Encode(value));

    private static List<object?> Lines(object? value) =>
        Assert.IsType<List<object?>>(BlueRead.Fields(Assert.IsType<BlueInstance>(value).State)["lines"]);
}
