using Eden.Data.World.Structures;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Notifications;
using Eden.Gameplay.Structures;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Alliances.AllianceCreationTests;

namespace Eden.Protocol.Tests.Alliances;

public sealed class CorporationWarNegotiationWireTests
{
    [Fact]
    public void NativeAllyNegotiationListsOnlyAuthorizedOffersAndUpdatesWarAllyEntries()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var (attacker, defender, warId) = StartWar(world, clock);
        long allyPilot = CreateAlly(world);
        using CorporationTestClient aggressor = new(world, 1000001);
        using CorporationTestClient defending = new(world, 1000002);
        using CorporationTestClient offering = new(world, 1000002, allyPilot);
        int ally = offering.Context.Pilot.CorpId;

        Assert.IsType<PilotCallResult.Failure>(aggressor.CallService("warRegistry", "SetOpenForAllies", [warId, true]));
        Assert.Null(WarReply(defending, "SetOpenForAllies", [warId, true]));
        var available = Assert.IsType<List<object?>>(Reply(offering, "warsInfoMgr", "GetWarsRequiringAssistance", []));
        Assert.Equal(warId, Fields(Assert.Single(available))["warID"]);
        Assert.Equal(true, Fields(Assert.Single(available))["openForAllies"]);
        Assert.True(Assert.IsType<double>(Reply(defending, "corpRegistry", "CharGetAllyBaseCost", [])) > 0);
        Assert.Empty(Assert.IsType<List<object?>>(WarReply(defending, "GetNegotiations", [])));
        defending.Context.Notifications.Clear();

        Assert.IsType<PilotCallResult.Failure>(
            offering.CallService("warRegistry", "CreateWarAllyOffer", [warId, 15.25d, (long)attacker, "Wrong recipient"])
        );
        Assert.IsType<PilotCallResult.Failure>(
            offering.CallService("warRegistry", "CreateWarAllyOffer", [warId, double.NaN, (long)defender, "Invalid amount"])
        );
        Assert.Null(WarReply(offering, "CreateWarAllyOffer", [warId, 0L, (long)defender, "Withdraw later"]));
        long withdrawn = Assert.IsType<long>(Pending(offering)["warNegotiationID"]);
        Assert.Null(WarReply(offering, "RetractWarAllyOffer", [withdrawn]));
        Assert.Null(WarReply(offering, "CreateWarAllyOffer", [warId, 15.25d, (long)defender, "Help from 星"]));
        defending.Services.GetRequiredService<CorporationWarMonitor>().Poll(defending.Context);
        Assert.Single(defending.Context.Notifications.OfType<CorporationWarChanged>());
        var offer = Pending(defending);
        Assert.Equal((long)ally, offer["ownerID1"]);
        Assert.Equal((long)defender, offer["ownerID2"]);
        Assert.Equal(15.25d, offer["iskValue"]);
        Assert.Equal("Help from 星", BlueMarshal.Text(offer["description"]));
        long id = Assert.IsType<long>(offer["warNegotiationID"]);
        Assert.Empty(Assert.IsType<List<object?>>(WarReply(aggressor, "GetNegotiations", [])));
        Assert.IsType<PilotCallResult.Failure>(aggressor.CallService("warRegistry", "GetWarNegotiation", [id]));
        Assert.Empty(WarNotices(aggressor, InboxNotificationType.WarMercenaryOffer));
        var offeredNotice = Assert.Single(
            WarNotices(defending, InboxNotificationType.WarMercenaryOffer),
            notice => Equals(notice["iskValue"], 15.25d)
        );
        Assert.Equal((long)ally, offeredNotice["mercID"]);
        Assert.Equal((long)defender, offeredNotice["defenderID"]);
        Assert.Equal((long)attacker, offeredNotice["aggressorID"]);

