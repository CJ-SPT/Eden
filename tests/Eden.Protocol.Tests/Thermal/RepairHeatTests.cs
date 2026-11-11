using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Repair;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FittingFixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Thermal;

public sealed class RepairHeatTests
{
    private const int Ibis = 601;
    private const int ArmorRepairer = 523;
    private const int HullRepairer = 524;
    private const int AncillaryArmor = 33076;
    private const int AncillaryRemoteArmor = 41476;
    private const int AncillaryRemoteShield = 41480;
    private const int Navy25 = 33330;
    private const double Damage = 500000;

    private sealed class RepairEvents : IRemoteRepairEvents
    {
        internal List<RemoteRepairApplied> Applied { get; } = [];

        public void Publish(RemoteRepairApplied repair) => Applied.Add(repair);
    }

    private sealed class Fixture : IDisposable
    {
        internal FittingFixture Fit { get; } = new();
        internal long Pilot { get; }
        internal long Ship { get; }
        internal long Target { get; }
        internal long TargetPilot { get; }
        internal long Module { get; }
        internal long Second { get; }
        internal int Type { get; }
        internal int Group { get; }
        internal LiveWorld Live { get; }
        internal RepairEvents Events { get; } = new();
        internal long Now => Fit.Clock.Now;
        internal PilotLocalRepairStore Local => new(new(Fit.Db.Path));
        internal PilotRemoteRepairStore Remote => new(new(Fit.Db.Path), Events, Fit.Clock);
        internal IPilotShipHeat Heat => Fit.Db.Services.GetRequiredService<IPilotShipHeat>();
        internal IPilotAmmunition Ammo => Fit.Db.Services.GetRequiredService<IPilotAmmunition>();

