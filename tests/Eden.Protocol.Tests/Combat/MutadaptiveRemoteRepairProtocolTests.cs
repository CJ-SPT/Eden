using System.Text.Json;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Repair;
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

public sealed partial class MutadaptiveRemoteRepairTests
{
    private const string EffectName = "ShipModuleRemoteArmorMutadaptiveRepairer";

    private static PilotCallContext Client(Fixture f, long account)
    {
        var pilot = f.Db.Store.FindPilot(account)!;
        var shared = f.Db.Services.GetRequiredService<SharedSpace>();
        PilotCallContext context = new(
            account,
            pilot.Account,
            [pilot],
            p =>
                new(
                    f.Db.Store,
                    p,
                    f.Clock,
                    shared,
                    f.Db.Objects,
                    health: f.Db.Services.GetRequiredService<Eden.Gameplay.Combat.IPilotHealth>()
                ),
            account,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        context.Flight.Updates.Clear();
        return context;
    }

    private static void Lock(Fixture f, IServiceProvider services, PilotCallContext source)
    {
        var targeting = services.GetRequiredService<TargetingHandler>();
        targeting.CallWire(source, "dogmaIM", "AddTarget", [Target(f)]);
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        Assert.True(targeting.IsLocked(Target(f)));
    }

    [Fact]
    public void NativeRepeatsRampOnceAndManualStopFinishesItsPaidCycle()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Repairer);
        Space(f);
        using var scope = f.Db.Services.CreateScope();
        using var recipientScope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var target = Client(f, 1000002);
        Lock(f, scope.ServiceProvider, source);
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<RemoteRepairHandler>();
        var receiver = recipientScope.ServiceProvider.GetRequiredService<RemoteRepairHandler>();
        receiver.Enter(target);
        source.Notifications.Clear();
        double before = Health(f).Remaining.Armor;
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [module, EffectName, Target(f), 10L]));
        Assert.Equal(Effect, handler.ActiveEffect(source.Pilot, module)!.Effect);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        var prime = scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>().PrimeModule(source.Pilot, item);
        Assert.Contains(prime.ActiveEffects, e => e.Key == Effect);
        foreach (string graphic in new[] { "effects.TriglavianBeam", "effects.AttackMode" })
        {
            target.Flight.RefreshPeers();
            Assert.Contains(
                target.Flight.Updates,
                u =>
                    u.Command is DestinyCommand.TimedSpecialEffect fx
                    && fx.ModuleId == module
                    && fx.TargetId == Target(f)
                    && fx.Graphic == graphic
                    && fx.Start
                    && !fx.Offensive
            );
        }
        target.Flight.Updates.Clear();
        target.Flight.Snapshot();
        Assert.Equal(
            2,
            target.Flight.Updates.Count(u => u.Command is DestinyCommand.TimedSpecialEffect fx && fx.ModuleId == module && fx.Start)
        );
        for (int cycle = 0; cycle < 2; cycle++)
        {
            f.Clock.Milliseconds += 6000;
            handler.Advance(source);
            receiver.Advance(target);
            handler.Advance(source);
        }
        Assert.Equal(before + 392 * 1.625 * 2.1, Health(f).Remaining.Armor, 7);
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Deactivate", [module, EffectName]));
        Assert.NotNull(handler.ActiveEffect(source.Pilot, module));
        f.Clock.Milliseconds += 6000;
        handler.Advance(source);
        receiver.Advance(target);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        Assert.Equal(before + 392 * 1.625 * 3.3, Health(f).Remaining.Armor, 7);
        Assert.Single(source.Notifications.OfType<ShipEffectChanged>(), n => n.Active && n.Effect.Effect == Effect);
        Assert.Single(source.Notifications.OfType<ShipEffectChanged>(), n => !n.Active && n.Effect.Effect == Effect);
        Assert.Contains(
            target.Notifications.OfType<ModuleAttributesChanged>().SelectMany(n => n.Changes),
            n => n.Attribute == DogmaAttributeId.ArmorDamage
        );
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [module, EffectName, Target(f), 0L]));
        using WorldDbContext world = new(f.Db.Path);
        var cycleRow = world.Set<WorldRemoteRepair>().Find(module)!;
        Assert.Equal(0, JsonSerializer.Deserialize<RemoteRepairCycle>(cycleRow.CycleJson)!.RampCycles);
    }

    [Theory]
    [InlineData("range")]
    [InlineData("lock")]
    [InlineData("offline")]
    [InlineData("disconnect")]
    [InlineData("loss")]
    public void CancellationClearsTheRampWithoutRefundOrLateHealing(string reason)
    {
        using Fixture f = Prepare();
        long module = f.Fit(Repairer);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var recipient = Client(f, 1000002);
        Lock(f, scope.ServiceProvider, source);
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<RemoteRepairHandler>();
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [module, EffectName, Target(f), 10L]));
        f.Clock.Milliseconds += 6000;
        handler.Advance(source);
        double healed = Health(f).Remaining.Armor;
        double paid;
        using (WorldDbContext world = new(f.Db.Path))
        {
            paid = world.Set<ShipEnergy>().Find(f.ShipId)!.Charge;
            if (reason == "offline")
            {
                world.Set<OwnedItem>().Find(module)!.Online = false;
                world.SaveChanges();
            }
        }
        if (reason == "range")
        {
            var shared = f.Db.Services.GetRequiredService<SharedSpace>();
            var peer = shared.Peers(source.Pilot).Single(p => p.Pilot.ShipId == Target(f));
            var position = source.Pilot.Position! + new SpacePosition(70000, 0, 0);
            shared.Publish(peer with { Pilot = peer.Pilot with { Position = position, X = position.X, Y = position.Y, Z = position.Z } });
        }
        if (reason == "lock")
        {
            scope.ServiceProvider.GetRequiredService<TargetingHandler>().CallWire(source, "dogmaIM", "RemoveTarget", [Target(f)]);
        }
        if (reason == "disconnect")
        {
            handler.Cancel(source);
        }
        if (reason == "loss")
        {
            source.Flight.BeginShipLoss();
        }
        handler.Advance(source);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        f.Clock.Milliseconds += 6000;
        Store(f).Complete(f.PilotId, module, f.Clock.Now);
        Assert.Equal(healed, Health(f).Remaining.Armor, 8);
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Empty(verify.Set<WorldRemoteRepair>());
        Assert.Equal(paid, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge, 8);
    }

    [Fact]
    public void FailedNextPaymentEndsTheChainAndLateFramesEarnOnlyOneStep()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Repairer);
        Space(f);
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var target = Client(f, 1000002);
        Lock(f, scope.ServiceProvider, source);
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<RemoteRepairHandler>();
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [module, EffectName, Target(f), 10L]));
        f.Clock.Seconds += 60;
        handler.Advance(source);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var cycle = JsonSerializer.Deserialize<RemoteRepairCycle>(world.Set<WorldRemoteRepair>().Find(module)!.CycleJson)!;
            Assert.Equal(1, cycle.RampCycles);
            Assert.Equal(f.Clock.Now, cycle.StartedAt);
        }
        f.Clock.Milliseconds += 6000;
        Charge(f, 0);
        handler.Advance(source);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<WorldRemoteRepair>());
            Assert.Equal(0, world.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        }
        Charge(f, 2000);
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [module, EffectName, Target(f), 0L]));
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(0, JsonSerializer.Deserialize<RemoteRepairCycle>(verify.Set<WorldRemoteRepair>().Find(module)!.CycleJson)!.RampCycles);
    }

    [Fact]
    public void WarmRangeAndCyclePollingDoesNotReadSqlOrRecalculateFittings()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Repairer);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var target = Client(f, 1000002);
        Lock(f, scope.ServiceProvider, source);
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<RemoteRepairHandler>();
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [module, EffectName, Target(f), 2L]));
        handler.Advance(source);
        using var metrics = SessionPerformance.Begin("warm mutadaptive handlers", _ => { }, force: true);
        for (int frame = 0; frame < 12; frame++)
        {
            f.Clock.Milliseconds += 50;
            handler.Advance(source);
        }
        Assert.Equal(0, metrics!.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }
}
