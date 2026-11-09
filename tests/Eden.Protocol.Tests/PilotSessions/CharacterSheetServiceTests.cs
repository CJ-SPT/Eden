using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Skills;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Characters;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CharacterSheetServiceTests
{
    [Theory]
    [InlineData(90000001L)]
    [InlineData(90000002L)]
    public void ShowInfoReadsSavedPublicProfileAndCorporationAcrossAccountsAndReopen(long target)
    {
        using Fixture fixture = new();
        const string name = "星間 Étoile";
        const string biography = "<b>Explorer</b> — пилот 🚀";
        const long born = 134010000000000000L;
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            var pilot = world.Pilots.Find(target)!;
            pilot.Name = name;
            pilot.CreatedAt = born;
            pilot.RaceId = 8;
            pilot.BloodlineId = 11;
            pilot.AncestryId = 14;
            pilot.GenderId = 0;
            world.SaveChanges();
        }
        fixture.Db.Services.GetRequiredService<IPilotCharacterSheet>().SaveBiography(target, biography);
        fixture.Context.Select(fixture.Db.Store.FindPilot(1000001)!);
        AssertPublicProfile(fixture.Call("charMgr", "GetPublicInfo3", [target]), target, name, biography, born);
        var legacy = BlueRead.Fields(Assert.IsType<BlueInstance>(Reply(fixture.Call("charMgr", "GetPublicInfo", [target]))).State);
        Assert.Equal(target, legacy["characterID"]);
        Assert.Equal(name, legacy["characterName"]);
        Assert.Equal(born, legacy["createDateTime"]);
        Assert.DoesNotContain("description", legacy.Keys);
        var corporationReply = Assert.IsType<BlueInstance>(Reply(fixture.Call("corpRegistry", "GetInfoWindowDataForChar", [target])));
        var corporation = BlueRead.Fields(corporationReply.State);
        Assert.Equal(1000044L, corporation["corpID"]);
        Assert.Null(corporation["allianceID"]);
        Assert.Null(corporation["militiaFactionID"]);
        Assert.Equal("", corporation["title"]);
        for (int index = 1; index <= 16; index++)
        {
            Assert.Null(corporation[$"title{index}"]);
        }
        Assert.Equal(20, corporation.Count);
        Assert.Equal(90000001L, fixture.Context.Pilot.PilotId);
        Assert.Empty(fixture.Context.Notifications);

        using var reopened = ServerServices.Build(new(fixture.Db.Path), fixture.Clock);
        using var scope = reopened.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        AssertPublicProfile(
            router.Call(fixture.Context, "charMgr", "GetPublicInfo3", new object?[] { target }, _ => { }),
            target,
            name,
            biography,
            born
        );
    }

    [Theory]
    [InlineData("charMgr", "GetPublicInfo")]
    [InlineData("charMgr", "GetPublicInfo3")]
    [InlineData("corpRegistry", "GetInfoWindowDataForChar")]
    [InlineData("corporationSvc", "GetMedalsReceived")]
    [InlineData("corporationSvc", "GetEmployementRecordAndCharacterTransfers")]
    [InlineData("facWarMgr", "GetCharacterRankInfo")]
    public void PublicInfoRequiresSelectionAndValidArgumentsWithoutChangingEitherPilot(string service, string method)
    {
        using Fixture fixture = new();
        Assert.Same(PilotCallResult.Unsupported, fixture.Call(service, method, [90000002L]));
        fixture.Context.Select(fixture.Db.Store.FindPilot(1000001)!);
        foreach (object?[] args in new object?[][] { [], ["90000002"], [0L], [-1L], [123L], [long.MaxValue], [90000002L, true] })
        {
            Assert.IsType<PilotCallResult.Failure>(fixture.Call(service, method, args));
        }
        Assert.IsType<PilotCallResult.Failure>(fixture.Call(service, method, [90000002L], new() { ["unexpected"] = true }));
        Assert.IsType<PilotCallResult.Success>(fixture.Call(service, method, [90000002L], new() { ["machoVersion"] = 1L }));
        var sheet = fixture.Db.Services.GetRequiredService<IPilotCharacterSheet>();
        Assert.Equal("", sheet.ReadBiography(90000001));
        Assert.Equal("", sheet.ReadBiography(90000002));
        using WorldDbContext world = new(fixture.Db.Path);
        Assert.Empty(world.Set<SavedCharacterSheet>());
        Assert.Empty(fixture.Context.Notifications);
    }

    private static void AssertPublicProfile(PilotCallResult result, long target, string name, string biography, long born)
    {
        var rowset = Assert.IsType<BlueInstance>(Reply(result));
        Assert.Equal("eve.common.script.sys.rowset.Rowset", rowset.Name);
        var state = BlueRead.Fields(rowset.State);
        var header = Assert.IsType<List<object?>>(state["header"]).Select(BlueMarshal.Text).ToArray();
        var line = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(state["lines"])));
        var fields = header.Zip(line).ToDictionary(p => p.First, p => p.Second);
        Assert.Equal(11, fields.Count);
        Assert.Equal(target, fields["characterID"]);
        Assert.Equal(name, fields["characterName"]);
        Assert.Equal(1000044L, fields["corporationID"]);
        Assert.Equal(8L, fields["raceID"]);
        Assert.Equal(11L, fields["bloodlineID"]);
        Assert.Equal(0L, fields["gender"]);
        Assert.Equal(19L, fields["schoolID"]);
        Assert.Equal(14L, fields["ancestryID"]);
        Assert.Equal(born, fields["startDateTime"]);
        // The native header reads this on the public row after the visible corporation header,
        // before UpdateDataCharacter or construct_main_tabs can run. It must exist even when None.
        Assert.Null(fields["militiaFactionID"]);
        Assert.Equal(biography, fields["description"]);
    }

    [Theory]
    [InlineData(90000001L)]
    [InlineData(90000002L)]
    public void ShowInfoDecorationAndDefaultEmploymentTabReadsUseNativeShapesWithoutCreatingState(long target)
    {
        using Fixture fixture = new();
        fixture.Context.Select(fixture.Db.Store.FindPilot(1000001)!);
        var profile = fixture.Db.Services.GetRequiredService<IPilotCharacterSheet>().ReadPublicProfile(target)!;
        // UpdateDataCharacter runs these decoration reads before constructing tabs, even without any medals.
        for (int read = 0; read < 3; read++)
        {
            var medals = BlueRead.Tuple(Reply(fixture.Call("corporationSvc", "GetMedalsReceived", [target])), 2);
            AssertEmptyRowset(medals[0], ["medalID", "status"]);
            AssertEmptyRowset(medals[1], ["medalID"]);
        }
        Assert.Null(Reply(fixture.Call("facWarMgr", "GetCharacterRankInfo", [target])));
        Assert.Null(Reply(fixture.Call("facWarMgr", "GetMyCharacterRankInfo", [])));

        // A blank biography can cause Employment History to be selected first.
        var history = BlueRead.Tuple(Reply(fixture.Call("corporationSvc", "GetEmployementRecordAndCharacterTransfers", [target])), 2);
        var job = BlueRead.Fields(Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(history[0]))).State);
        Assert.Equal((long)profile.CorporationId, job["corporationID"]);
        Assert.Equal(profile.CreatedAt, job["startDate"]);
        Assert.Equal(false, job["deleted"]);
        Assert.Empty(Assert.IsType<List<object?>>(history[1]));
        using WorldDbContext world = new(fixture.Db.Path);
        Assert.Empty(world.Set<SavedCharacterSheet>());
        Assert.Equal(profile, fixture.Db.Services.GetRequiredService<IPilotCharacterSheet>().ReadPublicProfile(target));
        Assert.Empty(fixture.Context.Notifications);
    }

    [Fact]
    public void OwnPublicRankRequiresSelectionAndNoArguments()
    {
        using Fixture fixture = new();
        Assert.Same(PilotCallResult.Unsupported, fixture.Call("facWarMgr", "GetMyCharacterRankInfo", []));
        fixture.Context.Select(fixture.Db.Store.FindPilot(1000001)!);
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("facWarMgr", "GetMyCharacterRankInfo", [90000002L]));
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("facWarMgr", "GetMyCharacterRankInfo", [], new() { ["unexpected"] = true }));
        Assert.Null(Reply(fixture.Call("facWarMgr", "GetMyCharacterRankInfo", [], new() { ["machoVersion"] = 1L })));
        Assert.Empty(fixture.Context.Notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnRankOverviewReturnsAnEmptyListWithoutAwardingRanks(bool inSpace)
    {
        using Fixture fixture = new();
        const string service = "facWarMgr";
        const string method = "GetMyCharacterRankOverview";
        Assert.Same(PilotCallResult.Unsupported, fixture.Call(service, method, []));
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        if (inSpace)
        {
            fixture.Db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero);
            pilot = fixture.Db.Store.FindPilot(1000001)!;
        }
        fixture.Context.Select(pilot);
        var sheet = fixture.Db.Services.GetRequiredService<IPilotCharacterSheet>();
        var before = sheet.ReadPublicProfile(pilot.PilotId);
        Assert.Empty(Assert.IsType<List<object?>>(Reply(fixture.Call(service, method, []))));
        Assert.Empty(Assert.IsType<List<object?>>(Reply(fixture.Call(service, method, [], new() { ["machoVersion"] = 1L }))));
        foreach (object?[] args in new object?[][] { [90000002L], [null], [true], ["90000001"], [90000001L, 90000002L] })
        {
            Assert.IsType<PilotCallResult.Failure>(fixture.Call(service, method, args));
        }
        Assert.IsType<PilotCallResult.Failure>(fixture.Call(service, method, [], new() { ["unexpected"] = true }));
        Assert.Equal(before, sheet.ReadPublicProfile(pilot.PilotId));
        Assert.Null(Reply(fixture.Call(service, "GetMyCharacterRankInfo", [])));
        Assert.Empty(fixture.Context.Notifications);
        using WorldDbContext saved = new(fixture.Db.Path);
        Assert.Empty(saved.Set<SavedCharacterSheet>());
    }

    private static void AssertEmptyRowset(object? value, string[] expectedHeader)
    {
        var rowset = Assert.IsType<BlueInstance>(value);
        Assert.Equal("eve.common.script.sys.rowset.Rowset", rowset.Name);
        var fields = BlueRead.Fields(rowset.State);
        Assert.Equal(expectedHeader, Assert.IsType<List<object?>>(fields["header"]).Select(BlueMarshal.Text));
        Assert.Empty(Assert.IsType<List<object?>>(fields["lines"]));
    }

    [Fact]
    public void BiographyUsesUnicodeAndCanOnlyEditTheSelectedCharacter()
    {
        using Fixture fixture = new();
        var context = fixture.Context;
        Assert.IsType<PilotCallResult.UnsupportedCall>(fixture.Call("charMgr", "SetCharacterDescription", ["Before selection"]));
        context.Select(fixture.Db.Store.FindPilot(1000001)!);
        const string biography = "<b>星間旅行</b><br>Explorer — пилот 🚀";
        Assert.Null(Reply(fixture.Call("charMgr", "SetCharacterDescription", [biography])));
        Assert.Equal(biography, Reply(fixture.Call("charMgr", "GetCharacterDescription", [90000001L])));
        Assert.Equal("", Reply(fixture.Call("charMgr", "GetCharacterDescription", [90000002L])));
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("charMgr", "SetCharacterDescription", [90000002L, "Forged target"]));
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("charMgr", "SetCharacterDescription", [new string('x', 2501)]));
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("charMgr", "GetCharacterDescription", [123L]));
        Assert.Equal(biography, Reply(fixture.Call("charMgr", "GetCharacterDescription", [90000001L])));
        Assert.Empty(context.Notifications);
        Assert.Null(Reply(fixture.Call("charMgr", "SetCharacterDescription", [""])));
        Assert.Equal("", Reply(fixture.Call("charMgr", "GetCharacterDescription", [90000001L])));
    }

    [Fact]
    public void NativeRemapUpdatesAttributesDogmaTrainingDeadlineAndClientCaches()
    {
        using Fixture fixture = new();
        var context = fixture.Context;
        context.Select(fixture.Db.Store.FindPilot(1000001)!);
        var training = fixture.Db.Services.GetRequiredService<IPilotTraining>();
        training.Save(context.Pilot.PilotId, [new(3327, 4)], true);
        Reply(fixture.Call("@skills", "GetSkillQueueAndFreePoints", []));
        long previous = context.NextSkillCompletion!.Value;
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        CharacterAttributes attributes = new(17, 17, 17, 27, 21);
        Assert.Null(Reply(fixture.Call("@skills", "RespecCharacter", [Wire(attributes)])));
        Assert.True(context.NextSkillCompletion < previous);
        var dogma = fixture.Scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>().PrimePilot(context.Pilot);
        var expected = attributes.ToDogma().ToDictionary();
        expected[DogmaAttributeId.MaxJumpClones] = 0;
        expected[DogmaAttributeId.CloneJumpCooldown] = JumpCloneRules.BaseCooldownHours;
        expected[DogmaAttributeId.MaxActiveDrones] = 0;
        expected[DogmaAttributeId.DroneControlDistance] = (int)DroneRules.BaseControlRange;
        Assert.Equal(expected, dogma.Attributes);
        var values = Assert.IsType<Dictionary<long, object?>>(Reply(fixture.Call("@skills", "GetAttributes", [])));
        Assert.Equal(5, values.Count);
        Assert.Equal(27L, values[(long)DogmaAttributeId.Perception]);
        Assert.Equal(17L, values[(long)DogmaAttributeId.Charisma]);
        var remap = BlueRead.Fields(Reply(fixture.Call("@skills", "GetRespecInfo", [])));
        Assert.Equal(2L, remap["freeRespecs"]);
        Assert.Equal(fixture.Clock.Now + TimeSpan.FromDays(365).Ticks, remap["nextTimedRespec"]);
        Assert.Equal(
            new[]
            {
                "OnModuleAttributeChanges",
                "OnRespecInfoChanged",
                "OnServerSkillsChanged",
                "OnServerBrainUpdated",
                "OnModuleAttributeChanges",
                "OnNewSkillQueueSaved",
            },
            context.Notifications.Select(n => n.Name)
        );
        var changes = Assert.IsType<List<object?>>(Assert.Single(context.Notifications[0].Arguments));
        Assert.Equal(5, changes.Count);
        Assert.All(
            changes,
            change =>
            {
                var fields = Assert.IsType<object?[]>(change);
                Assert.Equal("OnModuleAttributeChange", fields[0]);
                Assert.Equal(context.Pilot.PilotId, fields[1]);
                Assert.Equal(context.Pilot.PilotId, fields[2]);
            }
        );
        Assert.Empty(context.Notifications[1].Arguments);
        var queue = Assert.IsType<List<object?>>(
            BlueMarshal.Decode(BlueMarshal.Encode(Assert.Single(context.Notifications[^1].Arguments)))
        );
        var head = BlueRead.Fields(Assert.IsType<BlueInstance>(Assert.Single(queue)).State);
        Assert.Equal(context.NextSkillCompletion, head["trainingEndTime"]);
        context.Notifications.Clear();
        Assert.Null(Reply(fixture.Call("@skills", "RespecCharacter", [Wire(attributes)])));
        Assert.Empty(context.Notifications);
        Assert.Equal(context.NextSkillCompletion, head["trainingEndTime"]);
    }

    [Fact]
    public void RemapRejectsMalformedValuesAndCooldownWithoutMutatingStateOrSendingNotifications()
    {
        using Fixture fixture = new();
        fixture.Context.Select(fixture.Db.Store.FindPilot(1000001)!);
        var sheet = fixture.Db.Services.GetRequiredService<IPilotCharacterSheet>();
        CharacterAttributes first = new(17, 17, 17, 27, 21);
        foreach (
            object?[] args in new object?[][]
            {
                [],
                [Wire(first), 90000002L],
                [new Dictionary<long, object?>()],
                [Wire(first).ToDictionary(p => p.Key, p => (object?)27.5)],
                [Wire(first).ToDictionary(p => p.Key + 1000, p => p.Value)],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(fixture.Call("@skills", "RespecCharacter", args));
            Assert.Equal(NeuralRemapState.Default, sheet.ReadAttributes(fixture.Context.Pilot.PilotId));
        }
        var invalid = Assert.IsType<PilotCallResult.NamedFailure>(
            fixture.Call("@skills", "RespecCharacter", [Wire(first with { Perception = 28 })])
        );
        Assert.Equal("RespecAttributesTooHigh", invalid.Name);
        Assert.Empty(BlueRead.Fields(invalid.Values.ToBlue()));
        Assert.Empty(fixture.Context.Notifications);
        foreach (var attributes in new[] { first, CharacterAttributes.Default, first })
        {
            Assert.Null(Reply(fixture.Call("@skills", "RespecCharacter", [Wire(attributes)])));
        }
        var before = sheet.ReadAttributes(fixture.Context.Pilot.PilotId);
        fixture.Context.Notifications.Clear();
        var error = Assert.IsType<PilotCallResult.NamedFailure>(
            fixture.Call("@skills", "RespecCharacter", [Wire(CharacterAttributes.Default)])
        );
        Assert.Equal("RespecTooSoon", error.Name);
        Assert.Equal(before.NextTimedRemap, BlueRead.Fields(error.Values.ToBlue())["nextTime"]);
        Assert.Equal(before, sheet.ReadAttributes(fixture.Context.Pilot.PilotId));
        Assert.Equal(NeuralRemapState.Default, sheet.ReadAttributes(90000002));
        Assert.Empty(fixture.Context.Notifications);
    }

    private static Dictionary<long, object?> Wire(CharacterAttributes attributes) =>
        attributes.ToDogma().ToDictionary(p => (long)p.Key, p => (object?)(long)p.Value);

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));

    private sealed class Fixture : IDisposable
    {
        public TestClock Clock { get; } = new(new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero));
        public TestWorld Db { get; }
        public IServiceScope Scope { get; }
        public PilotCallContext Context { get; }

        public Fixture()
        {
            Db = new(clock: Clock);
            Scope = Db.Services.CreateScope();
            var pilot = Db.Store.FindPilot(1000001)!;
            Context = new(1000001, pilot.Account, [pilot], p => new(Db.Store, p, Clock, new(), Db.Objects), 42, SignedSettings.Empty);
        }

        public PilotCallResult Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            Scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public void Dispose()
        {
            Context.Dispose();
            Scope.Dispose();
            Db.Dispose();
        }
    }
}
