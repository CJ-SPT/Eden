using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Defenses;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Defenses;

public sealed partial class SignatureSuppressionOperationTests
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
        var observer = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(observer.ShipId, source.Position! + new SpacePosition(1000, 0, 0));
    }

    [Fact]
    public void NativePrimeHudAndObserverFxUseOnePaidCycleAndCooldownSurvivesLateSourceFrames()
    {
        using Fixture f = Prepare(masking: 5);
        long module = f.Fit(Basic);
        Space(f);
        ObserverSpace(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        using var observer = Client(f, 1000002);
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<SignatureSuppressionHandler>();
        source.Notifications.Clear();
        Assert.IsType<PilotCallResult.Success>(
            activation.Call(source, "dogmaIM", "Activate", [module, SignatureSuppressionHandler.EffectName, null, 20L])
        );
        var effect = handler.ActiveEffect(source.Pilot, module)!;
        Assert.Equal(DogmaEffect.SignatureSuppression, effect.Effect);
        Assert.Equal(15000, effect.DurationMilliseconds);
        Assert.Equal(0, effect.Repeats);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        var prime = scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>().PrimeModule(source.Pilot, item);
        Assert.Contains(prime.ActiveEffects, e => e.Key == DogmaEffect.SignatureSuppression);
        Assert.Contains(
            source.Notifications.OfType<ModuleAttributesChanged>().SelectMany(n => n.Changes),
            c => c.Attribute == DogmaAttributeId.SignatureRadius && c.Value < 150
        );
        Assert.Contains(
            source.Notifications.OfType<ModuleAttributesChanged>().SelectMany(n => n.Changes),
            c => c.Attribute == DogmaAttributeId.NextActivationTime && c.Value > f.Clock.Now
        );
        observer.Flight.RefreshPeers();
        Assert.Contains(
            observer.Flight.Updates,
            u =>
                u.Command is DestinyCommand.TimedSpecialEffect fx
                && fx.ModuleId == module
                && fx.Graphic == SignatureSuppressionHandler.Graphic
                && fx.Start
        );
        Assert.IsType<PilotCallResult.Success>(
            activation.Call(source, "dogmaIM", "Deactivate", [module, SignatureSuppressionHandler.EffectName])
        );
        Assert.NotNull(handler.ActiveEffect(source.Pilot, module));
        observer.Flight.Updates.Clear();
        f.Clock.Milliseconds += 15000;
        // The source connection does not advance before the observer sees expiry.
        observer.Flight.RefreshPeers();
        Assert.Contains(
            observer.Flight.Updates,
            u => u.Command is DestinyCommand.TimedSpecialEffect fx && fx.ModuleId == module && !fx.Start
        );
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        Assert.Equal(369, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius], 8);
        observer.Flight.Updates.Clear();
        observer.Flight.Snapshot();
        Assert.DoesNotContain(
            observer.Flight.Updates,
            u => u.Command is DestinyCommand.TimedSpecialEffect fx && fx.ModuleId == module && fx.Start
        );
        handler.Advance(source);
        Assert.Single(
            source.Notifications.OfType<ShipEffectChanged>(),
            e => e.Effect.Effect == DogmaEffect.SignatureSuppression && e.Active
        );
        Assert.Single(
            source.Notifications.OfType<ShipEffectChanged>(),
            e => e.Effect.Effect == DogmaEffect.SignatureSuppression && !e.Active
        );
        Assert.IsType<PilotCallResult.Failure>(
            activation.Call(source, "dogmaIM", "Activate", [module, SignatureSuppressionHandler.EffectName, null, 0L])
        );
        f.Clock.Milliseconds += 149999;
        Assert.IsType<PilotCallResult.Failure>(
            activation.Call(source, "dogmaIM", "Activate", [module, SignatureSuppressionHandler.EffectName, null, 0L])
        );
        f.Clock.Milliseconds++;
        Assert.IsType<PilotCallResult.Success>(
            activation.Call(source, "dogmaIM", "Activate", [module, SignatureSuppressionHandler.EffectName, null, 0L])
        );
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("docked")]
    [InlineData("disconnect")]
    public void CancelledSuppressionNeverRefundsCapacitorOrLosesItsPaidCooldown(string reason)
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        using var source = Client(f, 1000001);
        var handler = scope.ServiceProvider.GetRequiredService<SignatureSuppressionHandler>();
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(source, "dogmaIM", "Activate", [module, SignatureSuppressionHandler.EffectName, null, 0L])
        );
        if (reason == "offline")
        {
            using (WorldDbContext world = new(f.Db.Path))
            {
                world.Set<OwnedItem>().Find(module)!.Online = false;
                world.SaveChanges();
            }
            handler.Advance(source);
        }
        else if (reason == "docked")
        {
            // The session coordinator cancels module controllers on a dock transition.
            handler.Cancel(source);
            f.Db.Store.SetPosition(f.ShipId, null);
        }
        else
        {
            handler.Cancel(source);
        }
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ActiveFittedModule>());
        Assert.Equal(4340, saved.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Equal(f.Clock.Now + TimeSpan.FromSeconds(162).Ticks, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Fact]
    public void SignatureConsumersSeeTheSameCommittedReductionAndRestoreAfterExpiry()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        long ship = f.ShipId;
        double passive = f.Fitting.SignatureRadius(ship, Raven);
        var activation = Store(f).Activate(f.PilotId, module, f.Clock.Now);
        double reduced = f.Fitting.SignatureRadius(ship, Raven);
        Assert.True(reduced < passive);
        Assert.True(
            Eden.Gameplay.Targeting.TargetingDefinitions.LockSeconds(200, reduced)
                > Eden.Gameplay.Targeting.TargetingDefinitions.LockSeconds(200, passive)
        );
        double Chance(double signature) => CombatDamage.HitChance(10000, .03, signature, 15000, 5000, .1, 400);
        Assert.True(Chance(reduced) < Chance(passive));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(activation.Schedule.ExpiresAt - f.Clock.Now));
        Assert.Equal(passive, f.Fitting.SignatureRadius(ship, Raven), 8);
        Assert.Equal(Chance(passive), Chance(f.Fitting.SignatureRadius(ship, Raven)), 8);
    }
}
