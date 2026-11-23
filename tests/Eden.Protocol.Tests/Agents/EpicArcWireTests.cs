using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;
using Eden.Protocol.Agents;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.Agents;

public sealed class EpicArcWireTests
{
    private const int Arc = 52;
    private const int Fork = 14980;
    private const int FirstChoice = 14950;
    private const int SecondChoice = 14951;
    private const long OfferId = 41;
    private const long AgentId = 3019444;
    private const long DestinationAgent = 3019369;
    private const int DestinationStation = 60003761;
    private const int DestinationSystem = 30000144;

    [Fact]
    public void StatusAndMessagesUseNativeNestedMapsWithNullableFiletimes()
    {
        const string ChapterTitle = "messages.epicMission.journalText.chapterTitle";
        const string CompletedMessage = "messages.epicMission.journalText.completedMessage";
        const long AcceptedAt = 134134080000000000;
        const long CompletedAt = AcceptedAt + TimeSpan.TicksPerMinute;
        var status = new EpicArcStatusReply([
            new(Arc, Fork, AcceptedAt, CompletedAt, null, 150001),
            new(Arc, FirstChoice, null, null, null, 150002),
            new(29, 14117, null, null, CompletedAt, 0),
        ]);
        var arcs = Assert.IsType<Dictionary<long, object?>>(RoundTrip(status));
        var progress = Assert.IsType<Dictionary<long, object?>>(arcs[Arc]);
        var done = Fields(progress[Fork]);
        Assert.Equal(AcceptedAt, done["acceptedDate"]);
        Assert.Equal(CompletedAt, done["completedDate"]);
        Assert.Null(done["quitDate"]);
        Assert.Equal(150001L, done["nameID"]);
        var pending = Fields(progress[FirstChoice]);
        Assert.Null(pending["acceptedDate"]);
        Assert.Null(pending["completedDate"]);
        Assert.Equal(150002L, pending["nameID"]);
        var closed = Fields(Assert.IsType<Dictionary<long, object?>>(arcs[29])[14117L]);
        Assert.Equal(CompletedAt, closed["quitDate"]);
        Assert.Null(closed["nameID"]);
        Assert.Null(closed["acceptedDate"]);

        var cache = Assert.IsType<BlueInstance>(
            RoundTrip(new CachedEpicArcMessages([new(Fork, ChapterTitle, 150003), new(Fork, CompletedMessage, 150004)]))
        );
        Assert.Equal("carbon.common.script.net.objectCaching.CachedMethodCallResult", cache.Name);
        var cacheState = BlueRead.Tuple(cache.State, 3);
        Assert.Equal("never", BlueMarshal.Text(BlueRead.Fields(cacheState[0])["versionCheck"]));
        var messages = BlueRead.Fields(BlueMarshal.Decode(Assert.IsType<byte[]>(cacheState[1])));
        Assert.Equal(150003L, Assert.IsType<Dictionary<long, object?>>(messages[ChapterTitle])[Fork]);
        Assert.Equal(150004L, Assert.IsType<Dictionary<long, object?>>(messages[CompletedMessage])[Fork]);
        var journal = new EpicArcJournalChanged();
        Assert.Equal("OnEpicJournalChange", journal.Name);
        Assert.Empty(journal.Arguments);
    }

    [Fact]
    public void BranchChoiceUsesTheNativeSpecialInteractionWithAnOfferScopedActionToken()
    {
        var offer = Offer(MissionKind.Choice);
        var conversation = new MissionConversation(offer, "", "")
        {
            EpicChoices = [new(OfferId, FirstChoice, 150003, 150004, "First"), new(OfferId, SecondChoice, 150005, 150006, "Second")],
        };
        var reply = Assert.IsType<object?[]>(RoundTrip(conversation));
        var dialogue = Assert.IsType<object?[]>(reply[0]);
        var actions = Assert.IsType<List<object?>>(dialogue[1]).Select(Assert.IsType<object?[]>).ToArray();
        Assert.DoesNotContain(actions, action => Equals(action[1], (long)MissionAction.Complete));
        foreach (var (action, expectedMission) in actions.Take(2).Zip(new[] { FirstChoice, SecondChoice }))
        {
            long token = Assert.IsType<long>(action[0]);
            Assert.Equal(new EpicArcChoiceAction(OfferId, expectedMission), EpicArcChoiceAction.Read(token));
            Assert.NotEqual(new EpicArcChoiceAction(OfferId + 1, expectedMission).Token, token);
            var fields = BlueRead.Fields(action[1]);
            Assert.Equal((long)expectedMission, fields["ContentID"]);
            Assert.IsType<long>(fields["Mission Title ID"]);
            Assert.IsType<long>(fields["Mission Briefing ID"]);
            Assert.Equal(AgentId, BlueRead.Fields(fields["Mission Keywords"])["agentID"]);
        }
        Assert.Null(EpicArcChoiceAction.Read(MissionRules.ActionStride * OfferId + (int)MissionAction.Complete));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HandoffTargetsTheNextAgentInJournalAndTracker(bool station)
    {
        var offer = Offer(MissionKind.AgentHandoff) with
        {
            EpicArcTargetAgentId = DestinationAgent,
            EpicArcTargetStation = station ? DestinationStation : null,
            EpicArcTargetTypeId = station ? 1531 : 596,
            EpicArcTargetSystem = DestinationSystem,
            EpicArcTargetPosition = station ? null : new SpacePosition(15, 20, 25),
        };
        var details = BlueRead.Fields(RoundTrip(new MissionJournalInfo(offer, 91)));
        Assert.Null(details["expirationTime"]);
        var objectives = BlueRead.Fields(details["objectives"]);
        var objective = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(objectives["objectives"])));
        Assert.Equal("agent", BlueMarshal.Text(objective[0]));
        var target = Assert.IsType<object?[]>(objective[1]);
        Assert.Equal(DestinationAgent, target[0]);
        var location = BlueRead.Fields(target[1]);
        Assert.Equal(station ? (long)DestinationStation : DestinationSystem, location["locationID"]);
        Assert.Equal((long)DestinationSystem, location["solarsystemID"]);

