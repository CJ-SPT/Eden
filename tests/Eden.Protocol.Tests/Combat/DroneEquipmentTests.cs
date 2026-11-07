using System.Reflection;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Content;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Repair;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Combat.CombatDroneTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class DroneEquipmentTests
{
    private const int SystemId = 30000144;
    private const int Warrior = 2488;
    private const int Hammerhead = 2185;
    private const int Berserker = 2476;
    private const int Garde = 23561;
    private const int OmniLink = 23533;
    private const int OptimalScript = 28999;
    private const int TrackingScript = 29001;
    private const string OmniEffect = "moduleBonusOmnidirectionalTrackingLink";
    private const double SecondStackingWeight = 0.8691199808003975;

    private static JsonDocument Inputs() =>
        JsonDocument.Parse(
            Assembly.GetExecutingAssembly().GetManifestResourceStream("Eden.Protocol.Tests.Fixtures.DroneEquipmentCatalog.json")!
        );

    public static IEnumerable<object[]> EquipmentCases()
    {
        using var inputs = Inputs();
        var sources = inputs
            .RootElement.GetProperty("records")
            .EnumerateArray()
            .Where(record => record.GetProperty("dataset").GetString() == "client/types")
            .ToDictionary(record => record.GetProperty("key").GetInt32(), record => record.GetProperty("data"));
        foreach (var type in inputs.RootElement.GetProperty("types").EnumerateArray())
        {
            int id = type.GetProperty("id").GetInt32();
            if (
                type.GetProperty("categoryId").GetInt32() == (int)InventoryCategory.Module
                && type.GetProperty("groupId").GetInt32() != (int)RigGroup.Drones
                && (!sources[id].TryGetProperty("metaGroupID", out var meta) || meta.GetInt32() != 15)
            )
            {
                yield return [id];
            }
        }
    }

    public static IEnumerable<object[]> CombatRigCases()
    {
        using var inputs = Inputs();
        foreach (var type in inputs.RootElement.GetProperty("types").EnumerateArray())
        {
            if (type.GetProperty("groupId").GetInt32() != (int)RigGroup.Drones)
            {
                continue;
            }
            if (
                type.GetProperty("attributes")
                    .EnumerateArray()
                    .Any(attribute =>
                        attribute.GetProperty("value").GetDouble() > 0
                        && (DogmaAttributeId)attribute.GetProperty("id").GetInt32()
                            is DogmaAttributeId.DroneControlRangeBonus
                                or DogmaAttributeId.DroneDurabilityBonus
                                or DogmaAttributeId.DroneOptimalRangeBonus
                                or DogmaAttributeId.DroneMaximumVelocityBonus
                                or DogmaAttributeId.SkillDamageBonus
                    )
            )
            {
                yield return [type.GetProperty("id").GetInt32()];
            }
        }
    }

    private static Fixture Prepare()
    {
        Fixture fixture = new();
        PassiveCatalogFixture.LoadDroneEquipment(fixture.Base.CatalogPath);
        Eden.Protocol.Tests.Persistence.SmallHullTests.TrainCatalog(fixture.Base);
        return fixture;
    }

    private static IPilotSupportModules Support(Fixture fixture) => fixture.Base.Db.Services.GetRequiredService<IPilotSupportModules>();

    private static IPilotAmmunition Ammo(Fixture fixture) => fixture.Base.Db.Services.GetRequiredService<IPilotAmmunition>();

    private static DroneProfile Profile(Fixture fixture, int type = Warrior) => fixture.Drones.Profile(fixture.Pilot, type)!;

    private static void Load(Fixture fixture, long module, int script)
    {
        long stack = fixture.Base.Grant(script, 3);
        Ammo(fixture).Load(fixture.Pilot, fixture.Ship, [module], [stack], fixture.Base.StationId, fixture.Now);
        fixture.Base.Clock.Milliseconds++;
    }

    [Theory]
    [MemberData(nameof(EquipmentCases))]
    public void StaticEquipmentFitsAndUsesItsOwnBonusesOnlyWhileOnlineOrActive(int type)
    {
        using Fixture fixture = Prepare();
        var before = Profile(fixture);
        int range = fixture.Drones.Control(fixture.Pilot).Range;
        long module = fixture.Base.Fit(type);
        using CatalogDbContext catalog = new(fixture.Base.CatalogPath);
        int group = catalog.FittingType(type)!.GroupId;
        var attributes = catalog.FittingAttributes(type);
        bool active = group == SupportModuleDefinitions.OmnidirectionalTrackingLinkGroup;
        if (active)
        {
            Assert.Equal(before, Profile(fixture));
            fixture.Space();
            var result = Support(fixture).Activate(fixture.Pilot, module, DogmaEffect.OmnidirectionalTrackingLink, fixture.Now);
            Assert.Equal(attributes[DogmaAttributeId.CapacitorNeed], result.Module.CapacitorNeed);
            Assert.Equal(attributes[DogmaAttributeId.Duration], result.Module.DurationMilliseconds);
        }
        var after = Profile(fixture);
        double damage = 1 + attributes.GetValueOrDefault(DogmaAttributeId.DroneDamageBonus) / 100;
        double speed = group == (int)PassiveModuleGroup.DroneNavigationComputer ? 1 + attributes[DogmaAttributeId.SpeedFactor] / 100 : 1;
        bool tracking = active || group == (int)PassiveModuleGroup.OmnidirectionalTrackingEnhancer;
        Assert.Equal(before.Damage.Total * damage, after.Damage.Total, 8);
        Assert.Equal(before.MaximumSpeed * speed, after.MaximumSpeed, 3);
        Assert.Equal(before.Tracking * (tracking ? 1 + attributes[DogmaAttributeId.TrackingSpeedBonus] / 100 : 1), after.Tracking, 8);
        Assert.Equal(before.Optimal * (tracking ? 1 + attributes[DogmaAttributeId.OptimalRangeBonus] / 100 : 1), after.Optimal, 8);
        Assert.Equal(before.Falloff * (tracking ? 1 + attributes[DogmaAttributeId.FalloffBonus] / 100 : 1), after.Falloff, 8);
        Assert.Equal(before.Defense, after.Defense);
        Assert.Equal(before.CycleMilliseconds, after.CycleMilliseconds);
        Assert.Equal(
            range + attributes.GetValueOrDefault(DogmaAttributeId.DroneControlRangeBonus),
            fixture.Drones.Control(fixture.Pilot).Range
        );
        if (active)
        {
            Support(fixture).Stop(fixture.Pilot, fixture.Ship, module);
            fixture.Base.Db.Store.SetPosition(fixture.Ship, null);
        }
        fixture.Base.Fitting.SetOnline(fixture.Pilot, fixture.Ship, module, false);
        Assert.Equal(before, Profile(fixture));
        Assert.Equal(range, fixture.Drones.Control(fixture.Pilot).Range);
    }

    [Theory]
    [MemberData(nameof(CombatRigCases))]
    public void CombatRigsConsumeEverySizeAndTierOnMatchingDrones(int type)
    {
        using Fixture fixture = Prepare();
        using (CatalogDbContext catalog = new(fixture.Base.CatalogPath, writable: true))
        {
            // Artificial rig-size probe; this does not admit a capital hull.
            double size = catalog.FittingAttributes(type)[DogmaAttributeId.RigSize];
            catalog.Database.ExecuteSql(
                $"UPDATE TypeAttribute SET Value={size} WHERE ItemTypeId=16229 AND AttributeId={(int)DogmaAttributeId.RigSize}"
            );
        }
        using CatalogDbContext inputs = new(fixture.Base.CatalogPath);
        var attributes = inputs.FittingAttributes(type);
        var before = new[] { Warrior, Hammerhead, Berserker, Garde }.ToDictionary(id => id, id => Profile(fixture, id));
        int range = fixture.Drones.Control(fixture.Pilot).Range;
        fixture.Base.Fit(type, online: false);
        foreach (var (id, basis) in before)
        {
            var actual = Profile(fixture, id);
            double damage = id == Garde ? 1 + attributes.GetValueOrDefault(DogmaAttributeId.SkillDamageBonus) / 100 : 1;
            double health = 1 + attributes.GetValueOrDefault(DogmaAttributeId.DroneDurabilityBonus) / 100;
            Assert.Equal(basis.Damage.Total * damage, actual.Damage.Total, 8);
            Assert.Equal(basis.Defense.Shield * health, actual.Defense.Shield, 8);
            Assert.Equal(basis.Defense.Armor * health, actual.Defense.Armor, 8);
            Assert.Equal(basis.Defense.Structure * health, actual.Defense.Structure, 8);
            Assert.Equal(
                basis.Optimal * (1 + attributes.GetValueOrDefault(DogmaAttributeId.DroneOptimalRangeBonus) / 100),
                actual.Optimal,
                8
            );
            Assert.Equal(
                basis.MaximumSpeed * (1 + attributes.GetValueOrDefault(DogmaAttributeId.DroneMaximumVelocityBonus) / 100),
                actual.MaximumSpeed,
                3
            );
        }
        Assert.Equal(
            range + attributes.GetValueOrDefault(DogmaAttributeId.DroneControlRangeBonus),
            fixture.Drones.Control(fixture.Pilot).Range
        );
    }

    [Fact]
    public void CoverageSeparatesWorkingCombatConsumersFromFightersMiningAndUtility()
    {
        using Fixture fixture = Prepare();
        using CatalogDbContext catalog = new(fixture.Base.CatalogPath);
        Assert.Equal(52, EquipmentCases().Count());
        Assert.Equal(40, CombatRigCases().Count());
        var report = ShipContentAudit.Read(catalog);
        foreach (int type in EquipmentCases().Select(row => (int)row[0]))
        {
            var entry = Assert.Single(report.Entries, entry => entry.TypeId == type);
            Assert.True(entry.Admitted);
            Assert.Contains(
                entry.Checks,
                check =>
                    check.Operation == ContentOperation.ConsumerCoverage
                    && check.Milestone == ShipContentMilestone.CombatDrones
                    && check.Implemented
            );
            if (catalog.FittingType(type)!.GroupId != (int)PassiveModuleGroup.DroneLinkAugmentor)
            {
                Assert.Contains(
                    entry.Checks,
                    check =>
                        check.Operation == ContentOperation.ConsumerCoverage
                        && check.Milestone == ShipContentMilestone.Fighters
                        && !check.Implemented
                );
            }
        }
        var rigs = report.Entries.Where(entry => entry.GroupId == (int)RigGroup.Drones).ToArray();
        Assert.Equal(64, rigs.Length);
        Assert.Equal(
            40,
            rigs.Count(entry =>
                entry.Checks.Any(check =>
                    check.Milestone == ShipContentMilestone.CombatDrones
                    && check.Implemented
                    && check.Operation == ContentOperation.ConsumerCoverage
                )
            )
        );
        Assert.Equal(
            8,
            rigs.Count(entry => entry.Checks.Any(check => check.Milestone == ShipContentMilestone.Mining && !check.Implemented))
        );
        Assert.Equal(
            16,
            rigs.Count(entry => entry.Checks.Any(check => check.Milestone == ShipContentMilestone.UtilityDrones && !check.Implemented))
        );
        Assert.All(report.Entries.Where(entry => entry.MetaGroup == 15), entry => Assert.False(entry.Admitted));
    }

    [Theory]
    [InlineData(OptimalScript, 1.1, 1.2, 1)]
    [InlineData(TrackingScript, 1, 1, 1.2)]
    public void SharedTrackingScriptsSpecializeOmniLinksAndSurvivePaidRestart(int script, double optimal, double falloff, double tracking)
    {
        using Fixture fixture = Prepare();
        long module = fixture.Base.Fit(OmniLink);
        var before = Profile(fixture, Garde);
        Load(fixture, module, script);
        fixture.Space();
        var activation = Support(fixture).Activate(fixture.Pilot, module, DogmaEffect.OmnidirectionalTrackingLink, fixture.Now);
        var active = Profile(fixture, Garde);
        Assert.Equal(before.Optimal * optimal, active.Optimal, 8);
        Assert.Equal(before.Falloff * falloff, active.Falloff, 8);
        Assert.Equal(before.Tracking * tracking, active.Tracking, 8);
        var magazine = Assert.Single(Ammo(fixture).Read(fixture.Pilot, fixture.Ship));
        fixture.Base.Clock.Milliseconds += 30_001;
        Assert.Throws<InvalidDataException>(() =>
            Ammo(fixture).Unload(fixture.Pilot, fixture.Ship, [module], fixture.Ship, (int)InventoryFlag.Cargo, null, fixture.Now)
        );
        WorldInitialization.Open(fixture.Base.Db.Path);
        Assert.Equal(before, Profile(fixture, Garde));
        Assert.Equal(magazine, Assert.Single(Ammo(fixture).Read(fixture.Pilot, fixture.Ship)));
        using WorldDbContext world = new(fixture.Base.Db.Path);
        Assert.Empty(world.Set<ActiveFittedModule>());
        Assert.Equal(activation.Capacitor, world.Set<ShipEnergy>().Find(fixture.Ship)!.Charge, 8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DamageNavigationAndTrackingShareStackingWithRigsAndActiveLinks(bool reverse)
    {
        using Fixture fixture = Prepare();
        var sentry = Profile(fixture, Garde);
        var mobile = Profile(fixture);
        int[] fittingOrder = reverse ? [32083, 32063, 24395, 4405] : [4405, 24395, 32063, 32083];
        foreach (int type in fittingOrder)
        {
            fixture.Base.Fit(type, online: type is not (32083 or 32063));
        }
        var damage = Profile(fixture, Garde);
        var speed = Profile(fixture);
        Assert.Equal(sentry.Damage.Total * 1.205 * (1 + .1 * SecondStackingWeight), damage.Damage.Total, 8);
        Assert.Equal(mobile.MaximumSpeed * 1.25 * (1 + .15 * SecondStackingWeight), speed.MaximumSpeed, 3);
        fixture.Base.Fit(33824);
        long link = fixture.Base.Fit(24438);
        Load(fixture, link, TrackingScript);
        fixture.Space();
        Support(fixture).Activate(fixture.Pilot, link, DogmaEffect.OmnidirectionalTrackingLink, fixture.Now);
        var tracking = Profile(fixture, Garde);
        Assert.Equal(sentry.Tracking * 1.3 * (1 + .095 * SecondStackingWeight), tracking.Tracking, 8);
        Assert.Equal(sentry.Optimal * 1.1, tracking.Optimal, 8);
        Assert.Equal(sentry.Falloff * 1.2, tracking.Falloff, 8);
    }

    [Fact]
    public void PassiveOnlineChangesPublishEffectiveBayAttributesAndControlRange()
    {
        using Fixture fixture = Prepare();
        var before = Profile(fixture);
        long navigation = fixture.Base.Fit(24395);
        long range = fixture.Base.Fit(23527);
        long stack = fixture.Bay(Warrior, 3);
        using var scope = fixture.Base.Db.Services.CreateScope();
        using var context = DroneSynchronizationTests.Context(fixture, scope.ServiceProvider, 1000001);
        var fitting = scope.ServiceProvider.GetRequiredService<FittingOperations>();
        Assert.Same(PilotCallResult.None, fitting.SetOnline(context, fixture.Ship, navigation, false));
        Assert.Contains(
            context.Notifications.OfType<ModuleAttributesChanged>().SelectMany(update => update.Changes),
            update =>
                update.Item == new DogmaItemKey.Item(stack)
                && update.Attribute == DogmaAttributeId.MaxVelocity
                && Math.Abs(update.Value - before.MaximumSpeed) < .001
        );
        context.Notifications.Clear();
        Assert.Same(PilotCallResult.None, fitting.SetOnline(context, fixture.Ship, range, false));
        Assert.Contains(
            context.Notifications.OfType<ModuleAttributesChanged>().SelectMany(update => update.Changes),
            update =>
                update.Item == new DogmaItemKey.Item(fixture.Pilot)
                && update.Attribute == DogmaAttributeId.DroneControlDistance
                && update.Value == 60_000
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeOmniCyclesKeepOnePaidRingRefreshBayAndSpaceAndLeaveWarmObserversWithoutSql(bool resident)
    {
        using Fixture fixture = Prepare();
        long module = fixture.Base.Fit(OmniLink);
        long bay = fixture.Bay(Warrior, 1);
        var before = Profile(fixture);
        using LiveWorld? live = resident ? new(new(fixture.Base.Db.Path), fixture.Base.Clock, _ => { }) : null;
        var ids = fixture.Launch(Warrior, 2);
        var wing = fixture.Base.Db.Store.FindPilot(1000002)!;
        var owner = fixture.Base.Db.Store.FindPilot(1000001)!;
        fixture.Base.Db.Store.SetPosition(wing.ShipId, owner.Position! + new Eden.Gameplay.Flight.SpacePosition(2000, 0, 0), SystemId);
        using var scope = fixture.Base.Db.Services.CreateScope();
        using var observerScope = fixture.Base.Db.Services.CreateScope();
        using var context = DroneSynchronizationTests.Context(fixture, scope.ServiceProvider, 1000001);
        using var observer = DroneSynchronizationTests.Context(fixture, observerScope.ServiceProvider, 1000002);
        context.Flight.Snapshot();
        observer.Flight.Snapshot();
        context.Notifications.Clear();
        context.Flight.Updates.Clear();
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var controller = scope.ServiceProvider.GetRequiredService<SupportModuleHandler>();
        Assert.Same(PilotCallResult.None, activation.Call(context, "dogmaIM", "Activate", [module, OmniEffect, null, 100L]));
        Assert.Single(context.Notifications, update => update.Name == "OnGodmaShipEffect");
        foreach (long id in ids.Append(bay))
        {
            Assert.Contains(
                context.Notifications.OfType<ModuleAttributesChanged>().SelectMany(update => update.Changes),
                update =>
                    update.Item == new DogmaItemKey.Item(id)
                    && update.Attribute == DogmaAttributeId.TrackingSpeed
                    && Math.Abs(update.Value - before.Tracking * 1.1) < 1e-8
            );
        }
        Assert.All(fixture.Drones.List(SystemId, fixture.Now), drone => Assert.Equal(before.Tracking * 1.1, drone.Profile.Tracking, 8));
        observer.Flight.RefreshPeers();
        Assert.DoesNotContain(
            context.Flight.Updates.Concat(observer.Flight.Updates),
            update => update.Command is DestinyCommand.TimedSpecialEffect effect && effect.ModuleId == module
        );
        Warm(before.Tracking * 1.1);
        fixture.Base.Clock.Milliseconds += 30_000;
        controller.Advance(context);
        Assert.Single(context.Notifications, update => update.Name == "OnGodmaShipEffect");
        Assert.Same(PilotCallResult.None, activation.Call(context, "dogmaIM", "Deactivate", [module, OmniEffect]));
        fixture.Base.Clock.Milliseconds += 15_000;
        controller.Advance(context);
        Assert.Equal(before.Tracking * 1.1, Profile(fixture).Tracking, 8);
        fixture.Base.Clock.Milliseconds += 15_000;
        controller.Advance(context);
        Assert.Equal(2, context.Notifications.Count(update => update.Name == "OnGodmaShipEffect"));
        Assert.All(fixture.Drones.List(SystemId, fixture.Now), drone => Assert.Equal(before.Tracking, drone.Profile.Tracking, 8));
        Warm(before.Tracking);

        void Warm(double tracking)
        {
            fixture.Tick();
            if (!resident)
            {
                return;
            }
            using var metrics = SessionPerformance.Begin("warm equipped drone frames", _ => { }, force: true);
            for (int frame = 0; frame < 12; frame++)
            {
                fixture.Tick();
                controller.Advance(context);
                observer.Flight.RefreshDrones();
                for (int viewer = 0; viewer < 10; viewer++)
                {
                    Assert.All(fixture.Drones.List(SystemId, fixture.Now), drone => Assert.Equal(tracking, drone.Profile.Tracking, 8));
                }
            }
            Assert.Equal(0, metrics!.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
    }

    [Theory]
    [InlineData(97243)]
    [InlineData(60482)]
    [InlineData(97242)]
    [InlineData(97245)]
    [InlineData(97244)]
    public void MutatedEquipmentCannotFitOrChangeInventory(int type)
    {
        using Fixture fixture = Prepare();
        long item = fixture.Base.Grant(type);
        Assert.Throws<InvalidDataException>(() =>
            fixture.Base.Fitting.Fit(fixture.Pilot, [item], fixture.Base.StationId, (int)InventoryFlag.AutoFit)
        );
        var unchanged = Assert.Single(fixture.Base.Inventory.List(fixture.Pilot, fixture.Base.StationId), value => value.Id == item);
        Assert.Equal(1, unchanged.Quantity);
        Assert.False(unchanged.Singleton);
        Assert.False(unchanged.Online);
    }

    [Fact]
    public void AnUnknownFighterModifierDoesNotSilentlyAdmitTheMixedProfile()
    {
        using Fixture fixture = Prepare();
        using (CatalogDbContext catalog = new(fixture.Base.CatalogPath, writable: true))
        {
            var source = catalog.Set<SourceRecord>().Find("client/dogmaeffects", "6556")!;
            source.Data = source.Data.Replace("2226", "900041", StringComparison.Ordinal);
            catalog.Update(source);
            catalog.SaveChanges();
        }
        long item = fixture.Base.Grant(4393);
        Assert.Throws<InvalidDataException>(() =>
            fixture.Base.Fitting.Fit(fixture.Pilot, [item], fixture.Base.StationId, (int)InventoryFlag.AutoFit)
        );
        Assert.Contains(
            fixture.Base.Inventory.List(fixture.Pilot, fixture.Base.StationId),
            value => value.Id == item && value.Quantity == 1
        );
    }

    [Theory]
    [InlineData(47447, 499, 1.07, .93, 1.1)]
    [InlineData(47448, 499, 1.1, .9, 1.15)]
    [InlineData(93998, 561, 1.07, .93, 1.1)]
    [InlineData(94020, 561, 1.1, .9, 1.15)]
    [InlineData(94058, 450, 1.07, .93, 1.1)]
    [InlineData(94060, 450, 1.1, .9, 1.15)]
    public void MixedUpgradesApplyBothTheirWeaponAndDroneBonuses(int type, int weapon, double damage, double cycle, double drones)
    {
        using Fixture fixture = Prepare();
        bool missile = weapon == 499;
        PassiveCatalogFixture.LoadFrigateHulls(fixture.Base.CatalogPath);
        if (missile)
        {
            PassiveCatalogFixture.LoadDroneHull(fixture.Base.CatalogPath, 621);
            PassiveCatalogFixture.LoadMissileTiers(
                fixture.Base.CatalogPath,
                Eden.Gameplay.Combat.MissileDefinitions.Skills.Concat([499, 212]).ToHashSet()
            );
            using WorldDbContext world = new(fixture.Base.Db.Path);
            world.Ships.Find(fixture.Ship)!.TypeId = 621;
            world.SaveChanges();
        }
        Eden.Protocol.Tests.Persistence.SmallHullTests.TrainCatalog(fixture.Base);
        long launcher = fixture.Base.Fit(weapon);
        var item = Assert.Single(fixture.Base.Inventory.List(fixture.Pilot, fixture.Ship), value => value.Id == launcher);
        if (missile)
        {
            long charge = fixture.Base.Grant(212, 3);
            Ammo(fixture).Load(fixture.Pilot, fixture.Ship, [launcher], [charge], fixture.Base.StationId, fixture.Now);
        }
        var before = fixture.Base.Fitting.ModuleAttributes(fixture.Pilot, item);
        var drone = Profile(fixture);
        double damageBefore = missile
            ? Ammo(fixture).ChargeAttributes(fixture.Pilot, fixture.Ship, launcher)[DogmaAttributeId.KineticDamage]
            : before[DogmaAttributeId.DamageMultiplier];
        long module = fixture.Base.Fit(type);
        var after = fixture.Base.Fitting.ModuleAttributes(fixture.Pilot, item);
        double damageAfter = missile
            ? Ammo(fixture).ChargeAttributes(fixture.Pilot, fixture.Ship, launcher)[DogmaAttributeId.KineticDamage]
            : after[DogmaAttributeId.DamageMultiplier];
        Assert.Equal(damageBefore * damage, damageAfter, 8);
        Assert.Equal(before[DogmaAttributeId.WeaponCycleTime] * cycle, after[DogmaAttributeId.WeaponCycleTime], 8);
        Assert.Equal(drone.Damage.Total * drones, Profile(fixture).Damage.Total, 8);
        fixture.Base.Fitting.SetOnline(fixture.Pilot, fixture.Ship, module, false);
        Assert.Equal(drone, Profile(fixture));
    }

    [Theory]
    [InlineData("launcher")]
    [InlineData("size")]
    [InlineData("active")]
    public void RejectedOmniScriptSwapsKeepTheOriginalMagazineAndDeadline(string reason)
    {
        using Fixture fixture = Prepare();
        long module = fixture.Base.Fit(OmniLink);
        Load(fixture, module, OptimalScript);
        long replacement = fixture.Base.Grant(TrackingScript, 2, cargo: true);
        if (reason == "active")
        {
            fixture.Space();
            Support(fixture).Activate(fixture.Pilot, module, DogmaEffect.OmnidirectionalTrackingLink, fixture.Now);
            fixture.Base.Clock.Milliseconds += 30_001;
        }
        else
        {
            using CatalogDbContext catalog = new(fixture.Base.CatalogPath, writable: true);
            int attribute = (int)(reason == "launcher" ? DogmaAttributeId.LauncherGroup3 : DogmaAttributeId.ChargeSize);
            catalog.Database.ExecuteSql($"UPDATE TypeAttribute SET Value=2 WHERE ItemTypeId={TrackingScript} AND AttributeId={attribute}");
        }
        var before = Assert.Single(Ammo(fixture).Read(fixture.Pilot, fixture.Ship));
        using WorldDbContext world = new(fixture.Base.Db.Path);
        long ready = world.Set<ModuleCooldown>().Find(module)!.ReadyAt;
        Assert.Throws<InvalidDataException>(() =>
            Ammo(fixture).Load(fixture.Pilot, fixture.Ship, [module], [replacement], fixture.Ship, fixture.Now)
        );
        Assert.Equal(before, Assert.Single(Ammo(fixture).Read(fixture.Pilot, fixture.Ship)));
        world.ChangeTracker.Clear();
        Assert.Equal(ready, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Equal(2, world.Set<OwnedItem>().Find(replacement)!.Quantity);
    }
}
