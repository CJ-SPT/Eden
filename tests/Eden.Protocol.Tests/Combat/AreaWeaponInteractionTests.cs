using Eden.Data.World.Combat;
using Eden.Data.World.Drones;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Thermal;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class AreaWeaponTests
{
    [Theory]
    [InlineData(11377, HullCapability.Bombs)]
    [InlineData(12032, HullCapability.Bombs)]
    [InlineData(12034, HullCapability.Bombs)]
    [InlineData(12038, HullCapability.Bombs)]
    [InlineData(45530, HullCapability.Bombs)]
    [InlineData(37480, HullCapability.Interdiction)]
    [InlineData(37481, HullCapability.Interdiction)]
    [InlineData(37482, HullCapability.Interdiction)]
    [InlineData(37483, HullCapability.Interdiction)]
    [InlineData(52254, HullCapability.Interdiction)]
    [InlineData(89649, HullCapability.Interdiction)]
    public void NativeBomberAndCommandDestroyerBonusesHaveOperationalModuleConsumers(int type, HullCapability capability)
    {
        using Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture fixture = new();
        PassiveCatalogFixture.LoadTechTwoHulls(fixture.CatalogPath);
        PassiveCatalogFixture.LoadAreaWeapons(fixture.CatalogPath);
        long ship = Eden.Protocol.Tests.Persistence.SmallHullTests.Board(fixture, type);
        long pilot = fixture.PilotId;
        int skill = capability == HullCapability.Bombs ? AreaWeaponRules.BombDeploymentSkill : AreaWeaponRules.DefenderSkill;
        long module = fixture.Fit(capability == HullCapability.Bombs ? BombLauncher : 44102, online: false);
        Load(fixture, module, capability == HullCapability.Bombs ? Concussion : 32782);
        IReadOnlyList<PassiveModifier> bonuses;
        long charge;
        using (CatalogDbContext catalog = new(fixture.CatalogPath))
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            Assert.Contains(HullCatalog.Capabilities(catalog, type), bonus => bonus.Capability == capability && bonus.Operational);
            bonuses = HullCatalog.Modifiers(catalog, type).Where(bonus => bonus.SelectionId == skill).ToArray();
            charge = world.Set<OwnedItem>().Single(item => item.LocationId == module).Id;
        }
        Assert.NotEmpty(bonuses);
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        FittingCalculation Read() =>
            live.Economy.Read(
                world =>
                {
                    using CatalogDbContext catalog = new(fixture.CatalogPath);
                    return FittingCalculation.Read(world, catalog, world.Pilots.Find(pilot)!);
                },
                new(pilots: [pilot], ships: [ship])
            );
        var enhanced = Read();
        using (CatalogDbContext catalog = new(fixture.CatalogPath, writable: true))
        {
            string empty = "{\"dogmaEffects\":[]}";
            string key = PassiveCatalog.Key(type);
            catalog.Database.ExecuteSql($"UPDATE SourceRecord SET Data={empty} WHERE Dataset='client/typedogma' AND Key={key}");
        }
        var baseline = Read();
        foreach (var bonus in bonuses)
        {
            long target = bonus.Selector == ModifierSelector.OwnerRequiredSkill ? charge : module;
            Assert.Equal(DogmaOperation.PostPercent, bonus.Operation);
            double expected = baseline.Graph.Read(target, bonus.Attribute) * (1 + enhanced.Graph.Read(ship, bonus.SourceAttribute) / 100);
            Assert.Equal(expected, enhanced.Graph.Read(target, bonus.Attribute), 8);
        }
    }

    [Fact]
    public void SmartbombsDamageBombDefensesAndPreventADestroyedBombFromDetonating()
    {
        using var fixture = Prepare();
        PassiveCatalogFixture.LoadSmartbombs(fixture.CatalogPath);
        long first = fixture.Fit(1549);
        long second = fixture.Fit(1549);
        var target = Place(fixture, new(1000, 0, 0));
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        long bombId;
        using (var action = live.Economy.Begin(new(pilots: [target.PilotId], ships: [target.ShipId])))
        using (CatalogDbContext catalog = new(fixture.CatalogPath))
        {
            var bomb = AreaWeaponCatalog.Bomb(catalog, Concussion);
            var module = AreaWeaponRules.Define(
                1,
                BombLauncher,
                AreaWeaponRules.BombLauncherGroup,
                DogmaEffect.UseMissiles,
                catalog.FittingAttributes(BombLauncher)
            );
            SavedAreaWeapon row = new()
            {
                Id = action.Next("SavedAreaWeapon", AreaWeaponRules.FirstFlightId),
                PilotId = target.PilotId,
                SourceShipId = target.ShipId,
                ModuleId = 1,
                SystemId = SystemId,
            };
            row.Save(
                new(
                    module,
                    bomb,
                    new(1000, 0, 0),
                    new(2500, 0, 0),
                    SpacePosition.Zero,
                    fixture.Clock.Now,
                    fixture.Clock.Now + TimeSpan.FromSeconds(12).Ticks,
                    Health: bomb.Defense.Undamaged
                )
            );
            action.Add(row);
            action.Commit();
            bombId = row.Id;
        }
        var smartbombs = fixture.Db.Services.GetRequiredService<IPilotSmartbombs>();
        SmartbombCandidates candidates = new([], [], [], [], []);
        Assert.False(smartbombs.Pulse(fixture.PilotId, fixture.ShipId, first, candidates, fixture.Clock.Now).InterceptedMissiles);
        Assert.Equal(18, live.Economy.Inspect(world => world.Set<SavedAreaWeapon>().Find(bombId)!.Flight().Health!.Armor));
        Assert.True(smartbombs.Pulse(fixture.PilotId, fixture.ShipId, second, candidates, fixture.Clock.Now).InterceptedMissiles);
        Assert.Null(live.Economy.Inspect(world => world.Set<SavedAreaWeapon>().Find(bombId)));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(12));
        fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>().Advance(SystemId, fixture.Clock.Now);
        Assert.Null(live.Economy.Inspect(world => world.Set<ShipHealthState>().Find(target.ShipId)));
    }

    [Theory]
    [InlineData(0, -80)]
    [InlineData(1000, -80)]
    [InlineData(9000, -40)]
    [InlineData(17000, -5)]
    public void GrapplerStrengthUsesTheTypedFalloffCurve(double distance, double expected)
    {
        TackleModule profile = new(1, Grappler, DogmaEffect.StasisWebifier, 10000, 20, 1000, 0, -80, 0, 0, 8000);
        Assert.Equal(expected, TackleDefinitions.AtDistance(profile, distance).WebPercent, 8);
        Assert.Throws<InvalidDataException>(() => TackleDefinitions.AtDistance(profile, double.NaN));
        Assert.Throws<InvalidDataException>(() => TackleDefinitions.AtDistance(profile, -1));
    }

    [Fact]
    public void GrapplerRejectsAnInvalidSourceHullBeforeCapacitorOrCooldownChanges()
    {
        using var fixture = Prepare();
        long module = fixture.Fit(Grappler);
        var target = Place(fixture, new(1000, 0, 0));
        long source = fixture.ShipId;
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        using (var action = live.Economy.Begin(new(pilots: [fixture.PilotId], ships: [fixture.ShipId])))
        {
            action.Ships.Find(source)!.TypeId = Manticore;
            action.Commit();
        }
        var before = live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(source)?.Charge);
        Assert.Throws<InvalidDataException>(() =>
            fixture
                .Db.Services.GetRequiredService<IPilotTackle>()
                .Activate(
                    fixture.PilotId,
                    module,
                    DogmaEffect.StasisWebifier,
                    new(target.ShipId, TackleTargetKind.Ship),
                    new HashSet<long> { target.ShipId },
                    fixture.Clock.Now
                )
        );
        Assert.Equal(before, live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(source)?.Charge));
        Assert.Null(live.Economy.Inspect(world => world.Set<ModuleCooldown>().Find(module)));
    }

    [Theory]
    [InlineData("movement")]
    [InlineData("security")]
    [InlineData("hull")]
    public void RejectedBombLaunchRetainsItsAmmunitionAndResources(string rejection)
    {
        using var fixture = Prepare(Manticore);
        long module = fixture.Fit(BombLauncher);
        Load(fixture, module, Concussion);
        _ = Place(fixture, new(30000, 0, 0));
        long source = fixture.ShipId;
        if (rejection == "security")
        {
            using CatalogDbContext catalog = new(fixture.CatalogPath, writable: true);
            string system = "{\"securityStatus\":0.5}";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142',{system})");
        }
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        using (var action = live.Economy.Begin(new(pilots: [fixture.PilotId], ships: [fixture.ShipId])))
        {
            if (rejection == "hull")
            {
                action.Ships.Find(source)!.TypeId = Scorpion;
            }
            action.Set<ModuleCooldown>().Remove(action.Set<ModuleCooldown>().Find(module)!);
            action.Commit();
        }
        var before = live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(source)?.Charge);
        Assert.Throws<InvalidDataException>(() =>
            fixture
                .Db.Services.GetRequiredService<IPilotAreaWeapons>()
                .Activate(
                    fixture.PilotId,
                    fixture.ShipId,
                    module,
                    DogmaEffect.UseMissiles,
                    new(1, 0, 0),
                    rejection == "movement" ? SpacePosition.Zero : new(100, 0, 0),
                    null,
                    fixture.Clock.Now
                )
        );
        Assert.Equal(2, live.Economy.Inspect(world => world.Set<OwnedItem>().Single(item => item.LocationId == module).Quantity));
        Assert.Equal(before, live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(source)?.Charge));
        Assert.Null(live.Economy.Inspect(world => world.Set<ModuleCooldown>().Find(module)));
        Assert.False(live.Economy.HasAreaWeapons(SystemId));
    }

    [Fact]
    public void OneWebProjectorTracksPlayerAndDroneRecipientsAndInvalidatesWarmedDroneViews()
    {
        using var fixture = Prepare(projectors: true);
        PassiveCatalogFixture.LoadCombatDrones(fixture.CatalogPath);
        long module = fixture.Fit(WebProjector);
        fixture.Grant(16272, 250, cargo: true);
        long droneId = fixture.Grant(2488);
        var target = Place(fixture, new(50000, 0, 0));
        using (WorldDbContext world = new(fixture.Db.Path))
        using (CatalogDbContext catalog = new(fixture.CatalogPath))
        {
            var item = world.Set<OwnedItem>().Find(droneId)!;
            item.Singleton = true;
            item.LocationId = SystemId;
            item.FlagId = (int)InventoryFlag.AutoFit;
            world.Add(
                new WorldDrone
                {
                    ItemId = droneId,
                    PilotId = fixture.PilotId,
                    ControllerShipId = fixture.ShipId,
                    SystemId = SystemId,
                    X = 50000,
                    Shield = catalog.FittingAttributes(2488)[DogmaAttributeId.ShieldCapacity],
                    UpdatedAt = fixture.Clock.Now,
                }
            );
            world.SaveChanges();
        }
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var store = fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>();
        var drones = fixture.Db.Services.GetRequiredService<Eden.Gameplay.Drones.IPilotDrones>();
        float speed = Assert.Single(drones.List(SystemId, fixture.Clock.Now)).Profile.MaximumSpeed;
        _ = store.Activate(
            fixture.PilotId,
            fixture.ShipId,
            module,
            DogmaEffect.ProjectorWeb,
            new(1, 0, 0),
            SpacePosition.Zero,
            new(50000, 0, 0),
            fixture.Clock.Now
        );
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(10));
        store.Advance(SystemId, fixture.Clock.Now);
        Assert.Equal(speed * .45, Assert.Single(drones.List(SystemId, fixture.Clock.Now)).Profile.MaximumSpeed, 3);
        using (var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot()))
        {
            snapshot.Project();
            using WorldDbContext image = new(snapshot.Database);
            Assert.Equal(2, image.Set<WorldPlayerEwar>().Count(row => row.ModuleId == module));
            Assert.NotNull(image.Set<WorldPlayerEwar>().Find(module, target.ShipId));
            Assert.NotNull(image.Set<WorldPlayerEwar>().Find(module, droneId));
        }
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(40));
        _ = fixture.Db.Services.GetRequiredService<IPilotFitting>().ShipAttributes(target.PilotId);
        Assert.Equal(speed, Assert.Single(drones.List(SystemId, fixture.Clock.Now)).Profile.MaximumSpeed);
    }

    [Fact]
    public void SharedBurstBreakInvalidatesLocalLocksAndPendingScansBeforeTheVictimFrame()
    {
        using var fixture = Prepare();
        _ = Place(fixture, new(1000, 0, 0));
        fixture.Db.Store.SetPosition(fixture.ShipId, new(1e12, 0, 0), SystemId);
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        var origin = pilot.Position!;
        using (CatalogDbContext catalog = new(fixture.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO TypeAttribute VALUES (1230,552,100)");
        }
        SharedSpace space = new();
        SharedTargetLocks locks = new();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected =>
                new(
                    fixture.Db.Store,
                    selected,
                    fixture.Clock,
                    space,
                    [
                        new(70000001, 1230, 462, 25, 1, origin + new SpacePosition(1000, 0, 0), 100),
                        new(70000002, 1230, 462, 25, 1, origin + new SpacePosition(2000, 0, 0), 100),
                    ]
                ),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        TargetingHandler targeting = new(fixture.Clock, fixture.Fitting, locks);
        _ = targeting.Call(context, "dogmaIM", "AddTarget", [70000001L]);
        fixture.Clock.Seconds += 30;
        targeting.Advance(context);
        Assert.True(targeting.IsLocked(70000001));
        _ = targeting.Call(context, "dogmaIM", "AddTarget", [70000002L]);
        Assert.True(targeting.IsPending(70000002));
        locks.BreakOutgoing(pilot.ShipId);
        Assert.False(targeting.IsLocked(70000001));
        targeting.Advance(context);
        Assert.False(targeting.IsPending(70000002));
        Assert.Contains(context.Notifications, notification => notification is TargetChanged);
    }

    [Fact]
    public void BurstOverloadUsesEarnedStrengthAndPaysRackHeat()
    {
        using var fixture = Prepare();
        PassiveCatalogFixture.LoadOverload(fixture.CatalogPath);
        long module = fixture.Fit(BurstJammer);
        fixture.Train(OverloadRules.ThermodynamicsSkill, 1);
        _ = Place(fixture, new(1000, 0, 0));
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        using CatalogDbContext catalog = new(fixture.CatalogPath);
        var controls = fixture.Db.Services.GetRequiredService<IPilotOverloading>();
        _ = controls.Set(
            new(
                fixture.PilotId,
                fixture.ShipId,
                module,
                OverloadCatalog.Definition(catalog, BurstJammer)!.EffectId,
                OverloadScope.Module,
                true,
                fixture.Clock.Now
            )
        );
        var result = fixture
            .Db.Services.GetRequiredService<IPilotAreaWeapons>()
            .Activate(
                fixture.PilotId,
                fixture.ShipId,
                module,
                DogmaEffect.BurstJammer,
                new(1, 0, 0),
                SpacePosition.Zero,
                null,
                fixture.Clock.Now
            );
        Assert.Equal(7.2, result.Module.Power.Jamming!.Gravimetric, 8);
        Assert.Equal(180, result.Module.CapacitorNeed);
        Assert.Single(
            fixture.Db.Services.GetRequiredService<IPilotShipHeat>().Read(fixture.PilotId, fixture.ShipId, fixture.Clock.Now).Cycles
        );
    }
}
