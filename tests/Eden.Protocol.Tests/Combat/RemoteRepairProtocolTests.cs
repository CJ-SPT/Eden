using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.Protocol.Targeting;
using Eden.World.Repair;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class RemoteRepairTests
{
    [Theory]
    [InlineData(3586, "shipModuleRemoteShieldBooster", "effects.ShieldTransfer")]
    [InlineData(11355, "shipModuleRemoteArmorRepairer", "effects.RemoteArmourRepair")]
    public void ProductionDispatchRepeatsPaidStopAndObserverSnapshotsPreserveTheHudCycle(int type, string effect, string graphic)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        Damage(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var sourceScope = f.Db.Services.CreateScope();
        using var targetScope = f.Db.Services.CreateScope();
        var shared = f.Db.Services.GetRequiredService<SharedSpace>();
        using var source = Client(1000001);
        using var target = Client(1000002);
        var targeting = sourceScope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var handler = sourceScope.ServiceProvider.GetRequiredService<RemoteRepairHandler>();
        var receiver = targetScope.ServiceProvider.GetRequiredService<RemoteRepairHandler>();
        var activation = sourceScope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        receiver.Enter(target);
        targeting.CallWire(source, "dogmaIM", "AddTarget", [Target(f)]);
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        Assert.True(targeting.IsLocked(Target(f)));
        source.Notifications.Clear();
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Activate", [module, effect, Target(f), 2L]));
        var prime = handler.ActiveEffect(source.Pilot, module);
        Assert.NotNull(prime);
        target.Flight.RefreshPeers();
        Assert.Contains(
            target.Flight.Updates,
            u =>
                u.Command is DestinyCommand.TimedSpecialEffect fx
                && fx.ModuleId == module
                && fx.TargetId == Target(f)
                && fx.Graphic == graphic
                && !fx.Offensive
                && fx.Start
        );
        target.Flight.Updates.Clear();
        target.Flight.Snapshot();
        Assert.Contains(target.Flight.Updates, u => u.Command is DestinyCommand.TimedSpecialEffect fx && fx.ModuleId == module && fx.Start);
        f.Clock.Milliseconds += (long)prime.DurationMilliseconds!.Value;
        receiver.Advance(target);
        handler.Advance(source);
        receiver.Advance(target);
        Assert.Single(source.Notifications, n => n.Name == "OnGodmaShipEffect");
        Assert.Contains(target.Notifications, n => n.Name == "OnModuleAttributeChanges");
        Assert.IsType<PilotCallResult.Success>(activation.Call(source, "dogmaIM", "Deactivate", [module, effect]));
        Assert.NotNull(handler.ActiveEffect(source.Pilot, module));
        f.Clock.Milliseconds += (long)prime.DurationMilliseconds!.Value;
        handler.Advance(source);
        Assert.Null(handler.ActiveEffect(source.Pilot, module));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<WorldRemoteRepair>());

        PilotCallContext Client(long account)
        {
            var pilot = f.Db.Store.FindPilot(account)!;
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
    }

    [Fact]
    public async Task ResidentHealthAndCapacitorSurviveCheckpointAndSourceCancellationNeverReplaysArmor()
    {
        using Fixture f = Prepare();
        long shield = f.Fit(3586);
        long armor = f.Fit(11355);
        Space(f);
        Damage(f);
        double remaining;
        double paid;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            _ = Health(f);
            var result = Start(f, shield, DogmaEffect.RemoteShieldBoost);
            remaining = Health(f).Remaining.Shield;
            Assert.Equal(result.Cycle.Module.Amount, remaining, 6);
            var pending = Start(f, armor, DogmaEffect.RemoteArmorRepair);
            paid = pending.Capacitor;
            Store(f).Stop(f.PilotId, armor);
            f.Clock.Seconds += 30;
            Store(f).Complete(f.PilotId, armor, f.Clock.Now);
            Assert.Equal(100, Health(f).Defense.Armor - Health(f).Remaining.Armor);
            long sourceShip = f.ShipId;
            Assert.True(Store(f).IsOnline(f.PilotId, sourceShip, shield));
            using (
                var metrics = Eden.Infrastructure.Diagnostics.SessionPerformance.Begin(
                    "warm remote module membership",
                    _ => { },
                    force: true
                )
            )
            {
                Assert.True(Store(f).IsOnline(f.PilotId, sourceShip, shield));
                Assert.Equal(0, metrics!.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            await live.FlushAsync();
        }
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Equal(paid, reopened.Set<Eden.Data.World.Fittings.ShipEnergy>().Find(f.ShipId)!.Charge, 6);
        Assert.Equal(remaining, reopened.Set<Eden.Data.World.Combat.ShipHealthState>().Find(Target(f))!.ShieldCharge, 6);
        Assert.Equal(100, reopened.Set<Eden.Data.World.Combat.ShipHealthState>().Find(Target(f))!.ArmorDamage);
    }

    [Fact]
    public void PreviousSchemaUpgradeIsIdempotentAndPreservesOwnedItems()
    {
        using Fixture f = Prepare();
        long module = f.Fit(11355);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRaw(
                world.Database,
                "DROP TABLE WorldRemoteRepair; PRAGMA user_version=97;"
            );
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Equal(f.PilotId, reopened.Set<Eden.Data.World.Inventory.OwnedItem>().Find(module)!.OwnerId);
        Assert.Empty(reopened.Set<WorldRemoteRepair>());
    }
}
