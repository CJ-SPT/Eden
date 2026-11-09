using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerDefenseModuleTests
{
    private const long Enemy = 90000002;
    private const long EnemyShip = 1000000000002;
    private const int System = 30000142;

    // Supported module IDs from the committed build-3662168 fixture extracts.
    [Theory]
    [InlineData(399, DogmaEffect.ShieldBoosting, 0)]
    [InlineData(523, DogmaEffect.ArmorRepair, 0)]
    [InlineData(524, DogmaEffect.StructureRepair, 0)]
    [InlineData(2293, DogmaEffect.ActiveShieldResistance, 0)]
    [InlineData(11301, DogmaEffect.ActiveArmorResistance, 0)]
    [InlineData(439, DogmaEffect.Afterburner, 0)]
    [InlineData(434, DogmaEffect.Microwarpdrive, 0)]
    [InlineData(1973, DogmaEffect.SensorBooster, 0)]
    [InlineData(1977, DogmaEffect.TrackingComputer, 0)]
    [InlineData(35788, DogmaEffect.MissileGuidanceComputer, 0)]
    [InlineData(23533, DogmaEffect.OmnidirectionalTrackingLink, 0)]
    [InlineData(526, DogmaEffect.StasisWebifier, 0)]
    [InlineData(3242, DogmaEffect.WarpDisruptor, 0)]
    [InlineData(447, DogmaEffect.WarpScrambler, 0)]
    [InlineData(1955, DogmaEffect.PlayerEcm, 0)]
    [InlineData(1968, DogmaEffect.SensorDampener, 0)]
    [InlineData(2108, DogmaEffect.TrackingDisruptor, 0)]
    [InlineData(37543, DogmaEffect.GuidanceDisruptor, 0)]
    [InlineData(12709, DogmaEffect.TargetPainter, 0)]
    [InlineData(533, DogmaEffect.EnergyNeutralizer, 0)]
    [InlineData(530, DogmaEffect.EnergyNosferatu, 0)]
    [InlineData(3566, DogmaEffect.CapacitorBoosting, 3554)]
    [InlineData(32774, DogmaEffect.AncillaryShieldBoosting, 263)]
    [InlineData(33076, DogmaEffect.AncillaryArmorRepair, 28668)]
    [InlineData(499, DogmaEffect.UseMissiles, 212)]
    [InlineData(561, DogmaEffect.ProjectileFired, 215)]
    [InlineData(4409, DogmaEffect.SignatureSuppression, 0)]
    [InlineData(47254, DogmaEffect.AssaultDamageControl, 0)]
    public void DefenseUsesTheRealSupportedModuleAndCannotReplayItsPaidCycle(int type, DogmaEffect effect, int charge)
    {
        using Fixture f = Prepare(type, effect);
        long module = f.Fit(type);
        if (type != (int)CombatWeaponType.CivilianGatlingRailgun && effect != DogmaEffect.ProjectileFired)
        {
            f.Fit((int)CombatWeaponType.CivilianGatlingRailgun);
        }
        if (charge != 0)
        {
            long stack = f.Grant(charge, 100);
            f.Db.Services.GetRequiredService<IPilotAmmunition>().Load(f.PilotId, f.ShipId, [module], [stack], f.StationId, f.Clock.Now);
            f.Clock.Milliseconds += 60_000;
        }
        Space(f, effect);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var controller = f.Db.Services.GetRequiredService<CapsuleerDefense>();
        NavigationState navigation = new();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        var first = controller.Advance(pilot, navigation, null, f.Clock.Now, 1, []);
        Assert.NotNull(first.State);
        f.Clock.Milliseconds += 30_000;
        var second = controller.Advance(first.Pilot, navigation, first.State, f.Clock.Now, 31, []);
        var cycle =
            first.State!.Cycles.FirstOrDefault(cycle => cycle.ModuleId == module)
            ?? Assert.Single(second.State!.Cycles, cycle => cycle.ModuleId == module);
        Assert.Equal(effect, cycle.Effect);
        Assert.True(cycle.ReadyAt > cycle.StartedAt);
        var before = f.Db.Services.GetRequiredService<IPilotHealth>().Read(f.PilotId, f.Clock.Now);
        var duplicate = controller.Advance(second.Pilot, navigation, second.State, f.Clock.Now, 31, []);
        Assert.Equal(
            second.State!.Cycles.Where(value => value.ModuleId == module).Select(value => value.ReadyAt),
            duplicate.State!.Cycles.Where(value => value.ModuleId == module).Select(value => value.ReadyAt)
        );
        Assert.Equal(before.Remaining, f.Db.Services.GetRequiredService<IPilotHealth>().Read(f.PilotId, f.Clock.Now).Remaining);
    }

    internal static Fixture Prepare(int type, DogmaEffect effect)
    {
        Fixture f = new();
        int hull = 601;
        if (effect == DogmaEffect.SignatureSuppression)
        {
            PassiveCatalogFixture.LoadSignatureSuppression(f.CatalogPath);
            hull = 638;
        }
        if (effect == DogmaEffect.AssaultDamageControl)
        {
            PassiveCatalogFixture.LoadAssaultDamageControls(f.CatalogPath);
            hull = 11371;
        }
        if (hull != 601)
        {
            SmallHullTests.TrainCatalog(f);
            using WorldDbContext world = new(f.Db.Path);
            world.Ships.Find(f.ShipId)!.TypeId = hull;
            world.SaveChanges();
        }
        if (effect is DogmaEffect.StasisWebifier or DogmaEffect.WarpDisruptor or DogmaEffect.WarpScrambler)
        {
            PassiveCatalogFixture.LoadTackleTiers(f.CatalogPath);
        }
        if (
            effect
            is DogmaEffect.PlayerEcm
                or DogmaEffect.SensorDampener
                or DogmaEffect.TrackingDisruptor
                or DogmaEffect.GuidanceDisruptor
                or DogmaEffect.TargetPainter
        )
        {
            PassiveCatalogFixture.LoadPlayerEwar(f.CatalogPath);
        }
        if (effect is DogmaEffect.EnergyNeutralizer or DogmaEffect.EnergyNosferatu)
        {
            PassiveCatalogFixture.LoadCapacitorModules(f.CatalogPath);
        }
        if (effect is DogmaEffect.SensorBooster or DogmaEffect.TrackingComputer or DogmaEffect.MissileGuidanceComputer)
        {
            PassiveCatalogFixture.LoadSupportTiers(f.CatalogPath);
        }
        if (effect == DogmaEffect.OmnidirectionalTrackingLink)
        {
            PassiveCatalogFixture.LoadDroneEquipment(f.CatalogPath);
        }
        if (effect is DogmaEffect.AncillaryShieldBoosting or DogmaEffect.AncillaryArmorRepair)
        {
            PassiveCatalogFixture.LoadAncillaryRepairs(f.CatalogPath);
        }
        if (effect == DogmaEffect.StructureRepair)
        {
            PassiveCatalogFixture.LoadHullRepairTiers(f.CatalogPath);
        }
        if (effect == DogmaEffect.UseMissiles)
        {
            PassiveCatalogFixture.LoadMissileTiers(f.CatalogPath);
        }
        if (effect == DogmaEffect.ProjectileFired)
        {
            PassiveCatalogFixture.LoadWeaponTiers(f.CatalogPath);
        }
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        string security = "{\"securityStatus\":0}";
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142',{security})");
        foreach (
            var attribute in new[]
            {
                DogmaAttributeId.HighSlots,
                DogmaAttributeId.MediumSlots,
                DogmaAttributeId.LowSlots,
                DogmaAttributeId.CpuOutput,
                DogmaAttributeId.PowerOutput,
                DogmaAttributeId.CapacitorCapacity,
            }
        )
        {
            double value = attribute is DogmaAttributeId.HighSlots or DogmaAttributeId.MediumSlots or DogmaAttributeId.LowSlots
                ? 8
                : 1_000_000;
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({hull},{(int)attribute},{value})");
        }
        return f;
    }

    internal static void Space(Fixture f, DogmaEffect effect)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        var position = new SpacePosition(pilot.X + pilot.Flight.StationRadius + 50_000, pilot.Y, pilot.Z);
        f.Db.Store.SetPosition(f.ShipId, position, System);
        f.Db.Store.SetPosition(EnemyShip, position + new SpacePosition(500, 0, 0), System);
        var health = f.Db.Services.GetRequiredService<IPilotHealth>().Read(f.PilotId, f.Clock.Now);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var state = world.Set<ShipHealthState>().Find(f.ShipId) ?? world.Add(new ShipHealthState { ShipId = f.ShipId }).Entity;
            state.ShieldCharge = health.Defense.Shield * .25;
            state.ArmorDamage = health.Defense.Armor * (effect == DogmaEffect.AssaultDamageControl ? .65 : .3);
            state.StructureDamage = effect == DogmaEffect.StructureRepair ? health.Defense.Structure * .21 : 0;
            state.UpdatedAt = f.Clock.Now;
            var sourceEnergy = world.Set<ShipEnergy>().Find(f.ShipId) ?? world.Add(new ShipEnergy { ShipId = f.ShipId }).Entity;
            sourceEnergy.Charge = effect is DogmaEffect.CapacitorBoosting or DogmaEffect.EnergyNosferatu ? 250_000 : 900_000;
            sourceEnergy.UpdatedAt = f.Clock.Now;
            world.Add(
                new ShipEnergy
                {
                    ShipId = EnemyShip,
                    Charge = 1_000_000,
                    UpdatedAt = f.Clock.Now,
                }
            );
            world.Add(
                new SavedPilotHostility
                {
                    SourceShipId = EnemyShip,
                    TargetShipId = f.ShipId,
                    LastAttackAt = f.Clock.Now,
                    ExpiresAt = f.Clock.Now + TimeSpan.FromMinutes(15).Ticks,
                }
            );
            world.SaveChanges();
        }
        var space = f.Db.Services.GetRequiredService<SharedSpace>();
        foreach (long account in new long[] { 1000001, 1000002 })
        {
            var contact = f.Db.Store.FindPilot(account)!;
            space.Publish(new(PilotContact.From(contact), SpacePosition.Zero, null, 0, null));
        }
    }
}