        internal Fixture(int type, bool fueled = false, bool second = false)
        {
            Type = type;
            PassiveCatalogFixture.LoadOverload(Fit.CatalogPath);
            PassiveCatalogFixture.LoadActiveTiers(Fit.CatalogPath, new HashSet<int> { ArmorRepairer, 1183 });
            PassiveCatalogFixture.LoadHullRepairTiers(Fit.CatalogPath);
            PassiveCatalogFixture.LoadRemoteRepairs(Fit.CatalogPath);
            PassiveCatalogFixture.LoadAncillaryRepairs(Fit.CatalogPath, Ibis);
            PassiveCatalogFixture.LoadAncillaryRemoteRepairs(Fit.CatalogPath, Ibis);
            using (CatalogDbContext catalog = new(Fit.CatalogPath, writable: true))
            {
                // Explicit test-only budgets cover payment/heat across module
                // sizes; these are not real corvette fits or hull admissions.
                foreach (
                    var attribute in new[]
                    {
                        DogmaAttributeId.CpuOutput,
                        DogmaAttributeId.PowerOutput,
                        DogmaAttributeId.CapacitorCapacity,
                        DogmaAttributeId.ShieldCapacity,
                        DogmaAttributeId.ArmorHitPoints,
                        DogmaAttributeId.StructureHitPoints,
                    }
                )
                {
                    catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)attribute},1000000)");
                }
                catalog.Database.ExecuteSqlRaw(
                    "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142','{{\"securityStatus\":0}}')"
                );
                Group = catalog.RequireItem(type).GroupId;
            }
            Pilot = Fit.PilotId;
            Ship = Fit.ShipId;
            var target = Fit.Db.Store.FindPilot(1000002)!;
            Target = target.ShipId;
            TargetPilot = target.PilotId;
            Fit.Train(OverloadRules.ThermodynamicsSkill, 3);
            Module = Fit.Fit(type);
            Second = second ? Fit.Fit(type) : 0;
            if (fueled)
            {
                int fuel = type == AncillaryRemoteShield ? Navy25 : NaniteRepairRules.PasteType;
                long stack = Fit.Grant(fuel, 50);
                Ammo.Load(Pilot, Ship, [Module], [stack], Fit.StationId, Now);
                Fit.Clock.Milliseconds += 60000;
            }
            Fit.Db.Store.SetPosition(Ship, SpacePosition.Zero);
            Fit.Db.Store.SetPosition(Target, new SpacePosition(1000, 0, 0));
            using (WorldDbContext world = new(Fit.Db.Path))
            {
                foreach (long ship in new[] { Ship, Target })
                {
                    world.Add(
                        new ShipHealthState
                        {
                            ShipId = ship,
                            ShieldCharge = 25,
                            ArmorDamage = Damage,
                            StructureDamage = Damage,
                            UpdatedAt = Now,
                        }
                    );
                }
                world.SaveChanges();
            }
            Live = new(new(Fit.Db.Path), Fit.Clock, _ => { });
        }

        internal IReadOnlyDictionary<DogmaAttributeId, double> Normal() =>
            Fit.Fitting.ModuleAttributes(Pilot, Fit.Inventory.List(Pilot, Ship).Single(i => i.Id == Module));

        internal double Expected(DogmaAttributeId attribute)
        {
            var normal = Normal();
            using CatalogDbContext catalog = new(Fit.CatalogPath);
            var bonus = OverloadCatalog.Definition(catalog, Type)!.Bonuses.SingleOrDefault(b => b.Attribute == attribute);
            return normal[attribute] * (bonus is null ? 1 : 1 + normal[bonus.SourceAttribute] / 100);
        }

        internal RemoteRepairActivation StartRemote(long? module = null) =>
            Remote.StartOverloaded(
                Pilot,
                module ?? Module,
                RemoteRepairRules.Effect(Group)!.Value,
                Target,
                new HashSet<long> { Target },
                Now
            );

        internal ShipHeatSnapshot Read() => Heat.Read(Pilot, Ship, Now);

        internal void At(long at) => Fit.Clock.AdvanceWallTime(TimeSpan.FromTicks(at - Now));

        public void Dispose()
        {
            Live.Dispose();
            Fit.Dispose();
        }
    }

    [Theory]
    [InlineData(ArmorRepairer, RepairLayer.Armor)]
    [InlineData(1183, RepairLayer.Armor)]
    [InlineData(HullRepairer, RepairLayer.Hull)]
    [InlineData(2355, RepairLayer.Hull)]
    public void LocalOverloadCommitsTheModifiedRepairAmountAndPaidDeadlineWithHeat(int type, RepairLayer layer)
    {
        using Fixture f = new(type);
        var normal = f.Normal();
        var amountAttribute = LocalRepairDefinitions.Profile(layer).AmountAttribute;
        double amount = f.Expected(amountAttribute);
        double duration = f.Expected(DogmaAttributeId.Duration);
        double before = f.Fit.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.Pilot, f.Now);
        var paid = f.Local.StartOverloaded(f.Pilot, f.Module, f.Now, layer);
        var heat = Assert.Single(f.Read().Cycles);
        Assert.Equal(paid.Schedule.StartedAt, heat.StartedAt);
        Assert.Equal(paid.Schedule.CompletesAt, heat.CompletesAt);
        Assert.Equal(LocalRepairDefinitions.Profile(layer).Rack, heat.Rack);
        Assert.Equal(f.Now + (long)decimal.Ceiling((decimal)duration * TimeSpan.TicksPerMillisecond), heat.CompletesAt);
        Assert.Equal(before - f.Expected(DogmaAttributeId.CapacitorNeed), paid.Capacitor, 8);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            var pending = Assert.Single(world.Set<PendingLocalRepair>());
            Assert.Equal(amount, pending.Amount, 8);
            Assert.Equal(heat.CompletesAt, pending.CompletesAt);
            Assert.Equal(heat.CompletesAt, world.Set<ModuleCooldown>().Find(f.Module)!.ReadyAt);
        }
        Assert.Equal(normal[DogmaAttributeId.Duration], f.Normal()[DogmaAttributeId.Duration]);
        f.At(heat.CompletesAt);
        var health = f.Local.Complete(f.Pilot, f.Ship, f.Now);
        Assert.Equal(
            Damage - amount,
            layer == RepairLayer.Armor
                ? health.Defense.Armor - health.Remaining.Armor
                : health.Defense.Structure - health.Remaining.Structure,
            7
        );
        Assert.Empty(f.Read().Cycles);
    }

    [Theory]
    [InlineData(3586)]
    [InlineData(3588)]
    [InlineData(11355)]
    [InlineData(26912)]
    [InlineData(27932)]
    [InlineData(4299)]
    [InlineData(3618)]
    [InlineData(41466)]
    [InlineData(41475)]
    public void RemoteOverloadPaysItsNativeModifiersAndRegistersTheSameTargetedCycle(int type)
    {
        using Fixture f = new(type);
        var normal = f.Normal();
        var kind = RemoteRepairRules.Effect(f.Group)!.Value;
        var amountAttribute =
            kind == DogmaEffect.RemoteShieldBoost ? DogmaAttributeId.ShieldBonus
            : kind == DogmaEffect.RemoteArmorRepair ? DogmaAttributeId.ArmorRepairAmount
            : DogmaAttributeId.HullRepairAmount;
        double amount = f.Expected(amountAttribute);
        double duration = f.Expected(DogmaAttributeId.Duration);
        var paid = f.StartRemote();
        var heat = Assert.Single(f.Read().Cycles);
        Assert.Equal(amount, paid.Cycle.Module.Amount, 8);
        Assert.Equal(duration, paid.Cycle.Module.DurationMilliseconds, 8);
        Assert.Equal(paid.Cycle.StartedAt, heat.StartedAt);
        Assert.Equal(paid.Cycle.CompletesAt, heat.CompletesAt);
        Assert.Equal(ModuleRack.High, heat.Rack);
        Assert.Equal(normal[DogmaAttributeId.Duration], f.Normal()[DogmaAttributeId.Duration]);
        Assert.Equal(kind == DogmaEffect.RemoteShieldBoost ? 1 : 0, f.Events.Applied.Count);
        f.At(heat.CompletesAt);
        f.Remote.Complete(f.Pilot, f.Module, f.Now);
        var applied = Assert.Single(f.Events.Applied);
        Assert.Equal(f.TargetPilot, applied.PilotId);
        if (kind == DogmaEffect.RemoteShieldBoost)
        {
            Assert.Equal(25 + amount, applied.Health.Remaining.Shield, 7);
        }
        else
        {
            Assert.Equal(
                Damage - amount,
                kind == DogmaEffect.RemoteArmorRepair
                    ? applied.Health.Defense.Armor - applied.Health.Remaining.Armor
                    : applied.Health.Defense.Structure - applied.Health.Remaining.Structure,
                7
            );
        }
        Assert.Empty(f.Read().Cycles);
    }

    [Theory]
    [InlineData(AncillaryArmor, false)]
    [InlineData(AncillaryArmor, true)]
    [InlineData(AncillaryRemoteArmor, false)]
    [InlineData(AncillaryRemoteArmor, true)]
    [InlineData(AncillaryRemoteShield, false)]
    [InlineData(AncillaryRemoteShield, true)]
    public void AncillaryOverloadPreservesFueledAndUnfueledPaymentAndHeatTiming(int type, bool fueled)
    {
        using Fixture f = new(type, fueled);
        var normal = f.Normal();
        bool shield = type == AncillaryRemoteShield;
        double amount = f.Expected(shield ? DogmaAttributeId.ShieldBonus : DogmaAttributeId.ArmorRepairAmount);
        if (fueled && !shield)
        {
            amount *= normal[DogmaAttributeId.ChargedArmorMultiplier];
        }
        double capacitor = f.Fit.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.Pilot, f.Now);
        int charges = fueled ? Assert.Single(f.Ammo.Read(f.Pilot, f.Ship)).Quantity : 0;
        double actualCapacitor;
        long end;
        if (type == AncillaryArmor)
        {
            var paid = f.Local.StartOverloaded(f.Pilot, f.Module, f.Now, ancillary: true);
            actualCapacitor = paid.Capacitor;
            end = paid.Schedule.CompletesAt;
            using WorldDbContext world = new(f.Fit.Db.Path);
            Assert.Equal(amount, Assert.Single(world.Set<PendingLocalRepair>()).Amount, 7);
        }
        else
        {
            var paid = f.StartRemote();
            actualCapacitor = paid.Capacitor;
            end = paid.Cycle.CompletesAt;
            Assert.Equal(amount, paid.Cycle.Module.Amount, 7);
        }
        Assert.Equal(capacitor - (shield && fueled ? 0 : f.Expected(DogmaAttributeId.CapacitorNeed)), actualCapacitor, 8);
        var heat = Assert.Single(f.Read().Cycles);
        Assert.Equal(end, heat.CompletesAt);
        Assert.Equal(normal[DogmaAttributeId.HeatAbsorption], heat.Absorption);
        if (fueled)
        {
            Assert.Equal(charges - (int)normal[DogmaAttributeId.ChargeRate], Assert.Single(f.Ammo.Read(f.Pilot, f.Ship)).Quantity);
        }
    }

    [Theory]
    [InlineData(AncillaryRemoteArmor)]
    [InlineData(AncillaryRemoteShield)]
    public void RejectedHeatPersistenceRollsBackRepairsFuelCapacitorAndEvents(int type)
    {
        using Fixture f = new(type, fueled: true);
        int charges = Assert.Single(f.Ammo.Read(f.Pilot, f.Ship)).Quantity;
        double capacitor = f.Fit.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.Pilot, f.Now);
        ModuleCooldownKind previousKind;
        long previousDeadline;
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            var existing = Assert.Single(world.Set<ModuleCooldown>());
            previousKind = existing.Kind;
            previousDeadline = existing.ReadyAt;
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectRepairHeat BEFORE INSERT ON WorldShipHeat BEGIN SELECT RAISE(ABORT,'test heat failure'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() =>
        {
            if (type == AncillaryArmor)
            {
                f.Local.StartOverloaded(f.Pilot, f.Module, f.Now, ancillary: true);
            }
            else
            {
                f.StartRemote();
            }
        });
        Assert.Equal(charges, Assert.Single(f.Ammo.Read(f.Pilot, f.Ship)).Quantity);
        Assert.Equal(capacitor, f.Fit.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.Pilot, f.Now), 8);
        Assert.Empty(f.Events.Applied);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            Assert.Empty(world.Set<SavedShipHeat>());
            var unchanged = Assert.Single(world.Set<ModuleCooldown>());
            Assert.Equal(previousKind, unchanged.Kind);
            Assert.Equal(previousDeadline, unchanged.ReadyAt);
            Assert.Empty(world.Set<PendingLocalRepair>());
            Assert.Empty(world.Set<WorldRemoteRepair>());
            Assert.Equal(25, world.Set<ShipHealthState>().Find(f.Target)!.ShieldCharge);
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectRepairHeat");
        }
        Assert.True(f.Read().Idle);
    }

    [Fact]
    public void ResidentAncillaryRepairCheckpointFailureRetainsFuelPaymentHeatAndCompletionForRetry()
    {
        using Fixture f = new(AncillaryArmor, fueled: true);
        int charges = Assert.Single(f.Ammo.Read(f.Pilot, f.Ship)).Quantity;
        double capacitor = f.Fit.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.Pilot, f.Now);
        ModuleCooldownKind previousKind;
        long previousDeadline;
        using (WorldDbContext baseline = new(f.Fit.Db.Path))
        {
            var previous = Assert.Single(baseline.Set<ModuleCooldown>());
            previousKind = previous.Kind;
            previousDeadline = previous.ReadyAt;
        }
        var paid = f.Local.StartOverloaded(f.Pilot, f.Module, f.Now, ancillary: true);
        var heat = Assert.Single(f.Read().Cycles);
        int remaining = f.Live.Economy.Inspect(world => world.Set<OwnedItem>().Single(item => item.LocationId == f.Module).Quantity);
        Assert.True(remaining < charges);
        Assert.True(paid.Capacitor < capacitor);
        Assert.Throws<InvalidDataException>(() => f.Local.StartOverloaded(f.Pilot, f.Module, f.Now, ancillary: true));
        Assert.Empty(f.Events.Applied);
        using (var failed = Assert.IsType<WorldSnapshot>(f.Live.CaptureSnapshot()))
        {
            using (var command = failed.Database.CreateCommand())
            {
                command.CommandText =
                    "CREATE TRIGGER RejectRepairHeat BEFORE INSERT ON WorldShipHeat BEGIN SELECT RAISE(ABORT,'test heat failure'); END";
                command.ExecuteNonQuery();
            }
            Assert.Throws<SqliteException>(() => failed.Project());
            using WorldDbContext saved = new(failed.Database);
            Assert.Empty(saved.Set<SavedShipHeat>());
            Assert.Empty(saved.Set<PendingLocalRepair>());
            Assert.Equal(charges, saved.Set<OwnedItem>().Single(item => item.LocationId == f.Module).Quantity);
            var unchanged = Assert.Single(saved.Set<ModuleCooldown>());
            Assert.Equal(previousKind, unchanged.Kind);
            Assert.Equal(previousDeadline, unchanged.ReadyAt);
        }
        Assert.Equal(heat.Key, Assert.Single(f.Read().Cycles).Key);
        using var retry = Assert.IsType<WorldSnapshot>(f.Live.CaptureSnapshot());
        retry.Project();
        using WorldDbContext reopened = new(retry.Database);
        Assert.Equal(remaining, reopened.Set<OwnedItem>().Single(item => item.LocationId == f.Module).Quantity);
        Assert.Equal(paid.Capacitor, reopened.Set<ShipEnergy>().Find(f.Ship)!.Charge, 8);
        Assert.Equal(paid.Schedule.CompletesAt, reopened.Set<PendingLocalRepair>().Find(f.Module)!.CompletesAt);
        Assert.Equal(paid.Schedule.CompletesAt, reopened.Set<ModuleCooldown>().Find(f.Module)!.ReadyAt);
        Assert.Equal(Damage, reopened.Set<ShipHealthState>().Find(f.Ship)!.ArmorDamage);
        Assert.Equal(heat.Key, Assert.Single(ShipHeatPersistence.Decode(reopened.Set<SavedShipHeat>().Find(f.Ship)!.StateJson).Cycles).Key);
    }

    [Theory]
    [InlineData(3586)]
    [InlineData(11355)]
    public void RemoteCancellationStopsOnlyItsHeatAtTheCancellationBoundary(int type)
    {
        using Fixture f = new(type, second: true);
        var first = f.StartRemote();
        var second = f.StartRemote(f.Second);
        f.At(first.Cycle.StartedAt + (first.Cycle.CompletesAt - first.Cycle.StartedAt) / 2);
        var before = f.Read();
        f.Remote.Stop(f.Pilot, f.Module);
        var cancelled = f.Read();
        Assert.Equal(f.Second, Assert.Single(cancelled.Cycles).ModuleId);
        Assert.Equal(before.Heat(ModuleRack.High), cancelled.Heat(ModuleRack.High), 8);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            Assert.Equal(f.Second, Assert.Single(world.Set<WorldRemoteRepair>()).ModuleId);
        }
        f.At(first.Cycle.CompletesAt);
        var rack = cancelled.Racks.Single(r => r.Profile.Rack == ModuleRack.High);
        double expected = HeatRules.Calculate(
            rack.Profile,
            rack.Heat,
            Assert.Single(cancelled.Cycles).Absorption,
            (f.Now - cancelled.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond
        );
        Assert.Equal(expected, f.Read().Heat(ModuleRack.High), 8);
        f.Remote.Complete(f.Pilot, f.Module, f.Now);
        Assert.Equal(type == 3586 ? 2 : 0, f.Events.Applied.Count);
        f.Remote.Complete(f.Pilot, f.Second, f.Now);
        Assert.Equal(type == 3586 ? 2 : 1, f.Events.Applied.Count);
        Assert.Equal(second.Cycle.CompletesAt, first.Cycle.CompletesAt);
        Assert.Empty(f.Read().Cycles);
    }

    [Fact]
    public void FailedRemoteCancellationPreservesBothTheRepairAndItsHeatSource()
    {
        using Fixture f = new(11355);
        var paid = f.StartRemote();
        f.At(paid.Cycle.StartedAt + (paid.Cycle.CompletesAt - paid.Cycle.StartedAt) / 2);
        var before = f.Read();
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectRepairStop BEFORE DELETE ON WorldRemoteRepair BEGIN SELECT RAISE(ABORT,'test stop failure'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() => f.Remote.Stop(f.Pilot, f.Module));
        Assert.True(HeatCycleRules.SameState(before, f.Read()));
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            Assert.Single(world.Set<WorldRemoteRepair>());
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectRepairStop");
        }
        Assert.Empty(f.Events.Applied);
        f.Remote.Stop(f.Pilot, f.Module);
        Assert.Empty(f.Read().Cycles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingThermodynamicsCannotPayForEitherRepairProducer(bool remote)
    {
        using Fixture f = new(remote ? 11355 : ArmorRepairer);
        f.Fit.Train(OverloadRules.ThermodynamicsSkill, 0);
        double before = f.Fit.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.Pilot, f.Now);
        Assert.Throws<InvalidDataException>(() =>
        {
            if (remote)
            {
                f.StartRemote();
            }
            else
            {
                f.Local.StartOverloaded(f.Pilot, f.Module, f.Now);
            }
        });
        Assert.Equal(before, f.Fit.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.Pilot, f.Now), 8);
        using WorldDbContext world = new(f.Fit.Db.Path);
        Assert.Empty(world.Set<SavedShipHeat>());
        Assert.Empty(world.Set<ModuleCooldown>());
        Assert.Empty(world.Set<PendingLocalRepair>());
        Assert.Empty(world.Set<WorldRemoteRepair>());
        Assert.Empty(f.Events.Applied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmRepairHeatFramesDoNotReloadThePaidSourceOrItsFitting(bool remote)
    {
        using Fixture f = new(remote ? 11355 : ArmorRepairer);
        if (remote)
        {
            f.StartRemote();
        }
        else
        {
            f.Local.StartOverloaded(f.Pilot, f.Module, f.Now);
        }
        var initial = f.Read();
        using var metrics = SessionPerformance.Begin("repair heat frames", _ => { }, force: true)!;
        for (int frame = 0; frame < 100; frame++)
        {
            f.At(f.Now + TimeSpan.TicksPerMillisecond);
            Assert.Equal(Assert.Single(initial.Cycles).Key, Assert.Single(f.Read().Cycles).Key);
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void ReopenedCheckpointKeepsThePaidOverloadedLocalRepairAndSettlesItOnce()
    {
        using Fixture f = new(ArmorRepairer);
        double amount = f.Expected(DogmaAttributeId.ArmorRepairAmount);
        var paid = f.Local.StartOverloaded(f.Pilot, f.Module, f.Now);
        f.At(paid.Schedule.StartedAt + (paid.Schedule.CompletesAt - paid.Schedule.StartedAt) / 2);
        using var snapshot = Assert.IsType<WorldSnapshot>(f.Live.CaptureSnapshot());
        snapshot.Project();
        using (WorldDbContext captured = new(snapshot.Database))
        {
            Assert.Equal(amount, Assert.Single(captured.Set<PendingLocalRepair>()).Amount, 8);
            var heat = ShipHeatPersistence.Decode(Assert.Single(captured.Set<SavedShipHeat>()).StateJson);
            Assert.Equal(paid.Schedule.CompletesAt, Assert.Single(heat.Cycles).CompletesAt);
        }
        string path = Path.Combine(Path.GetDirectoryName(f.Fit.Db.Path)!, "overloaded-local-repair.sqlite");
        LiveWorld.PersistSnapshot(snapshot.Database, path);
        f.At(paid.Schedule.CompletesAt);
        WorldInitialization.Initialize(new(path), f.Fit.Clock);
        var store = new PilotLocalRepairStore(new(path));
        var first = store.Complete(f.Pilot, f.Ship, f.Now);
        Assert.Equal(Damage - amount, first.Defense.Armor - first.Remaining.Armor, 7);
        var duplicate = store.Complete(f.Pilot, f.Ship, f.Now);
        Assert.Equal(first, duplicate);
        using WorldDbContext saved = new(path);
        Assert.Empty(saved.Set<PendingLocalRepair>());
        Assert.Equal(paid.Schedule.CompletesAt, Assert.Single(saved.Set<ModuleCooldown>()).ReadyAt);
    }
}
