using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Defenses;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Defenses;

public sealed partial class AssaultDamageControlTests
{
    private const int Basic = 47254;
    private const int Wolf = 11371;

    private static Fixture Prepare(int hull = Wolf)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadAssaultDamageControls(f.CatalogPath);
        SmallHullTests.TrainCatalog(f);
        using WorldDbContext world = new(f.Db.Path);
        // Eligible hull cohorts remain deliberately unadmitted until milestone
        // 15. Seed an existing owned hull for operation tests, keeping every
        // actual hull attribute, skill requirement and fitting budget intact.
        world.Ships.Find(f.ShipId)!.TypeId = hull;
        world.SaveChanges();
        return f;
    }

    private static IPilotAssaultDamageControls Store(Fixture f) => f.Db.Services.GetRequiredService<IPilotAssaultDamageControls>();

    private static void Space(Fixture f, double capacitor = 100)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        f.Db.Store.SetPosition(f.ShipId, new(pilot.X + pilot.Flight.StationRadius + 50000, pilot.Y, pilot.Z));
        using WorldDbContext world = new(f.Db.Path);
        var energy = world.Set<ShipEnergy>().Find(f.ShipId) ?? world.Add(new ShipEnergy { ShipId = f.ShipId }).Entity;
        energy.Charge = capacitor;
        energy.UpdatedAt = f.Clock.Now;
        world.SaveChanges();
    }

    [Theory]
    [InlineData(Basic, 32, 8775)]
    [InlineData(47255, 32, 8775)]
    [InlineData(47256, 26, 8775)]
    [InlineData(47257, 35, 10125)]
    [InlineData(47258, 30, 10800)]
    [InlineData(84961, 35, 12500)]
    public void EveryStaticProfilePaysOnceReplacesPassiveResonancesAndExpiresAtItsExactDeadline(int type, double cost, double duration)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using CatalogDbContext catalog = new(f.CatalogPath);
        var raw = catalog.FittingAttributes(type);
        var hull = catalog.FittingAttributes(Wolf);
        var passive = f.Fitting.ShipAttributes(f.PilotId);
        Assert.True(passive[DogmaAttributeId.CpuLoad] <= passive[DogmaAttributeId.CpuOutput]);
        Assert.True(passive[DogmaAttributeId.PowerLoad] <= passive[DogmaAttributeId.PowerOutput]);
        foreach (var binding in AssaultDamageControlRules.Resistances)
        {
            Assert.Equal(
                hull.GetValueOrDefault(binding.ShipAttribute, 1) * raw[binding.ModuleAttribute],
                passive[binding.ShipAttribute],
                8
            );
        }
        var activation = Store(f).Activate(f.PilotId, module, f.Clock.Now);
        Assert.Equal(duration, activation.Module.DurationMilliseconds);
        Assert.Equal(100 - cost, activation.Capacitor, 8);
        Assert.Equal(activation.Schedule.ExpiresAt + TimeSpan.FromSeconds(150).Ticks, activation.Schedule.ReadyAt);
        foreach (var binding in AssaultDamageControlRules.Resistances)
        {
            Assert.Equal(hull.GetValueOrDefault(binding.ShipAttribute, 1) * .25, activation.ShipAttributes[binding.ShipAttribute], 8);
        }
        Assert.Throws<InvalidDataException>(() => Store(f).Activate(f.PilotId, module, f.Clock.Now));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(activation.Schedule.ExpiresAt - f.Clock.Now - 1));
        Assert.True(Store(f).Active(f.PilotId, f.ShipId, module, f.Clock.Now));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        var expired = f.Fitting.ShipAttributes(f.PilotId);
        Assert.All(AssaultDamageControlRules.Resistances, b => Assert.Equal(passive[b.ShipAttribute], expired[b.ShipAttribute], 8));
        Assert.False(Store(f).Active(f.PilotId, f.ShipId, module, f.Clock.Now));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Null(world.Set<ActiveFittedModule>().Find(module));
        Assert.Equal(activation.Schedule.ReadyAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Throws<InvalidDataException>(() => Store(f).Activate(f.PilotId, module, f.Clock.Now));
    }

    [Theory]
    [InlineData("capacitor")]
    [InlineData("offline")]
    [InlineData("owner")]
    [InlineData("docked")]
    [InlineData("destroyed")]
    [InlineData("hull")]
    [InlineData("slot")]
    [InlineData("skills")]
    public void RejectionPreservesResourcesHealthAndDeadlines(string reason)
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        Space(f, reason == "capacitor" ? 0 : 100);
        if (reason == "docked")
        {
            f.Db.Store.SetPosition(f.ShipId, null);
        }
        if (reason == "skills")
        {
            f.Train(3394, 0);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            var item = world.Set<OwnedItem>().Find(module)!;
            if (reason == "offline")
            {
                item.Online = false;
            }
            if (reason == "owner")
            {
                item.OwnerId = f.Db.Store.FindPilot(1000002)!.PilotId;
            }
            if (reason == "slot")
            {
                item.FlagId = (int)InventoryFlag.Cargo;
            }
            if (reason == "hull")
            {
                world.Ships.Find(f.ShipId)!.TypeId = 601;
            }
            if (reason == "destroyed")
            {
                world.Add(new ShipHealthState { ShipId = f.ShipId, StructureDamage = 1000000 });
            }
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => Store(f).Activate(f.PilotId, module, f.Clock.Now));
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Empty(verify.Set<ActiveFittedModule>());
        Assert.Null(verify.Set<ModuleCooldown>().Find(module));
        Assert.Equal(reason == "capacitor" ? 0 : 100, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
    }

    [Theory]
    [InlineData(601, false)]
    [InlineData(Wolf, true)]
    [InlineData(11999, true)]
    [InlineData(97793, true)]
    public void ExactGroupAndHeraldRestrictionsAreEnforcedOnAdmittedHulls(int hull, bool allowed)
    {
        using Fixture f = Prepare(hull);
        using CatalogDbContext catalog = new(f.CatalogPath);
        Assert.True(HullCatalog.CanAssemble(catalog, catalog.FittingType(hull)!, (int)InventoryCategory.Ship));
        if (allowed)
        {
            long module = f.Fit(Basic);
            long ordinary = f.Grant(2048);
            Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.PilotId, [ordinary], f.StationId, (int)InventoryFlag.AutoFit));
            Space(f);
            Assert.NotNull(Store(f).Activate(f.PilotId, module, f.Clock.Now));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => f.Fit(Basic));
        }
    }

    [Fact]
    public async Task RestartClearsTheBurstButPreservesItsPaymentCooldownAndOnlineBonus()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        Space(f);
        var before = f.Fitting.ShipAttributes(f.PilotId);
        AssaultDamageControlActivation activation;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            activation = Store(f).Activate(f.PilotId, module, f.Clock.Now);
            await live.FlushAsync();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<ActiveFittedModule>());
        Assert.Equal(activation.Capacitor, world.Set<ShipEnergy>().Find(f.ShipId)!.Charge, 8);
        Assert.Equal(activation.Schedule.ReadyAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        var reopened = f.Fitting.ShipAttributes(f.PilotId);
        Assert.All(AssaultDamageControlRules.Resistances, b => Assert.Equal(before[b.ShipAttribute], reopened[b.ShipAttribute], 8));
        Assert.Throws<InvalidDataException>(() => Store(f).Activate(f.PilotId, module, f.Clock.Now));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(activation.Schedule.ReadyAt - f.Clock.Now));
        Assert.NotNull(Store(f).Activate(f.PilotId, module, f.Clock.Now));
    }

    [Fact]
    public void LateCleanupOfAnOldBurstCannotCancelANewerPaidActivation()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var first = Store(f).Activate(f.PilotId, module, f.Clock.Now);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(first.Schedule.ReadyAt - f.Clock.Now));
        var second = Store(f).Activate(f.PilotId, module, f.Clock.Now);
        var afterOldStop = Store(f).Stop(f.PilotId, f.ShipId, module, first.Schedule.ExpiresAt);
        Assert.True(Store(f).Active(f.PilotId, f.ShipId, module, f.Clock.Now));
        Assert.All(
            AssaultDamageControlRules.Resistances,
            b => Assert.Equal(second.ShipAttributes[b.ShipAttribute], afterOldStop[b.ShipAttribute], 8)
        );
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(second.Schedule.ExpiresAt, world.Set<ActiveFittedModule>().Find(module)!.ExpiresAt);
        Assert.Equal(second.Schedule.ReadyAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Equal(second.Capacitor, world.Set<ShipEnergy>().Find(f.ShipId)!.Charge, 8);
    }
}
