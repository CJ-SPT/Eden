using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Propulsion;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Propulsion;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FittingFixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Thermal;

public sealed class MobilityHeatTests
{
    private const int Ibis = 601;
    private const int Afterburner = 439;
    private const int Microwarpdrive = 434;
    private const int Web = 526;
    private const int Scrambler = 447;
    private const int Disruptor = 3242;
    private const int CoreStabilizer = 10998;
    private const int FixtureSystem = 30000142;
    private const double Budget = 1000000;

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
        internal DogmaEffect Effect { get; }
        internal LiveWorld Live { get; }
        internal long Now => Fit.Clock.Now;
        internal PilotPropulsionStore Propulsion => Fit.Db.Services.GetRequiredService<PilotPropulsionStore>();
        internal PilotTackleStore Tackle => Fit.Db.Services.GetRequiredService<PilotTackleStore>();
        internal IPilotShipHeat Heat => Fit.Db.Services.GetRequiredService<IPilotShipHeat>();
        internal double Capacitor => Fit.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(Pilot, Now);

        internal Fixture(int type, int? second = null, bool npc = false)
        {
            Type = type;
            var selected = new HashSet<int> { type, second ?? type, Ibis };
            selected.UnionWith(Enum.GetValues<NavigationSkill>().Select(s => (int)s));
            PassiveCatalogFixture.LoadActiveTiers(Fit.CatalogPath, selected);
            PassiveCatalogFixture.LoadTackleTiers(Fit.CatalogPath);
            // Preserve the complete native price/material records needed by
            // nanite quotes after the older tier fixtures load fitting data.
            PassiveCatalogFixture.LoadOverload(Fit.CatalogPath);
            if (npc)
            {
                Eden.Protocol.Tests.Combat.NpcEncounterTests.Prepare(Fit.Db);
            }
            using (CatalogDbContext catalog = new(Fit.CatalogPath, writable: true))
            {
                // These explicit budgets exercise large propulsion/heavy tackle;
                // they do not establish real corvette fits or hull admission.
                foreach (
                    var attribute in new[]
                    {
                        DogmaAttributeId.CpuOutput,
                        DogmaAttributeId.PowerOutput,
                        DogmaAttributeId.CapacitorCapacity,
                        DogmaAttributeId.MaxTargetRange,
                    }
                )
                {
                    catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({Ibis},{(int)attribute},{Budget})");
                }
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({Ibis},{(int)DogmaAttributeId.MediumSlots},8)");
                catalog.Database.ExecuteSql(
                    $"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems',{FixtureSystem.ToString()},'{{\"securityStatus\":0}}')"
                );
                Effect = Eden.World.Fittings.FittingCatalog.DefaultEffect(catalog, type)!.Value;
            }
            Pilot = Fit.PilotId;
            Ship = Fit.ShipId;
            var target = Fit.Db.Store.FindPilot(1000002)!;
            Target = target.ShipId;
            TargetPilot = target.PilotId;
            Fit.Train(OverloadRules.ThermodynamicsSkill, 3);
            Fit.Train((int)NavigationSkill.Navigation, 3);
            Fit.Train((int)NavigationSkill.Afterburner, 4);
            Fit.Train((int)NavigationSkill.FuelConservation, 3);
            Fit.Train((int)NavigationSkill.AccelerationControl, 2);
            Fit.Train((int)NavigationSkill.HighSpeedManeuvering, 3);
            Module = Fit.Fit(type);
            Second = second is int other ? Fit.Fit(other) : 0;
            Fit.Db.Store.SetPosition(Ship, SpacePosition.Zero);
            Fit.Db.Store.SetPosition(Target, new SpacePosition(1000, 0, 0));
            Live = new(new(Fit.Db.Path), Fit.Clock, _ => { });
        }

        internal IReadOnlyDictionary<DogmaAttributeId, double> Normal()
        {
            var item = Fit.Inventory.List(Pilot, Ship).Single(i => i.Id == Module);
            return Effect is DogmaEffect.Afterburner or DogmaEffect.Microwarpdrive
                ? Propulsion.ModuleAttributes(Pilot, item)
                : Fit.Fitting.ModuleAttributes(Pilot, item);
        }