        var journal = Assert.IsType<object?[]>(RoundTrip(new MissionJournal([offer])));
        var row = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(journal[0])));
        Assert.Equal("UI/Agents/MissionTypes/EpicArcTalkToAgent", BlueMarshal.Text(row[2]));
        Assert.Equal(0L, row[5]);
        var bookmarks = Assert.IsType<List<object?>>(row[6]).Select(Fields).ToArray();
        Assert.Equal(
            new[] { "objective", "objective.destination" },
            bookmarks.Select(bookmark => BlueMarshal.Text(bookmark["locationType"]))
        );
        Assert.All(bookmarks, bookmark => Assert.Equal(station ? DestinationStation : DestinationAgent, bookmark["itemID"]));
        Assert.All(bookmarks, bookmark => Assert.Equal(station ? 0d : 15d, bookmark["x"]));

        var state = MissionTrackingRules.Read(new(offer, []), DestinationSystem, null, null);
        var detail = Assert.IsType<object?[]>(RoundTrip(new MissionTrackerObjectiveReply(state.Current)));
        Assert.Equal("TalkToAgent", BlueMarshal.Text(detail[0]));
        Assert.Equal(DestinationAgent, detail[1]);
        var legacy = Assert.IsType<List<object?>>(RoundTrip(new MissionTrackerObjectiveReply(state.Current, true)));
        Assert.Equal(DestinationAgent, legacy[1]);
    }

    [Fact]
    public void AuthoredGiftAndItemOnlyBonusUseNativeItemRowsWithoutZeroIsk()
    {
        const int FirstGift = 93641;
        const int ExtraGift = 32467;
        const int ExtraAmmo = 2613;
        const int NormalReward = 89240;
        const int BonusItem = 90039;
        const long Window = TimeSpan.TicksPerHour * 3;
        const long Deadline = Window + 20;
        var offer = Offer(MissionKind.Combat) with
        {
            CareerGift = new(FirstGift, 1),
            AdditionalGifts = [new(ExtraGift, 1), new(ExtraAmmo, 200)],
            ItemReward = new(NormalReward, 1),
            CareerBonusItem = new(BonusItem, 1),
            Bonus = new(0, Window, Deadline),
        };
        var objectives = BlueRead.Fields(RoundTrip(new MissionObjectives(offer, Now: 50)));
        var gifts = Assert.IsType<List<object?>>(objectives["agentGift"]).Select(Assert.IsType<object?[]>).ToArray();
        Assert.Equal(new long[] { FirstGift, ExtraGift, ExtraAmmo }, gifts.Select(gift => Assert.IsType<long>(gift[0])));
        Assert.Equal(200L, gifts[2][1]);
        var reward = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(objectives["normalRewards"])));
        Assert.Equal((long)NormalReward, reward[0]);
        var bonus = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(objectives["bonusRewards"])));
        Assert.Equal(new object?[] { Deadline - 50, (long)BonusItem, 1L, null, 180L }, bonus);
    }

    private static MissionOffer Offer(MissionKind kind) =>
        new(
            OfferId,
            Fork,
            "Branch",
            "Epic arc briefing",
            new(AgentId, 60003760, 30000142, 1000120, AgentType.EpicArc, 500004, 22, 4, false, null),
            4,
            kind,
            MissionState.Accepted,
            10,
            20,
            long.MaxValue,
            new(0, 0, 0, 0),
            false,
            null,
            30000142,
            new SpacePosition(0, 0, 0),
            false,
            null
        )
        {
            EpicArcId = Arc,
        };

    private static object? RoundTrip(BlueModel model) => BlueMarshal.Decode(BlueMarshal.Encode(model));

    private static Dictionary<string, object?> Fields(object? value)
    {
        var instance = Assert.IsType<BlueInstance>(value);
        Assert.Equal("utillib.KeyVal", instance.Name);
        return BlueRead.Fields(instance.State);
    }
}
