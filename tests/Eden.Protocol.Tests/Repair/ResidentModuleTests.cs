using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Repair;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RepairFixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Repair;

public sealed partial class ShieldBoostTests
{
    [Fact]
    public void ResidentBoostAndRejectedRepeatUseNoSqlAndSaveTheSamePaidState()
    {
        using Fixture fixture = new();
        using ResidentFitCache fits = new(new(fixture.Db.Path));
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var pilot = fixture.Pilot;
        long module = fixture.Module;
        var item = fixture.Inventory.List(pilot.PilotId, pilot.ShipId).Single(value => value.Id == module);
        _ = fixture.Boosting.ModuleAttributes(pilot.PilotId, item);
        live.Economy.Warm();
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
        _ = live.Economy.Read(world => PilotFittingStore.ShipAttributes(world, catalog, pilot.PilotId), new(pilots: [pilot.PilotId]));
        List<string> commands = [];
        Eden.Gameplay.Repair.ShieldBoost paid;
        using (var metrics = SessionPerformance.Begin("resident shield boost", _ => { }, force: true, command: commands.Add)!)
        {
            paid = fixture.Boosting.Boost(pilot.PilotId, module, fixture.Clock.Now);
            Assert.True(paid.Restored > 0);
            Assert.Throws<InvalidDataException>(() => fixture.Boosting.Boost(pilot.PilotId, module, fixture.Clock.Now));
            Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext saved = new(checkpoint.Database);
        var health = saved.Set<ShipHealthState>().Find(pilot.ShipId)!;
        Assert.Equal(paid.Health.Remaining.Shield, health.ShieldCharge);
        Assert.Equal(12, health.ArmorDamage);
        Assert.Equal(9, health.StructureDamage);
        Assert.Equal(paid.Capacitor, saved.Set<ShipEnergy>().Find(pilot.ShipId)!.Charge);
        Assert.Equal(fixture.Clock.Now + TimeSpan.FromSeconds(2).Ticks, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }
}

public sealed partial class ArmorRepairTests
{
    [Fact]
    public void ResidentRepairPaysOnceCompletesAtTheExactDeadlineAndNeverReplaysWithoutSql()
    {
        using RepairFixture fixture = new();
        using ResidentFitCache fits = new(new(fixture.Db.Path));
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        long module = Prepare(fixture);
        long pilot = fixture.PilotId;
        long ship = fixture.ShipId;
        var item = fixture.Inventory.List(pilot, ship).Single(value => value.Id == module);
        _ = fixture.Fitting.ModuleAttributes(fixture.PilotId, item);
        double capacitor = fixture.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(fixture.PilotId, fixture.Clock.Now);
        using CatalogDbContext catalog = new(fixture.CatalogPath);
        _ = live.Economy.Read(world => PilotFittingStore.ShipAttributes(world, catalog, pilot), new(pilots: [pilot]));
        var store = Store(fixture);
        List<string> commands = [];
        using (var metrics = SessionPerformance.Begin("resident paid armor repair", _ => { }, force: true, command: commands.Add)!)
        {
            var paid = store.Start(fixture.PilotId, module, fixture.Clock.Now);
            Assert.Equal(capacitor - 40, paid.Capacitor);
            Assert.Throws<InvalidDataException>(() => store.Start(fixture.PilotId, module, fixture.Clock.Now));
            fixture.Clock.Milliseconds += 5099;
            Assert.Single(store.Read(pilot, ship, fixture.Clock.Now));
            var pending = store.Complete(pilot, ship, fixture.Clock.Now);
            Assert.Equal(100, pending.Defense.Armor - pending.Remaining.Armor);
            fixture.Clock.Milliseconds++;
            var completed = store.Complete(pilot, ship, fixture.Clock.Now);
            Assert.Equal(31, completed.Defense.Armor - completed.Remaining.Armor, 8);
            Assert.Empty(store.Read(pilot, ship, fixture.Clock.Now));
            fixture.Clock.Milliseconds += 60000;
            var repeated = store.Complete(pilot, ship, fixture.Clock.Now);
            Assert.Equal(31, repeated.Defense.Armor - repeated.Remaining.Armor, 8);
            Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext saved = new(checkpoint.Database);
        Assert.Empty(saved.Set<PendingLocalRepair>());
        Assert.Equal(31, saved.Set<ShipHealthState>().Find(fixture.ShipId)!.ArmorDamage, 8);
    }
}
