using System.Text.Json.Nodes;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Data.World.Skills;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Content;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Repair;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class RemoteAssistanceTests
{
    private static Fixture Prepare()
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadRemoteAssistance(f.CatalogPath);
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
                DogmaAttributeId.MaxTargetRange,
            }
        )
        {
            double value =
                attribute == DogmaAttributeId.MediumSlots ? 8
                : attribute == DogmaAttributeId.MaxTargetRange ? 150000
                : 1000000;
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)attribute},{value})");
        }
        return f;
    }

    private static long Target(Fixture f) => f.Db.Store.FindPilot(1000002)!.ShipId;

    private static long TargetPilot(Fixture f) => f.Db.Store.FindPilot(1000002)!.PilotId;

    private static IPilotRemoteAssistance Store(Fixture f) => f.Db.Services.GetRequiredService<IPilotRemoteAssistance>();

    private static void Space(Fixture f, double capacitor = 1000)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        SpacePosition origin = new(pilot.X + pilot.Flight.StationRadius + 50000, pilot.Y, pilot.Z);
        f.Db.Store.SetPosition(f.ShipId, origin);
        f.Db.Store.SetPosition(Target(f), origin + new SpacePosition(1000, 0, 0));
        using WorldDbContext world = new(f.Db.Path);
        world.Add(
            new ShipEnergy
            {
                ShipId = f.ShipId,
                Charge = capacitor,
                UpdatedAt = f.Clock.Now,
            }
        );
        world.SaveChanges();
    }

    private static RemoteAssistanceActivation Activate(Fixture f, long module, DogmaEffect effect) =>
        Store(f).Activate(f.PilotId, module, effect, Target(f), new HashSet<long> { Target(f) }, f.Clock.Now);

    private static long TargetGun(Fixture f)
    {
        long gun = f.Fit((int)CombatWeaponType.GatlingRailgun75mm);
        using WorldDbContext world = new(f.Db.Path);
        var item = world.Set<OwnedItem>().Find(gun)!;
        item.OwnerId = TargetPilot(f);
        item.LocationId = Target(f);
        item.FlagId = (int)InventoryFlag.HighSlot1;
        foreach (var skill in world.Set<PilotSkill>().Where(s => s.PilotId == f.PilotId).AsNoTracking().ToArray())
        {
            if (world.Set<PilotSkill>().Find(TargetPilot(f), skill.TypeId) is null)
            {
                world.Add(
                    new PilotSkill
                    {
                        PilotId = TargetPilot(f),
                        TypeId = skill.TypeId,
                        Points = skill.Points,
                    }
                );
            }
        }
        world.SaveChanges();
        return gun;
    }

    private static InventoryItem Gun(Fixture f, long gun) => f.Inventory.List(TargetPilot(f), Target(f)).Single(i => i.Id == gun);

    private static long Script(Fixture f, long module, int type)
    {
        long stack = f.Grant(type, 3);
        f.Db.Services.GetRequiredService<IPilotAmmunition>().Load(f.PilotId, f.ShipId, [module], [stack], f.StationId, f.Clock.Now);
        f.Clock.Milliseconds++;
        return stack;
    }

    [Theory]
    [InlineData(2103, DogmaEffect.RemoteTrackingComputer)]
    [InlineData(1963, DogmaEffect.RemoteSensorBooster)]
    public void PaidActivationAtomicallyChangesOnlyTheRecipientAndRetainsExactCooldown(int type, DogmaEffect effect)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        long gun = TargetGun(f);
        Space(f);
        var before = f.Fitting.ShipAttributes(TargetPilot(f));
        var weapon = f.Fitting.ModuleAttributes(TargetPilot(f), Gun(f, gun));
        var result = Activate(f, module, effect);
        Assert.Equal(985, result.Capacitor, 8);
        var after = f.Fitting.ShipAttributes(TargetPilot(f));
        var updated = f.Fitting.ModuleAttributes(TargetPilot(f), Gun(f, gun));
        if (effect == DogmaEffect.RemoteTrackingComputer)
        {
            Assert.Equal(weapon[DogmaAttributeId.MaxRange] * 1.06, updated[DogmaAttributeId.MaxRange], 8);
            Assert.Equal(weapon[DogmaAttributeId.TrackingSpeed] * 1.12, updated[DogmaAttributeId.TrackingSpeed], 8);
            Assert.Equal(
                updated[DogmaAttributeId.MaxRange],
                f.Db.Services.GetRequiredService<IPilotCombat>().ModuleAttributes(TargetPilot(f), Gun(f, gun))[DogmaAttributeId.MaxRange]
            );
            Assert.Equal(before[DogmaAttributeId.ScanResolution], after[DogmaAttributeId.ScanResolution]);
        }
        else
        {
            Assert.Equal(before[DogmaAttributeId.MaxTargetRange] * 1.33, after[DogmaAttributeId.MaxTargetRange], 8);
            Assert.Equal(before[DogmaAttributeId.ScanResolution] * 1.28, after[DogmaAttributeId.ScanResolution], 8);
            Assert.Equal(weapon[DogmaAttributeId.TrackingSpeed], updated[DogmaAttributeId.TrackingSpeed]);
        }
        Assert.Equal(before[DogmaAttributeId.ScanResolution], f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ScanResolution]);
        using WorldDbContext world = new(f.Db.Path);
        var saved = Assert.Single(world.Set<WorldRemoteAssistance>());
        Assert.Equal(result.Contact, saved.Contact());
        Assert.Equal(result.Contact.ExpiresAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.NotNull(world.Set<ActiveFittedModule>().Find(module));
        Assert.Empty(world.Set<SavedPilotHostility>());
        Assert.Throws<InvalidDataException>(() => Activate(f, module, effect));
        Store(f).Stop(f.PilotId, f.ShipId, module);
        Assert.Equal(before[DogmaAttributeId.ScanResolution], f.Fitting.ShipAttributes(TargetPilot(f))[DogmaAttributeId.ScanResolution]);
        Assert.Equal(
            weapon[DogmaAttributeId.TrackingSpeed],
            f.Fitting.ModuleAttributes(TargetPilot(f), Gun(f, gun))[DogmaAttributeId.TrackingSpeed]
        );
    }

    [Fact]
    public void AllSixteenStaticProfilesHaveOneOperationalOwnerAndRetainHeatAsUnfinished()
    {
        using Fixture f = Prepare();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var modules = catalog.Set<ItemType>().Where(t => t.GroupId == 209 || t.GroupId == 290).ToArray();
        Assert.Equal(16, modules.Length);
        Assert.All(
            modules,
            m =>
            {
                Assert.True(RemoteAssistanceCatalog.Supported(catalog, m.Id, m.GroupId), m.Name);
                Assert.Equal(ModuleRack.Medium, FittingCatalog.Definition(catalog, m.Id)!.Rack);
            }
        );
        var report = ShipContentAudit.Read(catalog);
        Assert.All(
            report.Entries.Where(e => e.GroupId == 209 || e.GroupId == 290),
            e =>
            {
                Assert.True(e.Admitted);
                Assert.Contains(e.Checks, c => c.Operation == ContentOperation.Overheating && !c.Implemented);
            }
        );
    }

    [Theory]
    [InlineData(2103, 28999, 1.12, 1)]
    [InlineData(2103, 29001, 1, 1.24)]
    public void RealTrackingScriptsProjectTheirConcentratedBonusesWithoutConsumingTheScript(
        int type,
        int script,
        double range,
        double tracking
    )
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        long gun = TargetGun(f);
        long stack = Script(f, module, script);
        var before = f.Fitting.ModuleAttributes(TargetPilot(f), Gun(f, gun));
        Space(f);
        Activate(f, module, DogmaEffect.RemoteTrackingComputer);
        var after = f.Fitting.ModuleAttributes(TargetPilot(f), Gun(f, gun));
        Assert.Equal(before[DogmaAttributeId.MaxRange] * range, after[DogmaAttributeId.MaxRange], 8);
        Assert.Equal(before[DogmaAttributeId.TrackingSpeed] * tracking, after[DogmaAttributeId.TrackingSpeed], 8);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(2, world.Set<OwnedItem>().Find(stack)!.Quantity);
        Assert.Equal(1, Assert.Single(world.Set<OwnedItem>().Where(i => i.LocationId == module)).Quantity);
        Assert.Throws<InvalidDataException>(() =>
            f
                .Db.Services.GetRequiredService<IPilotAmmunition>()
                .Unload(f.PilotId, f.ShipId, [module], f.ShipId, (int)InventoryFlag.Cargo, null, f.Clock.Now)
        );
    }

    [Theory]
    [InlineData(29009, 1.66, 1, 1)]
    [InlineData(29011, 1, 1.56, 1)]
    [InlineData(41155, 1, 1, 2)]
    public void RealSensorScriptsSelectRangeResolutionOrSensorStrength(int script, double range, double scan, double strength)
    {
        using Fixture f = Prepare();
        long module = f.Fit(1963);
        Script(f, module, script);
        Space(f);
        var before = f.Fitting.ShipAttributes(TargetPilot(f));
        Activate(f, module, DogmaEffect.RemoteSensorBooster);
        var after = f.Fitting.ShipAttributes(TargetPilot(f));
        Assert.Equal(before[DogmaAttributeId.MaxTargetRange] * range, after[DogmaAttributeId.MaxTargetRange], 8);
        Assert.Equal(before[DogmaAttributeId.ScanResolution] * scan, after[DogmaAttributeId.ScanResolution], 8);
        Assert.Equal(before[DogmaAttributeId.GravimetricStrength] * strength, after[DogmaAttributeId.GravimetricStrength], 8);
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("self")]
    [InlineData("unavailable")]
    [InlineData("offline")]
    [InlineData("cost")]
    [InlineData("range")]
    [InlineData("wrong-effect")]
    [InlineData("immunity")]
    [InlineData("assistance")]
    public void RejectionsPreserveResourcesTimersSavedEffectsAndRecipientAttributes(string reason)
    {
        using Fixture f = Prepare();
        long module = f.Fit(1963);
        Space(f, capacitor: reason == "cost" ? 0 : 1000);
        if (reason is "immunity" or "assistance")
        {
            using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
            var id = reason == "immunity" ? DogmaAttributeId.DisallowOffensiveModifiers : DogmaAttributeId.DisallowAssistance;
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO DogmaAttribute VALUES ({(int)id},{id.ToString()})");
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)id},1)");
        }
        if (reason == "offline")
        {
            using WorldDbContext world = new(f.Db.Path);
            world.Set<OwnedItem>().Find(module)!.Online = false;
            world.SaveChanges();
        }
        if (reason == "range")
        {
            var pilot = f.Db.Store.FindPilot(1000001)!;
            f.Db.Store.SetPosition(Target(f), new(pilot.X + 2000000, pilot.Y, pilot.Z));
        }
        long target =
            reason == "self" ? f.ShipId
            : reason == "unavailable" ? long.MaxValue
            : Target(f);
        var before = f.Fitting.ShipAttributes(TargetPilot(f));
        Assert.Throws<InvalidDataException>(() =>
            Store(f)
                .Activate(
                    f.PilotId,
                    module,
                    reason == "wrong-effect" ? DogmaEffect.RemoteTrackingComputer : DogmaEffect.RemoteSensorBooster,
                    target,
                    reason == "lock" ? [] : new HashSet<long> { target },
                    f.Clock.Now
                )
        );
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Empty(verify.Set<WorldRemoteAssistance>());
        Assert.Empty(verify.Set<ActiveFittedModule>());
        Assert.Empty(verify.Set<SavedCrimewatch>());
        Assert.Null(verify.Set<ModuleCooldown>().Find(module));
        Assert.Equal(reason == "cost" ? 0 : 1000, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Equal(before[DogmaAttributeId.ScanResolution], f.Fitting.ShipAttributes(TargetPilot(f))[DogmaAttributeId.ScanResolution]);
    }

    [Fact]
    public async Task CheckpointRestartRetainsPaymentAndCooldownButRemovesAllRemoteBonuses()
    {
        using Fixture f = Prepare();
        long module = f.Fit(1963);
        Space(f);
        var before = f.Fitting.ShipAttributes(TargetPilot(f));
        RemoteAssistanceActivation result;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            result = Activate(f, module, DogmaEffect.RemoteSensorBooster);
            await live.FlushAsync();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Empty(verify.Set<WorldRemoteAssistance>());
        Assert.Empty(verify.Set<ActiveFittedModule>());
        Assert.Equal(985, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Equal(result.Contact.ExpiresAt, verify.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Equal(before[DogmaAttributeId.ScanResolution], f.Fitting.ShipAttributes(TargetPilot(f))[DogmaAttributeId.ScanResolution]);
        Assert.Throws<InvalidDataException>(() => Activate(f, module, DogmaEffect.RemoteSensorBooster));
    }

    [Fact]
    public void MigrationFromThePreviousVersionIsIdempotentAndPreservesInventory()
    {
        using Fixture f = Prepare();
        long module = f.Fit(1963);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(TestWorld.DropRemoteAssistanceSchema + "PRAGMA user_version=103;");
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(f.PilotId, verify.Set<OwnedItem>().Find(module)!.OwnerId);
        Assert.Empty(verify.Set<WorldRemoteAssistance>());
        Assert.Equal(WorldMigration.CurrentVersion, verify.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Theory]
    [InlineData(2103)]
    [InlineData(2104)]
    [InlineData(5339)]
    [InlineData(5340)]
    [InlineData(5342)]
    [InlineData(14240)]
    [InlineData(15967)]
    [InlineData(22937)]
    [InlineData(1963)]
    [InlineData(1964)]
    [InlineData(5279)]
    [InlineData(5281)]
    [InlineData(5282)]
    [InlineData(22925)]
    [InlineData(41198)]
    [InlineData(41199)]
    public void EveryStaticTierPaysItsEarnedSensorLinkingCostAndSavesItsOwnCycle(int type)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var raw = catalog.FittingAttributes(type);
        var result = Activate(f, module, RemoteAssistanceRules.Effect(catalog.FittingType(type)!.GroupId)!.Value);
        Assert.Equal(raw[DogmaAttributeId.CapacitorNeed] * .75, result.Contact.Module.Cycle.CapacitorNeed, 8);
        Assert.Equal(1000 - raw[DogmaAttributeId.CapacitorNeed] * .75, result.Capacitor, 8);
        Assert.Equal(raw[DogmaAttributeId.Duration], result.Contact.Module.Cycle.DurationMilliseconds);
        Assert.Equal(type, result.Contact.Module.Cycle.TypeId);
        Assert.Equal(f.Clock.Now + (long)(raw[DogmaAttributeId.Duration] * TimeSpan.TicksPerMillisecond), result.Contact.ExpiresAt);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, .5)]
    [InlineData(2, .0625)]
    public void ActualEdgeFalloffAndRecipientImpedanceScaleSavedSupport(int falloffs, double strength)
    {
        using Fixture f = Prepare();
        long module = f.Fit(2103);
        long gun = TargetGun(f);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            int attribute = (int)DogmaAttributeId.RemoteAssistanceImpedance;
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO DogmaAttribute VALUES ({attribute},{attribute.ToString()})");
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)DogmaAttributeId.RemoteAssistanceImpedance},0.5)"
            );
        }
        Space(f);
        var before = f.Fitting.ModuleAttributes(TargetPilot(f), Gun(f, gun));
        var source = f.Db.Store.FindPilot(1000001)!;
        using CatalogDbContext read = new(f.CatalogPath);
        double edge = 40000 + falloffs * 20000;
        double radii = 2 * (read.FittingType(601)!.Radius ?? 0);
        f.Db.Store.SetPosition(Target(f), new(source.Position!.X + edge + radii, source.Position.Y, source.Position.Z));
        var result = Activate(f, module, DogmaEffect.RemoteTrackingComputer);
        Assert.Equal(edge, result.Contact.EdgeDistance, 8);
        Assert.Equal(
            before[DogmaAttributeId.MaxRange] * (1 + .06 * strength * .5),
            f.Fitting.ModuleAttributes(TargetPilot(f), Gun(f, gun))[DogmaAttributeId.MaxRange],
            8
        );
    }

    [Fact]
    public void SameTickSourcesShareStackingAndRollbackCannotPublishTheirRemoval()
    {
        using Fixture f = Prepare();
        long first = f.Fit(1963);
        long second = f.Fit(1963);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var before = f.Fitting.ShipAttributes(TargetPilot(f));
        Activate(f, first, DogmaEffect.RemoteSensorBooster);
        Activate(f, second, DogmaEffect.RemoteSensorBooster);
        double multiplier = 1.33 * (1 + .33 * Math.Exp(-Math.Pow(1 / 2.67, 2)));
        Assert.Equal(
            before[DogmaAttributeId.MaxTargetRange] * multiplier,
            f.Fitting.ShipAttributes(TargetPilot(f))[DogmaAttributeId.MaxTargetRange],
            8
        );
        var snapshot = Store(f).Incoming(Target(f));
        Assert.Equal(2, snapshot.Count);
        long recipientPilot = TargetPilot(f);
        using (WorldDbContext world = new(f.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            world.Set<WorldRemoteAssistance>().Where(e => e.ModuleId == first).ExecuteDelete();
            Assert.Equal(
                before[DogmaAttributeId.MaxTargetRange] * 1.33,
                PilotFittingStore.ShipAttributes(world, catalog, recipientPilot)[DogmaAttributeId.MaxTargetRange],
                8
            );
            transaction.Rollback();
        }
        Assert.Equal(
            before[DogmaAttributeId.MaxTargetRange] * multiplier,
            f.Fitting.ShipAttributes(TargetPilot(f))[DogmaAttributeId.MaxTargetRange],
            8
        );
        Assert.Equal(2, Store(f).Incoming(Target(f)).Count);
        Assert.Equal(2, snapshot.Count);
        Store(f).Stop(f.PilotId, f.ShipId, first);
        Assert.Equal(
            before[DogmaAttributeId.MaxTargetRange] * 1.33,
            f.Fitting.ShipAttributes(TargetPilot(f))[DogmaAttributeId.MaxTargetRange],
            8
        );
        Assert.Single(Store(f).Incoming(Target(f)));
        Store(f).Stop(f.PilotId, f.ShipId, second);
        Assert.Equal(before[DogmaAttributeId.MaxTargetRange], f.Fitting.ShipAttributes(TargetPilot(f))[DogmaAttributeId.MaxTargetRange]);
    }

    [Theory]
    [InlineData(2103, DogmaEffect.RemoteTrackingComputer)]
    [InlineData(1963, DogmaEffect.RemoteSensorBooster)]
    public void UnsafeAssistanceRejectsAtomicallyAndAllowedAssistanceInheritsOnlyRemainingDeadlines(int type, DogmaEffect effect)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data='{{\"securityStatus\":1}}' WHERE Dataset='client/systems' AND Key='30000142'"
            );
        }
        long end = f.Clock.Now + TimeSpan.FromMinutes(5).Ticks;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var state = CrimewatchPersistence.State(world, TargetPilot(f));
            state.SuspectUntil = end;
            state.PvpUntil = end;
            state.NpcUntil = end;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => Activate(f, module, effect));
        using (WorldDbContext rejected = new(f.Db.Path))
        {
            Assert.Equal(1000, rejected.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
            Assert.Null(rejected.Set<ModuleCooldown>().Find(module));
            Assert.Null(rejected.Set<SavedCrimewatch>().Find(f.PilotId));
            Assert.Empty(rejected.Set<WorldRemoteAssistance>());
        }
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data='{{\"securityStatus\":0}}' WHERE Dataset='client/systems' AND Key='30000142'"
            );
        }
        var result = Activate(f, module, effect);
        f.Clock.Milliseconds += (long)result.Contact.Module.Cycle.DurationMilliseconds;
        Activate(f, module, effect);
        using WorldDbContext verify = new(f.Db.Path);
        var inherited = verify.Set<SavedCrimewatch>().Find(f.PilotId)!;
        Assert.Equal(end, inherited.SuspectUntil);
        Assert.Equal(end, inherited.PvpUntil);
        Assert.Equal(end, inherited.NpcUntil);
        Assert.Empty(verify.Set<SavedPilotHostility>());
    }

    [Theory]
    [InlineData("unpublished")]
    [InlineData("offensive")]
    [InlineData("resistance")]
    [InlineData("modifiers")]
    [InlineData("immunity-policy")]
    public void ChangedCatalogEffectShapesRemainUnadmitted(string change)
    {
        using Fixture f = Prepare();
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        if (change == "immunity-policy")
        {
            int attribute = (int)DogmaAttributeId.DisallowAgainstEwImmuneTarget;
            catalog.Database.ExecuteSql($"UPDATE TypeAttribute SET Value=0 WHERE ItemTypeId=2103 AND AttributeId={attribute}");
            Assert.False(RemoteAssistanceCatalog.Supported(catalog, 2103, RemoteAssistanceRules.TrackingGroup));
            Assert.Null(FittingCatalog.Definition(catalog, 2103));
            return;
        }
        string dataset = change == "unpublished" ? "client/types" : "client/dogmaeffects";
        int key = change == "unpublished" ? 2103 : (int)DogmaEffect.RemoteTrackingComputer;
        var record = catalog.Set<SourceRecord>().Find(dataset, key.ToString())!;
        var data = JsonNode.Parse(record.Data)!;
        if (change == "unpublished")
        {
            data["published"] = 0;
        }
        else if (change == "offensive")
        {
            data["isOffensive"] = 1;
        }
        else if (change == "resistance")
        {
            data["resistanceAttributeID"] = (int)DogmaAttributeId.RemoteRepairImpedance;
        }
        else
        {
            data["modifierInfo"] = new JsonArray(new JsonObject { ["func"] = "UnverifiedModifier" });
        }
        record.Data = data.ToJsonString();
        catalog.Update(record);
        catalog.SaveChanges();
        Assert.False(RemoteAssistanceCatalog.Supported(catalog, 2103, RemoteAssistanceRules.TrackingGroup));
        Assert.Null(FittingCatalog.Definition(catalog, 2103));
    }

    [Theory]
    [InlineData(11978)]
    [InlineData(11989)]
    public void FutureLogisticsHullSelectorsHaveKnownConsumersWithoutEnablingTheirHullCohort(int type)
    {
        using Fixture f = Prepare();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var effects = HullCatalog
            .Capabilities(catalog, type)
            .Where(e => e.Capability == HullCapability.RemoteElectronicAssistance)
            .ToArray();
        Assert.Equal(4, effects.Length);
        Assert.All(effects, e => Assert.True(e.Operational));
        Assert.Equal(ShipContentMilestone.EnergyAndRemoteSupport, ShipContentRoadmap.ForRole(HullCapability.RemoteElectronicAssistance));
        foreach (var effect in PassiveCatalog.Effects(catalog, type).Where(e => effects.Any(c => c.EffectId == e.EffectID)))
        {
            var modifier = Assert.Single(effect.ModifierInfo!.Value);
            var parsed = PassiveCatalog.Parse(modifier, effect.EffectCategory!.Value, hull: true);
            Assert.NotNull(parsed);
            Assert.Equal(ModifierSelector.Group, parsed.Selector);
            Assert.Equal(RemoteAssistanceRules.TrackingGroup, parsed.SelectionId);
        }
        Assert.False(HullCatalog.CanAssemble(catalog, catalog.FittingType(type)!, (int)InventoryCategory.Ship));
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("membership")]
    [InlineData("marker")]
    [InlineData("source-dock")]
    [InlineData("recipient-dock")]
    [InlineData("recipient-system")]
    public void CommittedMembershipChangesReleaseCachedRecipientBonusesBeforeAnySessionFrame(string change)
    {
        using Fixture f = Prepare();
        long module = f.Fit(1963);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        long targetShip = Target(f);
        long recipientPilot = TargetPilot(f);
        var before = f.Fitting.ShipAttributes(recipientPilot);
        Activate(f, module, DogmaEffect.RemoteSensorBooster);
        Assert.Single(Store(f).Incoming(targetShip));
        Assert.True(Store(f).IsActive(f.PilotId, f.ShipId, module));
        Assert.True(f.Fitting.ShipAttributes(recipientPilot)[DogmaAttributeId.ScanResolution] > before[DogmaAttributeId.ScanResolution]);
        using (WorldDbContext world = new(f.Db.Path))
        {
            if (change == "marker")
            {
                world.Set<ActiveFittedModule>().Where(m => m.ModuleId == module).ExecuteDelete();
            }
            else if (change == "recipient-system")
            {
                world.Positions.Find(targetShip)!.SystemId = 30000144;
                world.SaveChanges();
            }
            else if (change is "source-dock" or "recipient-dock")
            {
                world.Remove(world.Positions.Find(change == "source-dock" ? f.ShipId : targetShip)!);
                world.SaveChanges();
            }
            else
            {
                var item = world.Set<OwnedItem>().Find(module)!;
                if (change == "offline")
                {
                    item.Online = false;
                }
                else
                {
                    item.FlagId = (int)InventoryFlag.Cargo;
                }
                world.SaveChanges();
            }
        }
        Assert.Empty(Store(f).Incoming(targetShip));
        Assert.False(Store(f).IsActive(f.PilotId, f.ShipId, module));
        Assert.Equal(before[DogmaAttributeId.ScanResolution], f.Fitting.ShipAttributes(recipientPilot)[DogmaAttributeId.ScanResolution]);
        Assert.Equal(before[DogmaAttributeId.ScanResolution], f.Fitting.ShipAttributes(recipientPilot)[DogmaAttributeId.ScanResolution]);
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(985, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Empty(verify.Set<ActiveFittedModule>());
    }
}