        Assert.Null(WarReply(defending, "AcceptAllyNegotiation", [id]));
        Assert.Null(WarReply(defending, "AcceptAllyNegotiation", [id]));
        var acceptedNotice = Assert.Single(WarNotices(offering, InboxNotificationType.WarAllyAccepted));
        Assert.Equal((long)ally, acceptedNotice["allyID"]);
        Assert.Equal(Other, acceptedNotice["charID"]);
        Assert.Equal(15.25d, acceptedNotice["iskValue"]);
        Assert.Equal(clock.Now, acceptedNotice["time"]);
        Assert.Equal(
            (long)CorporationWarNegotiationState.Accepted,
            Fields(WarReply(defending, "GetWarNegotiation", [id]))["negotiationState"]
        );
        var wars = Assert.IsType<Dictionary<long, object?>>(WarReply(offering, "GetWars", [(long)ally]));
        var allies = Assert.IsType<Dictionary<long, object?>>(Fields(wars[warId])["allies"]);
        var allyEntry = Fields(allies[ally]);
        Assert.Equal((long)ally, allyEntry["allyID"]);
        Assert.Equal(clock.Now + CorporationWarRules.AllyWarmup, allyEntry["timeStarted"]);
        Assert.Equal(clock.Now + CorporationWarRules.AllyLifetime, allyEntry["timeFinished"]);
        Assert.Single(
            defending.Context.Notifications.OfType<CorporationWarChanged>(),
            notice => notice.Current.Allies.Any(organization => organization.OwnerId == ally)
        );
        int notices = defending.Context.Notifications.Count;
        defending.Services.GetRequiredService<CorporationWarMonitor>().Advance(defending.Context);
        Assert.Equal(notices, defending.Context.Notifications.Count);
    }

    [Fact]
    public void NativeSurrenderUsesTypedTreatyAndCooldownRepliesForBothSides()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var (attacker, defender, warId) = StartWar(world, clock);
        using CorporationTestClient aggressor = new(world, 1000001);
        using CorporationTestClient defending = new(world, 1000002);

        Assert.Null(WarReply(defending, "CreateSurrenderNegotiation", [warId, 0d, "Propose peace"]));
        long declined = Assert.IsType<long>(Pending(aggressor)["warNegotiationID"]);
        Assert.Null(WarReply(aggressor, "DeclineSurrender", [declined]));
        Assert.Empty(Assert.IsType<List<object?>>(WarReply(defending, "GetNegotiations", [])));

        Assert.Null(WarReply(defending, "CreateSurrenderNegotiation", [warId, 12.5d, "New offer"]));
        long accepted = Assert.IsType<long>(Pending(aggressor)["warNegotiationID"]);
        Assert.Null(WarReply(aggressor, "AcceptSurrender", [accepted]));
        Assert.Null(WarReply(aggressor, "AcceptSurrender", [accepted]));
        var acceptedNotice = Assert.Single(WarNotices(defending, InboxNotificationType.WarSurrenderAccepted));
        Assert.Equal((long)attacker, acceptedNotice["entityID"]);
        Assert.Equal((long)defender, acceptedNotice["offeringID"]);
        Assert.Equal(Ceo, acceptedNotice["charID"]);
        Assert.Equal(12.5d, acceptedNotice["iskValue"]);
        var ended = Fields(Reply(defending, "warsInfoMgr", "GetPublicWarInfo", [warId]));
        Assert.Equal((long)CorporationWarEndReason.Surrender, ended["reasonEnded"]);
        Assert.Equal(clock.Now + CorporationWarRules.Cooldown, ended["timeFinished"]);
        AssertSurrenderTreaties(aggressor, defender);
        AssertSurrenderTreaties(defending, attacker);
    }

    private static (int Attacker, int Defender, long WarId) StartWar(TestWorld world, TestClock clock)
    {
        int attacker = Corporation(world);
        int defender = Corporation(world, Other, "Defenders", "DFN");
        CorporationWarTests.SeedHeadquarters(world, attacker, defender);
        AllianceBillingTests.Fund(world, attacker, CorporationWarRules.WeeklyFeeCents * 2);
        AllianceBillingTests.Fund(world, defender, CorporationWarRules.WeeklyFeeCents);
        using CorporationTestClient client = new(world, 1000001);
        var declared = Reply(client, "corpRegistry", "DeclareWarAgainst", [(long)defender, CorporationWarTests.Headquarters]);
        clock.AdvanceWallTime(TimeSpan.FromTicks(CorporationWarRules.Warmup));
        return (attacker, defender, Assert.IsType<long>(Fields(declared)["warID"]));
    }

    private static long CreateAlly(TestWorld world)
    {
        TestCatalog.AddSchool(new WorldDatabase(world.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        var creation = world.Services.GetRequiredService<ICharacterCreation>();
        long pilot = creation.Create(1000002, new("Eden Ally", 1, 1, 1, 64, [1], [2], 18));
        int corporation = Corporation(world, pilot, "Allied Logistics", "ALLY");
        using var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None);
        action.Add(
            new SavedStructure
            {
                Id = CorporationWarTests.Headquarters + 2,
                TypeId = 35832,
                Name = "Ally Headquarters",
                CorporationId = corporation,
                SystemId = 30000142,
                HasCore = true,
                State = StructureState.ShieldVulnerable,
            }
        );
        action.Commit();
        return pilot;
    }

    private static void AssertSurrenderTreaties(CorporationTestClient client, int counterpart)
    {
        var result = client.CallService("peaceTreatyMgr", "GetPeaceTreatiesForSession", []);
        var reply = Assert.IsType<CorporationWarPeaceTreatiesReply>(Assert.IsType<PilotCallResult.Success>(result).Reply);
        Assert.Collection(
            BlueRead.Tuple(reply.ToBlue(), 2),
            rowset => AssertTreatyRow(rowset, counterpart),
            rowset => AssertTreatyRow(rowset, counterpart)
        );
        Assert.NotEmpty(BlueMarshal.Encode(reply));
    }

    private static void AssertTreatyRow(object? rowset, int counterpart)
    {
        var rows = Assert.IsType<List<object?>>(Assert.IsType<BlueConstructor>(rowset).Arguments[1]);
        var columns = BlueRead.Tuple(Assert.IsType<BlueConstructor>(Assert.Single(rows)).Arguments[1], 6);
        Assert.Equal(counterpart, columns[0]);
        Assert.Equal((int)CorporationWarPeaceReason.Surrender, columns[2]);
        Assert.Equal((int)CorporationWarEndReason.Surrender, columns[4]);
    }

    private static Dictionary<string, object?> Pending(CorporationTestClient client) =>
        Fields(Assert.Single(Assert.IsType<List<object?>>(WarReply(client, "GetNegotiations", []))));

    private static IEnumerable<Dictionary<string, object?>> WarNotices(CorporationTestClient client, InboxNotificationType type) =>
        Assert
            .IsType<List<object?>>(Reply(client, "notificationMgr", "GetByGroupID", [(long)InboxGroup.War]))
            .Select(Fields)
            .Where(fields => Equals(fields["typeID"], (long)type))
            .Select(fields => BlueRead.Fields(fields["data"]));

    private static object? WarReply(CorporationTestClient client, string method, object?[] args) =>
        Reply(client, "warRegistry", method, args);

    private static object? Reply(CorporationTestClient client, string service, string method, object?[] args) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(client.CallService(service, method, args)).Reply));

    private static Dictionary<string, object?> Fields(object? model) => BlueRead.Fields(Assert.IsType<BlueInstance>(model).State);
}
