using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Information;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Information;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Information;

public sealed partial class InformationTests
{
    private static PilotCallContext Client(Fixture f, long account)
    {
        var pilot = f.Db.Store.FindPilot(account)!;
        var space = f.Db.Services.GetRequiredService<SharedSpace>();
        PilotCallContext context = new(
            account,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, space, f.Db.Objects, wrecks: f.Db.Services.GetRequiredService<IWrecks>()),
            account,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        return context;
    }

    private static void Lock(Fixture f, PilotCallContext source, TargetingHandler targeting, long target)
    {
        _ = targeting.Call(source, "dogmaIM", "AddTarget", [target]);
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        Assert.True(targeting.IsLocked(target));
    }

    [Theory]
    [InlineData(442, "cargoScan", "OnCargoScanComplete", 2)]
    [InlineData(443, "shipScan", "OnShipScanCompleted", 4)]
    public void NativeScannerDispatchPublishesThePinnedReplyExactlyOnce(int type, string effect, string notification, int arity)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        using var source = Client(f, 1000001);
        using var recipient = Client(f, 1000002);
        using var scope = f.Db.Services.CreateScope();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<InformationHandler>();
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        Lock(f, source, targeting, Target(f));
        source.Notifications.Clear();
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [module, effect, Target(f), 99L]));
        var active = handler.ActiveEffect(source.Pilot, module);
        Assert.NotNull(active);
        Assert.Equal(0, active.Repeats);
        handler.Advance(source);
        Assert.DoesNotContain(source.Notifications, n => n.Name == notification);
        f.Clock.Milliseconds += (long)active.DurationMilliseconds!.Value;
        handler.Advance(source);
        handler.Advance(source);
        var result = Assert.Single(source.Notifications, n => n.Name == notification);
        Assert.Equal(arity, result.Arguments.Length);
        Assert.Equal(Target(f), result.Arguments[0]);
        Assert.IsType<List<object?>>(result.Arguments[^1]);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        Assert.DoesNotContain(recipient.Notifications, n => n.Name == notification);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<SavedInformationCycle>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CargoScanImmunityIsEnforcedBeforePaymentAndAtDisclosure(bool immuneAtActivation)
    {
        const int crane = 12729;
        using Fixture f = Prepare();
        PassiveCatalogFixture.LoadTechTwoHulls(f.CatalogPath, crane);
        long cargoScanner = f.Fit(442);
        long shipScanner = f.Fit(443);
        long ship = f.ShipId;
        long target = Target(f);
        Space(f);
        if (!immuneAtActivation)
        {
            SetResistance(0);
        }
        WorldDatabase database = new(f.Db.Path);
        using LiveWorld live = new(database, f.Clock, _ => { });
        using (WorldActionScope setup = new(database, new(ships: [target])))
        {
            setup.World.Ships.Find(target)!.TypeId = crane;
            setup.Commit();
        }
        using var source = Client(f, 1000001);
        using var recipient = Client(f, 1000002);
        using var scope = f.Db.Services.CreateScope();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<InformationHandler>();
        Lock(f, source, targeting, target);

        double? capacitorBefore = live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(ship)?.Charge);
        var activation = handler.CallModule(source, "Activate", [cargoScanner, "cargoScan", target, 0L]);
        if (immuneAtActivation)
        {
            Assert.Contains("immune to cargo scanning", Assert.IsType<PilotCallResult.Failure>(activation).Message);
            Assert.Equal(capacitorBefore, live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(ship)?.Charge));
            Assert.Null(live.Economy.Inspect(world => world.Set<ModuleCooldown>().Find(cargoScanner)));
            Assert.IsType<PilotCallResult.Success>(handler.CallModule(source, "Activate", [shipScanner, "shipScan", target, 0L]));
            f.Clock.Milliseconds += (long)handler.ActiveEffect(source.Pilot, shipScanner)!.DurationMilliseconds!.Value;
            handler.Advance(source);
            Assert.Single(source.Notifications, notification => notification.Name == "OnShipScanCompleted");
        }
        else
        {
            Assert.IsType<PilotCallResult.Success>(activation);
            double paid = live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(ship)!.Charge);
            long deadline = live.Economy.Inspect(world => world.Set<ModuleCooldown>().Find(cargoScanner)!.ReadyAt);
            SetResistance(1);
            f.Clock.Milliseconds += (long)handler.ActiveEffect(source.Pilot, cargoScanner)!.DurationMilliseconds!.Value;
            handler.Advance(source);
            Assert.Equal(paid, live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(ship)!.Charge));
            Assert.Equal(deadline, live.Economy.Inspect(world => world.Set<ModuleCooldown>().Find(cargoScanner)!.ReadyAt));
        }
        Assert.Null(handler.ActiveEffect(source.Pilot, cargoScanner));
        Assert.Null(live.Economy.Inspect(world => world.Set<SavedInformationCycle>().Find(cargoScanner)));
        Assert.DoesNotContain(source.Notifications, notification => notification.Name == "OnCargoScanComplete");
        Assert.DoesNotContain(recipient.Notifications, notification => notification.Name == "OnCargoScanComplete");

        void SetResistance(double value)
        {
            using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
            int attributeId = (int)DogmaAttributeId.CargoScanResistance;
            Assert.Equal(
                1,
                catalog.Database.ExecuteSql(
                    $"UPDATE TypeAttribute SET Value={value} WHERE ItemTypeId={crane} AND AttributeId={attributeId}"
                )
            );
        }
    }

    [Fact]
    public void LockLossCancelsAPaidScanWithoutResultsOrRefund()
    {
        using Fixture f = Prepare();
        long module = f.Fit(442);
        Space(f);
        using var source = Client(f, 1000001);
        using var recipient = Client(f, 1000002);
        using var scope = f.Db.Services.CreateScope();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<InformationHandler>();
        Lock(f, source, targeting, Target(f));
        Assert.IsType<PilotCallResult.Success>(handler.CallModule(source, "Activate", [module, "cargoScan", Target(f), 0L]));
        double paid;
        using (WorldDbContext world = new(f.Db.Path))
        {
            paid = world.Set<Eden.Data.World.Fittings.ShipEnergy>().Find(f.ShipId)!.Charge;
        }
        _ = targeting.Call(source, "dogmaIM", "RemoveTarget", [Target(f)]);
        handler.Advance(source);
        f.Clock.Seconds += 10;
        handler.Advance(source);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        Assert.DoesNotContain(source.Notifications, n => n.Name == "OnCargoScanComplete");
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(paid, verify.Set<Eden.Data.World.Fittings.ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.NotNull(verify.Set<ModuleCooldown>().Find(module));
    }

    [Fact]
    public void PassiveTargetingRetainsTheSourceLockWithoutPublishingATargetedMarker()
    {
        using Fixture f = Prepare();
        long module = f.Fit(581);
        Space(f);
        using var source = Client(f, 1000001);
        using var recipient = Client(f, 1000002);
        using var scope = f.Db.Services.CreateScope();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<InformationHandler>();
        Assert.IsType<PilotCallResult.Success>(handler.CallModule(source, "Activate", [module, "targetPassively", Target(f), 0L]));
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        Assert.True(targeting.IsLocked(Target(f)));
        Assert.True(f.Db.Services.GetRequiredService<SharedTargetLocks>().Contains(f.ShipId, Target(f)));
        Assert.Empty(f.Db.Services.GetRequiredService<SharedTargetLocks>().Targeters(Target(f)));
        Assert.Contains(source.Notifications.OfType<TargetChanged>(), n => n.Change == TargetChange.Added);
    }

    [Fact]
    public void ResidentTractorFramesDoNotOpenSqlAndCheckpointTheSameMovedPosition()
    {
        using Fixture f = Prepare();
        long module = f.Fit(24348);
        Space(f);
        long wreck = Wreck(f, 10000);
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var activation = Store(f).Begin(f.PilotId, module, DogmaEffect.TractorBeam, wreck, new HashSet<long> { wreck }, f.Clock.Now);
            var wrecks = f.Db.Services.GetRequiredService<IWrecks>();
            _ = wrecks.List(30000142, f.Clock.Now);
            using (var metrics = SessionPerformance.Begin("warm tractors", _ => { }, force: true))
            {
                for (int i = 0; i < 10; i++)
                {
                    f.Clock.Milliseconds += 100;
                    Store(f).AdvanceTractor(activation.Cycle, f.Clock.Now);
                    _ = wrecks.List(30000142, f.Clock.Now);
                }
                Assert.Equal(0, metrics!.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            double expected = f.Db.Store.FindPilot(1000001)!.Position!.X + 9500;
            Assert.Equal(expected, Assert.Single(wrecks.List(30000142, f.Clock.Now)).Position.X, 5);
            using var snapshot = live.CaptureSnapshot();
            Assert.NotNull(snapshot);
            _ = snapshot.Project();
        }
        WorldInitialization.Initialize(new(f.Db.Path));
        using WorldDbContext reopened = new(f.Db.Path);
        double origin = f.Db.Store.FindPilot(1000001)!.Position!.X;
        Assert.Equal(origin + 9500, reopened.Set<WorldWreck>().Find(wreck)!.X, 5);
        Assert.Empty(reopened.Set<SavedInformationCycle>());
    }
}
