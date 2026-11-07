using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class CapacitorWarfareTests
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
    [InlineData(529, "shipModuleRemoteCapacitorTransmitter", "effects.EnergyTransfer", false)]
    [InlineData(530, "energyNosferatuFalloff", "effects.EnergyVampire", true)]
    [InlineData(533, "energyNeutralizerFalloff", "effects.EnergyDestabilization", true)]
    public void NativeCyclesRepeatWithOneHudActivationAndReplayTargetedEffects(int type, string effect, string graphic, bool offensive)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var sourceScope = f.Db.Services.CreateScope();
        using var targetScope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var target = Client(f, 1000002);
        var handler = sourceScope.ServiceProvider.GetRequiredService<CapacitorWarfareHandler>();
        var receiver = targetScope.ServiceProvider.GetRequiredService<CapacitorWarfareHandler>();
        var activation = sourceScope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var targeting = sourceScope.ServiceProvider.GetRequiredService<TargetingHandler>();
        receiver.Enter(target);
        targeting.CallWire(source, "dogmaIM", "AddTarget", [Target(f)]);
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        Assert.True(targeting.IsLocked(Target(f)));
        source.Notifications.Clear();
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [module, effect, Target(f), 2L]));
        var prime = handler.ActiveEffect(source.Pilot, module);
        Assert.NotNull(prime);
        receiver.Advance(target);
        Assert.Equal(offensive, target.NotificationsBeforeFlight.OfType<CapacitorEwarChanged>().Any(n => n.Active));
        if (offensive)
        {
            var native = target.NotificationsBeforeFlight.OfType<CapacitorEwarChanged>().Single();
            Assert.Equal(type == 530 ? "ewEnergyVampire" : "ewEnergyNeut", native.Arguments[3]);
        }
        var capUpdate = target
            .Notifications.OfType<ModuleAttributesChanged>()
            .SelectMany(n => n.Changes)
            .Last(c => c.Attribute == DogmaAttributeId.CapacitorCharge);
        var resources = f.Db.Services.GetRequiredService<IPilotShipResources>();
        Assert.Equal(resources.Capacitor(target.Pilot.PilotId, f.Clock.Now), capUpdate.Value, 7);
        target.Flight.RefreshPeers();
        Assert.Contains(
            target.Flight.Updates,
            u =>
                u.Command is DestinyCommand.TimedSpecialEffect fx
                && fx.ModuleId == module
                && fx.TargetId == Target(f)
                && fx.Graphic == graphic
                && fx.Offensive == offensive
                && fx.Start
        );
        target.Flight.Updates.Clear();
        target.Flight.Snapshot();
        Assert.Contains(target.Flight.Updates, u => u.Command is DestinyCommand.TimedSpecialEffect fx && fx.ModuleId == module && fx.Start);
        f.Clock.Milliseconds += (long)prime.DurationMilliseconds!.Value;
        handler.Advance(source);
        receiver.Advance(target);
        Assert.Single(source.Notifications, n => n.Name == "OnGodmaShipEffect");
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Deactivate", [module, effect]));
        Assert.NotNull(handler.ActiveEffect(source.Pilot, module));
        double sourceCharge;
        double targetCharge;
        using (WorldDbContext paid = new(f.Db.Path))
        {
            sourceCharge = paid.Set<ShipEnergy>().Find(f.ShipId)!.Charge;
            targetCharge = paid.Set<ShipEnergy>().Find(Target(f))!.Charge;
        }
        f.Clock.Milliseconds += (long)prime.DurationMilliseconds.Value;
        handler.Advance(source);
        receiver.Advance(target);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        Assert.Empty(f.Db.Services.GetRequiredService<CapacitorSignal>().Incoming(Target(f), f.Clock.Now));
        using WorldDbContext stopped = new(f.Db.Path);
        Assert.Equal(sourceCharge, stopped.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Equal(targetCharge, stopped.Set<ShipEnergy>().Find(Target(f))!.Charge);
        Assert.Empty(stopped.Set<ActiveFittedModule>());
    }

    [Theory]
    [InlineData(529)]
    [InlineData(530)]
    [InlineData(533)]
    public void WarmUnchangedSourceAndRecipientFramesDoNotReadSqlOrRecalculateFits(int type)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var sourceScope = f.Db.Services.CreateScope();
        using var targetScope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var target = Client(f, 1000002);
        var handler = sourceScope.ServiceProvider.GetRequiredService<CapacitorWarfareHandler>();
        var receiver = targetScope.ServiceProvider.GetRequiredService<CapacitorWarfareHandler>();
        var targeting = sourceScope.ServiceProvider.GetRequiredService<TargetingHandler>();
        receiver.Enter(target);
        targeting.CallWire(source, "dogmaIM", "AddTarget", [Target(f)]);
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        var effect =
            type == 529 ? "shipModuleRemoteCapacitorTransmitter"
            : type == 530 ? "energyNosferatuFalloff"
            : "energyNeutralizerFalloff";
        Assert.IsType<PilotCallResult.Success>(handler.Call(source, "dogmaIM", "Activate", [module, effect, Target(f), 0L]));
        handler.Advance(source);
        receiver.Advance(target);
        using var metrics = Eden.Infrastructure.Diagnostics.SessionPerformance.Begin("warm capacitor frames", _ => { }, force: true);
        for (int frame = 0; frame < 12; frame++)
        {
            f.Clock.Milliseconds += 50;
            handler.Advance(source);
            receiver.Advance(target);
        }
        Assert.Equal(0, metrics!.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void RecipientReadsCurrentCapacitorAfterALaterCommittedSpend()
    {
        using Fixture f = Prepare();
        long module = f.Fit(529);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var target = Client(f, 1000002);
        var receiver = scope.ServiceProvider.GetRequiredService<CapacitorWarfareHandler>();
        receiver.Enter(target);
        Activate(f, module, DogmaEffect.RemoteCapacitorTransfer);
        double expected;
        var attributes = f.Fitting.ShipAttributes(target.Pilot.PilotId);
        using (WorldDbContext world = new(f.Db.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            expected = ShipResources.SpendCapacitor(world, target.Pilot.ShipId, attributes, 123, f.Clock.Now);
            world.SaveChanges();
            transaction.Commit();
        }
        receiver.Advance(target);
        var update = target
            .Notifications.OfType<ModuleAttributesChanged>()
            .SelectMany(n => n.Changes)
            .Last(c => c.Attribute == DogmaAttributeId.CapacitorCharge);
        Assert.Equal(expected, update.Value, 7);
    }

    [Theory]
    [InlineData(529, "lock")]
    [InlineData(530, "lock")]
    [InlineData(533, "lock")]
    [InlineData(529, "disconnect")]
    [InlineData(530, "disconnect")]
    [InlineData(533, "disconnect")]
    [InlineData(533, "offline")]
    public void LifecycleCancellationClearsEffectsWithoutRefundOrAnotherPulse(int type, string reason)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var target = Client(f, 1000002);
        var handler = scope.ServiceProvider.GetRequiredService<CapacitorWarfareHandler>();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        targeting.CallWire(source, "dogmaIM", "AddTarget", [Target(f)]);
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        var effect =
            type == 529 ? "shipModuleRemoteCapacitorTransmitter"
            : type == 530 ? "energyNosferatuFalloff"
            : "energyNeutralizerFalloff";
        Assert.IsType<PilotCallResult.Success>(handler.Call(source, "dogmaIM", "Activate", [module, effect, Target(f), 3L]));
        if (reason == "lock")
        {
            targeting.CallWire(source, "dogmaIM", "RemoveTarget", [Target(f)]);
            handler.Advance(source);
        }
        else if (reason == "offline")
        {
            // In-space refitting is a later milestone; exercise committed membership invalidation here.
            using (WorldDbContext world = new(f.Db.Path))
            {
                world.Set<OwnedItem>().Find(module)!.Online = false;
                world.SaveChanges();
            }
            handler.Advance(source);
        }
        else
        {
            handler.Cancel(source);
        }
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        Assert.Empty(f.Db.Services.GetRequiredService<CapacitorSignal>().Incoming(Target(f), f.Clock.Now));
        double sourceCharge;
        double targetCharge;
        using (WorldDbContext paid = new(f.Db.Path))
        {
            sourceCharge = paid.Set<ShipEnergy>().Find(f.ShipId)!.Charge;
            targetCharge = paid.Set<ShipEnergy>().Find(Target(f))!.Charge;
        }
        f.Clock.Seconds += 60;
        handler.Advance(source);
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(sourceCharge, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Equal(targetCharge, verify.Set<ShipEnergy>().Find(Target(f))!.Charge);
        Assert.Empty(verify.Set<ActiveFittedModule>());
    }
}
