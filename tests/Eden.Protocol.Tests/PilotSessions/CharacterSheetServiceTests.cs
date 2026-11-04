using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Skills;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CharacterSheetServiceTests
{
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
        Assert.Equal(attributes.ToDogma(), dogma.Attributes);
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

        public PilotCallResult Call(string service, string method, object?[] args) =>
            Scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { });

        public void Dispose()
        {
            Context.Dispose();
            Scope.Dispose();
            Db.Dispose();
        }
    }
}
