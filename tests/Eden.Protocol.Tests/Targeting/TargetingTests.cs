using Eden.Data.Catalog;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Targeting;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Clock = Eden.Protocol.Tests.Fixtures.TestClock;

namespace Eden.Protocol.Tests.Targeting;

public sealed class TargetingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeScanRequestWaitsForCompletionAndOtherCallsRemainAvailable(bool success)
    {
        using Fixture fixture = new();
        var (context, targeting) = fixture.Client(1000001);
        using var scope = fixture.Db.Services.CreateScope();
        List<BlueInstance> packets = [];
        PilotSessionOutput output = new(
            context,
            1000001,
            42,
            packet =>
            {
                packets.Add(Assert.IsType<BlueInstance>(packet));
                return Task.CompletedTask;
            },
            _ => { }
        );
        PilotSessionCalls calls = new(
            context,
            output,
            new PilotCallRouter([], [targeting]),
            scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>(),
            scope.ServiceProvider.GetRequiredService<SkillHandler>(),
            fixture.Clock,
            _ => { },
            1000001,
            42
        );
        var request = PilotServicesTests.Request("dogmaIM", "AddTarget", [70000001L]);
        await calls.ProcessAsync(request);
        Assert.DoesNotContain(packets, p => p.Name.EndsWith(".CallRsp", StringComparison.Ordinal));
        packets.Clear();
        await calls.ProcessAsync(PilotServicesTests.Request("dogmaIM", "GetTargets", []));
        Assert.Single(packets, p => p.Name.EndsWith(".CallRsp", StringComparison.Ordinal));
        packets.Clear();
        if (success)
        {
            fixture.Clock.Seconds = 10;
            targeting.Advance(context);
        }
        else
        {
            targeting.Call(context, "dogmaIM", "CancelAddTarget", [70000001L]);
        }
        await output.FlushNotificationsAsync();
        var reply = Assert.Single(packets, p => p.Name.EndsWith(success ? ".CallRsp" : ".ErrorResponse", StringComparison.Ordinal));
        var incoming = IncomingMachoPacket.Read(request);
        var expected = success
            ? incoming.CallSucceeded(new TargetLockReply(true, [70000001]))
            : incoming.UserError(new("TargetingAttemptCancelled", BlueEmpty.Dictionary));
        Assert.Equal(BlueMarshal.Encode(expected), BlueMarshal.Encode(reply));
        int count = packets.Count;
        await output.FlushNotificationsAsync();
        Assert.Equal(count, packets.Count);
        Assert.Equal(success, targeting.IsLocked(70000001));
    }

    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; } = new(liveWorld: true);
        public Clock Clock { get; } = new();
        public SharedSpace Space { get; } = new();
        public SharedTargetLocks Locks { get; } = new();
        public IPilotFitting Fitting { get; }
        public IReadOnlyList<StaticSpaceObject> Objects { get; }
        private readonly List<(PilotCallContext Context, TargetingHandler Handler)> _clients = [];

        public Fixture()
        {
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT INTO DogmaAttribute(Id,Name) VALUES (76,'maxTargetRange'),(192,'maxLockedTargets'),(552,'signatureRadius'),
                        (564,'scanResolution');
                    INSERT INTO TypeAttribute VALUES (601,76,27000),(601,192,3),(601,552,52),(601,564,475);
                    INSERT INTO ItemType(Id,Name,GroupId) VALUES (3428,'Long Range Targeting',257),(3429,'Target Management',257),
                        (3430,'Advanced Target Management',257),(3431,'Signature Analysis',257);
                    INSERT INTO TypeAttribute VALUES (3428,275,2),(3429,275,1),(3430,275,3),(3431,275,1);
                    INSERT INTO ItemCategory(Id,Name) VALUES (25,'Asteroid');
                    INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (462,'Veldspar',25);
                    INSERT INTO ItemType(Id,Name,GroupId) VALUES (1230,'Veldspar',462);
                    INSERT INTO TypeAttribute VALUES (1230,552,100);
                    """
                );
            }
            Fitting = Db.Services.GetRequiredService<IPilotFitting>();
            var pilot = Db.Store.FindPilot(1000001)!;
            var launch = StationUndocking.Launch(pilot.ShipId, pilot.Home, pilot.Flight).Position;
            Objects = Db
                .Objects.Concat(
                    Enumerable
                        .Range(1, 3)
                        .Select(index => new StaticSpaceObject(
                            70000000 + index,
                            1230,
                            462,
                            TargetingDefinitions.AsteroidCategory,
                            1,
                            launch + new SpacePosition(1000 * index, 0, 0),
                            50
                        ))
                )
                .Append(new(70000004, 1230, 462, TargetingDefinitions.AsteroidCategory, 1, launch + new SpacePosition(100000, 0, 0), 50))
                .ToArray();
        }

        public (PilotCallContext Context, TargetingHandler Handler) Client(long account, bool undock = true)
        {
            var pilot = Db.Store.FindPilot(account)!;
            PilotCallContext context = new(
                account,
                pilot.Account,
                [pilot],
                selected => new(Db.Store, selected, Clock, Space, Objects, docking: Db.Store),
                account,
                SignedSettings.Empty
            );
            context.Select(pilot);
            if (undock)
            {
                context.Flight.CallWire("ship", "Undock", [pilot.ShipId]);
                context.Flight.CallWire("beyonce", "CmdStop", []);
                context.Flight.Snapshot();
            }
            TargetingHandler handler = new(Clock, Fitting, Locks);
            _clients.Add((context, handler));
            return (context, handler);
        }

        public void Dispose()
        {
            foreach (var (context, handler) in _clients)
            {
                handler.Dispose();
                context.Dispose();
            }
            Db.Dispose();
        }
    }

    [Fact]
    public void DeclinedCallsCannotCheckpointOrCompleteALock()
    {
        using Fixture fixture = new();
        var (context, handler) = fixture.Client(1000001);
        Assert.IsType<PilotCallResult.Success>(handler.Call(context, "dogmaIM", "AddTarget", [70000001L]));
        context.Flight.CallWire("beyonce", "CmdGotoDirection", [1d, 0d, 0d]);
        var position = context.Pilot.Position;
        context.Notifications.Clear();
        context.Flight.Updates.Clear();
        fixture.Clock.Seconds += 60;
        Assert.Same(PilotCallResult.Unsupported, handler.Call(context, "dogmaIM", "Unknown", []));
        Assert.Same(PilotCallResult.Unsupported, handler.Call(context, "dogmaIM", "AddTarget", ["invalid"]));
        Assert.Equal(position, context.Pilot.Position);
        Assert.False(handler.IsLocked(70000001));
        Assert.Empty(context.Notifications);
        Assert.Empty(context.Flight.Updates);
        handler.Advance(context);
        Assert.True(handler.IsLocked(70000001));
    }

    [Fact]
    public void LockUsesScanTimeAndNotifiesBothPilotsUntilUnlocked()
    {
        using Fixture fixture = new();
        var (pilot, targeting) = fixture.Client(1000001);
        var (wing, observed) = fixture.Client(1000002);
        long target = wing.Pilot.ShipId;
        object? Call(string method, params object?[] args) => targeting.CallWire(pilot, "dogmaIM", method, args);
        Assert.Equal(new object?[] { true, new List<object?>() }, Assert.IsType<object?[]>(Call("AddTarget", target)));
        fixture.Clock.Seconds = 3;
        targeting.Advance(pilot);
        Assert.Empty(Assert.IsType<List<object?>>(Call("GetTargets")));
        Assert.Empty(pilot.Notifications);
        _ = Call("AddTarget", target); // A retry must not restart the scan timer.
        fixture.Clock.Seconds = 4;
        targeting.Advance(pilot);
        Assert.True(targeting.IsLocked(target));
        Assert.Equal(new object?[] { target }, Assert.IsType<List<object?>>(Call("GetTargets")));
        Assert.Equal(new object?[] { "add", target, null }, Assert.Single(pilot.Notifications).Arguments);
        observed.Advance(wing);
        Assert.Equal(new object?[] { "otheradd", pilot.Pilot.ShipId, null }, Assert.Single(wing.Notifications).Arguments);
        Assert.Equal(
            new object?[] { pilot.Pilot.ShipId },
            Assert.IsType<List<object?>>(observed.CallWire(wing, "dogmaIM", "GetTargeters", []))
        );
        var duplicate = Assert.IsType<object?[]>(Call("AddTarget", target));
        Assert.Equal(false, duplicate[0]);
        Assert.Single(pilot.Notifications);
        pilot.Notifications.Clear();
        wing.Notifications.Clear();
        Assert.Null(Call("RemoveTarget", target));
        Assert.Equal(new object?[] { "lost", target, null }, Assert.Single(pilot.Notifications).Arguments);
        observed.Advance(wing);
        Assert.Equal(new object?[] { "otherlost", pilot.Pilot.ShipId, null }, Assert.Single(wing.Notifications).Arguments);
        Assert.Empty(Assert.IsType<List<object?>>(Call("GetTargets")));
    }

    [Fact]
    public void CancellingAndLosingPendingTargetsNeverCompletesTheirLock()
    {
        using Fixture fixture = new();
        var (pilot, targeting) = fixture.Client(1000001);
        var (wing, _) = fixture.Client(1000002);
        long target = wing.Pilot.ShipId;
        _ = targeting.CallWire(pilot, "dogmaIM", "AddTarget", [target]);
        Assert.Null(targeting.CallWire(pilot, "dogmaIM", "CancelAddTarget", [target]));
        fixture.Clock.Seconds = 10;
        targeting.Advance(pilot);
        Assert.False(targeting.IsLocked(target));
        Assert.DoesNotContain(pilot.Notifications, n => Equals(n.Arguments[0], "add"));
        pilot.Notifications.Clear();
        _ = targeting.CallWire(pilot, "dogmaIM", "AddTarget", [target]);
        wing.Flight.Dispose();
        fixture.Clock.Seconds += 10;
        targeting.Advance(pilot);
        Assert.False(targeting.IsLocked(target));
        Assert.Equal(new object?[] { "lost", target, null }, Assert.Single(pilot.Notifications).Arguments);
    }

    [Fact]
    public void ChecksRangeSelfDockingAndPendingSlotsAndAllowsRetryAfterCancellation()
    {
        using Fixture fixture = new();
        var (pilot, targeting) = fixture.Client(1000001, undock: false);
        object? Call(string method, params object?[] args) => targeting.CallWire(pilot, "dogmaIM", method, args);
        Assert.IsType<PilotCallResult.NamedFailure>(Call("AddTarget", 70000001L));
        pilot.Flight.CallWire("ship", "Undock", [pilot.Pilot.ShipId]);
        pilot.Flight.Snapshot();
        Assert.Equal("DeniedTargetSelf", Assert.IsType<PilotCallResult.NamedFailure>(Call("AddTarget", pilot.Pilot.ShipId)).Name);
        Assert.Equal("TargetCantValidate", Assert.IsType<PilotCallResult.NamedFailure>(Call("AddTarget", 999L)).Name);
        Assert.Equal(
            "TargetCantValidate",
            Assert.IsType<PilotCallResult.NamedFailure>(Call("AddTarget", (long)pilot.Pilot.StationId)).Name
        );
        Assert.Equal("UnableToTarget", Assert.IsType<PilotCallResult.NamedFailure>(Call("AddTarget", 70000004L)).Name);
        Assert.IsType<object?[]>(Call("AddTarget", 70000001L));
        Assert.IsType<object?[]>(Call("AddTarget", 70000002L));
        var limit = Assert.IsType<PilotCallResult.NamedFailure>(Call("AddTarget", 70000003L));
        Assert.Equal("TargetingSystemsAlreadyFullyUtilized", limit.Name);
        Assert.Equal(TargetingDefinitions.BasePilotTargetLimit, Assert.IsType<TargetLimitErrorValues>(limit.Values).Limit);
        Assert.Null(Call("CancelAddTarget", 70000001L));
        Assert.IsType<object?[]>(Call("AddTarget", 70000003L));
        Assert.Null(Call("ClearTargets"));
        fixture.Clock.Seconds = 20;
        Assert.Empty(Assert.IsType<List<object?>>(Call("GetTargets")));
    }

    [Theory]
    [InlineData("warp")]
    [InlineData("ownwarp")]
    [InlineData("jump")]
    [InlineData("dock")]
    [InlineData("disconnect")]
    [InlineData("range")]
    public void DepartureOrRangeLossClearsBothSides(string action)
    {
        using Fixture fixture = new();
        if (action == "jump")
        {
            var gate = fixture.Objects.First(o => o.DestinationGateId.HasValue);
            var point = gate.Position + new SpacePosition(gate.Radius + 1000, 0, 0);
            fixture.Db.Store.SetPosition(1000000000001, point);
            fixture.Db.Store.SetPosition(1000000000002, point + new SpacePosition(0, 1500, 0));
        }
        else if (action is "warp" or "ownwarp")
        {
            var saved = fixture.Db.Store.FindPilot(1000001)!;
            SpacePosition station = new(saved.X, saved.Y, saved.Z);
            var gate = fixture.Objects.First(o => o.DestinationGateId.HasValue);
            var point = station + (gate.Position - station).Unit * (saved.Flight.StationRadius + 50000);
            fixture.Db.Store.SetPosition(1000000000001, point);
            fixture.Db.Store.SetPosition(1000000000002, point + new SpacePosition(0, 1500, 0));
        }
        else if (action == "dock")
        {
            var saved = fixture.Db.Store.FindPilot(1000001)!;
            var point =
                saved.Home.Position
                + new SpacePosition(saved.Flight.StationRadius + saved.Flight.ShipRadius + DockingDefinitions.MaximumRange / 2d, 0, 0);
            fixture.Db.Store.SetPosition(1000000000001, point);
            fixture.Db.Store.SetPosition(1000000000002, point + new SpacePosition(0, 1500, 0));
        }
        var (pilot, targeting) = fixture.Client(1000001);
        var (wing, observed) = fixture.Client(1000002);
        _ = targeting.CallWire(pilot, "dogmaIM", "AddTarget", [wing.Pilot.ShipId]);
        fixture.Clock.Seconds = 10;
        targeting.Advance(pilot);
        observed.Advance(wing);
        Assert.True(targeting.IsLocked(wing.Pilot.ShipId));
        pilot.Notifications.Clear();
        wing.Notifications.Clear();
        switch (action)
        {
            case "warp":
                var gate = fixture.Objects.First(o => o.DestinationGateId.HasValue);
                Assert.Null(wing.Flight.CallWire("beyonce", "CmdWarpToStuff", ["item", (long)gate.Id]));
                Assert.True(targeting.IsLocked(wing.Pilot.ShipId));
                fixture.Clock.Seconds += 10;
                wing.Flight.Checkpoint();
                break;
            case "ownwarp":
                var destination = fixture.Objects.First(o => o.DestinationGateId.HasValue);
                Assert.Null(pilot.Flight.CallWire("beyonce", "CmdWarpToStuff", ["item", (long)destination.Id]));
                Assert.True(targeting.IsLocked(wing.Pilot.ShipId));
                fixture.Clock.Seconds += 10;
                pilot.Flight.Checkpoint();
                break;
            case "jump":
                var source = fixture.Objects.First(o => o.DestinationGateId.HasValue);
                Assert.Same(
                    PilotCallResult.None,
                    wing.Flight.StartJump(
                        [(long)source.Id, (long)source.DestinationGateId!.Value, wing.Pilot.ShipId],
                        fixture.Db.Services.GetRequiredService<ISystemObjects>()
                    )
                );
                break;
            case "dock":
                Assert.Null(wing.Flight.CallWire("beyonce", "CmdDock", [(long)wing.Pilot.StationId, wing.Pilot.ShipId]));
                break;
            case "disconnect":
                observed.Dispose();
                wing.Flight.Dispose();
                break;
            case "range":
                wing.Flight.CallWire("beyonce", "CmdGotoDirection", [0d, 1d, 0d]);
                fixture.Clock.Seconds += 150;
                wing.Flight.Checkpoint();
                wing.Flight.RefreshPeers();
                break;
        }
        observed.Advance(wing);
        targeting.Advance(pilot);
        observed.Advance(wing);
        Assert.False(targeting.IsLocked(wing.Pilot.ShipId));
        Assert.Empty(fixture.Locks.Targeters(wing.Pilot.ShipId));
        Assert.Contains(pilot.Notifications, n => n.Name == "OnTarget" && Equals(n.Arguments[0], "lost"));
    }

    [Fact]
    public void PilotSkillsImproveRangeScanTimeAndTargetLimitWithinHullLimits()
    {
        using Fixture fixture = new();
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "INSERT INTO WorldPilotSkill VALUES (90000001,3428,2829),(90000001,3429,8000),(90000001,3431,1415)"
            );
        }
        var attributes = fixture.Fitting.ShipAttributes(pilot.PilotId);
        Assert.Equal(29700, attributes[DogmaAttributeId.MaxTargetRange], precision: 6);
        Assert.Equal(522.5, attributes[DogmaAttributeId.ScanResolution], precision: 6);
        Assert.Equal(3, attributes[DogmaAttributeId.MaxLockedTargets]);
        Assert.InRange(TargetingDefinitions.LockSeconds(attributes[DogmaAttributeId.ScanResolution], 52), 3.54, 3.56);
    }
}
