using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Skills;
using Eden.Protocol.Tests.Combat;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CombatHullOperationTests
{
    [Theory]
    [InlineData(623, 567, 5)]
    [InlineData(641, 572, 7)]
    [InlineData(639, 494, 6)]
    [InlineData(642, 460, 8)]
    [InlineData(4302, 460, 8)]
    [InlineData(4306, 572, 8)]
    [InlineData(4308, 572, 8)]
    [InlineData(4310, 494, 8)]
    [InlineData(89807, 503, 8)]
    public void FullWeaponBatteryFitsWithinUnmodifiedCatalogResources(int hull, int weapon, int count)
    {
        using Fixture f = Prepare(hull);
        for (int index = 0; index < count; index++)
        {
            f.Fit(weapon);
        }
        Assert.Throws<InvalidDataException>(() => f.Fit(weapon));
        var attributes = f.Fitting.ShipAttributes(f.PilotId);
        Assert.True(attributes[DogmaAttributeId.CpuLoad] <= attributes[DogmaAttributeId.CpuOutput]);
        Assert.True(attributes[DogmaAttributeId.PowerLoad] <= attributes[DogmaAttributeId.PowerOutput]);
        Assert.Equal(count, f.Inventory.List(f.PilotId, f.ShipId).Count(i => InventoryDefinitions.IsHighSlot(i.FlagId)));
    }

    [Theory]
    [InlineData(623, 567, 223)]
    [InlineData(4308, 572, 231)]
    [InlineData(629, 490, 187)]
    [InlineData(639, 494, 195)]
    [InlineData(2006, 456, 254)]
    [InlineData(642, 460, 262)]
    public void MediumAndLargeFiringSpendsRealChargesAndTheEvaluatedCostAndCycle(int hull, int weapon, int charge)
    {
        using Fixture f = Prepare(hull);
        long module = f.Fit(weapon);
        long stack = f.Grant(charge, 20);
        var ammo = f.Db.Services.GetRequiredService<IPilotAmmunition>();
        ammo.Load(f.PilotId, f.ShipId, [module], [stack], f.StationId, f.Clock.Now);
        f.Clock.Milliseconds += 40000;
        NpcEncounterTests.Prepare(f.Db);
        var npc = f.Db.Services.GetRequiredService<INpcEncounters>().List(30000144, f.Clock.Now)[0];
        f.Db.Store.SetPosition(f.ShipId, npc.Position + new SpacePosition(1000, 0, 0), npc.SystemId);
        var item = Assert.Single(f.Inventory.List(f.PilotId, f.ShipId), i => i.Id == module);
        var combat = f.Db.Services.GetRequiredService<IPilotCombat>();
        var fitting = combat.ModuleAttributes(f.PilotId, item);
        var before = Assert.Single(ammo.Read(f.PilotId, f.ShipId));
        var shot = combat.Fire(f.PilotId, module, npc.Id, SpacePosition.Zero, f.Clock.Now, .5, crystalRoll: .9);
        Assert.Equal(fitting[DogmaAttributeId.WeaponCycleTime], shot.Weapon.CycleMilliseconds, 8);
        Assert.Equal(fitting.GetValueOrDefault(DogmaAttributeId.CapacitorNeed), shot.Weapon.CapacitorNeed, 8);
        Assert.Equal(fitting[DogmaAttributeId.MaxRange], shot.Weapon.Optimal, 8);
        var payload = f.Fitting.Attributes(charge);
        double damage = new[]
        {
            DogmaAttributeId.EmDamage,
            DogmaAttributeId.ExplosiveDamage,
            DogmaAttributeId.KineticDamage,
            DogmaAttributeId.ThermalDamage,
        }.Sum(a => payload.GetValueOrDefault(a));
        Assert.Equal(damage * fitting[DogmaAttributeId.DamageMultiplier], shot.Weapon.Damage.Total, 8);
        bool laser = item.GroupId == CombatDefinitions.EnergyWeaponGroup;
        Assert.Equal(before.Quantity - (laser ? 0 : 1), shot.Magazine!.Quantity);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(
            f.Clock.Now + (long)Math.Ceiling(shot.Weapon.CycleMilliseconds * TimeSpan.TicksPerMillisecond),
            saved.Set<ModuleCooldown>().Find(module)!.ReadyAt
        );
        Assert.Throws<InvalidDataException>(() => combat.Fire(f.PilotId, module, npc.Id, SpacePosition.Zero, f.Clock.Now, .5));
    }

    [Theory]
    [InlineData(623, 3334, 561)]
    [InlineData(623, 3334, 572)]
    [InlineData(623, 3334, 490)]
    [InlineData(641, 3336, 561)]
    [InlineData(641, 3336, 567)]
    [InlineData(641, 3336, 494)]
    public void MediumAndLargeWeaponBonusesExcludeOtherSizesAndFamilies(int hull, int skill, int weapon)
    {
        using Fixture f = Prepare(hull);
        long module = f.Fit(weapon, online: false);
        var item = Assert.Single(f.Inventory.List(f.PilotId, f.ShipId), i => i.Id == module);
        var enhanced = f.Fitting.ModuleAttributes(f.PilotId, item);
        f.Train(skill, 0);
        var baseline = f.Fitting.ModuleAttributes(f.PilotId, item);
        Assert.Equal(baseline[DogmaAttributeId.DamageMultiplier], enhanced[DogmaAttributeId.DamageMultiplier], 8);
        Assert.Equal(baseline[DogmaAttributeId.WeaponCycleTime], enhanced[DogmaAttributeId.WeaponCycleTime], 8);
    }

    private static Fixture Prepare(int hull)
    {
        Fixture fixture = new();
        PassiveCatalogFixture.LoadCombatHull(fixture.CatalogPath, hull);
        SmallHullTests.Board(fixture, hull);
        return fixture;
    }

    [Fact]
    public void CenotaphResistanceReducesAnIncomingPaidWebOnTheActualFlightHull()
    {
        using Fixture f = Prepare(85086);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            // This test checks hull/web physics; legality is covered by Crimewatch tests.
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142','{{\"securityStatus\":0}}')"
            );
        }
        var wing = f.Db.Store.FindPilot(1000002)!;
        long module;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var skill =
                world.Set<PilotSkill>().Find(wing.PilotId, TackleDefinitions.PropulsionJammingSkill)
                ?? world.Set<PilotSkill>().Add(new() { PilotId = wing.PilotId, TypeId = TackleDefinitions.PropulsionJammingSkill }).Entity;
            skill.Points = TrainedSkill.PointsForLevel(3, 5);
            module = InventoryPersistence.NextId(world);
            world.Add(
                new OwnedItem
                {
                    Id = module,
                    OwnerId = wing.PilotId,
                    TypeId = 527,
                    LocationId = wing.StationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 1,
                }
            );
            world.SaveChanges();
        }
        f.Fitting.Fit(wing.PilotId, [module], wing.StationId, (int)InventoryFlag.AutoFit);
        f.Fitting.SetOnline(wing.PilotId, wing.ShipId, module, true);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        SpacePosition origin = new(pilot.X + pilot.Flight.StationRadius + 50000, pilot.Y, pilot.Z);
        f.Db.Store.SetPosition(f.ShipId, origin);
        f.Db.Store.SetPosition(wing.ShipId, origin + new SpacePosition(1000, 0, 0));
        var tackle = f.Db.Services.GetRequiredService<IPilotTackle>();
        var before = tackle.ShipAttributes(f.PilotId);
        Assert.Equal(.75, before[DogmaAttributeId.StasisWebifierResistance], 8);
        var paid = tackle.Activate(
            wing.PilotId,
            module,
            DogmaEffect.StasisWebifier,
            new(f.ShipId, TackleTargetKind.Ship),
            new HashSet<long> { f.ShipId },
            f.Clock.Now
        );
        Assert.Equal(-60, paid.Module.WebPercent, 8);
        double expected = before[DogmaAttributeId.MaxVelocity] * .55;
        Assert.Equal(expected, tackle.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxVelocity], 8);
        Assert.Equal(expected, f.Db.Store.FindPilot(1000001)!.Flight.MaxSpeed, 5);
        Assert.True(paid.Capacitor < f.Fitting.ShipAttributes(wing.PilotId)[DogmaAttributeId.CapacitorCapacity]);
    }

    [Fact]
    public void BattleshipTankRolesReachBothTheFittedModuleAndTheActualHullDefense()
    {
        using Fixture f = Prepare(638);
        var before = f.Fitting.ShipAttributes(f.PilotId);
        var basis = f.Fitting.Attributes(638);
        long extender = f.Fit(3839);
        long plate = f.Fit(11279);
        long bulkhead = f.Fit(1333);
        var items = f.Inventory.List(f.PilotId, f.ShipId);
        Assert.Equal(
            3800,
            f.Fitting.ModuleAttributes(f.PilotId, items.Single(i => i.Id == extender))[DogmaAttributeId.ShieldCapacityBonus]
        );
        Assert.Equal(5250, f.Fitting.ModuleAttributes(f.PilotId, items.Single(i => i.Id == plate))[DogmaAttributeId.ArmorHitPointsBonus]);
        Assert.Equal(
            1.2,
            f.Fitting.ModuleAttributes(f.PilotId, items.Single(i => i.Id == bulkhead))[DogmaAttributeId.StructureHitPointsMultiplier],
            8
        );
        var enhanced = f.Fitting.ShipAttributes(f.PilotId);
        Assert.Equal(
            before[DogmaAttributeId.ShieldCapacity] * (1 + 3800 / basis[DogmaAttributeId.ShieldCapacity]),
            enhanced[DogmaAttributeId.ShieldCapacity],
            8
        );
        Assert.Equal(
            before[DogmaAttributeId.ArmorHitPoints] * (1 + 5250 / basis[DogmaAttributeId.ArmorHitPoints]),
            enhanced[DogmaAttributeId.ArmorHitPoints],
            8
        );
        Assert.Equal(before[DogmaAttributeId.StructureHitPoints] * 1.2, enhanced[DogmaAttributeId.StructureHitPoints], 8);
        using WorldDbContext saved = new(f.Db.Path);
        var health = PilotHealthStore.Read(saved, f.ShipId, ShipHealth.DefenseFrom(enhanced), f.Clock.Now);
        Assert.Equal(enhanced[DogmaAttributeId.ShieldCapacity], health.Remaining.Shield, 8);
        Assert.Equal(enhanced[DogmaAttributeId.ArmorHitPoints], health.Remaining.Armor, 8);
        Assert.Equal(enhanced[DogmaAttributeId.StructureHitPoints], health.Remaining.Structure, 8);
    }

    [Theory]
    [InlineData(621, 501, 209)]
    [InlineData(638, 13320, 203)]
    [InlineData(89807, 503, 267)]
    public void MissileLaunchCommitsTheBonusedPayloadFlightAndCycle(int hull, int launcher, int charge)
    {
        using Fixture f = Prepare(hull);
        long module = f.Fit(launcher);
        long stack = f.Grant(charge, 10);
        var ammo = f.Db.Services.GetRequiredService<IPilotAmmunition>();
        ammo.Load(f.PilotId, f.ShipId, [module], [stack], f.StationId, f.Clock.Now);
        f.Clock.Milliseconds += 40000;
        NpcEncounterTests.Prepare(f.Db);
        var npc = f.Db.Services.GetRequiredService<INpcEncounters>().List(30000144, f.Clock.Now)[0];
        f.Db.Store.SetPosition(f.ShipId, npc.Position + new SpacePosition(10000, 0, 0), npc.SystemId);
        var missiles = f.Db.Services.GetRequiredService<IPilotMissiles>();
        var launch = missiles.Launch(
            f.PilotId,
            module,
            npc.Id,
            new HashSet<long> { npc.Id },
            new(SpacePosition.Zero, 200, new(0, 0, -1), 42),
            f.Clock.Now
        );
        using CatalogDbContext catalog = new(f.CatalogPath);
        using WorldDbContext world = new(f.Db.Path);
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == f.PilotId);
        var graph = FittingCalculation.Read(world, catalog, pilot).Graph;
        long magazine = world.Set<OwnedItem>().Single(i => i.LocationId == module).Id;
        var payload = world.Set<WorldMissile>().Find(launch.Projectile.Id)!.Payload();
        Assert.Equal(graph.Read(module, DogmaAttributeId.WeaponCycleTime), launch.Launcher.CycleMilliseconds, 8);
        Assert.Equal(graph.Read(magazine, DogmaAttributeId.KineticDamage), payload.Warhead.Damage.Kinetic, 8);
        Assert.Equal(graph.Read(magazine, DogmaAttributeId.MaxVelocity), payload.Flight.Drive.MaxVelocity, .01);
        Assert.Equal(graph.Read(magazine, DogmaAttributeId.MissileFlightTime), payload.Flight.Drive.LifetimeMilliseconds, 8);
        Assert.Equal(9, launch.Magazine.Quantity);
    }
}
