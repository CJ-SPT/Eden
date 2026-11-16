using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Propulsion;

public sealed class InterdictionTests
{
    internal const int Launcher = 22782;
    internal const int Probe = 22778;
    internal const int Surgical = 34260;
    internal const int Sabre = 22456;

    [Fact]
    public void BubbleGeometryBlocksDepartureCatchesDragsAndIgnoresUnrelatedFields()
    {
        var origin = SpacePosition.Zero;
        SpacePosition destination = new(1_000_000, 0, 0);
        Assert.True(InterdictionRules.Resolve(origin, destination, [new(1, origin, 10)]).Blocked);
        Assert.False(InterdictionRules.Resolve(origin, destination, [new(1, origin, 10)], true).Blocked);
        var caught = InterdictionRules.Resolve(origin, destination, [new(1, new(900_000, 0, 0), 20_000)]);
        Assert.Equal(new(880_001, 0, 0), caught.Destination);
        var dragged = InterdictionRules.Resolve(origin, destination, [new(1, new(1_100_000, 0, 0), 20_000)]);
        Assert.Equal(new(1_080_001, 0, 0), dragged.Destination);
        Assert.Equal(destination, InterdictionRules.Resolve(origin, destination, [new(1, new(200_000, 0, 0), 10)]).Destination);
        Assert.Equal(destination, InterdictionRules.Resolve(origin, destination, [new(1, new(900_000, 30_000, 0), 20_000)]).Destination);
        var overlap = InterdictionRules.Resolve(
            origin,
            destination,
            [new(2, new(900_000, 0, 0), 20_000), new(1, new(900_000, 0, 0), 20_000)]
        );
        Assert.Equal(1, overlap.FieldId);
        Assert.Throws<InvalidDataException>(() => InterdictionRules.Resolve(new(double.NaN, 0, 0), destination, []));
    }

    [Theory]
    [InlineData(Probe, 20000, 120)]
    [InlineData(Surgical, 10000, 180)]
    public void PaidProbeSurvivesLauncherRemovalAndCheckpointAndExpiresExactly(int chargeType, double radius, int seconds)
    {
        using var fixture = Prepare();
        long ship = fixture.ShipId;
        long module = fixture.Fit(Launcher);
        Load(fixture, module, chargeType);
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        fixture.Db.Store.SetPosition(ship, SpacePosition.Zero, pilot.SystemId);
        using ResidentFitCache cache = new(new(fixture.Db.Path));
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var store = fixture.Db.Services.GetRequiredService<IPilotInterdiction>();
        var first = store.Launch(fixture.PilotId, ship, module, fixture.Clock.Now);
        var contact = Assert.Single(store.Contacts(pilot.SystemId, fixture.Clock.Now));
        Assert.Equal(radius, contact.InterdictionRadius);
        Assert.Equal(2, first.Magazine!.Current!.Quantity);
        Assert.Throws<InvalidDataException>(() => store.Launch(fixture.PilotId, ship, module, fixture.Clock.Now));
        Assert.Equal(2, Assert.Single(fixture.Db.Services.GetRequiredService<IPilotAmmunition>().Read(fixture.PilotId, ship)).Quantity);
        Assert.True(store.Resolve(ship, pilot.SystemId, SpacePosition.Zero, new(1_000_000, 0, 0), fixture.Clock.Now).Blocked);
        using (var metrics = SessionPerformance.Begin("warmed bubble reads", _ => { }, force: true)!)
        {
            for (int i = 0; i < 8; i++)
            {
                Assert.True(store.Resolve(ship, pilot.SystemId, SpacePosition.Zero, new(1_000_000, 0, 0), fixture.Clock.Now).Blocked);
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using (var transaction = live.Economy.Begin(new(ships: [ship])))
        {
            transaction.Set<Eden.Data.World.Inventory.OwnedItem>().Find(module)!.Online = false;
            transaction.Commit();
        }
        Assert.Single(store.Contacts(pilot.SystemId, fixture.Clock.Now));
        using (var snapshot = live.CaptureSnapshot())
        {
            Assert.NotNull(snapshot);
            snapshot.Project();
            using WorldDbContext saved = new(snapshot.Database);
            var row = Assert.Single(saved.Set<SavedInterdictionField>());
            Assert.Equal(fixture.Clock.Now + TimeSpan.FromSeconds(seconds).Ticks, row.ExpiresAt);
            string reopenedPath = Path.Combine(Path.GetDirectoryName(fixture.Db.Path)!, "reopened.sqlite");
            using (var output = Eden.World.Storage.WorldConnections.FileConnection(reopenedPath))
            {
                output.Open();
                snapshot.Database.BackupDatabase(output);
            }
            using LiveWorld reopened = new(new(reopenedPath), fixture.Clock, _ => { });
            var reopenedStore = new Eden.World.Propulsion.PilotInterdictionStore(new(reopenedPath));
            Assert.Single(reopenedStore.Contacts(pilot.SystemId, fixture.Clock.Now));
        }
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(seconds) - TimeSpan.FromTicks(1));
        Assert.Single(store.Contacts(pilot.SystemId, fixture.Clock.Now));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Empty(store.Contacts(pilot.SystemId, fixture.Clock.Now));
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.9)]
    public void EmpireRejectionPreservesChargesAndCapacitor(double security)
    {
        using var fixture = Prepare(security);
        long ship = fixture.ShipId;
        long module = fixture.Fit(Launcher);
        Load(fixture, module, Probe);
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        fixture.Db.Store.SetPosition(ship, SpacePosition.Zero, pilot.SystemId);
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var store = fixture.Db.Services.GetRequiredService<IPilotInterdiction>();
        Assert.Throws<InvalidDataException>(() => store.Launch(fixture.PilotId, ship, module, fixture.Clock.Now));
        Assert.Empty(store.Contacts(pilot.SystemId, fixture.Clock.Now));
        Assert.Equal(3, Assert.Single(fixture.Db.Services.GetRequiredService<IPilotAmmunition>().Read(fixture.PilotId, ship)).Quantity);
    }

