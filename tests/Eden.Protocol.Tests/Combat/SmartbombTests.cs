using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class SmartbombTests
{
    private const int SmallProton = 1547;
    private const int SmallProtonTechTwo = 1549;
    private const int SystemId = 30000142;
    private const int NpcType = 11901;
    private const int ScourgeLightMissile = 210;
    private const int Warrior = 2488;
    private const long Npc = 9_800_000_001;
    private const long Missile = 9_800_000_002;

    [Fact]
    public void AllStaticSmartbombsFitAndOverloadWithoutHardpoints()
    {
        using Fixture fixture = new();
        PassiveCatalogFixture.LoadSmartbombs(fixture.CatalogPath);
        using CatalogDbContext catalog = new(fixture.CatalogPath);
        var cohort = catalog.Set<ItemType>().Where(type => type.GroupId == SmartbombRules.Group).ToArray();
        Assert.Equal(137, cohort.Length);
        int admitted = 0;
        foreach (var type in cohort)
        {
            var fitting = FittingCatalog.Definition(catalog, type.Id);
            if (catalog.Require(CatalogData.Client.Types, (long)type.Id).MetaGroupID == 15)
            {
                Assert.Null(fitting);
                continue;
            }
            Assert.Equal(new(ModuleRack.High, false, false, 0, 0), fitting);
            Assert.NotNull(OverloadCatalog.Definition(catalog, type.Id));
            admitted++;
        }
        Assert.Equal(125, admitted);
    }

    [Fact]
    public void PulsePaysOnceHitsMultipleTargetsAndPersistsMissileInterception()
    {
        using var fixture = Prepare();
        long module = fixture.Fit(SmallProton);
        long stronger = fixture.Fit(SmallProtonTechTwo);
        var (origin, target) = Place(fixture);
        using (WorldDbContext world = new(fixture.Db.Path))
        using (CatalogDbContext catalog = new(fixture.CatalogPath))
        {
            world.Add(
                new WorldNpc
                {
                    Id = Npc,
                    TypeId = NpcType,
                    SystemId = SystemId,
                    X = origin.X + 1000,
                    Y = origin.Y,
                    Z = origin.Z,
                    ShieldCharge = catalog.FittingAttributes(NpcType)[DogmaAttributeId.ShieldCapacity],
                    UpdatedAt = fixture.Clock.Now,
                }
            );
            var missile = new WorldMissile
            {
                Id = Missile,
                PilotId = target.PilotId,
                SourceShipId = target.ShipId,
                SourceTypeId = target.ShipTypeId,
                ChargeTypeId = ScourgeLightMissile,
                TargetId = fixture.ShipId,
                SystemId = SystemId,
                UpdatedAt = fixture.Clock.Now,
                LaunchedAt = fixture.Clock.Now,
                ExpiresAt = fixture.Clock.Now + TimeSpan.TicksPerMinute,
                Snapshot = "",
            };
            missile.Save(new(new(new(1, 2, 1000, 1, 60000), origin, SpacePosition.Zero, origin), new(new(0, 0, 100, 0), 10, 1000, 1)));
            world.Add(missile);
            world.SaveChanges();
        }

        SmartbombCandidates candidates = new([new(target.ShipId, target.Position!)], [Npc, Npc], [], [Missile], []);
        long source = fixture.ShipId;
        using ResidentFitCache cache = new(new(fixture.Db.Path));
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var store = fixture.Db.Services.GetRequiredService<IPilotSmartbombs>();
        SmartbombPulse Fire() => store.Pulse(fixture.PilotId, source, module, candidates, fixture.Clock.Now);
        var first = Fire();
        Assert.Equal(2, first.Hits.Count);
        Assert.All(first.Hits, hit => Assert.InRange(hit.Applied, 1, 50));
        Assert.Equal(70, first.Capacitor);
        Assert.Equal(7500, first.Module.DurationMilliseconds);
        Assert.False(first.InterceptedMissiles);
        Assert.Throws<InvalidDataException>(() => Fire());
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(first.ReadyAt - fixture.Clock.Now));
        var next = Fire();
        Assert.False(next.InterceptedMissiles);
        Assert.All(next.Hits, hit => Assert.True(hit.Applied > 0));
        var intercepted = store.Pulse(fixture.PilotId, source, stronger, candidates with { Ships = [], Npcs = [] }, fixture.Clock.Now);
        Assert.True(intercepted.InterceptedMissiles);

        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        Assert.Equal(next.ReadyAt, reopened.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Equal(intercepted.Capacitor, reopened.Set<ShipEnergy>().Find(source)!.Charge);
        Assert.Equal(MissileFlightStatus.Expired, reopened.Set<WorldMissile>().Find(Missile)!.Status);
        Assert.NotNull(reopened.Set<ShipHealthState>().Find(target.ShipId));
        Assert.Null(reopened.Set<ShipHealthState>().Find(source));
        Assert.True(reopened.Set<NpcAggression>().Any(row => row.NpcId == Npc && row.TargetShipId == source));
        Assert.Equal(
            intercepted.ReadyAt + CrimewatchRules.WeaponsDuration.Ticks,
            reopened.Set<SavedCrimewatch>().Find(fixture.PilotId)!.WeaponsUntil
        );
    }

    [Fact]
    public void ProtectedDistanceAndSafetyRejectWithoutPaymentOrDamage()
    {
        using var fixture = Prepare(.9);
        long module = fixture.Fit(SmallProton);
        var (origin, target) = Place(fixture);
        var store = fixture.Db.Services.GetRequiredService<IPilotSmartbombs>();
        SmartbombCandidates candidates = new([new(target.ShipId, target.Position!)], [], [], [], []);
        Assert.Contains(
            "red safety",
            Assert
                .Throws<InvalidDataException>(() => store.Pulse(fixture.PilotId, fixture.ShipId, module, candidates, fixture.Clock.Now))
                .Message
        );
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Omega;
            world.SaveChanges();
        }
        fixture.Db.Services.GetRequiredService<IPilotSafety>().Set(fixture.PilotId, ShipSafetyLevel.Disabled);
        var source = fixture.Db.Store.FindPilot(1000001)!;
        StaticSpaceObject gate = new(
            1,
            16,
            SmartbombRules.StargateGroup,
            2,
            1,
            origin + new SpacePosition(4899 + source.Flight.ShipRadius, 0, 0),
            100
        );
        Assert.Contains(
            "stations and gates",
            Assert
                .Throws<InvalidDataException>(() =>
                    store.Pulse(fixture.PilotId, fixture.ShipId, module, candidates with { ProtectedObjects = [gate] }, fixture.Clock.Now)
                )
                .Message
        );
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            Assert.Equal(100, world.Set<ShipEnergy>().Find(source.ShipId)!.Charge);
            Assert.Null(world.Set<ModuleCooldown>().Find(module));
            Assert.Null(world.Set<ShipHealthState>().Find(target.ShipId));
        }
        var paid = store.Pulse(fixture.PilotId, fixture.ShipId, module, candidates, fixture.Clock.Now);
        Assert.Single(paid.Hits);
        using WorldDbContext after = new(fixture.Db.Path);
        Assert.NotNull(after.Set<SavedConcordResponse>().Find(source.ShipId));
    }

    [Fact]
    public void NativeTargetlessActivationRepeatsAndStopsAfterThePaidInterval()
    {
        using var fixture = Prepare();
        long module = fixture.Fit(SmallProton);
        _ = Place(fixture);
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        SharedSpace space = new();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(fixture.Db.Store, p, fixture.Clock, space, fixture.Db.Objects),
            1000001,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        context.Flight.Updates.Clear();
        using var scope = fixture.Db.Services.CreateScope();
        var route = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var handler = scope.ServiceProvider.GetRequiredService<SmartbombHandler>();
        Assert.IsType<PilotCallResult.Failure>(route.Call(context, "dogmaIM", "Activate", [module, "empWave", 42L, 3L]));
        Assert.Same(PilotCallResult.None, route.Call(context, "dogmaIM", "Activate", [module, "empWave", null, 3L]));
        var active = Assert.IsType<DogmaActiveEffect>(handler.ActiveEffect(pilot, module));
        Assert.Equal(DogmaEffect.Smartbomb, active.Effect);
        Assert.Null(active.TargetId);
        var fx = Assert.Single(context.Flight.Updates.Select(update => update.Command).OfType<DestinyCommand.TimedSpecialEffect>());
        Assert.Equal("effects.EMPWave", fx.Graphic);
        Assert.Null(fx.TargetId);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(7500));
        handler.Advance(context);
        Assert.Equal(2L, handler.ActiveEffect(pilot, module)!.Repeats);
        Assert.Same(PilotCallResult.None, route.Call(context, "dogmaIM", "Deactivate", [module, "empWave"]));
        Assert.NotNull(handler.ActiveEffect(pilot, module));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(7500));
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(pilot, module));
        Assert.Equal([true, false], context.Notifications.OfType<ShipEffectChanged>().Select(effect => effect.Active));
    }

    [Fact]
    public void OverloadedPulsePaysHeatAndCanDestroyTheOwnersDrone()
    {
        using var fixture = Prepare();
        PassiveCatalogFixture.LoadCombatDrones(fixture.CatalogPath);
        PassiveCatalogFixture.LoadOverload(fixture.CatalogPath);
        long module = fixture.Fit(SmallProton);
        fixture.Train(OverloadRules.ThermodynamicsSkill, 1);
        long drone = fixture.Grant(Warrior);
        long ship = fixture.ShipId;
        var (origin, _) = Place(fixture);
        using (WorldDbContext world = new(fixture.Db.Path))
        using (CatalogDbContext catalog = new(fixture.CatalogPath))
        {
            var item = world.Set<OwnedItem>().Find(drone)!;
            item.Singleton = true;
            item.LocationId = SystemId;
            item.FlagId = (int)InventoryFlag.AutoFit;
            var attributes = catalog.FittingAttributes(Warrior);
            world.Add(
                new WorldDrone
                {
                    ItemId = drone,
                    PilotId = fixture.PilotId,
                    ControllerShipId = ship,
                    SystemId = SystemId,
                    X = origin.X + 500,
                    Y = origin.Y,
                    Z = origin.Z,
                    ArmorDamage = attributes[DogmaAttributeId.ArmorHitPoints],
                    StructureDamage = attributes[DogmaAttributeId.StructureHitPoints] - 1,
                    UpdatedAt = fixture.Clock.Now,
                }
            );
            world.SaveChanges();
        }
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var controls = fixture.Db.Services.GetRequiredService<IPilotOverloading>();
        using (CatalogDbContext catalog = new(fixture.CatalogPath))
        {
            int effect = OverloadCatalog.Definition(catalog, SmallProton)!.EffectId;
            Assert.Single(controls.Set(new(fixture.PilotId, ship, module, effect, OverloadScope.Module, true, fixture.Clock.Now)));
        }
        var result = fixture
            .Db.Services.GetRequiredService<IPilotSmartbombs>()
            .Pulse(fixture.PilotId, ship, module, new([], [], [drone], [], []), fixture.Clock.Now);
        var hit = Assert.Single(result.Hits);
        Assert.Equal(drone, hit.Health.ShipId);
        Assert.True(hit.Health.Remaining.Destroyed);
        Assert.Equal(6375, result.Module.DurationMilliseconds, 8);
        Assert.Single(fixture.Db.Services.GetRequiredService<IPilotShipHeat>().Read(fixture.PilotId, ship, fixture.Clock.Now).Cycles);
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        Assert.Null(reopened.Set<WorldDrone>().Find(drone)!.SystemId);
        Assert.Equal((long)InventoryLocation.Graveyard, reopened.Set<OwnedItem>().Find(drone)!.LocationId);
    }

    private static Fixture Prepare(double security = 0)
    {
        Fixture fixture = new();
        PassiveCatalogFixture.LoadSmartbombs(
            fixture.CatalogPath,
            new HashSet<int> { SmallProton, SmallProtonTechTwo, ScourgeLightMissile, 3421 }
        );
        PassiveCatalogFixture.LoadNpcPopulation(fixture.CatalogPath);
        using CatalogDbContext catalog = new(fixture.CatalogPath, writable: true);
        string source = System.Text.Json.JsonSerializer.Serialize(new { securityStatus = security });
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142',{source})");
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)DogmaAttributeId.CpuOutput},100)");
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)DogmaAttributeId.HighSlots},4)");
        if (security >= CrimewatchRules.HighSecurity)
        {
            foreach (int type in new[] { ConcordRules.CaptainType, ConcordRules.CommanderType })
            {
                catalog.Database.ExecuteSql(
                    $"""
                    INSERT INTO ItemType(Id,Name,GroupId,Mass,Radius)
                    SELECT {type},Name,GroupId,Mass,Radius FROM ItemType WHERE Id={NpcType}
                    """
                );
                catalog.Database.ExecuteSql(
                    $"INSERT INTO TypeAttribute SELECT {type},AttributeId,Value FROM TypeAttribute WHERE ItemTypeId={NpcType}"
                );
            }
        }
        return fixture;
    }

    private static (SpacePosition Origin, DockedPilot Target) Place(Fixture fixture)
    {
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        var target = fixture.Db.Store.FindPilot(1000002)!;
        SpacePosition origin = new(pilot.X + pilot.Flight.StationRadius + 50000, pilot.Y, pilot.Z);
        fixture.Db.Store.SetPosition(pilot.ShipId, origin);
        fixture.Db.Store.SetPosition(target.ShipId, origin + new SpacePosition(1000, 0, 0));
        using WorldDbContext world = new(fixture.Db.Path);
        world.Add(
            new ShipEnergy
            {
                ShipId = pilot.ShipId,
                Charge = 100,
                UpdatedAt = fixture.Clock.Now,
            }
        );
        world.SaveChanges();
        return (origin, fixture.Db.Store.FindPilot(1000002)!);
    }
}