        internal double Expected(DogmaAttributeId attribute)
        {
            var normal = Normal();
            using CatalogDbContext catalog = new(Fit.CatalogPath);
            var bonus = OverloadCatalog.Definition(catalog, Type)!.Bonuses.SingleOrDefault(b => b.Attribute == attribute);
            return normal[attribute] * (bonus is null ? 1 : 1 + normal[bonus.SourceAttribute] / 100);
        }

        internal PropulsionCycle StartPropulsion() => Propulsion.ActivateOverloaded(Pilot, Module, Now, Effect);

        internal TackleActivation StartTackle() =>
            Tackle.ActivateOverloaded(
                Pilot,
                Module,
                Effect,
                new TackleTarget(Target, TackleTargetKind.Ship),
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
    [InlineData(Afterburner)]
    [InlineData(438)]
    [InlineData(Microwarpdrive)]
    [InlineData(440)]
    [InlineData(12068)]
    [InlineData(12084)]
    public void OverloadedPropulsionPaysItsEarnedSpeedAndDurationAndRegistersTheSameHeatCycle(int type)
    {
        using Fixture f = new(type);
        var normal = f.Normal();
        var basis = f.Fit.Db.Store.FindPilot(1000001)!.Flight;
        double speed = f.Expected(DogmaAttributeId.SpeedFactor);
        double duration = f.Expected(DogmaAttributeId.Duration);
        double capacitor = f.Capacitor;
        var paid = f.StartPropulsion();
        var cycle = Assert.Single(f.Read().Cycles);
        Assert.Equal(speed, paid.Module.SpeedFactor, 8);
        Assert.Equal(duration, paid.Module.DurationMilliseconds, 8);
        Assert.Equal(capacitor - f.Expected(DogmaAttributeId.CapacitorNeed), paid.Capacitor, 8);
        Assert.Equal(f.Now + (long)Math.Ceiling(duration * TimeSpan.TicksPerMillisecond), cycle.CompletesAt);
        Assert.Equal(ModuleRack.Medium, cycle.Rack);
        double expectedSpeed =
            basis.MaxSpeed * (1 + speed / PropulsionDefinitions.Percent * paid.Module.Thrust / (basis.Mass + paid.Module.MassAddition));
        var effective = paid.Module.Apply(basis);
        Assert.Equal((float)expectedSpeed, effective.MaxSpeed);
        Assert.Equal(basis.Mass + paid.Module.MassAddition, effective.Mass, 8);
        Assert.Equal(normal[DogmaAttributeId.SpeedFactor], f.Normal()[DogmaAttributeId.SpeedFactor], 8);
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Equal(f.Module, Assert.Single(saved.Set<ActiveFittedModule>()).ModuleId);
        Assert.Equal(cycle.CompletesAt, Assert.Single(saved.Set<ModuleCooldown>()).ReadyAt);
    }

    [Theory]
    [InlineData(Web)]
    [InlineData(527)]
    [InlineData(Scrambler)]
    [InlineData(448)]
    [InlineData(Disruptor)]
    [InlineData(3244)]
    [InlineData(40734)]
    [InlineData(40758)]
    public void OverloadedTacklePersistsItsEarnedRangeAndPaidHeatWithoutChangingStrength(int type)
    {
        using Fixture f = new(type);
        var normal = f.Normal();
        double range = f.Expected(DogmaAttributeId.MaxRange);
        double capacitor = f.Capacitor;
        var paid = f.StartTackle();
        var heat = Assert.Single(f.Read().Cycles);
        var incoming = Assert.Single(f.Tackle.Incoming(f.Target));
        Assert.Equal(range, paid.Module.Range, 8);
        Assert.Equal(paid.Module, incoming.Module);
        Assert.Equal(capacitor - paid.Module.CapacitorNeed, paid.Capacitor, 8);
        Assert.Equal(incoming.ExpiresAt, heat.CompletesAt);
        Assert.Equal(incoming.StartedAt, heat.StartedAt);
        Assert.Equal(ModuleRack.Medium, heat.Rack);
        var ordinary = TackleDefinitions.Define(f.Module, type, f.Effect, normal);
        Assert.Equal(ordinary.Strength, paid.Module.Strength);
        Assert.Equal(ordinary.WebPercent, paid.Module.WebPercent);
        Assert.Equal(ordinary.MwdBlock, paid.Module.MwdBlock);
        Assert.Equal(normal[DogmaAttributeId.MaxRange], f.Normal()[DogmaAttributeId.MaxRange]);
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Equal(range, Assert.Single(saved.Set<WorldTackle>()).Range, 8);
        Assert.Equal(heat.CompletesAt, Assert.Single(saved.Set<ModuleCooldown>()).ReadyAt);
    }

    [Theory]
    [InlineData(Afterburner)]
    [InlineData(Microwarpdrive)]
    public void PropulsionStopPreservesPaidCooldownAndCoolsFromTheStopBoundary(int type)
    {
        using Fixture f = new(type);
        double signature = f.Propulsion.SignatureRadius(f.Pilot);
        var paid = f.StartPropulsion();
        var source = Assert.Single(f.Read().Cycles);
        if (f.Effect == DogmaEffect.Microwarpdrive)
        {
            Assert.True(paid.SignatureRadius > signature);
        }
        f.At(source.StartedAt + (source.CompletesAt - source.StartedAt) / 2);
        var before = f.Read();
        double capacitor = f.Capacitor;
        Assert.Equal(signature, f.Propulsion.Stop(f.Pilot, f.Ship, f.Module), 8);
        Assert.Equal(capacitor, f.Capacitor, 8);
        var stopped = f.Read();
        Assert.Empty(stopped.Cycles);
        Assert.Equal(before.Heat(ModuleRack.Medium), stopped.Heat(ModuleRack.Medium), 8);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            Assert.Empty(world.Set<ActiveFittedModule>());
            Assert.Equal(source.CompletesAt, Assert.Single(world.Set<ModuleCooldown>()).ReadyAt);
        }
        f.At(source.CompletesAt);
        var rack = stopped.Racks.Single(r => r.Profile.Rack == ModuleRack.Medium);
        double expected = HeatRules.Calculate(
            rack.Profile,
            rack.Heat,
            0,
            (f.Now - stopped.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond
        );
        Assert.Equal(expected, f.Read().Heat(ModuleRack.Medium), 8);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("disconnect")]
    [InlineData("sourceTravel")]
    [InlineData("targetTravel")]
    [InlineData("sourceInvalid")]
    [InlineData("targetInvalid")]
    public void TackleRemovalSettlesItsHeatInTheSameLifecycleTransaction(string action)
    {
        using Fixture f = new(Web);
        f.StartTackle();
        var source = Assert.Single(f.Read().Cycles);
        f.At(source.StartedAt + (source.CompletesAt - source.StartedAt) / 2);
        var before = f.Read();
        double capacitor = f.Capacitor;
        switch (action)
        {
            case "stop":
                f.Tackle.Stop(f.Pilot, f.Ship, f.Module);
                break;
            case "disconnect":
                f.Tackle.Disconnect(f.Pilot, f.Ship);
                break;
            case "sourceTravel":
                f.Tackle.Cancel(f.Pilot, f.Ship);
                break;
            case "targetTravel":
                f.Tackle.Cancel(f.TargetPilot, f.Target);
                break;
            case "sourceInvalid":
                f.Fit.Db.Store.SetPosition(f.Ship, null);
                f.Tackle.Sweep(f.Now);
                break;
            case "targetInvalid":
                f.Fit.Db.Store.SetPosition(f.Target, null);
                f.Tackle.Sweep(f.Now);
                break;
        }
        var stopped = f.Read();
        Assert.Empty(stopped.Cycles);
        Assert.Equal(before.Heat(ModuleRack.Medium), stopped.Heat(ModuleRack.Medium), 8);
        Assert.Equal(capacitor, f.Capacitor, 8);
        Assert.Empty(f.Tackle.Incoming(f.Target));
        using WorldDbContext world = new(f.Fit.Db.Path);
        Assert.Empty(world.Set<WorldTackle>());
        Assert.Equal(source.CompletesAt, Assert.Single(world.Set<ModuleCooldown>()).ReadyAt);
    }

    [Theory]
    [InlineData(false)]
    public void FailedHeatInsertionRollsBackPaymentEffectsAndAggression(bool propulsion)
    {
        using Fixture f = new(propulsion ? Afterburner : Web);
        f.Live.Dispose();
        double capacitor = f.Capacitor;
        var crimewatch = f.Fit.Db.Services.GetRequiredService<IPilotCrimewatch>();
        var before = crimewatch.Read(f.Pilot, FixtureSystem);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectMobilityHeat BEFORE INSERT ON WorldShipHeat BEGIN SELECT RAISE(ABORT,'test heat failure'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() =>
        {
            if (propulsion)
            {
                f.StartPropulsion();
            }
            else
            {
                f.StartTackle();
            }
        });
        Assert.Equal(capacitor, f.Capacitor, 8);
        var after = crimewatch.Read(f.Pilot, FixtureSystem);
        Assert.Equal(before.Timers, after.Timers);
        Assert.Empty(after.Engagements);
        Assert.Empty(after.Flags);
        Assert.Empty(f.Tackle.Incoming(f.Target));
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            Assert.Empty(world.Set<SavedShipHeat>());
            Assert.Empty(world.Set<ModuleCooldown>());
            Assert.Empty(world.Set<ActiveFittedModule>());
            Assert.Empty(world.Set<WorldTackle>());
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectMobilityHeat");
        }
        Assert.True(f.Read().Idle);
    }

