using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Propulsion;

public sealed partial class SpatialModuleOperationTests
{
    [Theory]
    [InlineData(Prototype, Raven, false)]
    [InlineData(11578, 33468, true)]
    public void OnlyCovertCloaksPermitWarp(int type, int hull, bool warpSafe)
    {
        using Fixture f = Prepare(hull);
        long module = f.Fit(type);
        Space(f);
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        var handler = scope.ServiceProvider.GetRequiredService<SpatialModuleHandler>();
        string effect = warpSafe ? "cloakingWarpSafe" : "cloakingPrototype";
        Assert.IsType<PilotCallResult.Success>(handler.Call(source, "Activate", [module, effect, null, 0L]));
        var destination = source.Pilot.Position! + new SpacePosition(500000000, 0, 0);
        var result = source.Flight.Execute(new FlightCommand.WarpToPoint(123, destination, 0));
        Assert.Equal(warpSafe, result is PilotCallResult.Success);
        Assert.Equal(warpSafe, source.Flight.PreparingWarp);
    }

    [Fact]
    public void CloakAllowsFirstMwdPulseButNeverRenewsThePaidCycle()
    {
        using Fixture f = Prepare();
        long cloak = f.Fit(Prototype);
        long mwd = f.Fit(434);
        Space(f);
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var propulsion = scope.ServiceProvider.GetRequiredService<PropulsionHandler>();
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [cloak, "cloakingPrototype", null, 0L]));
        Assert.IsType<PilotCallResult.Success>(
            activation.Call(source, "dogmaIM", "Activate", [mwd, PropulsionWireDefinitions.MicrowarpdriveEffectName, null, 100L])
        );
        Assert.NotNull(propulsion.ActiveEffect(source.Pilot, mwd));
        f.Clock.Milliseconds += 10000;
        propulsion.Advance(source);
        Assert.Null(propulsion.ActiveEffect(source.Pilot, mwd));
        Assert.IsType<PilotCallResult.Failure>(
            activation.Call(source, "dogmaIM", "Activate", [mwd, PropulsionWireDefinitions.MicrowarpdriveEffectName, null, 100L])
        );
    }

    private static PilotCallContext Client(Fixture f, long account)
    {
        var pilot = f.Db.Store.FindPilot(account)!;
        var shared = f.Db.Services.GetRequiredService<SharedSpace>();
        PilotCallContext context = new(
            account,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, shared, f.Db.Objects, health: f.Db.Services.GetRequiredService<IPilotHealth>()),
            account,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        context.Flight.Updates.Clear();
        return context;
    }

    private static void ObserverSpace(Fixture f, double distance = 10000)
    {
        var source = f.Db.Store.FindPilot(1000001)!;
        var observer = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(observer.ShipId, source.Position! + new SpacePosition(distance, 0, 0));
    }

    [Fact]
    public void NativeCloakHidesFromPeersTargetingNpcsAndDirectionalScannerThenReappears()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Prototype);
        Space(f);
        ObserverSpace(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var observer = Client(f, 1000002);
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<SpatialModuleHandler>();
        var shared = f.Db.Services.GetRequiredService<SharedSpace>();
        var scanner = f.Db.Services.GetRequiredService<IPilotInformation>();
        var locks = f.Db.Services.GetRequiredService<Eden.Protocol.Targeting.SharedTargetLocks>();
        locks.Add(source.Pilot.ShipId, observer.Pilot.ShipId);
        Assert.Contains(shared.Peers(observer.Pilot), p => p.Pilot.ShipId == source.Pilot.ShipId);
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [module, "cloakingPrototype", null, 0L]));
        Assert.True(source.Flight.Cloaked);
        Assert.False(source.Flight.CanTarget);
        Assert.False(locks.Contains(source.Pilot.ShipId, observer.Pilot.ShipId));
        Assert.Null(observer.Flight.FindTarget(source.Pilot.ShipId));
        Assert.Null(shared.Find(source.Pilot.SystemId, source.Pilot.ShipId));
        Assert.DoesNotContain(shared.NpcTargets(source.Pilot.SystemId), c => c.ShipId == source.Pilot.ShipId);
        Assert.DoesNotContain(
            scanner.Directional(observer.Pilot.PilotId, Math.Tau, 1000000, new(1, 0, 0), f.Clock.Now),
            c => c.Id == source.Pilot.ShipId
        );
        observer.Flight.RefreshPeers();
        Assert.Contains(observer.Flight.Updates, u => u.Command is DestinyCommand.RemoveBall r && r.Id == source.Pilot.ShipId);
        Assert.Contains(source.Flight.Updates, u => u.Command is DestinyCommand.CloakBall);
        Assert.NotNull(handler.ActiveEffect(source.Pilot, module));
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Deactivate", [module, "cloakingPrototype"]));
        observer.Flight.Updates.Clear();
        observer.Flight.RefreshPeers();
        Assert.Contains(observer.Flight.Updates, u => u.Command is DestinyCommand.AddBalls);
        Assert.True(source.Flight.CanTarget);
        Assert.False(source.Flight.CanAcquireTargets);
        Assert.NotNull(shared.Find(source.Pilot.SystemId, source.Pilot.ShipId));
        var targeting = scope.ServiceProvider.GetRequiredService<Eden.Protocol.Targeting.TargetingHandler>();
        Assert.IsType<PilotCallResult.Failure>(targeting.CallWire(source, "dogmaIM", "AddTarget", [observer.Pilot.ShipId]));
        Assert.Contains(
            scanner.Directional(observer.Pilot.PilotId, Math.Tau, 1000000, new(1, 0, 0), f.Clock.Now),
            c => c.Id == source.Pilot.ShipId
        );
        f.Clock.Milliseconds += 15000;
        Assert.True(source.Flight.CanAcquireTargets);
    }

    [Fact]
    public void NearbyShipRejectsActivationAndLaterApproachForcesDecloak()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Prototype);
        Space(f);
        ObserverSpace(f, 1000);
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var observer = Client(f, 1000002);
        var handler = scope.ServiceProvider.GetRequiredService<SpatialModuleHandler>();
        Assert.IsType<PilotCallResult.Failure>(handler.Call(source, "Activate", [module, "cloakingPrototype", null, 0L]));
        Assert.False(source.Flight.Cloaked);
        var shared = f.Db.Services.GetRequiredService<SharedSpace>();
        shared.Leave(observer.Pilot.ShipId);
        f.Db.Store.SetPosition(observer.Pilot.ShipId, source.Pilot.Position! + new SpacePosition(10000, 0, 0));
        Assert.IsType<PilotCallResult.Success>(handler.Call(source, "Activate", [module, "cloakingPrototype", null, 0L]));
        f.Db.Store.SetPosition(observer.Pilot.ShipId, source.Pilot.Position! + new SpacePosition(1000, 0, 0));
        observer.Flight.Snapshot();
        handler.Advance(source);
        Assert.False(source.Flight.Cloaked);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
    }

    [Fact]
    public void NativeDriveLocksNavigationAndPublishesOneJumpWithPreservedVelocity()
    {
        using Fixture f = Prepare();
        long module = f.Fit(LargeDrive);
        Space(f);
        ObserverSpace(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var observer = Client(f, 1000002);
        var handler = scope.ServiceProvider.GetRequiredService<SpatialModuleHandler>();
        Assert.IsType<PilotCallResult.Success>(source.Flight.Execute(new FlightCommand.Heading(0, 1, 0)));
        Assert.IsType<PilotCallResult.Success>(handler.Call(source, "Activate", [module, "microJumpDrive", null, 20L]));
        Assert.IsType<PilotCallResult.Failure>(source.Flight.Execute(new FlightCommand.Heading(1, 0, 0)));
        Assert.IsType<PilotCallResult.Success>(handler.Call(source, "Deactivate", [module, "microJumpDrive"]));
        Assert.True(source.Flight.MicroJumpSpooling);
        f.Clock.Milliseconds += 8999;
        handler.Advance(source);
        Assert.True(source.Flight.MicroJumpSpooling);
        f.Clock.Milliseconds++;
        source.Flight.Checkpoint();
        var beforeJump = source.Pilot.Position!;
        handler.Advance(source);
        Assert.Equal(beforeJump + new SpacePosition(0, 100000, 0), source.Pilot.Position);
        Assert.False(source.Flight.MicroJumpSpooling);
        Assert.Single(source.Flight.Updates, u => u.Command is DestinyCommand.MicroJumpVisual);
        handler.Advance(source);
        observer.Flight.RefreshPeers();
        Assert.Contains(observer.Flight.Updates, u => u.Command is DestinyCommand.SetBallPosition p && p.Id == source.Pilot.ShipId);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
    }

    [Fact]
    public void WarmConcealmentAndMembershipReadsUseNoSqlAndOffliningInvalidatesImmediately()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Prototype);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var store = Store(f);
        store.Activate(f.PilotId, module, DogmaEffect.PrototypeCloaking, new(1, 0, 0), SpacePosition.Zero, f.Clock.Now);
        Assert.NotEqual(0, store.Status(f.ShipId).CloakModuleId);
        Assert.True(store.Available(f.PilotId, f.ShipId, module));
        long ship = f.ShipId;
        using (var measured = SessionPerformance.Begin("spatial.warm", _ => { }, force: true))
        {
            for (int i = 0; i < 20; i++)
            {
                Assert.NotEqual(0, store.Status(ship).CloakModuleId);
                Assert.True(store.Available(f.PilotId, ship, module));
            }
            Assert.Equal(0, measured!.Commands);
            Assert.Equal(0, measured.Connections);
            Assert.Equal(0, measured.FittingCalculations);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(module)!.Online = false;
            world.SaveChanges();
        }
        Assert.False(store.Available(f.PilotId, f.ShipId, module));
        Assert.Equal(0, store.Status(f.ShipId).CloakModuleId);
        store.Stop(f.PilotId, f.ShipId, module, f.Clock.Now);
        Assert.True(store.Status(f.ShipId).TargetingReadyAt > f.Clock.Now);
    }

    [Fact]
    public async Task RestartCancelsPendingJumpWithoutRefundOrReplayAndRetainsCloakDelays()
    {
        using Fixture f = Prepare();
        long cloak = f.Fit(Prototype);
        long drive = f.Fit(LargeDrive);
        Space(f);
        var store = Store(f);
        var origin = f.Db.Store.FindPilot(1000001)!.Position!;
        long ready;
        double capacitor;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            store.Activate(f.PilotId, cloak, DogmaEffect.PrototypeCloaking, new(1, 0, 0), SpacePosition.Zero, f.Clock.Now);
            store.Stop(f.PilotId, f.ShipId, cloak, f.Clock.Now);
            var jump = store.Activate(f.PilotId, drive, DogmaEffect.MicroJumpDrive, new(1, 0, 0), SpacePosition.Zero, f.Clock.Now);
            ready = jump.ReadyAt;
            capacitor = jump.Capacitor;
            await live.FlushAsync();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(0, saved.Set<SpatialNavigationState>().Find(f.ShipId)!.JumpModuleId);
        Assert.True(saved.Set<SpatialNavigationState>().Find(f.ShipId)!.TargetingReadyAt > f.Clock.Now);
        Assert.Equal(ready, saved.Set<Eden.Data.World.Combat.ModuleCooldown>().Find(drive)!.ReadyAt);
        Assert.Equal(capacitor, saved.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Null(store.CompleteJump(f.PilotId, f.ShipId, drive, f.Clock.Now, ready));
        Assert.Equal(origin, f.Db.Store.FindPilot(1000001)!.Position);
    }
}
