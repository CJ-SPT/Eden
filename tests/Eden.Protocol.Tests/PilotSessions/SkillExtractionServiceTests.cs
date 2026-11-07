using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Skills;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class SkillExtractionServiceTests
{
    private static Dictionary<long, object?> Selection(object? points = null) =>
        new() { [SkillExtractionTestData.Filler] = points ?? 500000L };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeExtractionAcceptsIntegerAndIntegralFloatAmountsAndUpdatesBothSessions(bool floating)
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        using CourierServiceTests.Client writer = new(db, clock, 1000001);
        using CourierServiceTests.Client observer = new(db, clock, 1000001);
        long item = SkillExtractionTestData.Item(db);
        Assert.Same(PilotCallResult.Unsupported, writer.Call("ExtractSkills", [Selection(), item, ""], "@skills"));
        writer.Refresh();
        observer.Refresh();
        writer.Context.Notifications.Clear();
        observer.Context.Notifications.Clear();
        object amount = floating ? (object)500000d : 500000L;
        Assert.Null(WireAssertions.Materialize(writer.Call("ExtractSkills", [Selection(amount), item, ""], "@skills")));
        Assert.Equal(2, writer.Context.Notifications.Count(n => n.Name == "OnItemChange"));
        var removed = Assert.Single(writer.Context.Notifications.OfType<ServerSkillsRemoved>());
        var args = removed.Arguments;
        Assert.NotEmpty(BlueMarshal.Encode(args));
        var info = Assert.IsType<Dictionary<long, object?>>(args[0]);
        var skill = Assert.IsType<BlueConstructor>(info[SkillExtractionTestData.Filler]);
        Assert.Equal(7_500_000L, skill.Arguments[2]);
        Assert.Equal(clock.Now, args[1]);
        Assert.Single(writer.Context.Notifications.OfType<ServerBrainUpdated>());
        Assert.Single(writer.Context.Notifications.OfType<SkillQueueSaved>());
        Assert.Empty(writer.Context.Notifications.OfType<FreeSkillPointsChanged>());
        Assert.Empty(writer.Context.Notifications.OfType<ServerSkillsChanged>());
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        observer.Advance();
        Assert.Single(observer.Context.Notifications.OfType<ServerSkillsRemoved>());
        Assert.Single(observer.Context.Notifications.OfType<ServerBrainUpdated>());
        Assert.Single(observer.Context.Notifications.OfType<SkillQueueSaved>());
        observer.Context.Notifications.Clear();
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        observer.Advance();
        Assert.Empty(observer.Context.Notifications.OfType<ServerSkillsRemoved>());
        writer.Context.Notifications.Clear();
        writer.Advance();
        Assert.Empty(writer.Context.Notifications.OfType<ServerSkillsRemoved>());
    }

    [Fact]
    public void FirstCallWithoutCoordinatorInitializationStillNotifiesAndInvalidRequestsNeverConsumeStock()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        client.Context.Select(db.Store.FindPilot(1000001)!);
        long item = SkillExtractionTestData.Item(db);
        object?[][] bad =
        [
            [Selection(), item],
            [Selection(), item, null],
            [Selection(), item, 1L],
            [Selection(), -1L, ""],
            [Selection(500000.1d), item, ""],
            [Selection(double.NaN), item, ""],
            [Selection(double.PositiveInfinity), item, ""],
            [Selection(long.MaxValue), item, ""],
            [Selection(-1L), item, ""],
            [new Dictionary<long, object?> { [-1] = 500000L }, item, ""],
            [new Dictionary<string, object?> { ["skill"] = 500000L }, item, ""],
            [new Dictionary<long, object?>(), item, ""],
        ];
        foreach (var args in bad)
        {
            Assert.IsType<PilotCallResult.Failure>(client.Call("ExtractSkills", args, "@skills"));
        }
        Assert.IsType<PilotCallResult.Failure>(
            client.Call("ExtractSkills", [Selection(), item, ""], "@skills", new() { ["pilotID"] = 90000002L })
        );
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Equal(1, world.Set<OwnedItem>().Find(item)!.Quantity);
            Assert.Empty(world.Set<SavedSkillExtraction>());
        }
        client.Context.Notifications.Clear();
        Assert.Null(WireAssertions.Materialize(client.Call("ExtractSkills", [Selection(), item, Array.Empty<byte>()], "@skills")));
        Assert.Single(client.Context.Notifications.OfType<ServerSkillsRemoved>());
        Assert.Equal(7_500_000L, WireAssertions.Materialize(client.Call("GetSkillPoints", [], "@skills")));
    }

    [Fact]
    public void NativeMinimumErrorIncludesItsRequiredParametersAndACompletedExtractionCannotBeReplayed()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        SkillInjectorTestData.Total(db, 5_499_999);
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        client.Refresh();
        long item = SkillExtractionTestData.Item(db);
        var error = Assert.IsType<PilotCallResult.NamedFailure>(client.Call("ExtractSkills", [Selection(), item, ""], "@skills"));
        Assert.Equal("SkillExtractionNotEnoughSP", error.Name);
        var values = Assert.IsType<Dictionary<string, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(error.Values)));
        Assert.Equal(5_500_000L, values["limit"]);
        Assert.Equal(40519L, values["extractor"]);
        SkillInjectorTestData.Total(db, 8_000_000);
        Assert.Null(WireAssertions.Materialize(client.Call("ExtractSkills", [Selection(), item, ""], "@skills")));
        client.Context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Failure>(client.Call("ExtractSkills", [Selection(), item, ""], "@skills"));
        Assert.Empty(client.Context.Notifications);
    }

    [Theory]
    [InlineData(InfomorphSkill.Psychology, 100000, 14, 19)]
    [InlineData(InfomorphSkill.AdvancedPsychology, 500000, 14, 19)]
    [InlineData(InfomorphSkill.ElitePsychology, 500000, 14, 19)]
    [InlineData(InfomorphSkill.Synchronizing, 500000, 15, 22)]
    public void SkillLossUpdatesNativeCloneCapacityAndCooldown(InfomorphSkill skill, long removed, int capacity, int cooldown)
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        JumpCloneTestData.Train(db, AccountCloneGrade.Omega, new(5, 5, 5, 5));
        using CourierServiceTests.Client client = new(db, clock, 1000001);
        client.Refresh();
        client.Context.Notifications.Clear();
        Dictionary<long, object?> selected = new() { [(int)skill] = removed };
        if (removed < SkillExtractionRules.Points)
        {
            selected[SkillExtractionTestData.Filler] = SkillExtractionRules.Points - removed;
        }
        Assert.Null(WireAssertions.Materialize(client.Call("ExtractSkills", [selected, SkillExtractionTestData.Item(db), ""], "@skills")));
        var changes = client.Context.Notifications.OfType<ModuleAttributesChanged>().SelectMany(n => n.Changes).ToArray();
        Assert.Equal(capacity, Assert.Single(changes, c => c.Attribute == DogmaAttributeId.MaxJumpClones).Value);
        Assert.Equal(cooldown, Assert.Single(changes, c => c.Attribute == DogmaAttributeId.CloneJumpCooldown).Value);
    }
}
