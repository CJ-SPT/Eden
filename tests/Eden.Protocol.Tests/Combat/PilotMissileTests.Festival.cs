using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class PilotMissileTests
{
    [Theory]
    [InlineData(19660, 32993, 4000, true)]
    [InlineData(44109, 53343, 20000, false)]
    public void NativeFestivalLaunchersPayCyclesAndShowHarmlessContact(int type, int charge, int cycle, bool useNpc)
    {
        using var f = Prepare(security: .9);
        PassiveCatalogFixture.LoadFestivalLaunchers(f.CatalogPath);
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            Assert.False(FittingCatalog.Definition(catalog, type)!.Launcher);
            Assert.True(PilotAmmunitionStore.CompatibleCharge(catalog, type, charge));
            Assert.False(PilotAmmunitionStore.CompatibleCharge(catalog, type, Charge));
            Assert.False(PilotAmmunitionStore.CompatibleCharge(catalog, Launcher, charge));
        }
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        long module = MissileCombatTests.Load(f, type, charge, quantity: 2);
        var npc = MissileCombatTests.Space(f, 2000);
        int system = npc.SystemId;
        var space = f.Db.Services.GetRequiredService<SharedSpace>();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        f.Db.Store.SetPosition(VictimShip, pilot.Position! + new SpacePosition(1500, 0, 0), system);
        var victim = f.Db.Store.FindPilot(1000002)!;
        using FlightSession observer = new(f.Db.Store, victim, f.Clock, space, [], missiles: Store(f));
        observer.Snapshot();

        using var scope = f.Db.Services.CreateScope();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create(p),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var controller = scope.ServiceProvider.GetRequiredService<MissileHandler>();
        var ammo = f.Db.Services.GetRequiredService<IPilotAmmunition>();
        long target = useNpc ? npc.Id : victim.ShipId;
        Assert.Equal(ShipSafetyLevel.Full, f.Db.Services.GetRequiredService<IPilotSafety>().Read(f.PilotId));
        PilotCallResult Activate() => activation.Call(context, "dogmaIM", "Activate", [module, "useMissiles", target, 0L]);

        Assert.IsType<PilotCallResult.Failure>(Activate());
        Assert.Equal(2, Assert.Single(ammo.Read(f.PilotId, f.ShipId)).Quantity);
        Assert.IsType<object?[]>(targeting.CallWire(context, "dogmaIM", "AddTarget", [target]));
        f.Clock.Milliseconds += 5000;
        targeting.Advance(context);
        Assert.True(targeting.IsLocked(target));

        context.NotificationsBeforeFlight.Clear();
        long launchedAt = f.Clock.Now;
        Assert.Equal(PilotCallResult.None, Activate());
        Assert.Equal(cycle, controller.ActiveEffect(context.Pilot, module)!.DurationMilliseconds);
        Assert.Equal(1, Assert.Single(ammo.Read(f.PilotId, f.ShipId)).Quantity);
        Assert.True(Assert.Single(Store(f).List(system)).Cosmetic);
        observer.RefreshMissiles();
        long projectile = Assert.Single(observer.VisibleMissiles).Id;
        Assert.Contains(
            observer.Updates,
            u =>
                u.Command is DestinyCommand.AddBalls add
                && add.Addition.Slims.OfType<MissileSlimItem>().Any(m => m.Missile.Id == projectile && m.Missile.TypeId == charge)
        );

        f.Clock.Milliseconds += 3000;
        controller.Advance(context);
        observer.RefreshMissiles();
        Assert.Empty(observer.VisibleMissiles);
        Assert.Contains(observer.Updates, u => u.Command is DestinyCommand.RemoveBall remove && remove.Id == projectile && remove.Explode);
        Assert.DoesNotContain(
            observer.Updates.Select(u => u.Command).OfType<DestinyCommand.AttackIndicator>(),
            u => u.ModuleId == projectile
        );
        Assert.Empty(context.NotificationsBeforeFlight);
        Assert.Empty(Store(f).TakeImpacts(f.PilotId));

        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        var saved = reopened.Set<WorldMissile>().Find(projectile)!;
        Assert.Equal(MissileFlightStatus.Impact, saved.Status);
        Assert.Null(saved.Outcome);
        Assert.Equal(launchedAt + cycle * TimeSpan.TicksPerMillisecond, reopened.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Empty(reopened.Set<SavedCrimewatch>());
        Assert.Empty(reopened.Set<SavedPilotHostility>());
        Assert.Empty(reopened.Set<NpcAggression>());
        Assert.Empty(reopened.Set<SavedCombatDamage>());
        Assert.Equal(npc.Health.Remaining.Shield, reopened.Set<WorldNpc>().Find(npc.Id)!.ShieldCharge);
        Assert.Null(reopened.Set<ShipHealthState>().Find(VictimShip));
    }
}
