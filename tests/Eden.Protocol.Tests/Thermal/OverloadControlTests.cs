using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Repair;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Thermal;

public sealed class OverloadControlTests
{
    private const int ShieldBooster = 399;
    private const int ArmorRepairer = 523;
    private const int ShieldExtender = 377;

    [Fact]
    public void NativePreheatPaysARealModifiedCycleAndStopWaitsForItsFundedBoundary()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        f.Train(OverloadRules.ThermodynamicsSkill, 3);
        long module = f.Fit(ShieldBooster);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new SharedSpace(), f.Db.Objects),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        using CatalogDbContext catalog = new(f.CatalogPath);
        long effect = OverloadCatalog.Definition(catalog, ShieldBooster)!.EffectId;
        var controls = scope.ServiceProvider.GetRequiredService<OverloadHandler>();
        Assert.IsType<PilotCallResult.Success>(controls.Call(context, "dogmaIM", "Overload", [module, effect]));
        Assert.Single(scope.ServiceProvider.GetRequiredService<IPilotOverloading>().Requested(f.PilotId, f.ShipId));
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        Assert.IsType<PilotCallResult.Success>(activation.Call(context, "dogmaIM", "Activate", [module, "shieldBoosting", null, 0L]));
        var heat = scope.ServiceProvider.GetRequiredService<IPilotShipHeat>();
        var cycle = Assert.Single(heat.Read(f.PilotId, f.ShipId, f.Clock.Now).Cycles);
        Assert.True(cycle.CompletesAt > f.Clock.Now);
        Assert.IsType<PilotCallResult.Success>(controls.Call(context, "dogmaIM", "StopOverload", [module, effect]));
        Assert.Empty(scope.ServiceProvider.GetRequiredService<IPilotOverloading>().Requested(f.PilotId, f.ShipId));
        Assert.Single(heat.Read(f.PilotId, f.ShipId, f.Clock.Now).Cycles);
        f.Clock.Milliseconds += 10000;
        Assert.Empty(heat.Read(f.PilotId, f.ShipId, f.Clock.Now).Cycles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PaidDamageAndBurnoutAdvanceWithoutObserversAndSurviveCheckpoint(bool fatal)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        f.Train(OverloadRules.ThermodynamicsSkill, 3);
        long module = f.Fit(ShieldBooster);
        long ship = f.ShipId;
        f.Db.Store.SetPosition(ship, SpacePosition.Zero);
        double damage;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            new PilotShieldBoostStore(new(f.Db.Path)).BoostOverloaded(f.PilotId, module, f.Clock.Now);
            var heat = f.Db.Services.GetRequiredService<IPilotShipHeat>();
            var conditions = f.Db.Services.GetRequiredService<IPilotModuleConditions>();
            _ = conditions.Read(f.PilotId, ship);
            var state = heat.Read(f.PilotId, ship, f.Clock.Now);
            var cycle = Assert.Single(state.Cycles);
            state = state with
            {
                Racks = state.Racks.Select(r => r with { Heat = 100 }).ToArray(),
                Modules = state.Modules.Select(m => m.ItemId == module ? m with { Damage = fatal ? 1000 : 1 } : m).ToArray(),
            };
            for (ulong seed = 1; ; seed++)
            {
                state = state with { DamageSeed = seed };
                if (HeatDamageRules.Advance(state, cycle.CompletesAt).Modules.Single(m => m.ItemId == module).Condition.Damage > 0)
                {
                    break;
                }
                Assert.True(seed < 10000);
            }
            if (fatal)
            {
                using WorldDbContext blocking = new(f.Db.Path);
                blocking.Database.ExecuteSqlRaw(
                    "CREATE TRIGGER RejectHeatBurnout BEFORE UPDATE OF Online ON WorldItem "
                        + "WHEN NEW.Online=0 BEGIN SELECT RAISE(ABORT,'reject burnout'); END"
                );
            }
            live.Heat.PublishCommitted(f.PilotId, ship, state);
            live.Heat.PublishResident(ship, state);
            using var before = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
            if (fatal)
            {
                long originalTime = f.Clock.Milliseconds;
                f.Clock.Milliseconds += (cycle.CompletesAt - f.Clock.Now) / TimeSpan.TicksPerMillisecond + 1;
                Assert.Throws<DbUpdateException>(() =>
                {
                    using var ownership = live.Enter();
                });
                f.Clock.Milliseconds = originalTime;
                Assert.True(HeatCycleRules.SameState(state, heat.Read(f.PilotId, ship, f.Clock.Now)));
                using WorldDbContext blocking = new(f.Db.Path);
                Assert.True(blocking.Set<OwnedItem>().Find(module)!.Online);
                Assert.Null(blocking.Set<SavedModuleCondition>().Find(module));
                blocking.Database.ExecuteSqlRaw("DROP TRIGGER RejectHeatBurnout");
                live.Heat.PublishCommitted(f.PilotId, ship, state);
                live.Heat.PublishResident(ship, state);
            }
            f.Clock.Milliseconds += (cycle.CompletesAt - f.Clock.Now) / TimeSpan.TicksPerMillisecond + 1;
            using (var metrics = SessionPerformance.Begin("unobserved paid heat completion", _ => { }, force: true)!)
            {
                using (live.Enter()) { }
                var condition = conditions.Read(f.PilotId, ship).Single(m => m.Condition.ItemId == module).Condition;
                damage = condition.Damage;
                Assert.True(damage > 0);
                Assert.Equal(fatal, condition.BurnedOut);
                if (!fatal)
                {
                    Assert.Equal(0, metrics.Commands);
                    Assert.Equal(0, metrics.Connections);
                    Assert.Equal(0, metrics.FittingCalculations);
                }
            }
            Assert.Empty(heat.Read(f.PilotId, ship, f.Clock.Now).Cycles);
            before.Project();
            using (WorldDbContext detached = new(before.Database))
            {
                Assert.Null(detached.Set<SavedModuleCondition>().Find(module));
                Assert.True(detached.Set<OwnedItem>().Find(module)!.Online);
            }
            await live.FlushAsync();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(damage, saved.Set<SavedModuleCondition>().Find(module)!.Damage);
        Assert.Equal(!fatal, saved.Set<OwnedItem>().Find(module)!.Online);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("effect")]
    [InlineData("skill")]
    [InlineData("offline")]
    [InlineData("missing")]
    public void InvalidNativeSelectionsPreserveResourcesAndRequests(string variant)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        if (variant != "skill")
        {
            f.Train(OverloadRules.ThermodynamicsSkill, 3);
        }
        long module = f.Fit(ShieldBooster, online: variant != "offline");
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var controls = f.Db.Services.GetRequiredService<IPilotOverloading>();
        using CatalogDbContext catalog = new(f.CatalogPath);
        int effect = OverloadCatalog.Definition(catalog, ShieldBooster)!.EffectId;
        Assert.Throws<InvalidDataException>(() =>
            controls.Set(
                new(
                    variant == "foreign" ? long.MaxValue : f.PilotId,
                    f.ShipId,
                    variant == "missing" ? long.MaxValue : module,
                    variant == "effect" ? effect + 1 : effect,
                    OverloadScope.Module,
                    true,
                    f.Clock.Now
                )
            )
        );
        Assert.Empty(controls.Requested(f.PilotId, f.ShipId));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<SavedShipHeat>());
        Assert.Empty(world.Set<SavedModuleCondition>());
        Assert.Equal(variant != "offline", world.Set<OwnedItem>().Find(module)!.Online);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RackSelectionIncludesEligibleModulesInTheirPhysicalRack(bool offline, bool passive)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        f.Train(OverloadRules.ThermodynamicsSkill, 3);
        long first = f.Fit(ShieldBooster);
        long second = f.Fit(passive ? ShieldExtender : ShieldBooster, online: !offline);
        long armor = f.Fit(ArmorRepairer);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var controls = f.Db.Services.GetRequiredService<IPilotOverloading>();
        var selected = controls.Set(new(f.PilotId, f.ShipId, passive ? second : first, null, OverloadScope.Rack, true, f.Clock.Now));
        Assert.Equal(offline || passive ? 1 : 2, selected.Count);
        Assert.Contains(selected, m => m.ModuleId == first);
        Assert.Equal(!offline && !passive, selected.Any(m => m.ModuleId == second));
        Assert.DoesNotContain(selected, m => m.ModuleId == armor);
        controls.Clear(f.PilotId, f.ShipId);
        Assert.Empty(controls.Requested(f.PilotId, f.ShipId));
    }

    [Fact]
    public void CoordinateUpdatesPreservePreheatAndDockingClearsItAfterCommit()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        f.Train(OverloadRules.ThermodynamicsSkill, 3);
        long module = f.Fit(ShieldBooster);
        long ship = f.ShipId;
        f.Db.Store.SetPosition(ship, SpacePosition.Zero);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var controls = f.Db.Services.GetRequiredService<IPilotOverloading>();
        using CatalogDbContext catalog = new(f.CatalogPath);
        controls.Set(
            new(
                f.PilotId,
                ship,
                module,
                OverloadCatalog.Definition(catalog, ShieldBooster)!.EffectId,
                OverloadScope.Module,
                true,
                f.Clock.Now
            )
        );
        f.Db.Store.SetPosition(ship, new SpacePosition(100, 0, 0));
        Assert.Contains(module, controls.Requested(f.PilotId, ship));
        using (var action = live.Economy.Begin(new(pilots: [f.PilotId], ships: [ship])))
        {
            Assert.True(SqliteWorld.SetPosition(action, catalog, ship, null, null, f.Clock.Now));
        }
        Assert.Contains(module, controls.Requested(f.PilotId, ship));
        f.Db.Store.SetPosition(ship, null);
        Assert.Empty(controls.Requested(f.PilotId, ship));
    }

    [Fact]
    public void PendingHeatBecomesPreheatWhenTheLastOrdinaryCycleStops()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        f.Train(OverloadRules.ThermodynamicsSkill, 3);
        long module = f.Fit(ShieldBooster);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new SharedSpace(), f.Db.Objects),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        using CatalogDbContext catalog = new(f.CatalogPath);
        long effect = OverloadCatalog.Definition(catalog, ShieldBooster)!.EffectId;
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        Assert.IsType<PilotCallResult.Success>(activation.Call(context, "dogmaIM", "Activate", [module, "shieldBoosting", null, 0L]));
        var controls = scope.ServiceProvider.GetRequiredService<OverloadHandler>();
        Assert.IsType<PilotCallResult.Success>(controls.Call(context, "dogmaIM", "Overload", [module, effect]));
        Assert.True(controls.IsMonitoring);
        context.Notifications.Clear();
        f.Clock.Milliseconds += 10000;
        scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().Advance(context);
        Assert.False(controls.IsMonitoring);
        Assert.Contains(
            context.Notifications.OfType<ShipEffectChanged>(),
            notice => notice.Effect.Effect == (DogmaEffect)effect && notice.Active
        );
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        var prime = scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>().PrimeModule(context.Pilot, item);
        Assert.Contains((DogmaEffect)effect, prime.ActiveEffects.Keys);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<IPilotShipHeat>().Read(f.PilotId, f.ShipId, f.Clock.Now).Cycles);
        controls.Dispose();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<IPilotOverloading>().Requested(f.PilotId, f.ShipId));
    }
}