    [Fact]
    public void NativeLauncherRoutePublishesProbeBallsAndConsumesOneCharge()
    {
        using var fixture = Prepare();
        long ship = fixture.ShipId;
        long module = fixture.Fit(Launcher);
        Load(fixture, module, Probe);
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        fixture.Db.Store.SetPosition(ship, SpacePosition.Zero, pilot.SystemId);
        pilot = fixture.Db.Store.FindPilot(1000001)!;
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        using var scope = fixture.Db.Services.CreateScope();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create(p),
            1000001,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        var route = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        Assert.Same(PilotCallResult.None, route.Call(context, "dogmaIM", "Activate", [module, "useMissiles", null, 0L]));
        Assert.Contains(context.Notifications, notification => notification is ShipEffectChanged { Start: true });
        Assert.Contains(context.Flight.Updates, update => update.Command is DestinyCommand.AddBalls);
        Assert.IsType<PilotCallResult.Failure>(context.Flight.Execute(new FlightCommand.WarpToPoint(1, new(1_000_000, 0, 0), 0)));
    }

    [Fact]
    public void SmartbombDestroysProbe()
    {
        using var fixture = Prepare();
        PassiveCatalogFixture.LoadSmartbombs(fixture.CatalogPath);
        long ship = fixture.ShipId;
        long module = fixture.Fit(1547);
        int system = fixture.Db.Store.FindPilot(1000001)!.SystemId;
        fixture.Db.Store.SetPosition(ship, SpacePosition.Zero, system);
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        using (var transaction = live.Economy.Begin(new(systems: [system])))
        {
            transaction.Add(
                new SavedInterdictionField
                {
                    Id = InterdictionRules.FirstProbeId,
                    TypeId = Probe,
                    SystemId = system,
                    X = 1000,
                    HitPoints = 10,
                    Radius = 20000,
                    ExpiresAt = fixture.Clock.Now + TimeSpan.TicksPerMinute,
                }
            );
            transaction.Commit();
        }
        fixture
            .Db.Services.GetRequiredService<Eden.Gameplay.Combat.IPilotSmartbombs>()
            .Pulse(fixture.PilotId, ship, module, new([], [], [], [], []), fixture.Clock.Now);
        Assert.Empty(fixture.Db.Services.GetRequiredService<IPilotInterdiction>().Contacts(system, fixture.Clock.Now));
    }

    internal static Fixture Prepare(double security = 0, int hull = Sabre)
    {
        Fixture fixture = new();
        PassiveCatalogFixture.LoadInterdiction(fixture.CatalogPath);
        using WorldDbContext world = new(fixture.Db.Path);
        using CatalogDbContext catalog = new(fixture.CatalogPath, writable: true);
        string system = fixture.Db.Store.FindPilot(1000001)!.SystemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string data = System.Text.Json.JsonSerializer.Serialize(new { securityStatus = security });
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems',{system},{data})");
        world.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Omega;
        world.Ships.Find(fixture.ShipId)!.TypeId = hull;
        world.SaveChanges();
        return fixture;
    }

    internal static void Load(Fixture fixture, long module, int charge)
    {
        if (charge is Probe or Surgical)
        {
            fixture.Train(3412, 5);
        }
        long stack = fixture.Grant(charge, 3);
        fixture
            .Db.Services.GetRequiredService<IPilotAmmunition>()
            .Load(fixture.PilotId, fixture.ShipId, [module], [stack], fixture.StationId, fixture.Clock.Now);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
    }
}
