using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Defenses;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Defenses;

public sealed partial class AssaultDamageControlTests
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

    private static void ObserverSpace(Fixture f)
    {
        var source = f.Db.Store.FindPilot(1000001)!;
        f.Db.Store.SetPosition(f.Db.Store.FindPilot(1000002)!.ShipId, source.Position! + new SpacePosition(1000, 0, 0));
    }

    [Fact]
    public void NativePrimeHudAndObserverFxHaveOnePaidBurstAndExpireWithoutSourceFrames()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        Space(f);
        ObserverSpace(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var observer = Client(f, 1000002);
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<AssaultDamageControlHandler>();
        source.Notifications.Clear();
        Assert.IsType<PilotCallResult.Success>(
            activation.Call(source, "dogmaIM", "Activate", [module, AssaultDamageControlHandler.EffectName, null, 20L])
        );
        var effect = handler.ActiveEffect(source.Pilot, module)!;
        Assert.IsType<PilotCallResult.Success>(
            activation.Call(source, "dogmaIM", "Activate", [module, AssaultDamageControlHandler.EffectName, null, 20L])
        );
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(68, world.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        }
        Assert.Equal(DogmaEffect.AssaultDamageControl, effect.Effect);
        Assert.Equal(8775, effect.DurationMilliseconds);
        Assert.Equal(0, effect.Repeats);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        var prime = scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>().PrimeModule(source.Pilot, item);
        Assert.Contains(prime.ActiveEffects, e => e.Key == DogmaEffect.AssaultDamageControl);
        var attributes = source.Notifications.OfType<ModuleAttributesChanged>().SelectMany(n => n.Changes).ToArray();
        Assert.Equal(12, attributes.Count(c => AssaultDamageControlRules.Resistances.Any(b => b.ShipAttribute == c.Attribute)));
        Assert.Contains(attributes, c => c.Attribute == DogmaAttributeId.NextActivationTime && c.Value > f.Clock.Now);
        observer.Flight.RefreshPeers();
        Assert.Contains(
            observer.Flight.Updates,
            u =>
                u.Command is DestinyCommand.TimedSpecialEffect fx
                && fx.ModuleId == module
                && fx.Graphic == AssaultDamageControlHandler.Graphic
                && fx.Start
        );
        Assert.IsType<PilotCallResult.Success>(
            activation.Call(source, "dogmaIM", "Deactivate", [module, AssaultDamageControlHandler.EffectName])
        );
        Assert.NotNull(handler.ActiveEffect(source.Pilot, module));
        observer.Flight.Updates.Clear();
        f.Clock.Milliseconds += 8775;
        observer.Flight.RefreshPeers();
        Assert.Single(
            observer.Flight.Updates,
            u => u.Command is DestinyCommand.TimedSpecialEffect fx && fx.ModuleId == module && !fx.Start
        );
        observer.Flight.Updates.Clear();
        observer.Flight.RefreshPeers();
        observer.Flight.Snapshot();
        Assert.DoesNotContain(
            observer.Flight.Updates,
            u => u.Command is DestinyCommand.TimedSpecialEffect fx && fx.ModuleId == module && fx.Start
        );
        handler.Advance(source);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        Assert.Single(
            source.Notifications.OfType<ShipEffectChanged>(),
            e => e.Effect.Effect == DogmaEffect.AssaultDamageControl && e.Active
        );
        Assert.Single(
            source.Notifications.OfType<ShipEffectChanged>(),
            e => e.Effect.Effect == DogmaEffect.AssaultDamageControl && !e.Active
        );
        Assert.IsType<PilotCallResult.Failure>(
            activation.Call(source, "dogmaIM", "Activate", [module, AssaultDamageControlHandler.EffectName, null, 0L])
        );
        f.Clock.Milliseconds += 149999;
        Assert.IsType<PilotCallResult.Failure>(
            activation.Call(source, "dogmaIM", "Activate", [module, AssaultDamageControlHandler.EffectName, null, 0L])
        );
        f.Clock.Milliseconds++;
        Assert.IsType<PilotCallResult.Success>(
            activation.Call(source, "dogmaIM", "Activate", [module, AssaultDamageControlHandler.EffectName, null, 0L])
        );
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("docked")]
    [InlineData("disconnect")]
    [InlineData("loss")]
    public void CancellationClearsOnlyTheActiveBonusAndRetainsPaymentAndCooldown(string reason)
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        var handler = scope.ServiceProvider.GetRequiredService<AssaultDamageControlHandler>();
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(source, "dogmaIM", "Activate", [module, AssaultDamageControlHandler.EffectName, null, 0L])
        );
        if (reason == "offline")
        {
            using WorldDbContext world = new(f.Db.Path);
            world.Set<OwnedItem>().Find(module)!.Online = false;
            world.SaveChanges();
        }
        if (reason == "loss")
        {
            source.Flight.BeginShipLoss();
        }
        if (reason is "disconnect" or "docked")
        {
            handler.Cancel(source);
        }
        if (reason == "docked")
        {
            f.Db.Store.SetPosition(f.ShipId, null);
        }
        handler.Advance(source);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ActiveFittedModule>());
        Assert.Equal(68, saved.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Equal(f.Clock.Now + TimeSpan.FromMilliseconds(158775).Ticks, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void HealthAndCombatConsumersUseCommittedShieldArmorAndStructureResistances(int layer)
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var health = f.Db.Services.GetRequiredService<IPilotHealth>();
        var before = health.Read(f.PilotId, f.Clock.Now);
        var activation = Store(f).Activate(f.PilotId, module, f.Clock.Now);
        var active = health.Read(f.PilotId, f.Clock.Now);
        Assert.Equal(before.Remaining, active.Remaining);
        HullDamage remaining = new(layer == 0 ? 100 : 0, layer <= 1 ? 100 : 0, 100);
        DamageVector damage = new(1, 1, 1, 1);
        double passiveDamage = CombatDamage.Apply(remaining, before.Defense, damage).Applied;
        double activeDamage = CombatDamage.Apply(remaining, active.Defense, damage).Applied;
        Assert.True(activeDamage < passiveDamage);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(activation.Schedule.ExpiresAt - f.Clock.Now));
        var expired = health.Read(f.PilotId, f.Clock.Now);
        Assert.Equal(passiveDamage, CombatDamage.Apply(remaining, expired.Defense, damage).Applied, 8);
    }

    [Fact]
    public void WarmActivePollingUsesNoSqlConnectionsOrFittingCalculations()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        var handler = scope.ServiceProvider.GetRequiredService<AssaultDamageControlHandler>();
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(source, "dogmaIM", "Activate", [module, AssaultDamageControlHandler.EffectName, null, 0L])
        );
        handler.Advance(source);
        using var metrics = SessionPerformance.Begin("warm assault damage control", _ => { }, force: true)!;
        for (int frame = 0; frame < 12; frame++)
        {
            f.Clock.Milliseconds += 50;
            handler.Advance(source);
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }
}