    [Theory]
    [InlineData(false)]
    public void FailedEffectRemovalKeepsThePaidHeatAndEffectTogether(bool propulsion)
    {
        using Fixture f = new(propulsion ? Afterburner : Web);
        f.Live.Dispose();
        if (propulsion)
        {
            f.StartPropulsion();
        }
        else
        {
            f.StartTackle();
        }
        var source = Assert.Single(f.Read().Cycles);
        f.At(source.StartedAt + (source.CompletesAt - source.StartedAt) / 2);
        var before = f.Read();
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            const string PropulsionFailure =
                "CREATE TRIGGER RejectMobilityStop BEFORE DELETE ON WorldActiveModule "
                + "BEGIN SELECT RAISE(ABORT,'test stop failure'); END";
            const string TackleFailure =
                "CREATE TRIGGER RejectMobilityStop BEFORE DELETE ON WorldTackle " + "BEGIN SELECT RAISE(ABORT,'test stop failure'); END";
            world.Database.ExecuteSqlRaw(propulsion ? PropulsionFailure : TackleFailure);
        }
        Assert.Throws<DbUpdateException>(() =>
        {
            if (propulsion)
            {
                f.Propulsion.Stop(f.Pilot, f.Ship, f.Module);
            }
            else
            {
                f.Tackle.Cancel(f.TargetPilot, f.Target);
            }
        });
        Assert.True(HeatCycleRules.SameState(before, f.Read()));
        using WorldDbContext saved = new(f.Fit.Db.Path);
        if (propulsion)
        {
            Assert.Single(saved.Set<ActiveFittedModule>());
        }
        else
        {
            Assert.Single(saved.Set<WorldTackle>());
        }
        saved.Database.ExecuteSqlRaw("DROP TRIGGER RejectMobilityStop");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResidentMobilityCheckpointFailureRetainsTheCompletePaidActivationForRetry(bool propulsion)
    {
        using Fixture f = new(propulsion ? Afterburner : Web);
        double before = f.Capacitor;
        if (propulsion)
        {
            f.StartPropulsion();
        }
        else
        {
            f.StartTackle();
        }
        var paid = Assert.Single(f.Read().Cycles);
        double capacitor = f.Live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(f.Ship)!.Charge);
        Assert.True(capacitor < before);
        RejectCheckpoint(
            f,
            "CREATE TRIGGER RejectMobilityHeat BEFORE INSERT ON WorldShipHeat BEGIN SELECT RAISE(ABORT,'test heat failure'); END",
            saved =>
            {
                Assert.Empty(saved.Set<SavedShipHeat>());
                Assert.Empty(saved.Set<ActiveFittedModule>());
                Assert.Empty(saved.Set<ModuleCooldown>());
                Assert.Null(saved.Set<ShipEnergy>().Find(f.Ship));
            }
        );
        Assert.Equal(paid.Key, Assert.Single(f.Read().Cycles).Key);
        using var retry = Assert.IsType<WorldSnapshot>(f.Live.CaptureSnapshot());
        retry.Project();
        using WorldDbContext reopened = new(retry.Database);
        Assert.Equal(capacitor, reopened.Set<ShipEnergy>().Find(f.Ship)!.Charge, 8);
        if (propulsion)
        {
            Assert.NotNull(reopened.Set<ActiveFittedModule>().Find(f.Module));
        }
        else
        {
            Assert.NotNull(reopened.Set<WorldTackle>().Find(f.Module));
        }
        Assert.Equal(paid.Key, Assert.Single(ShipHeatPersistence.Decode(reopened.Set<SavedShipHeat>().Find(f.Ship)!.StateJson).Cycles).Key);
        Assert.Equal(paid.CompletesAt, reopened.Set<ModuleCooldown>().Find(f.Module)!.ReadyAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResidentMobilityStopCheckpointFailureRetainsTheCompleteCancellationForRetry(bool propulsion)
    {
        using Fixture f = new(propulsion ? Afterburner : Web);
        if (propulsion)
        {
            f.StartPropulsion();
        }
        else
        {
            f.StartTackle();
        }
        var paid = Assert.Single(f.Read().Cycles);
        f.At(paid.StartedAt + (paid.CompletesAt - paid.StartedAt) / 2);
        _ = f.Read();
        // Retain a relational image of the paid state, then cancel in resident state.
        using (WorldDbContext baseline = new(f.Fit.Db.Path))
        {
            Assert.NotNull(baseline.Set<SavedShipHeat>().Find(f.Ship));
            Assert.Equal(propulsion, baseline.Set<ActiveFittedModule>().Any(row => row.ModuleId == f.Module));
            Assert.Equal(!propulsion, baseline.Set<WorldTackle>().Any(row => row.ModuleId == f.Module));
        }
        if (propulsion)
        {
            f.Propulsion.Stop(f.Pilot, f.Ship, f.Module);
        }
        else
        {
            f.Tackle.Cancel(f.TargetPilot, f.Target);
        }
        Assert.Empty(f.Read().Cycles);
        RejectCheckpoint(
            f,
            propulsion
                ? """
                CREATE TRIGGER RejectMobilityStop BEFORE DELETE ON WorldActiveModule
                BEGIN SELECT RAISE(ABORT,'test stop failure'); END
                """
                : """
                CREATE TRIGGER RejectMobilityStop BEFORE DELETE ON WorldTackle
                BEGIN SELECT RAISE(ABORT,'test stop failure'); END
                """,
            saved =>
            {
                Assert.Equal(propulsion, saved.Set<ActiveFittedModule>().Any(row => row.ModuleId == f.Module));
                Assert.Equal(!propulsion, saved.Set<WorldTackle>().Any(row => row.ModuleId == f.Module));
                Assert.Equal(
                    paid.Key,
                    Assert.Single(ShipHeatPersistence.Decode(saved.Set<SavedShipHeat>().Find(f.Ship)!.StateJson).Cycles).Key
                );
            }
        );
        Assert.Empty(f.Read().Cycles);
        using var retry = Assert.IsType<WorldSnapshot>(f.Live.CaptureSnapshot());
        retry.Project();
        using WorldDbContext reopened = new(retry.Database);
        Assert.Null(reopened.Set<ActiveFittedModule>().Find(f.Module));
        Assert.Null(reopened.Set<WorldTackle>().Find(f.Module));
        Assert.Empty(ShipHeatPersistence.Decode(reopened.Set<SavedShipHeat>().Find(f.Ship)!.StateJson).Cycles);
        Assert.Equal(paid.CompletesAt, reopened.Set<ModuleCooldown>().Find(f.Module)!.ReadyAt);
    }

    private static void RejectCheckpoint(Fixture fixture, string trigger, Action<WorldDbContext> unchanged)
    {
        using var failed = Assert.IsType<WorldSnapshot>(fixture.Live.CaptureSnapshot());
        using (var command = failed.Database.CreateCommand())
        {
            command.CommandText = trigger;
            command.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(() => failed.Project());
        using WorldDbContext saved = new(failed.Database);
        unchanged(saved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherPilotCannotRemoveAPaidMobilitySource(bool propulsion)
    {
        using Fixture f = new(propulsion ? Afterburner : Web);
        if (propulsion)
        {
            f.StartPropulsion();
        }
        else
        {
            f.StartTackle();
        }
        var before = f.Read();
        if (propulsion)
        {
            Assert.Equal(0, f.Propulsion.Stop(f.TargetPilot, f.Ship, f.Module));
        }
        else
        {
            f.Tackle.Stop(f.TargetPilot, f.Ship, f.Module);
        }
        Assert.True(HeatCycleRules.SameState(before, f.Read()));
    }

    [Fact]
    public void AnotherPropulsionModuleCannotConsumeCapacitorOrRegisterASecondHeatSource()
    {
        using Fixture f = new(Afterburner, Microwarpdrive);
        f.StartPropulsion();
        double capacitor = f.Capacitor;
        var state = f.Read();
        Assert.Throws<InvalidDataException>(() => f.Propulsion.ActivateOverloaded(f.Pilot, f.Second, f.Now, DogmaEffect.Microwarpdrive));
        Assert.Equal(capacitor, f.Capacitor, 8);
        Assert.True(HeatCycleRules.SameState(state, f.Read()));
        using WorldDbContext world = new(f.Fit.Db.Path);
        Assert.Equal(f.Module, Assert.Single(world.Set<ModuleCooldown>()).ModuleId);
    }

    [Fact]
    public void ACoreStabilizerWithoutAnOverloadProgramCannotPayForHeat()
    {
        using Fixture f = new(CoreStabilizer);
        double capacitor = f.Capacitor;
        Assert.Throws<InvalidDataException>(() =>
            f.Tackle.ActivateOverloaded(f.Pilot, f.Module, DogmaEffect.WarpCoreStabilizer, null, new HashSet<long>(), f.Now)
        );
        Assert.Equal(capacitor, f.Capacitor, 8);
        using WorldDbContext world = new(f.Fit.Db.Path);
        Assert.Empty(world.Set<ModuleCooldown>());
        Assert.Empty(world.Set<ActiveFittedModule>());
        Assert.Empty(world.Set<SavedShipHeat>());
    }

    [Theory]
    [InlineData(Web)]
    [InlineData(Scrambler)]
    public void NpcTackleUsesItsPaidOverloadRangeAndCancelsHeatWhenTheNpcBecomesUnavailable(int type)
    {
        const int NpcSystem = 30000144;
        using Fixture f = new(type, npc: true);
        var npc = f.Fit.Db.Services.GetRequiredService<INpcEncounters>().List(NpcSystem, f.Now)[0];
        f.Fit.Db.Store.SetPosition(f.Ship, npc.Position + new SpacePosition(1000, 0, 0), npc.SystemId);
        var paid = f.Tackle.ActivateOverloaded(
            f.Pilot,
            f.Module,
            f.Effect,
            new TackleTarget(npc.Id, TackleTargetKind.Npc),
            new HashSet<long> { npc.Id },
            f.Now
        );
        Assert.Equal(f.Expected(DogmaAttributeId.MaxRange), paid.Module.Range, 8);
        var source = Assert.Single(f.Read().Cycles);
        f.At(source.StartedAt + (source.CompletesAt - source.StartedAt) / 2);
        double heat = f.Read().Heat(ModuleRack.Medium);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            world.Set<WorldNpc>().Find(npc.Id)!.RetiredAt = f.Now;
            world.SaveChanges();
        }
        f.Tackle.Sweep(f.Now);
        Assert.Empty(f.Read().Cycles);
        Assert.Equal(heat, f.Read().Heat(ModuleRack.Medium), 8);
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Empty(saved.Set<WorldTackle>());
        Assert.Equal(source.CompletesAt, Assert.Single(saved.Set<ModuleCooldown>()).ReadyAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARepairOnAnotherModuleRejectsMobilityHeatWithoutChangingEitherPayment(bool propulsion)
    {
        using Fixture f = new(propulsion ? Afterburner : Web, propulsion ? Scrambler : Microwarpdrive);
        f.Fit.Grant(NaniteRepairRules.PasteType, 50, cargo: true);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            world.Add(new SavedModuleCondition { ModuleId = f.Second, Damage = 20 });
            world.SaveChanges();
        }
        var repairs = f.Fit.Db.Services.GetRequiredService<IPilotNaniteRepair>();
        var repair = Assert.Single(repairs.Start(f.Pilot, f.Ship, [f.Second], f.Now).Started).Schedule!;
        double capacitor = f.Capacitor;
        Assert.Throws<InvalidDataException>(() =>
        {
            if (propulsion)
            {
                f.StartPropulsion();
            }
            else
            {
                f.StartTackle();
            }
        });
        Assert.Equal(capacitor, f.Capacitor, 8);
        Assert.Equal(repair.Key, Assert.Single(repairs.Read(f.Pilot, f.Ship, f.Now)).Key);
        Assert.Empty(f.Tackle.Incoming(f.Target));
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Empty(saved.Set<SavedShipHeat>());
        Assert.Empty(saved.Set<ModuleCooldown>());
        Assert.Empty(saved.Set<ActiveFittedModule>());
        Assert.Empty(saved.Set<WorldTackle>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingThermodynamicsCannotPayForMobilityOverload(bool propulsion)
    {
        using Fixture f = new(propulsion ? Afterburner : Web);
        f.Fit.Train(OverloadRules.ThermodynamicsSkill, 0);
        double capacitor = f.Capacitor;
        Assert.Throws<InvalidDataException>(() =>
        {
            if (propulsion)
            {
                f.StartPropulsion();
            }
            else
            {
                f.StartTackle();
            }
        });
        Assert.Equal(capacitor, f.Capacitor, 8);
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Empty(saved.Set<SavedShipHeat>());
        Assert.Empty(saved.Set<ModuleCooldown>());
        Assert.Empty(saved.Set<ActiveFittedModule>());
        Assert.Empty(saved.Set<WorldTackle>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmMobilityHeatAndIncomingReadsUseNoSqlOrFittingRecalculation(bool propulsion)
    {
        using Fixture f = new(propulsion ? Afterburner : Web);
        if (propulsion)
        {
            f.StartPropulsion();
        }
        else
        {
            f.StartTackle();
        }
        var source = Assert.Single(f.Read().Cycles);
        var incoming = f.Tackle.Incoming(f.Target);
        using var metrics = SessionPerformance.Begin("mobility heat reads", _ => { }, force: true)!;
        for (int frame = 0; frame < 100; frame++)
        {
            f.At(f.Now + TimeSpan.TicksPerMillisecond);
            Assert.Equal(source.Key, Assert.Single(f.Read().Cycles).Key);
            Assert.Equal(incoming, f.Tackle.Incoming(f.Target));
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetachedMobilityCheckpointKeepsItsPaidHeatAndEffectBeforeALaterCancellation(bool propulsion)
    {
        using Fixture f = new(propulsion ? Afterburner : Web);
        if (propulsion)
        {
            f.StartPropulsion();
        }
        else
        {
            f.StartTackle();
        }
        var source = Assert.Single(f.Read().Cycles);
        f.At(source.StartedAt + (source.CompletesAt - source.StartedAt) / 2);
        using var snapshot = Assert.IsType<WorldSnapshot>(f.Live.CaptureSnapshot());
        if (propulsion)
        {
            f.Propulsion.Stop(f.Pilot, f.Ship, f.Module);
        }
        else
        {
            f.Tackle.Cancel(f.TargetPilot, f.Target);
        }
        Assert.Empty(f.Read().Cycles);
        snapshot.Project();
        using (WorldDbContext captured = new(snapshot.Database))
        {
            var heat = ShipHeatPersistence.Decode(Assert.Single(captured.Set<SavedShipHeat>()).StateJson);
            Assert.Equal(source.Key, Assert.Single(heat.Cycles).Key);
            Assert.Equal(source.CompletesAt, Assert.Single(captured.Set<ModuleCooldown>()).ReadyAt);
            if (propulsion)
            {
                Assert.Equal(f.Module, Assert.Single(captured.Set<ActiveFittedModule>()).ModuleId);
            }
            else
            {
                Assert.Equal(f.Module, Assert.Single(captured.Set<WorldTackle>()).ModuleId);
            }
        }
        string path = Path.Combine(Path.GetDirectoryName(f.Fit.Db.Path)!, "mobility-heat-checkpoint.sqlite");
        LiveWorld.PersistSnapshot(snapshot.Database, path);
        f.At(source.CompletesAt);
        WorldInitialization.Initialize(new(path), f.Fit.Clock);
        using WorldDbContext reopened = new(path);
        Assert.Empty(reopened.Set<ActiveFittedModule>());
        Assert.Empty(reopened.Set<WorldTackle>());
        Assert.Equal(source.CompletesAt, Assert.Single(reopened.Set<ModuleCooldown>()).ReadyAt);
        var heatStore = new PilotShipHeatStore(new(path));
        Assert.Empty(heatStore.Read(f.Pilot, f.Ship, f.Now).Cycles);
    }

    [Theory]
    [InlineData(Web)]
    [InlineData(Scrambler)]
    [InlineData(Disruptor)]
    public void OnlyThePaidOverloadedCycleCanReachTheTargetBeyondNormalTackleRange(int type)
    {
        using Fixture f = new(type);
        double normal = f.Normal()[DogmaAttributeId.MaxRange];
        double overloaded = f.Expected(DogmaAttributeId.MaxRange);
        Assert.True(overloaded > normal);
        double radius;
        using (CatalogDbContext catalog = new(f.Fit.CatalogPath))
        {
            radius = catalog.FittingType(Ibis)!.Radius ?? 0;
        }
        f.Fit.Db.Store.SetPosition(f.Target, new SpacePosition((normal + overloaded) / 2 + 2 * radius, 0, 0));
        double capacitor = f.Capacitor;
        Assert.Throws<InvalidDataException>(() =>
            f.Tackle.Activate(
                f.Pilot,
                f.Module,
                f.Effect,
                new TackleTarget(f.Target, TackleTargetKind.Ship),
                new HashSet<long> { f.Target },
                f.Now
            )
        );
        Assert.Equal(capacitor, f.Capacitor, 8);
        Assert.Empty(f.Tackle.Incoming(f.Target));
        Assert.Equal(overloaded, f.StartTackle().Module.Range, 8);
        Assert.Single(f.Read().Cycles);
        Assert.Single(f.Tackle.Incoming(f.Target));
    }

    [Fact]
    public void AStalePaidKeyCannotRemoveTheCurrentMobilitySource()
    {
        using Fixture f = new(Web);
        f.StartTackle();
        var before = f.Read();
        var key = Assert.Single(before.Cycles).Key;
        using (WorldDbContext world = new(f.Fit.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            ShipHeatPersistence.CancelSource(world, f.Pilot, f.Ship, key with { CompletesAt = key.CompletesAt + 1 }, f.Now);
            world.SaveChanges();
            transaction.Commit();
        }
        Assert.True(HeatCycleRules.SameState(before, f.Read()));
        Assert.Equal(f.Module, Assert.Single(f.Tackle.Incoming(f.Target)).Module.ItemId);
    }
}
