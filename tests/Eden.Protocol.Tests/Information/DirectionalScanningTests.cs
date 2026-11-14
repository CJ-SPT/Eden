using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Docking;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Information;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Information;

public sealed partial class InformationTests
{
    private static object?[] Cone(double angle = Math.Tau, double range = 2_000_000, double x = 1) => [angle, range, x, 0d, 0d];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectionalFirstOpenClientRangeIsCappedToTheShipLimit(bool resident)
    {
        using Fixture f = Prepare();
        Space(f);
        var origin = f.Db.Store.FindPilot(1000001)!.Position!;
        long target = Target(f);
        using LiveWorld? live = resident ? new(new(f.Db.Path), f.Clock, _ => { }) : null;
        using var source = Client(f, 1000001);
        using var scope = f.Db.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<DirectionalScanHandler>();
        // Build 3662168 GetScanRange defaults to AU * GetMaxRangeAU in metres,
        // then GetScanRangeInMeters multiplies that value by 1000 once more.
        const double metresPerKilometre = 1000;
        double allowed = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxDirectionalScanRange];
        double clientDefaultRange = allowed * metresPerKilometre;
        var request = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(Cone(Math.PI / 2, clientDefaultRange))));
        f.Db.Store.SetPosition(target, origin + new SpacePosition(allowed, 0, 0));
        var success = Assert.IsType<PilotCallResult.Success>(handler.Call(source, "@systemScan", "ConeScan", request));
        var contacts = Assert.IsType<DirectionalScanReply>(success.Reply).Contacts;
        Assert.Contains(contacts, contact => contact.Id == target);
        f.Db.Store.SetPosition(target, origin + new SpacePosition(allowed + 1, 0, 0));
        f.Clock.Milliseconds += (long)InformationRules.DirectionalScanCooldown.TotalMilliseconds;
        success = Assert.IsType<PilotCallResult.Success>(handler.Call(source, "@systemScan", "ConeScan", request));
        Assert.DoesNotContain(Assert.IsType<DirectionalScanReply>(success.Reply).Contacts, contact => contact.Id == target);
    }

    [Fact]
    public void DirectionalNativeBindingResolvesOffGridNamesWithoutCoordinatesAndExpiresWithLocation()
    {
        using Fixture f = Prepare();
        Space(f);
        var origin = f.Db.Store.FindPilot(1000001)!.Position!;
        long target = Target(f);
        f.Db.Store.SetPosition(target, origin + new SpacePosition(1_000_000, 0, 0));
        using var source = Client(f, 1000001);
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        PilotCallResult Call(string service, string method, params object?[] args) => router.Call(source, service, method, args, _ => { });
        LocationTable Names() =>
            Assert.IsType<LocationTable>(
                Assert
                    .IsType<PilotCallResult.Success>(Call("config", "GetMultiLocationsEx", new List<object?> { target, long.MaxValue }))
                    .Reply
            );

        Assert.DoesNotContain(Names().Locations, row => row.Id == target);
        var binding = Assert.IsType<BlueObjectReference>(
            Assert.IsType<PilotCallResult.Success>(Call("scanMgr", "GetSystemScanMgr")).Reply.ToBlue()
        );
        string service = router.ResolveService(source, [1L, (long)PilotLogin.NodeId, null, 0L], binding.Id);
        Assert.Equal("@systemScan", service);
        var result = Assert.IsType<DirectionalScanReply>(Assert.IsType<PilotCallResult.Success>(Call(service, "ConeScan", Cone())).Reply);
        Assert.Contains(result.Contacts, contact => contact.Id == target);
        var decoded = Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(result.ToBlue())));
        var fields = decoded.Select(value => Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(value).State));
        Assert.All(fields, row => Assert.Equal(new[] { "groupID", "id", "typeID" }, row.Keys.Order().ToArray()));
        var name = Assert.Single(Names().Locations);
        Assert.Equal(target, name.Id);
        Assert.Equal(f.Db.Store.FindPilot(1000002)!.ShipName, name.Name);
        Assert.Equal(0, name.X);
        Assert.Equal(0, name.Y);
        Assert.Equal(0, name.Z);
        source.ReleaseLocationReferences();
        Assert.Empty(Names().Locations);
        Assert.Equal("unknown binding", router.ResolveService(source, [1L, (long)PilotLogin.NodeId, null, 0L], binding.Id));
    }

    [Fact]
    public void DirectionalCooldownHasAnExactBoundaryAndInvalidCallsDoNotConsumeIt()
    {
        using Fixture f = Prepare();
        Space(f);
        using var source = Client(f, 1000001);
        using var scope = f.Db.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<DirectionalScanHandler>();
        PilotCallResult Scan(object?[] args) => handler.Call(source, "@systemScan", "ConeScan", args);
        Assert.IsType<PilotCallResult.Failure>(Scan([]));
        Assert.IsType<PilotCallResult.Failure>(Scan([true, 1000L, 1L, 0L, 0L]));
        Assert.IsType<PilotCallResult.Failure>(Scan(Cone(range: double.NaN)));
        Assert.IsType<PilotCallResult.Success>(Scan(Cone()));
        Assert.IsType<PilotCallResult.Failure>(Scan(Cone()));
        f.Clock.Milliseconds += 1499;
        Assert.IsType<PilotCallResult.Failure>(Scan(Cone()));
        f.Clock.Milliseconds++;
        Assert.IsType<PilotCallResult.Success>(Scan(Cone()));
        Assert.Empty(source.Notifications);
    }

    [Fact]
    public void DirectionalUndockRestorationReturnsEmptyUntilBallparkAndDoesNotConsumeCooldown()
    {
        using Fixture f = Prepare();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using PilotCallContext source = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, f.Db.Services.GetRequiredService<Eden.Protocol.Flight.SharedSpace>(), f.Db.Objects),
            1000001,
            SignedSettings.Empty
        );
        source.Select(pilot);
        using var scope = f.Db.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<DirectionalScanHandler>();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        IReadOnlyList<DirectionalScanContact> Scan()
        {
            var result = Assert.IsType<PilotCallResult.Success>(
                router.Call(source, "@systemScan", "ConeScan", BlueMarshal.Decode(BlueMarshal.Encode(Cone())), _ => { })
            );
            var reply = Assert.IsType<DirectionalScanReply>(result.Reply);
            Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(reply)));
            return reply.Contacts;
        }
        Assert.Empty(Scan());
        Assert.Empty(Scan());
        Assert.Throws<InvalidDataException>(() => Store(f).Directional(f.PilotId, Math.Tau, 1000, new(1, 0, 0), f.Clock.Now));
        for (int cycle = 0; cycle < 2; cycle++)
        {
            Assert.Same(PilotCallResult.None, source.Flight.Execute(new FlightCommand.Undock(pilot.ShipId)));
            Assert.NotNull(source.Pilot.Position);
            Assert.False(source.Flight.InBallpark);
            f.Db.Store.SetPosition(Target(f), source.Pilot.Position + new SpacePosition(1000, 0, 0));
            Assert.Empty(Scan());
            source.Flight.Snapshot();
            Assert.True(source.Flight.InBallpark);
            Assert.Contains(Scan(), contact => contact.Id == Target(f));
            Assert.IsType<PilotCallResult.Failure>(handler.Call(source, "@systemScan", "ConeScan", Cone()));
            // Dock before cooldown expiry to exercise restoration ordering and
            // discard the previous scan's disclosed names.
            Assert.Same(PilotCallResult.None, source.Flight.Execute(new FlightCommand.Dock(pilot.StationId, pilot.ShipId)));
            Assert.Null(source.Pilot.Position);
            Assert.Null(f.Db.Store.FindPilot(1000001)!.Position);
            Assert.Empty(Scan());
            Assert.Empty(handler.Names(source, new HashSet<long> { Target(f) }));
            f.Clock.Milliseconds += (long)InformationRules.DirectionalScanCooldown.TotalMilliseconds;
        }
        Assert.Empty(source.Notifications);
        Assert.Empty(source.NotificationsBeforeFlight);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectionalWarmReadsUseResidentSourceAndTargetPositionsWithoutSqlOrFitting(bool residentReads)
    {
        using Fixture f = Prepare();
        Space(f);
        long ship = f.ShipId;
        long target = Target(f);
        long other = f.Db.Store.FindPilot(1000002)!.PilotId;
        int system = f.Db.Store.FindPilot(1000001)!.SystemId;
        var origin = f.Db.Store.FindPilot(1000001)!.Position!;
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }, residentReads: residentReads);
        var store = Store(f);
        _ = store.Directional(f.PilotId, Math.Tau, 10000, new(1, 0, 0), f.Clock.Now);
        _ = store.Directional(other, Math.Tau, 10000, new(1, 0, 0), f.Clock.Now);
        using (var metrics = SessionPerformance.Begin("directional resident reads", _ => { }, force: true))
        {
            for (int index = 0; index < 10; index++)
            {
                using var ownership = live.Enter();
                live.Combat!.PublishPilotPosition(ship, system, origin + new SpacePosition(index * 100000, 0, 0));
                live.Combat.PublishPilotPosition(target, system, origin + new SpacePosition(index * 100000 + 1000, 0, 0));
                Assert.Contains(store.Directional(f.PilotId, Math.PI / 4, 1001, new(1, 0, 0), f.Clock.Now), c => c.Id == target);
                Assert.Contains(store.Directional(other, Math.PI / 4, 1001, new(-1, 0, 0), f.Clock.Now), c => c.Id == ship);
                Assert.DoesNotContain(store.Directional(f.PilotId, Math.PI / 4, 1001, new(-1, 0, 0), f.Clock.Now), c => c.Id == target);
            }
            Assert.Equal(0, metrics!.Commands);
            Assert.Equal(0, metrics.Connections);
            if (residentReads)
            {
                Assert.Equal(0, metrics.FittingCalculations);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectionalExpiryMembershipAndRollbackAreVisibleOnRepeatedSameTickReads(bool resident)
    {
        using Fixture f = Prepare();
        Space(f);
        long target = Target(f);
        long wreck = Wreck(f);
        long other = f.Db.Store.FindPilot(1000002)!.PilotId;
        int system = f.Db.Store.FindPilot(1000001)!.SystemId;
        using LiveWorld? live = resident ? new(new(f.Db.Path), f.Clock, _ => { }) : null;
        var store = Store(f);
        IReadOnlyList<DirectionalScanContact> Scan() => store.Directional(f.PilotId, Math.Tau, 10000, new(1, 0, 0), f.Clock.Now);
        Assert.Contains(Scan(), c => c.Id == target);
        Assert.Contains(Scan(), c => c.Id == wreck);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new SavedLogoff
                {
                    PilotId = other,
                    ShipId = target,
                    SystemId = system,
                    ExpiresAt = f.Clock.Now + 1,
                }
            );
            world.SaveChanges();
        }
        Assert.Contains(Scan(), c => c.Id == target);
        Assert.DoesNotContain(store.Directional(f.PilotId, Math.Tau, 10000, new(1, 0, 0), f.Clock.Now + 1), c => c.Id == target);
        using (WorldDbContext world = new(f.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            world.Database.ExecuteSql($"DELETE FROM WorldPosition WHERE ShipId={target}");
            transaction.Rollback();
        }
        Assert.Contains(Scan(), c => c.Id == target);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSql($"UPDATE WorldPosition SET SystemId={system + 1} WHERE ShipId={target}");
        }
        Assert.DoesNotContain(Scan(), c => c.Id == target);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSql($"UPDATE WorldPosition SET SystemId={system} WHERE ShipId={target}");
        }
        Assert.Contains(Scan(), c => c.Id == target);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSql($"DELETE FROM WorldPosition WHERE ShipId={target}");
        }
        Assert.DoesNotContain(Scan(), c => c.Id == target);
        f.Clock.Seconds += 3600;
        Assert.DoesNotContain(Scan(), c => c.Id == wreck);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectionalEligibilityIncludesUnpilotedShipsAndDronesButExcludesOrdinaryNpcsAndImmuneHulls(bool resident)
    {
        using Fixture f = Prepare();
        Space(f);
        var origin = f.Db.Store.FindPilot(1000001)!.Position!;
        PassiveCatalogFixture.LoadCombatDrones(f.CatalogPath);
        long drone = f.Grant(2454);
        const long abandoned = 7000000000008;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new Ship
                {
                    Id = abandoned,
                    OwnerId = f.PilotId,
                    TypeId = 601,
                    StationId = (int)f.StationId,
                    Name = "Empty hull",
                    Position = new FlightPosition
                    {
                        ShipId = abandoned,
                        SystemId = 30000142,
                        X = origin.X + 1000,
                        Y = origin.Y,
                        Z = origin.Z,
                    },
                }
            );
            world.Add(
                new WorldDrone
                {
                    ItemId = drone,
                    PilotId = f.PilotId,
                    ControllerShipId = f.ShipId,
                    SystemId = 30000142,
                    X = origin.X + 1000,
                    Y = origin.Y,
                    Z = origin.Z,
                }
            );
            world.Add(
                new WorldNpc
                {
                    Id = 991,
                    TypeId = 601,
                    SystemId = 30000142,
                    Role = NpcRole.MiningMiner,
                    X = origin.X + 1000,
                    Y = origin.Y,
                    Z = origin.Z,
                }
            );
            world.Add(
                new WorldNpc
                {
                    Id = 992,
                    TypeId = 601,
                    SystemId = 30000142,
                    Role = NpcRole.Pirate,
                    X = origin.X + 1000,
                    Y = origin.Y,
                    Z = origin.Z,
                }
            );
            world.SaveChanges();
        }
        using LiveWorld? live = resident ? new(new(f.Db.Path), f.Clock, _ => { }) : null;
        var scan = Store(f).Directional(f.PilotId, Math.Tau, 10000, new(1, 0, 0), f.Clock.Now);
        Assert.Contains(scan, c => c.Id == abandoned);
        Assert.Contains(scan, c => c.Id == drone);
        Assert.Contains(scan, c => c.Id == 991);
        Assert.DoesNotContain(scan, c => c.Id == 992);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Add(
                new Eden.Data.Catalog.DogmaAttribute { Id = (int)DogmaAttributeId.DirectionalScanImmune, Name = "directionalScanImmune" }
            );
            catalog.Add(
                new TypeAttribute
                {
                    ItemTypeId = 601,
                    AttributeId = (int)DogmaAttributeId.DirectionalScanImmune,
                    Value = 1,
                }
            );
            catalog.SaveChanges();
        }
        Assert.DoesNotContain(Store(f).Directional(f.PilotId, Math.Tau, 10000, new(1, 0, 0), f.Clock.Now), c => c.Id == abandoned);
    }

    [Fact]
    public void DirectionalScanDuringWarpUsesTheAdvancedPosition()
    {
        using Fixture f = Prepare();
        Space(f);
        using var source = Client(f, 1000001);
        using var scope = f.Db.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<DirectionalScanHandler>();
        var destination = source.Pilot.Position! + new SpacePosition(1e11, 0, 0);
        Assert.Same(PilotCallResult.None, source.Flight.Execute(new FlightCommand.WarpToPoint(1, destination, 0)));
        for (int index = 0; index < 30 && !source.Flight.InWarp; index++)
        {
            f.Clock.Seconds++;
            source.Flight.Checkpoint();
        }
        Assert.True(source.Flight.InWarp);
        Assert.IsType<PilotCallResult.Success>(handler.Call(source, "@systemScan", "ConeScan", Cone()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectionalHullRangeAndEmptyResultsInvalidateWithCatalogChanges(bool resident)
    {
        using Fixture f = Prepare();
        Space(f);
        using LiveWorld? live = resident ? new(new(f.Db.Path), f.Clock, _ => { }) : null;
        var store = Store(f);
        Assert.Empty(store.Directional(f.PilotId, Math.Tau, 0, new(1, 0, 0), f.Clock.Now));
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"INSERT OR IGNORE INTO DogmaAttribute VALUES ({(int)DogmaAttributeId.MaxDirectionalScanRange},{"maxDirectionalScanRange"})"
            );
            catalog.Database.ExecuteSql(
                $"""
                INSERT INTO TypeAttribute VALUES ({601},{(int)DogmaAttributeId.MaxDirectionalScanRange},{500d})
                ON CONFLICT(ItemTypeId,AttributeId) DO UPDATE SET Value=excluded.Value
                """
            );
        }
        Assert.Empty(store.Directional(f.PilotId, Math.Tau, 501, new(1, 0, 0), f.Clock.Now));
        Assert.Empty(store.Directional(f.PilotId, Math.Tau, 500, new(1, 0, 0), f.Clock.Now));
    }

    [Fact]
    public void DirectionalResidentPositionsSurviveDetachedCheckpointAndReopen()
    {
        using Fixture f = Prepare();
        Space(f);
        long ship = f.ShipId;
        long target = Target(f);
        int system = f.Db.Store.FindPilot(1000001)!.SystemId;
        var origin = f.Db.Store.FindPilot(1000001)!.Position!;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            _ = Store(f).Directional(f.PilotId, Math.Tau, 2000, new(1, 0, 0), f.Clock.Now);
            using (live.Enter())
            {
                live.Combat!.PublishPilotPosition(ship, system, origin + new SpacePosition(1_000_000, 0, 0));
                live.Combat.PublishPilotPosition(target, system, origin + new SpacePosition(1_001_000, 0, 0));
            }
            using var checkpoint = live.CaptureSnapshot();
            Assert.NotNull(checkpoint);
            _ = checkpoint.Project();
            using (WorldDbContext image = new(checkpoint.Database))
            {
                Assert.Equal(origin.X + 1_000_000, image.Positions.Find(ship)!.X);
                Assert.Equal(origin.X + 1_001_000, image.Positions.Find(target)!.X);
            }
            LiveWorld.PersistSnapshot(checkpoint.Database, f.Db.Path);
        }
        Assert.Contains(Store(f).Directional(f.PilotId, Math.PI / 4, 1001, new(1, 0, 0), f.Clock.Now), contact => contact.Id == target);
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Equal(origin.X + 1_000_000, reopened.Positions.Find(ship)!.X);
        Assert.Equal(origin.X + 1_001_000, reopened.Positions.Find(target)!.X);
    }
}
