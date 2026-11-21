using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Protocol.Characters;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class JumpCloneServiceTests
{
    [Theory]
    [InlineData(InfomorphSkill.Psychology, 3, 22)]
    [InlineData(InfomorphSkill.Synchronizing, 2, 21)]
    public void SkillCompletionRefreshesCapacityAndCooldownInTheExistingGodmaCache(InfomorphSkill skill, int capacity, int hours)
    {
        using Fixture f = new();
        f.Select();
        var state = f.Db.Services.GetRequiredService<IPilotTraining>().Save(f.Context.Pilot.PilotId, [new((int)skill, 3)], true);
        f.Clock.UtcNow = new DateTimeOffset(DateTime.FromFileTimeUtc(Assert.Single(state.Queue).EndTime!.Value));
        var handler = f.Scope.ServiceProvider.GetRequiredService<SkillHandler>();
        Assert.Empty(handler.Advance(f.Context));
        var changes = f
            .Context.Notifications.OfType<ModuleAttributesChanged>()
            .SelectMany(n => n.Changes)
            .Where(c => c.Attribute is DogmaAttributeId.MaxJumpClones or DogmaAttributeId.CloneJumpCooldown)
            .ToArray();
        Assert.Equal(2, changes.Length);
        Assert.Equal(capacity, Assert.Single(changes, c => c.Attribute == DogmaAttributeId.MaxJumpClones).Value);
        Assert.Equal(hours, Assert.Single(changes, c => c.Attribute == DogmaAttributeId.CloneJumpCooldown).Value);
        Assert.All(changes, c => Assert.Equal(f.Context.Pilot.PilotId, Assert.IsType<DogmaItemKey.Item>(c.Item).Id));
        Assert.Single(f.Context.Notifications.OfType<ServerBrainUpdated>());
        f.Context.Notifications.Clear();
        Assert.Empty(handler.Advance(f.Context));
        Assert.Empty(f.Context.Notifications);
    }

    [Fact]
    public void NativeStatePreflightFeesNamesAndJournalRoundTripThroughTheSelectedRouter()
    {
        using Fixture f = new();
        Assert.Same(PilotCallResult.Unsupported, f.Call("jumpCloneSvc", "GetCloneState", []));
        f.Select();
        Assert.Equal(900000d, Reply(f.Call("jumpCloneSvc", "GetPriceForClone", [])));
        Assert.Empty(Assert.IsType<List<object?>>(Reply(f.Call("jumpCloneSvc", "ValidateInstallJumpClone", []))));
        Assert.Null(Reply(f.Call("jumpCloneSvc", "InstallCloneInStation", [])));
        Assert.Null(Reply(f.Call("jumpCloneSvc", "InstallCloneInStation", [])));
        var state = Fields(Reply(f.Call("jumpCloneSvc", "GetCloneState", [])));
        Assert.Null(state["timeLastJump"]);
        Assert.Empty(Rows(state["implants"]));
        var bodies = Rows(state["clones"]);
        Assert.Equal(2, bodies.Count);
        var first = Assert.IsType<object?[]>(bodies[0]);
        long id = Assert.IsType<long>(first[0]);
        Assert.Equal((long)HomeStationTestData.Origin, first[1]);
        Assert.Equal("", BlueMarshal.Text(first[2]));
        Assert.Null(Reply(f.Call("jumpCloneSvc", "SetJumpCloneName", [id, new BlueUnicode("Étoile 学習")])));
        state = Fields(Reply(f.Call("jumpCloneSvc", "GetCloneState", [])));
        Assert.Equal("Étoile 学習", BlueMarshal.Text(Assert.IsType<object?[]>(Rows(state["clones"])[0])[2]));
        var errors = Assert.IsType<List<object?>>(Reply(f.Call("jumpCloneSvc", "ValidateInstallJumpClone", [])));
        var error = Assert.IsType<object?[]>(Assert.Single(errors));
        Assert.Equal("UI/Messages/1016/JumpCharStoringMaxClonesBody", BlueMarshal.Text(error[0]));
        Assert.Equal(2L, BlueRead.Fields(error[1])["have"]);
        Assert.Equal(2L, BlueRead.Fields(error[1])["max"]);
        Assert.IsType<PilotCallResult.Failure>(f.Call("jumpCloneSvc", "InstallCloneInStation", []));
        Assert.Null(Reply(f.Call("jumpCloneSvc", "DestroyInstalledClone", [id])));
        Assert.Single(Rows(Fields(Reply(f.Call("jumpCloneSvc", "GetCloneState", [])))["clones"]));
        Assert.Equal(4, f.Context.Notifications.OfType<JumpCloneCacheInvalidated>().Count());
        Assert.Equal(4, f.Context.Notifications.OfType<StationJumpCloneCacheInvalidated>().Count());
        Assert.Equal(2, f.Context.Notifications.OfType<AccountBalanceChanged>().Count());
        var journal = Assert.IsType<BlueList<WalletJournalEntry>>(
            Assert.IsType<PilotCallResult.Success>(f.Call("account", "GetTransactions", [1000L, null, null, false])).Reply
        );
        Assert.Equal(2, journal.Items.Count);
        Assert.All(
            journal.Items,
            entry =>
            {
                Assert.Equal(WalletEntryType.JumpCloneInstallation, entry.Type);
                Assert.Equal(-JumpCloneRules.FeeCents, entry.AmountCents);
                Assert.Equal(HomeStationTestData.Origin, entry.ReferenceId);
                Assert.NotEmpty(BlueMarshal.Encode(entry));
            }
        );
        var prime = f.Scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>().PrimePilot(f.Context.Pilot);
        Assert.Equal(2, prime.Attributes[DogmaAttributeId.MaxJumpClones]);
        Assert.Equal(22, prime.Attributes[DogmaAttributeId.CloneJumpCooldown]);
    }

    [Fact]
    public void NativeStationCloneStateUsesTheBoundLocationAndRefreshesOnRename()
    {
        using Fixture f = new();
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        Assert.Same(PilotCallResult.Unsupported, f.Call("jumpCloneSvc", "GetStationCloneState", []));
        f.Select();
        Assert.Null(Reply(f.Call("jumpCloneSvc", "InstallCloneInStation", [])));
        HomeStationTestData.Travel(f.Db, HomeStationTestData.Other);
        f.Context.ChangeShip(f.Db.Store.FindPilot(1000001)!);
        Assert.Empty(Rows(Reply(f.Call("jumpCloneSvc", "GetStationCloneState", []))));
        Assert.Null(Reply(f.Call("jumpCloneSvc", "InstallCloneInStation", [])));

        var binding = new object?[] { (long)HomeStationTestData.Other, 15L };
        var boundCall = new object?[] { "GetStationCloneState", Array.Empty<object?>() };
        var bound = Assert.IsType<BoundPilotReply>(
            Assert.IsType<PilotCallResult.Success>(f.Call("jumpCloneSvc", "MachoBindObject", [binding, boundCall])).Reply
        );
        Assert.Equal("jumpCloneSvc", f.Context.ResolveReference(bound.Reference.Id));
        var rowset = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(bound.Reply)));
        Assert.Equal("eve.common.script.sys.rowset.Rowset", rowset.Name);
        Assert.Equal(
            new[] { "jumpCloneID", "locationID", "cloneName" },
            Assert.IsType<List<object?>>(Fields(rowset)["header"]).Select(BlueMarshal.Text)
        );
        var body = Assert.IsType<object?[]>(Assert.Single(Rows(rowset)));
        long id = Assert.IsType<long>(body[0]);
        Assert.Equal((long)HomeStationTestData.Other, body[1]);
        Assert.Equal("", BlueMarshal.Text(body[2]));
        Assert.IsType<PilotCallResult.Failure>(f.Call("jumpCloneSvc", "GetStationCloneState", [(long)HomeStationTestData.Origin]));

        f.Context.Notifications.Clear();
        Assert.Null(Reply(f.Call("jumpCloneSvc", "SetJumpCloneName", [id, new BlueUnicode("Étoile")])));
        Assert.Equal(
            new StationJumpCloneCacheInvalidated(HomeStationTestData.Other, f.Context.Pilot.PilotId),
            Assert.Single(f.Context.Notifications.OfType<StationJumpCloneCacheInvalidated>())
        );
        var renamed = Assert.IsType<object?[]>(Assert.Single(Rows(Reply(f.Call("jumpCloneSvc", "GetStationCloneState", [])))));
        Assert.Equal("Étoile", BlueMarshal.Text(renamed[2]));
        Assert.Equal(2, Rows(Fields(Reply(f.Call("jumpCloneSvc", "GetCloneState", [])))["clones"]).Count);

        f.Db.Store.SetPosition(f.Context.Pilot.ShipId, new(1_000_000, 0, 0), f.Context.Pilot.SystemId);
        Assert.Empty(Rows(Reply(f.Call("jumpCloneSvc", "GetStationCloneState", []))));
    }

    [Fact]
    public async Task NativeUnboardAndCloneJumpReplaceTheCapsuleAndReleaseOldStationReferencesBeforeTheSessionDelta()
    {
        using Fixture f = new();
        var clones = f.Db.Services.GetRequiredService<IPilotJumpClones>();
        clones.Install(JumpCloneTestData.PilotId);
        var target = Assert.Single(clones.Read(JumpCloneTestData.PilotId).Clones);
        HomeStationTestData.Travel(f.Db, HomeStationTestData.Other);
        f.Select();
        long hull = f.Context.Pilot.ShipId;
        Assert.Null(Reply(f.Call("ship", "LeaveShip", [hull])));
        Assert.NotEqual(hull, f.Context.Pilot.ShipId);
        var previous = f.Context.Pilot;
        f.Context.Notifications.Clear();
        var binding = f.Context.Reference("jumpCloneSvc", PilotBindingLifetime.Location);
        Assert.Null(Reply(f.Call("jumpCloneSvc", "CloneJump", [(long)target.StationId, target.Id, 900000d, false])));
        Assert.Equal(HomeStationTestData.Origin, f.Context.Pilot.StationId);
        Assert.Null(f.Context.ResolveReference(binding.Id));
        Assert.NotEqual(previous.ShipId, f.Context.Pilot.ShipId);
        Assert.Equal(2, f.Context.Notifications.OfType<ItemChanged>().Count());
        Assert.Single(f.Context.Notifications.OfType<JumpCloneCacheInvalidated>());
        Assert.Single(f.Context.Notifications.OfType<AccountBalanceChanged>());
        List<object?> packets = [];
        PilotSessionOutput output = new(
            f.Context,
            1000001,
            1,
            packet =>
            {
                packets.Add(packet);
                return Task.CompletedTask;
            },
            _ => { }
        );
        await output.AnnounceTransitionAsync(previous);
        var transition = Assert.IsType<BlueInstance>(packets[^1]);
        Assert.EndsWith(".SessionChangeNotification", transition.Name);
        var changes = BlueRead.Fields(BlueRead.Tuple(BlueRead.Tuple(BlueRead.Tuple(transition.State, 14)[4], 3)[1], 2)[1]);
        Assert.Equal(new object?[] { previous.ShipId, f.Context.Pilot.ShipId }, changes["shipid"]);
        Assert.Equal(new object?[] { HomeStationTestData.Other, HomeStationTestData.Origin }, changes["stationid"]);
        Assert.Empty(f.Context.Notifications);
        var journal = Assert.IsType<BlueList<WalletJournalEntry>>(
            Assert.IsType<PilotCallResult.Success>(f.Call("account", "GetTransactions", [1000L, null, null, false])).Reply
        );
        Assert.Equal(
            -JumpCloneRules.FeeCents,
            Assert.Single(journal.Items, e => e.Type == WalletEntryType.JumpCloneActivation).AmountCents
        );
        Assert.IsType<PilotCallResult.Failure>(f.Call("jumpCloneSvc", "CloneJump", [(long)target.StationId, target.Id, 900000d, true]));
    }

    [Fact]
    public void InvalidWireArgumentsAndForgedBindingsCannotChangeStateOrChargeFees()
    {
        using Fixture f = new();
        f.Select();
        foreach (
            var (method, args) in new (string, object?[])[]
            {
                ("GetCloneState", [90000002L]),
                ("GetPriceForClone", [1L]),
                ("ValidateInstallJumpClone", [1L]),
                ("InstallCloneInStation", [90000002L]),
                ("DestroyInstalledClone", ["1"]),
                ("SetJumpCloneName", [1L, true]),
                ("CloneJump", [long.MaxValue, 1L, 900000d, false]),
                ("CloneJump", [(long)HomeStationTestData.Origin, 1L, double.NaN, false]),
                ("CloneJump", [(long)HomeStationTestData.Origin, 1L, 900000d, "yes"]),
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(f.Call("jumpCloneSvc", method, args));
        }
        Assert.IsType<PilotCallResult.Failure>(f.Call("jumpCloneSvc", "GetCloneState", [], new() { ["pilotID"] = 90000002L }));
        Assert.Same(PilotCallResult.Unsupported, f.Call("jumpCloneSvc", "InstallCloneInStructure", []));
        foreach (
            object? location in new object?[]
            {
                null,
                new object?[] { (long)HomeStationTestData.Other, 15L },
                new object?[] { (long)HomeStationTestData.Origin, 5L },
            }
        )
        {
            Assert.Same(PilotCallResult.Unsupported, f.Call("jumpCloneSvc", "MachoBindObject", [location, null]));
        }
        Assert.IsType<PilotCallResult.Success>(
            f.Call("jumpCloneSvc", "MachoBindObject", [new object?[] { (long)HomeStationTestData.Origin, 15L }, null])
        );
        Assert.Empty(f.Db.Services.GetRequiredService<IPilotJumpClones>().Read(JumpCloneTestData.PilotId).Clones);
        Assert.Equal(JumpCloneTestData.Funds, f.Db.Services.GetRequiredService<IPilotWalletReader>().Balance(JumpCloneTestData.PilotId));
        Assert.Empty(f.Context.Notifications);
    }

    private static List<object?> Rows(object? value) => Assert.IsType<List<object?>>(Fields(value)["lines"]);

    private static Dictionary<string, object?> Fields(object? value) => BlueRead.Fields(Assert.IsType<BlueInstance>(value).State);

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));

    private sealed class Fixture : IDisposable
    {
        public TestClock Clock { get; } = new();
        public TestWorld Db { get; }
        public IServiceScope Scope { get; }
        public PilotCallContext Context { get; }

        public Fixture()
        {
            Db = new(clock: Clock);
            JumpCloneTestData.Prepare(Db);
            Scope = Db.Services.CreateScope();
            var pilot = Db.Store.FindPilot(1000001)!;
            Context = new(1000001, pilot.Account, [pilot], p => new(Db.Store, p, Clock, new(), Db.Objects), 42, SignedSettings.Empty);
        }

        public void Select() => Context.Select(Db.Store.FindPilot(1000001)!);

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
