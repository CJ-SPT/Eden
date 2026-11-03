using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Persistence;

public sealed class SmallHullOperationTests
{
    [Fact]
    public void CormorantUsesSevenTurretsAndOneLauncherWithinItsRealFittingBudget()
    {
        using Fixture f = Prepare(16238);
        for (int index = 0; index < 7; index++)
        {
            f.Fit(561);
        }
        Assert.Throws<InvalidDataException>(() => f.Fit(561));
        f.Fit(10629);
        Assert.Throws<InvalidDataException>(() => f.Fit(10629));
        Assert.Equal(8, f.Inventory.List(f.PilotId, f.ShipId).Count(i => InventoryDefinitions.IsHighSlot(i.FlagId)));
        var fitting = f.Fitting.ShipAttributes(f.PilotId);
        Assert.True(fitting[DogmaAttributeId.CpuLoad] <= fitting[DogmaAttributeId.CpuOutput]);
        Assert.True(fitting[DogmaAttributeId.PowerLoad] <= fitting[DogmaAttributeId.PowerOutput]);
    }

    [Theory]
    [InlineData(17932)]
    [InlineData(33397)]
    [InlineData(78333)]
    public void ProjectileReloadRolePaysItsBonusedDeadlineAndRetainsItAfterRestart(int type)
    {
        using Fixture f = Prepare(type);
        long module = f.Fit(484);
        var item = Assert.Single(f.Inventory.List(f.PilotId, f.ShipId), i => i.Id == module);
        Assert.Equal(2500, f.Fitting.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.ReloadTime]);
        long charge = f.Grant(179, 200);
        var ammo = f.Db.Services.GetRequiredService<IPilotAmmunition>();
        var loaded = ammo.Load(f.PilotId, f.ShipId, [module], [charge], f.StationId, f.Clock.Now);
        Assert.Equal(2500, loaded.ReloadMilliseconds);
        long ready = f.Clock.Now + 2500 * TimeSpan.TicksPerMillisecond;
        Assert.Equal(ready, Assert.Single(ammo.Read(f.PilotId, f.ShipId)).ReadyAt);
        using WorldDbContext restarted = new(f.Db.Path);
        Assert.Equal(ready, restarted.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Fact]
    public void SuccubusAfterburnerUsesItsCaldariSkillBonusInThePaidFlightCycle()
    {
        using Fixture f = Prepare(17924);
        long module = f.Fit((int)PropulsionModuleType.OneMnAfterburnerI);
        var item = Assert.Single(f.Inventory.List(f.PilotId, f.ShipId), i => i.Id == module);
        var propulsion = f.Db.Services.GetRequiredService<IPilotPropulsion>();
        double enhanced = propulsion.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.SpeedFactor];
        f.Train(3330, 0);
        double baseline = propulsion.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.SpeedFactor];
        Assert.Equal(baseline * 2, enhanced, 8);
        f.Train(3330, 5);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        var activation = propulsion.Activate(f.PilotId, module, f.Clock.Now);
        Assert.Equal(enhanced, activation.Module.SpeedFactor, 8);
        var flight = f.Db.Store.FindPilot(1000001)!.Flight;
        var boosted = activation.Module.Apply(flight);
        double expected = flight.MaxSpeed * (1 + enhanced / 100 * activation.Module.Thrust / boosted.Mass);
        Assert.Equal(expected, boosted.MaxSpeed, 3);
        Assert.True(activation.Capacitor < f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.CapacitorCapacity]);
    }

    [Fact]
    public void DaredevilWebRoleSlowsTheOtherPilotsActualHullByNinetyPercent()
    {
        using Fixture f = Prepare(17928);
        long module = f.Fit(527);
        var wing = f.Db.Store.FindPilot(1000002)!;
        var pilot = f.Db.Store.FindPilot(1000001)!;
        SpacePosition origin = new(pilot.X + pilot.Flight.StationRadius + 50000, pilot.Y, pilot.Z);
        f.Db.Store.SetPosition(f.ShipId, origin);
        f.Db.Store.SetPosition(wing.ShipId, origin + new SpacePosition(1000, 0, 0));
        var tackle = f.Db.Services.GetRequiredService<IPilotTackle>();
        double speed = tackle.ShipAttributes(wing.PilotId)[DogmaAttributeId.MaxVelocity];
        var activated = tackle.Activate(
            f.PilotId,
            module,
            DogmaEffect.StasisWebifier,
            new(wing.ShipId, TackleTargetKind.Ship),
            new HashSet<long> { wing.ShipId },
            f.Clock.Now
        );
        Assert.Equal(-90, activated.Module.WebPercent, 8);
        Assert.Equal(speed * 0.1, tackle.ShipAttributes(wing.PilotId)[DogmaAttributeId.MaxVelocity], 8);
        Assert.Equal(speed * 0.1, f.Db.Store.FindPilot(1000002)!.Flight.MaxSpeed, 5);
    }

    [Theory]
    [InlineData(32880, 5000)]
    [InlineData(89648, 6250)]
    public void BothVentureHullsUseTheirOwnHoldAndOneOreYieldOwner(int type, double capacity)
    {
        using Fixture f = Prepare(type);
        f.Train((int)MiningSkill.Astrogeology, 5);
        long module = f.Fit((int)MiningLaserType.MinerII);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemCategory VALUES (25,'Asteroid');
                INSERT OR IGNORE INTO ItemGroup VALUES (462,'Veldspar',25);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (1230,'Veldspar',462);
                INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','1230','{{"published":1,"volume":0.1}}');
                """
            );
        }
        long ore = f.Grant(1230, 100000);
        Assert.Throws<CargoCapacityException>(() =>
            f.Inventory.Move(f.PilotId, [ore], f.StationId, f.ShipId, (int)InventoryFlag.GeneralMiningHold, null)
        );
        long incompatible = f.Grant(215);
        Assert.Throws<InvalidDataException>(() =>
            f.Inventory.Move(f.PilotId, [incompatible], f.StationId, f.ShipId, (int)InventoryFlag.GeneralMiningHold, null)
        );
        f.Inventory.Move(f.PilotId, [ore], f.StationId, f.ShipId, (int)InventoryFlag.GeneralMiningHold, 100);
        using (WorldDbContext world = new(f.Db.Path))
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            Assert.Equal(
                capacity,
                PilotInventoryStore.HoldCapacity(world, catalog, f.PilotId, f.ShipId, (int)InventoryFlag.GeneralMiningHold)
            );
            world.Add(
                new WorldAsteroid
                {
                    Id = AsteroidDefinitions.FirstAsteroidId,
                    BeltId = 40009087,
                    SystemId = 30000142,
                    TypeId = 1230,
                    Quantity = 10000,
                    X = 1500,
                    Radius = 100,
                }
            );
            world.SaveChanges();
        }
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero, 30000142);
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        var begun = mining.Begin(f.PilotId, module, AsteroidDefinitions.FirstAsteroidId, f.Clock.Now);
        // 15 m³ catalog yield, Mining V, Astrogeology V, 2x role and 25% hull skill.
        Assert.Equal(15 * 1.25 * 1.25 * 2 * 1.25, begun.Laser.Volume, 8);
        var collected = mining.Collect(f.PilotId, module, AsteroidDefinitions.FirstAsteroidId, new(1, true, new(.5, .99, .5, .99, .5)));
        Assert.Equal(586, collected.Extracted);
        Assert.Equal((int)InventoryFlag.GeneralMiningHold, collected.Cargo!.Item.FlagId);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(686, saved.Set<OwnedItem>().Where(i => i.LocationId == f.ShipId && i.TypeId == 1230).Sum(i => i.Quantity));
    }

    [Fact]
    public void ConsortiumShieldBoostRoleRestoresItsBonusedAmountInOnePaidCycle()
    {
        using Fixture f = Prepare(89648);
        long module = f.Fit((int)ShieldBoosterType.SmallI);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new ShipHealthState
                {
                    ShipId = f.ShipId,
                    ShieldCharge = 0,
                    UpdatedAt = f.Clock.Now,
                }
            );
            world.SaveChanges();
        }
        var boost = f.Db.Services.GetRequiredService<IPilotShieldBoosting>().Boost(f.PilotId, module, f.Clock.Now);
        Assert.Equal(39, boost.Restored, 8);
        Assert.Equal(39, boost.Booster.ShieldAmount, 8);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(39, saved.Set<ShipHealthState>().Find(f.ShipId)!.ShieldCharge, 8);
    }

    private static Fixture Prepare(int type)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadSmallHulls(f.CatalogPath);
        SmallHullTests.Board(f, type);
        return f;
    }
}
