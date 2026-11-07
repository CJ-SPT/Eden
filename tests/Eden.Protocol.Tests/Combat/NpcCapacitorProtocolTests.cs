using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class CapacitorWarfareTests
{
    [Theory]
    [InlineData(530, "energyNosferatuFalloff", "effects.EnergyVampire")]
    [InlineData(533, "energyNeutralizerFalloff", "effects.EnergyDestabilization")]
    public void NativeNpcCapacitorCyclesReplayObserverEffectsAndKeepOneHudActivation(int type, string effect, string graphic)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        NpcSpace(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001, npcContacts: true);
        using var observer = Client(f, 1000002, npcContacts: true);
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<CapacitorWarfareHandler>();
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        Assert.NotNull(source.Flight.FindTarget(EnergyNpcId));
        targeting.CallWire(source, "dogmaIM", "AddTarget", [EnergyNpcId]);
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        Assert.True(targeting.IsLocked(EnergyNpcId));
        using (WorldDbContext world = new(f.Db.Path))
        {
            var energy = world.Set<ShipEnergy>().Find(f.ShipId)!;
            energy.Charge = 100;
            energy.UpdatedAt = f.Clock.Now;
            world.SaveChanges();
        }
        source.Notifications.Clear();
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [module, effect, EnergyNpcId, 2L]));
        var prime = handler.ActiveEffect(source.Pilot, module);
        Assert.NotNull(prime);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.True(world.Set<WorldNpcEnergy>().Find(EnergyNpcId)!.Charge < 700);
        }
        observer.Flight.RefreshPeers();
        Assert.Contains(
            observer.Flight.Updates,
            u =>
                u.Command is DestinyCommand.TimedSpecialEffect fx
                && fx.ModuleId == module
                && fx.TargetId == EnergyNpcId
                && fx.Graphic == graphic
                && fx.Start
                && fx.Offensive
        );
        observer.Flight.Updates.Clear();
        observer.Flight.Snapshot();
        Assert.Contains(
            observer.Flight.Updates,
            u => u.Command is DestinyCommand.TimedSpecialEffect fx && fx.ModuleId == module && fx.TargetId == EnergyNpcId && fx.Start
        );
        Assert.Empty(f.Db.Services.GetRequiredService<CapacitorSignal>().Incoming(EnergyNpcId, f.Clock.Now));
        f.Clock.Milliseconds += (long)prime.DurationMilliseconds!.Value;
        handler.Advance(source);
        Assert.Single(source.Notifications, n => n.Name == "OnGodmaShipEffect");
        Assert.NotNull(handler.ActiveEffect(source.Pilot, module));
        activation.Call(source, "dogmaIM", "Deactivate", [module, effect]);
        f.Clock.Milliseconds += (long)prime.DurationMilliseconds.Value;
        handler.Advance(source);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NpcWarpOrDestructionCancelsBeforeTheNextPulseWithoutResourceRefunds(bool destroyed)
    {
        using Fixture f = Prepare();
        long module = f.Fit(533);
        NpcSpace(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001, npcContacts: true);
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<CapacitorWarfareHandler>();
        Assert.NotNull(source.Flight.FindTarget(EnergyNpcId));
        targeting.CallWire(source, "dogmaIM", "AddTarget", [EnergyNpcId]);
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        Assert.True(targeting.IsLocked(EnergyNpcId));
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(source, "dogmaIM", "Activate", [module, "energyNeutralizerFalloff", EnergyNpcId, 2L])
        );
        double paid;
        double drained;
        using (WorldDbContext world = new(f.Db.Path))
        {
            paid = world.Set<ShipEnergy>().Find(f.ShipId)!.Charge;
            drained = world.Set<WorldNpcEnergy>().Find(EnergyNpcId)!.Charge;
            var npc = world.Set<WorldNpc>().Find(EnergyNpcId)!;
            if (destroyed)
            {
                npc.DestroyedAt = f.Clock.Now;
            }
            else
            {
                npc.Navigation = NpcNavigationState.Warping;
            }
            world.SaveChanges();
        }
        source.Flight.RefreshNpcs();
        handler.Advance(source);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        f.Clock.Seconds += 30;
        handler.Advance(source);
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(paid, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Equal(drained, verify.Set<WorldNpcEnergy>().Find(EnergyNpcId)!.Charge);
    }

    [Fact]
    public void WarmNpcCapacitorFramesNeedNoSqlConnectionsOrFittingRecalculations()
    {
        using Fixture f = Prepare();
        long module = f.Fit(533);
        NpcSpace(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001, npcContacts: true);
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<CapacitorWarfareHandler>();
        targeting.CallWire(source, "dogmaIM", "AddTarget", [EnergyNpcId]);
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(source, "dogmaIM", "Activate", [module, "energyNeutralizerFalloff", EnergyNpcId, 0L])
        );
        handler.Advance(source);
        using var metrics = SessionPerformance.Begin("warm NPC capacitor frames", _ => { }, force: true);
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
