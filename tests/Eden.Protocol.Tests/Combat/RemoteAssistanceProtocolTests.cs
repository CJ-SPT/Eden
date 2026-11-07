using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.Protocol.Targeting;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class RemoteAssistanceTests
{
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

    [Theory]
    [InlineData(2103, "shipModuleRemoteTrackingComputer", "effects.TurretWeaponRangeTrackingSpeedMultiplyTarget", "remoteTracking")]
    [InlineData(1963, "remoteSensorBoostFalloff", "effects.ElectronicAttributeModifyTarget", "sensorBooster")]
    public void NativeDispatchRepeatsPaidStopAndObserverSnapshotsRetainOneHudActivation(
        int type,
        string effect,
        string graphic,
        string slot
    )
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        TargetGun(f);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var sourceScope = f.Db.Services.CreateScope();
        using var recipientScope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var recipient = Client(f, 1000002);
        var handler = sourceScope.ServiceProvider.GetRequiredService<RemoteAssistanceHandler>();
        var receiver = recipientScope.ServiceProvider.GetRequiredService<RemoteAssistanceHandler>();
        var activation = sourceScope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var targeting = sourceScope.ServiceProvider.GetRequiredService<TargetingHandler>();
        targeting.CallWire(source, "dogmaIM", "AddTarget", [Target(f)]);
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        Assert.True(targeting.IsLocked(Target(f)));
        source.Notifications.Clear();
        recipient.Notifications.Clear();
        recipient.NotificationsBeforeFlight.Clear();
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [module, effect, Target(f), 2L]));
        var prime = handler.ActiveEffect(source.Pilot, module);
        Assert.NotNull(prime);
        var fitted = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        var dogma = sourceScope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>().PrimeModule(source.Pilot, fitted);
        Assert.Contains(dogma.ActiveEffects, e => e.Key == prime.Effect && e.Value.TargetId == Target(f));
        receiver.Advance(recipient);
        var started = Assert.Single(recipient.NotificationsBeforeFlight.OfType<RemoteAssistanceChanged>());
        Assert.Equal(slot, started.Arguments[3]);
        Assert.True(started.Active);
        Assert.Contains(recipient.Notifications, n => n.Name == "OnModuleAttributeChanges");
        recipient.Flight.RefreshPeers();
        Assert.Contains(
            recipient.Flight.Updates,
            u =>
                u.Command is DestinyCommand.TimedSpecialEffect fx
                && fx.ModuleId == module
                && fx.TargetId == Target(f)
                && fx.Graphic == graphic
                && !fx.Offensive
                && fx.Start
        );
        recipient.Flight.Updates.Clear();
        recipient.Flight.Snapshot();
        Assert.Contains(
            recipient.Flight.Updates,
            u => u.Command is DestinyCommand.TimedSpecialEffect fx && fx.ModuleId == module && fx.TargetId == Target(f) && fx.Start
        );
        f.Clock.Milliseconds += (long)prime.DurationMilliseconds!.Value;
        // An observer advancing before the source must not expire a paid continuous effect.
        receiver.Advance(recipient);
        Assert.Single(Store(f).Incoming(Target(f)));
        handler.Advance(source);
        receiver.Advance(recipient);
        Assert.Single(source.Notifications, n => n.Name == "OnGodmaShipEffect");
        Assert.Single(recipient.NotificationsBeforeFlight.OfType<RemoteAssistanceChanged>(), n => n.Active);
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Deactivate", [module, effect]));
        Assert.NotNull(handler.ActiveEffect(source.Pilot, module));
        double paid;
        using (WorldDbContext world = new(f.Db.Path))
        {
            paid = world.Set<ShipEnergy>().Find(f.ShipId)!.Charge;
        }
        f.Clock.Milliseconds += (long)prime.DurationMilliseconds.Value;
        handler.Advance(source);
        receiver.Advance(recipient);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        Assert.Empty(Store(f).Incoming(Target(f)));
        Assert.Single(recipient.NotificationsBeforeFlight.OfType<RemoteAssistanceChanged>(), n => !n.Active);
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(paid, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Empty(verify.Set<ActiveFittedModule>());
    }

    [Theory]
    [InlineData(2103, "shipModuleRemoteTrackingComputer")]
    [InlineData(1963, "remoteSensorBoostFalloff")]
    public void WarmSourceAndRecipientFramesNeedNoSqlConnectionsOrFittingCalculations(int type, string effect)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var sourceScope = f.Db.Services.CreateScope();
        using var recipientScope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var recipient = Client(f, 1000002);
        var handler = sourceScope.ServiceProvider.GetRequiredService<RemoteAssistanceHandler>();
        var receiver = recipientScope.ServiceProvider.GetRequiredService<RemoteAssistanceHandler>();
        var targeting = sourceScope.ServiceProvider.GetRequiredService<TargetingHandler>();
        targeting.CallWire(source, "dogmaIM", "AddTarget", [Target(f)]);
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        Assert.IsType<PilotCallResult.Success>(handler.Call(source, "dogmaIM", "Activate", [module, effect, Target(f), 0L]));
        handler.Advance(source);
        receiver.Advance(recipient);
        using var metrics = SessionPerformance.Begin("warm remote assistance frames", _ => { }, force: true);
        for (int frame = 0; frame < 12; frame++)
        {
            f.Clock.Milliseconds += 50;
            handler.Advance(source);
            receiver.Advance(recipient);
        }
        Assert.Equal(0, metrics!.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("offline")]
    [InlineData("disconnect")]
    [InlineData("recipient-disconnect")]
    [InlineData("recipient-warp")]
    [InlineData("source-loss")]
    [InlineData("recipient-loss")]
    public void CancellationRemovesTheRecipientBonusWithoutRefundOrAnotherPaidPulse(string reason)
    {
        using Fixture f = Prepare();
        long module = f.Fit(1963);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var recipient = Client(f, 1000002);
        var handler = scope.ServiceProvider.GetRequiredService<RemoteAssistanceHandler>();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var before = f.Fitting.ShipAttributes(TargetPilot(f));
        targeting.CallWire(source, "dogmaIM", "AddTarget", [Target(f)]);
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(source, "dogmaIM", "Activate", [module, "remoteSensorBoostFalloff", Target(f), 3L])
        );
        if (reason == "lock")
        {
            targeting.CallWire(source, "dogmaIM", "RemoveTarget", [Target(f)]);
        }
        else if (reason == "offline")
        {
            using WorldDbContext world = new(f.Db.Path);
            world.Set<OwnedItem>().Find(module)!.Online = false;
            world.SaveChanges();
        }
        else if (reason == "disconnect")
        {
            handler.Cancel(source);
        }
        else if (reason == "recipient-disconnect")
        {
            using var recipientScope = f.Db.Services.CreateScope();
            var receiver = recipientScope.ServiceProvider.GetRequiredService<RemoteAssistanceHandler>();
            receiver.Advance(recipient);
            receiver.Cancel(recipient);
        }
        else if (reason == "source-loss")
        {
            source.Flight.BeginShipLoss();
        }
        else if (reason == "recipient-loss")
        {
            recipient.Flight.BeginShipLoss();
        }
        else
        {
            var space = f.Db.Services.GetRequiredService<SharedSpace>();
            var peer = space.Peers(source.Pilot).Single(p => p.Pilot.ShipId == Target(f));
            var pilot = peer.Pilot;
            space.Publish(
                peer with
                {
                    Warp = new WarpCourse(pilot.Position!, pilot.Position! + new SpacePosition(1000000000, 0, 0), 1, pilot.Flight, 100),
                }
            );
        }
        handler.Advance(source);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        Assert.Empty(Store(f).Incoming(Target(f)));
        Assert.Equal(before[DogmaAttributeId.ScanResolution], f.Fitting.ShipAttributes(TargetPilot(f))[DogmaAttributeId.ScanResolution]);
        double paid;
        using (WorldDbContext world = new(f.Db.Path))
        {
            paid = world.Set<ShipEnergy>().Find(f.ShipId)!.Charge;
        }
        f.Clock.Seconds += 60;
        handler.Advance(source);
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(paid, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Empty(verify.Set<WorldRemoteAssistance>());
        Assert.Empty(verify.Set<ActiveFittedModule>());
    }

    [Fact]
    public void LateSessionFramesStartOnlyOneNewPaidCycleAtTheCurrentDeadline()
    {
        using Fixture f = Prepare();
        long module = f.Fit(1963);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var recipient = Client(f, 1000002);
        var handler = scope.ServiceProvider.GetRequiredService<RemoteAssistanceHandler>();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        targeting.CallWire(source, "dogmaIM", "AddTarget", [Target(f)]);
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(source, "dogmaIM", "Activate", [module, "remoteSensorBoostFalloff", Target(f), 3L])
        );
        f.Clock.Seconds += 120;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var energy = world.Set<ShipEnergy>().Find(f.ShipId)!;
            energy.Charge = 1000;
            energy.UpdatedAt = f.Clock.Now;
            world.SaveChanges();
        }
        handler.Advance(source);
        var contact = Assert.Single(Store(f).Incoming(Target(f)));
        Assert.Equal(f.Clock.Now, contact.StartedAt);
        Assert.Equal(f.Clock.Now + TimeSpan.FromSeconds(10).Ticks, contact.ExpiresAt);
        handler.Advance(source);
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(985, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Equal(contact.ExpiresAt, verify.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }
}
