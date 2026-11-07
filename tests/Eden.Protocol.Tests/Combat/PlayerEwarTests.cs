using System.Text.Json.Nodes;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class PlayerEwarTests
{
    private sealed class Clients : IDisposable
    {
        internal PilotCallContext Pilot { get; }
        internal PilotCallContext Wing { get; }
        internal TargetingHandler Targeting { get; }
        internal TargetingHandler WingTargeting { get; }
        internal PlayerEwarHandler Handler { get; }
        internal PlayerEwarHandler WingHandler { get; }

        internal Clients(Fixture f)
        {
            SharedSpace space = new();
            SharedTargetLocks locks = new();
            Pilot = Client(1000001);
            Wing = Client(1000002);
            Targeting = new(f.Clock, f.Fitting, locks, electronicWarfare: Store(f));
            WingTargeting = new(f.Clock, f.Fitting, locks, electronicWarfare: Store(f));
            var wire = f.Db.Services.GetRequiredService<SupportModuleWire>();
            Handler = new(f.Clock, Store(f), Targeting, wire);
            WingHandler = new(f.Clock, Store(f), WingTargeting, wire);
            _ = Targeting.CallWire(Pilot, "dogmaIM", "AddTarget", [Wing.Pilot.ShipId]);
            f.Clock.Seconds += 30;
            Targeting.Advance(Pilot);
            Assert.True(Targeting.IsLocked(Wing.Pilot.ShipId));
            Pilot.Notifications.Clear();
            Wing.Notifications.Clear();

            PilotCallContext Client(long account)
            {
                var pilot = f.Db.Store.FindPilot(account)!;
                PilotCallContext context = new(
                    account,
                    pilot.Account,
                    [pilot],
                    p => new(f.Db.Store, p, f.Clock, space, f.Db.Objects, electronicWarfare: Store(f)),
                    account,
                    SignedSettings.Empty
                );
                context.Select(pilot);
                context.Flight.Snapshot();
                context.Flight.Updates.Clear();
                return context;
            }
        }

        public void Dispose()
        {
            Handler.Dispose();
            WingHandler.Dispose();
            Targeting.Dispose();
            WingTargeting.Dispose();
            Pilot.Dispose();
            Wing.Dispose();
        }
    }

    private sealed class Roll(double value = 0) : INpcEwarRoll
    {
        public double Next() => value;
    }

    private static Fixture Prepare()
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadPlayerEwar(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142','{{\"securityStatus\":0}}')"
        );
        foreach (
            var attribute in new[]
            {
                DogmaAttributeId.MediumSlots,
                DogmaAttributeId.CpuOutput,
                DogmaAttributeId.PowerOutput,
                DogmaAttributeId.CapacitorCapacity,
            }
        )
        {
            double value = attribute == DogmaAttributeId.MediumSlots ? 8 : 1000000;
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)attribute},{value})");
        }
        return f;
    }

    private static PlayerEwarStore Store(Fixture f, double roll = 0) => new(new(f.Db.Path), new Roll(roll));

    private static long Wing(Fixture f) => f.Db.Store.FindPilot(1000002)!.ShipId;

    private static void Space(Fixture f, double separation = 1000)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        SpacePosition origin = new(pilot.X + pilot.Flight.StationRadius + 50000, pilot.Y, pilot.Z);
        f.Db.Store.SetPosition(f.ShipId, origin);
        f.Db.Store.SetPosition(Wing(f), origin + new SpacePosition(separation, 0, 0));
    }

    private static ElectronicWarfareActivation Activate(Fixture f, long module, DogmaEffect effect, double roll = 0) =>
        Store(f, roll).Activate(f.PilotId, module, effect, new(Wing(f), TackleTargetKind.Ship), new HashSet<long> { Wing(f) }, f.Clock.Now);

    [Fact]
    public void AllUnrestrictedCatalogModulesAndScriptsHaveCompleteFittingProfiles()
    {
        using Fixture f = Prepare();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var modules = catalog.Set<ItemType>().AsEnumerable().Where(t => PlayerEwarRules.Groups.Contains(t.GroupId)).ToArray();
        Assert.Equal(71, modules.Length);
        Assert.Equal(43, modules.Count(t => t.GroupId == PlayerEwarRules.EcmGroup));
        Assert.Equal(6, modules.Count(t => t.GroupId == PlayerEwarRules.SensorDampenerGroup));
        Assert.Equal(13, modules.Count(t => t.GroupId == PlayerEwarRules.WeaponDisruptorGroup));
        Assert.Equal(9, modules.Count(t => t.GroupId == PlayerEwarRules.TargetPainterGroup));
        foreach (var type in modules)
        {
            Assert.True(PlayerEwarCatalog.Supported(catalog, type.Id, type.GroupId), $"Unsupported {type.Id}: {type.Name}");
            Assert.NotNull(FittingCatalog.Definition(catalog, type.Id));
        }
        Assert.All(catalog.Set<ItemType>().Where(t => t.GroupId == 80), t => Assert.Null(FittingCatalog.Definition(catalog, t.Id)));
        var scripts = catalog.Set<ItemType>().AsEnumerable().Where(t => t.GroupId is 909 or 911 or 1569).ToArray();
        Assert.Equal(6, scripts.Length);
        Assert.All(scripts, t => Assert.True(ScriptCatalog.Supported(catalog, t.Id, t.GroupId)));
    }

    [Theory]
    [InlineData(1948, DogmaEffect.PlayerEcm)]
    [InlineData(2563, DogmaEffect.PlayerEcm)]
    [InlineData(20207, DogmaEffect.PlayerEcm)]
    [InlineData(85004, DogmaEffect.PlayerEcm)]
    [InlineData(1968, DogmaEffect.SensorDampener)]
    [InlineData(1969, DogmaEffect.SensorDampener)]
    [InlineData(22945, DogmaEffect.SensorDampener)]
    [InlineData(2108, DogmaEffect.TrackingDisruptor)]
    [InlineData(2109, DogmaEffect.TrackingDisruptor)]
    [InlineData(37543, DogmaEffect.GuidanceDisruptor)]
    [InlineData(92451, DogmaEffect.GuidanceDisruptor)]
    [InlineData(12709, DogmaEffect.TargetPainter)]
    [InlineData(31944, DogmaEffect.TargetPainter)]
    [InlineData(84742, DogmaEffect.TargetPainter)]
    public void RepresentativeTiersPayCatalogCyclesAndApplyTheirOwnPower(int type, DogmaEffect effect)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        var activation = Activate(f, module, effect);
        Assert.Equal(effect, activation.Module.Effect);
        var contact = Assert.Single(activation.Contacts);
        Assert.NotNull(contact.Applied);
        Assert.Equal(Wing(f), contact.Target.Id);
        Assert.Equal(f.Clock.Now + (long)(activation.Module.DurationMilliseconds * TimeSpan.TicksPerMillisecond), contact.ExpiresAt);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(contact.ExpiresAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Equal(activation.Capacitor, world.Set<ShipEnergy>().Find(f.ShipId)!.Charge, 6);
        Assert.Throws<InvalidDataException>(() => Activate(f, module, effect));
    }

    [Fact]
    public void DampenersAndPaintersModifyActualTargetAttributesAndRestoreOnStop()
    {
        using Fixture f = Prepare();
        long dampener = f.Fit(1968);
        long painter = f.Fit(12709);
        var wing = f.Db.Store.FindPilot(1000002)!;
        var baseline = f.Fitting.ShipAttributes(wing.PilotId);
        Space(f);
        Activate(f, dampener, DogmaEffect.SensorDampener);
        Activate(f, painter, DogmaEffect.TargetPainter);
        var changed = f.Fitting.ShipAttributes(wing.PilotId);
        Assert.Equal(baseline[DogmaAttributeId.MaxTargetRange] * .863, changed[DogmaAttributeId.MaxTargetRange], 6);
        Assert.Equal(baseline[DogmaAttributeId.ScanResolution] * .863, changed[DogmaAttributeId.ScanResolution], 6);
        Assert.Equal(baseline[DogmaAttributeId.SignatureRadius] * 1.25, changed[DogmaAttributeId.SignatureRadius], 6);
        Store(f).Stop(f.PilotId, f.ShipId, dampener);
        Store(f).Stop(f.PilotId, f.ShipId, painter);
        var restored = f.Fitting.ShipAttributes(wing.PilotId);
        Assert.Equal(baseline[DogmaAttributeId.MaxTargetRange], restored[DogmaAttributeId.MaxTargetRange]);
        Assert.Equal(baseline[DogmaAttributeId.SignatureRadius], restored[DogmaAttributeId.SignatureRadius]);
    }

    [Fact]
    public void FailedEcmStillPaysAndCreatesAggressionWithoutJamming()
    {
        using Fixture f = Prepare();
        long module = f.Fit(1948);
        Space(f);
        var activation = Activate(f, module, DogmaEffect.PlayerEcm, .99999);
        Assert.Null(Assert.Single(activation.Contacts).Applied);
        Assert.True(PlayerEwarRules.CanTarget(activation.Contacts, 123));
        using WorldDbContext world = new(f.Db.Path);
        Assert.True(world.Set<ShipEnergy>().Any(e => e.ShipId == f.ShipId));
        Assert.True(world.Set<SavedPilotHostility>().Any(e => e.SourceShipId == f.ShipId && e.TargetShipId == Wing(f)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, .5)]
    [InlineData(2, .0625)]
    public void NonEcmFalloffReducesStrengthContinuously(double falloffs, double expected)
    {
        var module = PlayerEwarRules.Define(
            1,
            1968,
            DogmaEffect.SensorDampener,
            new Dictionary<DogmaAttributeId, double>
            {
                [DogmaAttributeId.Duration] = 10000,
                [DogmaAttributeId.MaxRange] = 25000,
                [DogmaAttributeId.FalloffEffectiveness] = 50000,
                [DogmaAttributeId.TargetRangeBonus] = -20,
                [DogmaAttributeId.ScanResolutionBonus] = -20,
            }
        );
        var applied = PlayerEwarRules.Attempt(
            module,
            module.Optimal + module.Falloff * falloffs,
            new Dictionary<DogmaAttributeId, double>(),
            .99
        )!;
        Assert.Equal(1 - .2 * expected, applied.TargetRangeMultiplier, 9);
    }

    private static IPilotAmmunition Ammo(Fixture f) => f.Db.Services.GetRequiredService<IPilotAmmunition>();

    private static void Load(Fixture f, long module, int script)
    {
        long stack = f.Grant(script, 3);
        var transfer = Ammo(f).Load(f.PilotId, f.ShipId, [module], [stack], f.StationId, f.Clock.Now);
        Assert.Equal(.01, transfer.ReloadMilliseconds, 8);
        f.Clock.Milliseconds++;
    }

    [Theory]
    [InlineData(1968, 29013, DogmaAttributeId.ScanResolutionBonus, DogmaAttributeId.TargetRangeBonus)]
    [InlineData(1968, 29015, DogmaAttributeId.TargetRangeBonus, DogmaAttributeId.ScanResolutionBonus)]
    [InlineData(2108, 29005, DogmaAttributeId.OptimalRangeBonus, DogmaAttributeId.TrackingSpeedBonus)]
    [InlineData(2108, 29007, DogmaAttributeId.TrackingSpeedBonus, DogmaAttributeId.OptimalRangeBonus)]
    [InlineData(37543, 40334, DogmaAttributeId.MissileVelocityBonus, DogmaAttributeId.ExplosionRadiusBonus)]
    [InlineData(37543, 40335, DogmaAttributeId.ExplosionRadiusBonus, DogmaAttributeId.MissileVelocityBonus)]
    public void AllSixScriptsDoubleSelectedBonusesSuppressAlternativesAndConserveUnits(
        int type,
        int script,
        DogmaAttributeId doubled,
        DogmaAttributeId suppressed
    )
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        var baseline = f.Fitting.ModuleAttributes(f.PilotId, item);
        Load(f, module, script);
        var scripted = f.Fitting.ModuleAttributes(f.PilotId, item);
        Assert.Equal(baseline[doubled] * 2, scripted[doubled], 7);
        Assert.Equal(0, scripted[suppressed], 7);
        Space(f);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var effect = FittingCatalog.DefaultEffect(catalog, type)!.Value;
        Activate(f, module, effect);
        f.Clock.Seconds += 30;
        Assert.Throws<InvalidDataException>(() =>
            Ammo(f).Unload(f.PilotId, f.ShipId, [module], f.ShipId, (int)InventoryFlag.Cargo, null, f.Clock.Now)
        );
        Store(f).Stop(f.PilotId, f.ShipId, module);
        Ammo(f).Unload(f.PilotId, f.ShipId, [module], f.ShipId, (int)InventoryFlag.Cargo, null, f.Clock.Now);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(3, world.Set<OwnedItem>().Where(i => i.OwnerId == f.PilotId && i.TypeId == script).Sum(i => i.Quantity));
    }

    [Fact]
    public void TrackingAndGuidanceDisruptionChangeOnlyMatchingWeaponsAndLoadedMissiles()
    {
        using Fixture f = Prepare();
        PassiveCatalogFixture.LoadMissileTiers(f.CatalogPath, MissileDefinitions.Skills.Concat(new[] { 499, 212 }).ToHashSet());
        long gun = f.Fit(561);
        long launcher = f.Fit(499);
        long rounds = f.Grant(212, 10);
        Ammo(f).Load(f.PilotId, f.ShipId, [launcher], [rounds], f.StationId, f.Clock.Now);
        long tracking = f.Fit(2108);
        long guidance = f.Fit(37543);
        using (WorldDbContext world = new(f.Db.Path))
        {
            foreach (long id in new[] { tracking, guidance })
            {
                var item = world.Set<OwnedItem>().Find(id)!;
                item.OwnerId = f.Db.Store.FindPilot(1000002)!.PilotId;
                item.LocationId = Wing(f);
            }
            foreach (var skill in world.Set<Eden.Data.World.Skills.PilotSkill>().Where(s => s.PilotId == f.PilotId).ToArray())
            {
                var copy =
                    world.Set<Eden.Data.World.Skills.PilotSkill>().Find(f.Db.Store.FindPilot(1000002)!.PilotId, skill.TypeId)
                    ?? world
                        .Add(
                            new Eden.Data.World.Skills.PilotSkill
                            {
                                PilotId = f.Db.Store.FindPilot(1000002)!.PilotId,
                                TypeId = skill.TypeId,
                            }
                        )
                        .Entity;
                copy.Points = skill.Points;
            }
            world.SaveChanges();
        }
        f.Clock.Seconds += 30;
        var beforeGun = f.Fitting.ModuleAttributes(f.PilotId, f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == gun));
        var beforeCharge = Ammo(f).ChargeAttributes(f.PilotId, f.ShipId, launcher);
        Space(f);
        long attacker = f.Db.Store.FindPilot(1000002)!.PilotId;
        var trackingResult = Store(f)
            .Activate(
                attacker,
                tracking,
                DogmaEffect.TrackingDisruptor,
                new(f.ShipId, TackleTargetKind.Ship),
                new HashSet<long> { f.ShipId },
                f.Clock.Now
            );
        var afterGun = f.Fitting.ModuleAttributes(f.PilotId, f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == gun));
        Assert.Equal(
            beforeGun[DogmaAttributeId.TrackingSpeed] * (1 + trackingResult.Module.Power.TrackingPercent / 100),
            afterGun[DogmaAttributeId.TrackingSpeed],
            6
        );
        Assert.Equal(
            beforeCharge[DogmaAttributeId.MaxVelocity],
            Ammo(f).ChargeAttributes(f.PilotId, f.ShipId, launcher)[DogmaAttributeId.MaxVelocity]
        );
        var result = Store(f)
            .Activate(
                attacker,
                guidance,
                DogmaEffect.GuidanceDisruptor,
                new(f.ShipId, TackleTargetKind.Ship),
                new HashSet<long> { f.ShipId },
                f.Clock.Now
            );
        var afterCharge = Ammo(f).ChargeAttributes(f.PilotId, f.ShipId, launcher);
        Assert.Equal(
            beforeCharge[DogmaAttributeId.MaxVelocity] * (1 + result.Module.Power.MissileSpeedPercent / 100),
            afterCharge[DogmaAttributeId.MaxVelocity],
            6
        );
        Assert.Equal(
            beforeCharge[DogmaAttributeId.ExplosionRadius] * (1 + result.Module.Power.ExplosionRadiusPercent / 100),
            afterCharge[DogmaAttributeId.ExplosionRadius],
            6
        );
        Assert.Equal(
            afterGun[DogmaAttributeId.TrackingSpeed],
            f.Fitting.ModuleAttributes(f.PilotId, f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == gun))[
                DogmaAttributeId.TrackingSpeed
            ]
        );
    }

    [Fact]
    public void SourceSkillsApplyOnceAndDampenersStackStrongestFirstWithResistance()
    {
        using Fixture f = Prepare();
        long one = f.Fit(1968);
        long two = f.Fit(1969);
        f.Train((int)ElectronicWarfareSkill.SignalSuppression, 4);
        f.Train((int)ElectronicWarfareSkill.LongDistanceJamming, 3);
        f.Train((int)ElectronicWarfareSkill.FrequencyModulation, 2);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO TypeAttribute VALUES (601,2112,0.5)");
        }
        Space(f);
        long victim = f.Db.Store.FindPilot(1000002)!.PilotId;
        double baseline = f.Fitting.ShipAttributes(victim)[DogmaAttributeId.MaxTargetRange];
        var first = Activate(f, one, DogmaEffect.SensorDampener);
        var second = Activate(f, two, DogmaEffect.SensorDampener);
        Assert.Equal(30 * .75, first.Module.CapacitorNeed, 6);
        Assert.Equal(25000 * 1.3, first.Module.Optimal, 6);
        Assert.Equal(50000 * 1.2, first.Module.Falloff, 6);
        Assert.Equal(1 - .137 * 1.2, first.Module.Power.TargetRangeMultiplier, 8);
        const double SecondWeight = .8691199808003975;
        double strongest = Math.Min(first.Module.Power.TargetRangeMultiplier, second.Module.Power.TargetRangeMultiplier) - 1;
        double weakest = Math.Max(first.Module.Power.TargetRangeMultiplier, second.Module.Power.TargetRangeMultiplier) - 1;
        Assert.Equal(
            baseline * (1 + strongest * .5) * (1 + weakest * .5 * SecondWeight),
            f.Fitting.ShipAttributes(victim)[DogmaAttributeId.MaxTargetRange],
            6
        );
    }

    [Fact]
    public void RepeatedCyclesPreserveVictimEffectsWhenVictimAdvancesFirstAndPaidStopRestoresAttributes()
    {
        using Fixture f = Prepare();
        long module = f.Fit(1968);
        Space(f);
        using Clients clients = new(f);
        long victim = clients.Wing.Pilot.PilotId;
        double baseline = f.Fitting.ShipAttributes(victim)[DogmaAttributeId.MaxTargetRange];
        Assert.IsType<PilotCallResult.Success>(
            clients.Handler.Call(clients.Pilot, "dogmaIM", "Activate", [module, "remoteSensorDampFalloff", Wing(f), 4L])
        );
        double changed = f.Fitting.ShipAttributes(victim)[DogmaAttributeId.MaxTargetRange];
        for (int index = 0; index < 3; index++)
        {
            clients.WingHandler.Advance(clients.Wing);
            clients.Wing.NotificationsBeforeFlight.Clear();
            f.Clock.Seconds += 10;
            clients.WingHandler.Advance(clients.Wing);
            Assert.Equal(changed, f.Fitting.ShipAttributes(victim)[DogmaAttributeId.MaxTargetRange]);
            clients.Handler.Advance(clients.Pilot);
            clients.WingHandler.Advance(clients.Wing);
            Assert.Empty(clients.Wing.NotificationsBeforeFlight.OfType<PlayerEwarChanged>());
        }
        clients.Handler.Call(clients.Pilot, "dogmaIM", "Deactivate", [module, "remoteSensorDampFalloff"]);
        Assert.Equal(changed, f.Fitting.ShipAttributes(victim)[DogmaAttributeId.MaxTargetRange]);
        f.Clock.Seconds += 10;
        clients.Handler.Advance(clients.Pilot);
        clients.WingHandler.Advance(clients.Wing);
        Assert.Equal(baseline, f.Fitting.ShipAttributes(victim)[DogmaAttributeId.MaxTargetRange]);
        Assert.Contains(clients.Wing.NotificationsBeforeFlight.OfType<PlayerEwarChanged>(), e => !e.Active);
    }

    [Fact]
    public void RestartRemovesLiveEffectsButRetainsScriptsPaymentAndCooldownAndMigrationIsIdempotent()
    {
        using Fixture f = Prepare();
        long module = f.Fit(1968);
        Load(f, module, 29015);
        Space(f);
        var paid = Activate(f, module, DogmaEffect.SensorDampener);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<WorldPlayerEwar>());
        Assert.Empty(world.Set<ActiveFittedModule>());
        Assert.Equal(paid.Capacitor, world.Set<ShipEnergy>().Find(f.ShipId)!.Charge, 6);
        Assert.Equal(paid.Contacts[0].ExpiresAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Equal(1, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
        Assert.Equal(3, world.Set<OwnedItem>().Where(i => i.TypeId == 29015 && i.OwnerId == f.PilotId).Sum(i => i.Quantity));
        Assert.Throws<InvalidDataException>(() => Activate(f, module, DogmaEffect.SensorDampener));
    }

    [Theory]
    [InlineData("unlocked")]
    [InlineData("offline")]
    [InlineData("capacitor")]
    [InlineData("skill")]
    [InlineData("owner")]
    [InlineData("safety")]
    public void RejectedActivationsPreserveInventoryCapacitorCooldownAndAggression(string rejection)
    {
        using Fixture f = Prepare();
        long module = f.Fit(1968);
        Space(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            switch (rejection)
            {
                case "offline":
                    world.Set<OwnedItem>().Find(module)!.Online = false;
                    break;
                case "owner":
                    world.Set<OwnedItem>().Find(module)!.OwnerId = f.Db.Store.FindPilot(1000002)!.PilotId;
                    break;
                case "skill":
                    world.Set<Eden.Data.World.Skills.PilotSkill>().Find(f.PilotId, 3433)!.Points = 0;
                    break;
                case "capacitor":
                    world.Add(
                        new ShipEnergy
                        {
                            ShipId = f.ShipId,
                            Charge = 0,
                            UpdatedAt = f.Clock.Now,
                        }
                    );
                    break;
            }
            world.SaveChanges();
        }
        if (rejection == "safety")
        {
            using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142','{{\"securityStatus\":1}}')"
            );
        }
        using (WorldDbContext before = new(f.Db.Path))
        {
            double? capacitor = before.Set<ShipEnergy>().Find(f.ShipId)?.Charge;
            Assert.Throws<InvalidDataException>(() =>
                Store(f)
                    .Activate(
                        f.PilotId,
                        module,
                        DogmaEffect.SensorDampener,
                        new(Wing(f), TackleTargetKind.Ship),
                        rejection == "unlocked" ? [] : new HashSet<long> { Wing(f) },
                        f.Clock.Now
                    )
            );
            using WorldDbContext after = new(f.Db.Path);
            Assert.Equal(capacitor, after.Set<ShipEnergy>().Find(f.ShipId)?.Charge);
            Assert.Empty(after.Set<WorldPlayerEwar>());
            Assert.Null(after.Set<ModuleCooldown>().Find(module));
            Assert.Empty(after.Set<SavedPilotHostility>());
            Assert.Equal(1, after.Set<OwnedItem>().Find(module)!.Quantity);
        }
    }

    [Fact]
    public void UnknownEffectsRestrictionsAndMutationsRejectCompleteProfiles()
    {
        using Fixture f = Prepare();
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        var record = catalog.Set<SourceRecord>().Find("client/types", "1968")!;
        var node = JsonNode.Parse(record.Data)!;
        node["metaGroupID"] = 15;
        record.Data = node.ToJsonString();
        catalog.Update(record);
        catalog.SaveChanges();
        Assert.Null(FittingCatalog.Definition(catalog, 1968));
        node["metaGroupID"] = 1;
        record.Data = node.ToJsonString();
        catalog.Update(record);
        catalog.SaveChanges();
        catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO TypeAttribute VALUES (1968,1302,601)");
        Assert.Null(FittingCatalog.Definition(catalog, 1968));
        catalog.Database.ExecuteSqlRaw("DELETE FROM TypeAttribute WHERE ItemTypeId=1968 AND AttributeId=1302");
        var dogma = catalog.Set<SourceRecord>().Find("client/typedogma", "1968")!;
        node = JsonNode.Parse(dogma.Data)!;
        node["dogmaEffects"]!.AsArray().Add(new JsonObject { ["effectID"] = 42, ["isDefault"] = 0 });
        dogma.Data = node.ToJsonString();
        catalog.Update(dogma);
        catalog.SaveChanges();
        Assert.Null(FittingCatalog.Definition(catalog, 1968));
    }
}
