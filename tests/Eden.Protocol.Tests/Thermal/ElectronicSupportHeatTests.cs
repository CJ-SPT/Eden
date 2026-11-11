using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Data.World.Skills;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Repair;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FittingFixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Thermal;

public sealed partial class ElectronicSupportHeatTests
{
    private const int Ibis = 601;
    private const int System = 30000142;
    private const int Jammer = 1948;
    private const int TrackingLink = 2103;
    private const int SensorLink = 1963;
    private const int Transfer = 529;
    private const int Nosferatu = 530;
    private const int Neutralizer = 533;
    private const double Budget = 1000000;
    private const double TargetingRange = 100000;

    private enum Family
    {
        Ewar,
        Assistance,
        Capacitor,
    }

    private sealed class Roll : INpcEwarRoll
    {
        public double Next() => 0;
    }

    private sealed class EnergyEvents : ICapacitorEvents
    {
        internal List<CapacitorChanged> Changes { get; } = [];

        public void Publish(CapacitorChanged change) => Changes.Add(change);
    }

    private sealed class Fixture : IDisposable
    {
        internal FittingFixture Fit { get; } = new();
        internal long Pilot { get; }
        internal long Ship { get; }
        internal long Target { get; }
        internal long TargetPilot { get; }
        internal long Module { get; }
        internal long TargetGun { get; }
        internal int Type { get; }
        internal int Group { get; }
        internal Family Kind { get; }
        internal DogmaEffect Effect { get; }
        internal EnergyEvents Events { get; } = new();
        internal LiveWorld Live { get; }
        internal long Now => Fit.Clock.Now;
        internal PlayerEwarStore Ewar => new(new(Fit.Db.Path), new Roll(), Fit.Clock);
        internal PilotRemoteAssistanceStore Assistance => new(new(Fit.Db.Path), Fit.Clock);
        internal CapacitorWarfareStore Energy =>
            new(new(Fit.Db.Path), Events, Fit.Db.Services.GetRequiredService<NpcProfileCatalog>(), Fit.Clock);
        internal IPilotShipHeat Heat => Fit.Db.Services.GetRequiredService<IPilotShipHeat>();
        internal double SourceCharge => Resources.Capacitor(Pilot, Now);
        internal double TargetCharge => Resources.Capacitor(TargetPilot, Now);
        private IPilotShipResources Resources => Fit.Db.Services.GetRequiredService<IPilotShipResources>();

        internal Fixture(int type, int script = 0, bool targetGun = false, bool npc = false)
        {
            Type = type;
            PassiveCatalogFixture.LoadPlayerEwar(Fit.CatalogPath);
            PassiveCatalogFixture.LoadRemoteAssistance(Fit.CatalogPath);
            PassiveCatalogFixture.LoadCapacitorModules(Fit.CatalogPath);
            // Keep complete native material/price inputs after older tier data.
            PassiveCatalogFixture.LoadOverload(Fit.CatalogPath);
            if (npc)
            {
                Eden.Protocol.Tests.Combat.NpcEncounterTests.Prepare(Fit.Db);
            }
            using (CatalogDbContext catalog = new(Fit.CatalogPath, writable: true))
            {
                foreach (
                    var attribute in new[] { DogmaAttributeId.CpuOutput, DogmaAttributeId.PowerOutput, DogmaAttributeId.CapacitorCapacity }
                )
                {
                    catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({Ibis},{(int)attribute},{Budget})");
                }
                catalog.Database.ExecuteSql(
                    $"INSERT OR REPLACE INTO TypeAttribute VALUES ({Ibis},{(int)DogmaAttributeId.MaxTargetRange},{TargetingRange})"
                );
                // Explicit fixture budgets establish payment integration, not
                // real corvette fits or admission of additional hull cohorts.
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({Ibis},{(int)DogmaAttributeId.MediumSlots},8)");
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({Ibis},{(int)DogmaAttributeId.HighSlots},8)");
                catalog.Database.ExecuteSql(
                    $"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems',{System.ToString()},'{{\"securityStatus\":0}}')"
                );
                Group = catalog.RequireItem(type).GroupId;
                Kind =
                    Group
                        is CapacitorWarfareRules.TransferGroup
                            or CapacitorWarfareRules.NeutralizerGroup
                            or CapacitorWarfareRules.NosferatuGroup
                        ? Family.Capacitor
                    : Group is RemoteAssistanceRules.TrackingGroup or RemoteAssistanceRules.SensorGroup ? Family.Assistance
                    : Family.Ewar;
                Effect = FittingCatalog.DefaultEffect(catalog, type)!.Value;
            }
            Pilot = Fit.PilotId;
            Ship = Fit.ShipId;
            var target = Fit.Db.Store.FindPilot(1000002)!;
            Target = target.ShipId;
            TargetPilot = target.PilotId;
            Fit.Train(OverloadRules.ThermodynamicsSkill, 3);
            Module = Fit.Fit(type);
            if (targetGun)
            {
                const int Gun = 561;
                TargetGun = Fit.Fit(Gun);
                using WorldDbContext world = new(Fit.Db.Path);
                var item = world.Set<OwnedItem>().Find(TargetGun)!;
                item.OwnerId = TargetPilot;
                item.LocationId = Target;
                foreach (var skill in world.Set<PilotSkill>().Where(s => s.PilotId == Pilot).ToArray())
                {
                    var copy =
                        world.Set<PilotSkill>().Find(TargetPilot, skill.TypeId)
                        ?? world.Add(new PilotSkill { PilotId = TargetPilot, TypeId = skill.TypeId }).Entity;
                    copy.Points = skill.Points;
                }
                world.SaveChanges();
            }
            if (script != 0)
            {
                long item = Fit.Grant(script);
                Fit.Db.Services.GetRequiredService<IPilotAmmunition>().Load(Pilot, Ship, [Module], [item], Fit.StationId, Now);
                Fit.Clock.Milliseconds += 60000;
            }
            Fit.Db.Store.SetPosition(Ship, SpacePosition.Zero);
            Fit.Db.Store.SetPosition(Target, new SpacePosition(1000, 0, 0));
            using (WorldDbContext world = new(Fit.Db.Path))
            {
                double capacity = Fit.Fitting.ShipAttributes(Pilot)[DogmaAttributeId.CapacitorCapacity];
                double sourceFraction = Group == CapacitorWarfareRules.NosferatuGroup ? .2 : .8;
                foreach (var entry in new[] { (ShipId: Ship, Fraction: sourceFraction), (ShipId: Target, Fraction: 1 - sourceFraction) })
                {
                    var energy = world.Set<ShipEnergy>().Find(entry.ShipId) ?? world.Add(new ShipEnergy { ShipId = entry.ShipId }).Entity;
                    energy.Charge = capacity * entry.Fraction;
                    energy.UpdatedAt = Now;
                }
                world.SaveChanges();
            }
            Live = new(new(Fit.Db.Path), Fit.Clock, _ => { });
        }

        internal IReadOnlyDictionary<DogmaAttributeId, double> Normal() =>
            Fit.Fitting.ModuleAttributes(Pilot, Fit.Inventory.List(Pilot, Ship).Single(i => i.Id == Module));

        internal IReadOnlyDictionary<DogmaAttributeId, double> Expected()
        {
            var values = Normal().ToDictionary();
            using CatalogDbContext catalog = new(Fit.CatalogPath);
            foreach (var bonus in OverloadCatalog.Definition(catalog, Type)!.Bonuses)
            {
                double basis = values.GetValueOrDefault(
                    bonus.Attribute,
                    catalog.Read(CatalogData.Client.Dogmaattributes, (long)bonus.Attribute)?.DefaultValue ?? 0
                );
                values[bonus.Attribute] = basis * (1 + values[bonus.SourceAttribute] / 100);
            }
            return values;
        }

        internal ElectronicWarfareActivation StartEwar() =>
            Ewar.ActivateOverloaded(
                Pilot,
                Module,
                Effect,
                new TackleTarget(Target, TackleTargetKind.Ship),
                new HashSet<long> { Target },
                Now
            );

        internal RemoteAssistanceActivation StartAssistance() =>
            Assistance.ActivateOverloaded(Pilot, Module, Effect, Target, new HashSet<long> { Target }, Now);

        internal CapacitorActivation StartEnergy() =>
            Energy.ActivateOverloaded(Pilot, Module, Effect, Target, new HashSet<long> { Target }, Now);

        internal void Pay(bool overloaded = true)
        {
            switch (Kind)
            {
                case Family.Ewar:
                    if (overloaded)
                    {
                        StartEwar();
                    }
                    else
                    {
                        Ewar.Activate(
                            Pilot,
                            Module,
                            Effect,
                            new TackleTarget(Target, TackleTargetKind.Ship),
                            new HashSet<long> { Target },
                            Now
                        );
                    }
                    break;
                case Family.Assistance:
                    if (overloaded)
                    {
                        StartAssistance();
                    }
                    else
                    {
                        Assistance.Activate(Pilot, Module, Effect, Target, new HashSet<long> { Target }, Now);
                    }
                    break;
                case Family.Capacitor:
                    if (overloaded)
                    {
                        StartEnergy();
                    }
                    else
                    {
                        Energy.Activate(Pilot, Module, Effect, Target, new HashSet<long> { Target }, Now);
                    }
                    break;
            }
        }

        internal void Stop()
        {
            switch (Kind)
            {
                case Family.Ewar:
                    Ewar.Stop(Pilot, Ship, Module);
                    break;
                case Family.Assistance:
                    Assistance.Stop(Pilot, Ship, Module);
                    break;
                case Family.Capacitor:
                    Energy.Stop(Pilot, Ship, Module);
                    break;
            }
        }

        internal ShipHeatSnapshot Read() => Heat.Read(Pilot, Ship, Now);

        internal void At(long at) => Fit.Clock.AdvanceWallTime(TimeSpan.FromTicks(at - Now));

        public void Dispose()
        {
            Live.Dispose();
            Fit.Dispose();
        }
    }

    [Theory]
    [InlineData(Jammer, 0)]
    [InlineData(2563, 0)]
    [InlineData(1968, 0)]
    [InlineData(1969, 0)]
    [InlineData(2108, 0)]
    [InlineData(2109, 0)]
    [InlineData(37543, 0)]
    [InlineData(37546, 0)]
    [InlineData(12709, 0)]
    [InlineData(19806, 0)]
    [InlineData(2108, 29005)]
    [InlineData(2108, 29007)]
    [InlineData(1968, 29015)]
    [InlineData(37543, 40335)]
    public void EwarCommitsTheSelectedNativeOverloadProfileWithItsPaidContactAndHeat(int type, int script)
    {
        using Fixture f = new(type, script);
        var normal = f.Normal();
        var expected = PlayerEwarRules.Define(f.Module, type, f.Effect, f.Expected());
        double capacitor = f.SourceCharge;
        var paid = f.StartEwar();
        Assert.Equal(expected, paid.Module);
        Assert.Equal(capacitor - expected.CapacitorNeed, paid.Capacitor, 8);
        var contact = Assert.Single(paid.Contacts);
        var incoming = Assert.Single(f.Ewar.Incoming(f.Target));
        Assert.Equal(contact, incoming);
        var heat = Assert.Single(f.Read().Cycles);
        Assert.Equal(contact.StartedAt, heat.StartedAt);
        Assert.Equal(contact.ExpiresAt, heat.CompletesAt);
        Assert.Equal(ModuleRack.Medium, heat.Rack);
        Assert.Equal(normal[DogmaAttributeId.Duration], f.Normal()[DogmaAttributeId.Duration], 8);
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Equal(f.Module, Assert.Single(saved.Set<WorldPlayerEwar>()).ModuleId);
        Assert.Equal(heat.CompletesAt, Assert.Single(saved.Set<ModuleCooldown>()).ReadyAt);
    }

    [Theory]
    [InlineData(TrackingLink, 0)]
    [InlineData(2104, 0)]
    [InlineData(SensorLink, 0)]
    [InlineData(1964, 0)]
    [InlineData(TrackingLink, 28999)]
    [InlineData(TrackingLink, 29001)]
    [InlineData(SensorLink, 29009)]
    [InlineData(SensorLink, 29011)]
    [InlineData(SensorLink, 41155)]
    public void RemoteAssistancePersistsTheOverloadedScriptedBonusesForItsRecipient(int type, int script)
    {
        using Fixture f = new(type, script);
        var expected = RemoteAssistanceRules.Define(f.Module, type, f.Effect, f.Expected());
        double capacitor = f.SourceCharge;
        var before = f.Fit.Fitting.ShipAttributes(f.TargetPilot);
        var paid = f.StartAssistance();
        Assert.Equal(expected, paid.Contact.Module);
        Assert.Equal(capacitor - expected.Cycle.CapacitorNeed, paid.Capacitor, 8);
        Assert.Equal(paid.Contact, Assert.Single(f.Assistance.Incoming(f.Target)));
        var heat = Assert.Single(f.Read().Cycles);
        Assert.Equal(paid.Contact.StartedAt, heat.StartedAt);
        Assert.Equal(paid.Contact.ExpiresAt, heat.CompletesAt);
        var after = f.Fit.Fitting.ShipAttributes(f.TargetPilot);
        if (expected.Bonuses.TargetRange > 0)
        {
            Assert.Equal(
                before[DogmaAttributeId.MaxTargetRange] * (1 + expected.Bonuses.TargetRange / 100),
                after[DogmaAttributeId.MaxTargetRange],
                6
            );
        }
        if (expected.Bonuses.ScanResolution > 0)
        {
            Assert.Equal(
                before[DogmaAttributeId.ScanResolution] * (1 + expected.Bonuses.ScanResolution / 100),
                after[DogmaAttributeId.ScanResolution],
                6
            );
        }
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Equal(f.Module, Assert.Single(saved.Set<WorldRemoteAssistance>()).ModuleId);
        Assert.Equal(heat.CompletesAt, Assert.Single(saved.Set<ModuleCooldown>()).ReadyAt);
    }

    [Theory]
    [InlineData(Transfer)]
    [InlineData(1190)]
    [InlineData(Neutralizer)]
    [InlineData(13003)]
    [InlineData(Nosferatu)]
    [InlineData(13001)]
    public void CapacitorOverloadPaysTheModifiedCycleAndUpdatesBothPoolsOnlyOnce(int type)
    {
        using Fixture f = new(type);
        var expected = CapacitorWarfareRules.Define(f.Module, type, f.Effect, f.Expected());
        double source = f.SourceCharge;
        double target = f.TargetCharge;
        var paid = f.StartEnergy();
        Assert.Equal(expected, paid.Cycle.Module);
        Assert.Equal(expected.Amount, paid.Applied, 8);
        Assert.Equal(
            source - expected.CapacitorNeed + (expected.Kind == CapacitorModuleKind.Nosferatu ? paid.Applied : 0),
            paid.Capacitor,
            8
        );
        Assert.Equal(target + (expected.Kind == CapacitorModuleKind.Transfer ? paid.Applied : -paid.Applied), paid.TargetCapacitor, 8);
        Assert.Equal(paid.Capacitor, f.SourceCharge, 8);
        Assert.Equal(paid.TargetCapacitor, f.TargetCharge, 8);
        Assert.Equal(2, f.Events.Changes.Count);
        var heat = Assert.Single(f.Read().Cycles);
        Assert.Equal(paid.Cycle.StartedAt, heat.StartedAt);
        Assert.Equal(paid.Cycle.ExpiresAt, heat.CompletesAt);
        Assert.Equal(ModuleRack.High, heat.Rack);
        Assert.Throws<InvalidDataException>(() => f.StartEnergy());
        Assert.Equal(paid.Capacitor, f.SourceCharge, 8);
        Assert.Equal(paid.TargetCapacitor, f.TargetCharge, 8);
        Assert.Equal(2, f.Events.Changes.Count);
    }

    [Fact]
    public void OverloadedTrackingScriptChangesTheRecipientsActualWeaponAndStopRestoresIt()
    {
        const int TrackingScript = 29001;
        using Fixture f = new(TrackingLink, TrackingScript, targetGun: true);
        var item = f.Fit.Inventory.List(f.TargetPilot, f.Target).Single(i => i.Id == f.TargetGun);
        var before = f.Fit.Fitting.ModuleAttributes(f.TargetPilot, item);
        var paid = f.StartAssistance();
        var after = f.Fit.Fitting.ModuleAttributes(f.TargetPilot, item);
        double bonus = paid.Contact.Module.Bonuses.Tracking;
        Assert.True(bonus > 0);
        Assert.Equal(before[DogmaAttributeId.TrackingSpeed] * (1 + bonus / 100), after[DogmaAttributeId.TrackingSpeed], 8);
        f.Stop();
        Assert.Equal(
            before[DogmaAttributeId.TrackingSpeed],
            f.Fit.Fitting.ModuleAttributes(f.TargetPilot, item)[DogmaAttributeId.TrackingSpeed],
            8
        );
    }

    [Theory]
    [InlineData(Jammer)]
    [InlineData(TrackingLink)]
    [InlineData(Neutralizer)]
    public void StopRemovesThePaidHeatAndActivationWithoutRefundingItsCooldownOrCapacitor(int type)
    {
        using Fixture f = new(type);
        f.Pay();
        var source = Assert.Single(f.Read().Cycles);
        f.At(source.StartedAt + (source.CompletesAt - source.StartedAt) / 2);
        var before = f.Read();
        double capacitor = f.SourceCharge;
        f.Stop();
        Assert.Equal(capacitor, f.SourceCharge, 8);
        Assert.Empty(f.Read().Cycles);
        Assert.Equal(before.Heat(source.Rack), f.Read().Heat(source.Rack), 8);
        Assert.Empty(f.Ewar.Incoming(f.Target));
        Assert.Empty(f.Assistance.Incoming(f.Target));
        using WorldDbContext world = new(f.Fit.Db.Path);
        Assert.Empty(world.Set<ActiveFittedModule>());
        Assert.Empty(world.Set<WorldPlayerEwar>());
        Assert.Empty(world.Set<WorldRemoteAssistance>());
        Assert.Equal(source.CompletesAt, Assert.Single(world.Set<ModuleCooldown>()).ReadyAt);
    }

    [Theory]
    [InlineData(Jammer, false)]
    [InlineData(Jammer, true)]
    [InlineData(TrackingLink, false)]
    [InlineData(TrackingLink, true)]
    public void SourceAndRecipientTravelRemoveBothTheForeignMarkerAndItsHeat(int type, bool recipient)
    {
        using Fixture f = new(type);
        f.Pay();
        var cycle = Assert.Single(f.Read().Cycles);
        f.At(cycle.StartedAt + (cycle.CompletesAt - cycle.StartedAt) / 2);
        double heat = f.Read().Heat(cycle.Rack);
        long pilot = recipient ? f.TargetPilot : f.Pilot;
        long ship = recipient ? f.Target : f.Ship;
        if (f.Kind == Family.Ewar)
        {
            f.Ewar.Cancel(pilot, ship, incoming: recipient);
        }
        else
        {
            f.Assistance.Cancel(pilot, ship);
        }
        Assert.Empty(f.Read().Cycles);
        Assert.Equal(heat, f.Read().Heat(cycle.Rack), 8);
        Assert.Empty(f.Ewar.Incoming(f.Target));
        Assert.Empty(f.Assistance.Incoming(f.Target));
        using WorldDbContext world = new(f.Fit.Db.Path);
        Assert.Empty(world.Set<ActiveFittedModule>());
        Assert.Empty(world.Set<WorldPlayerEwar>());
        Assert.Empty(world.Set<WorldRemoteAssistance>());
    }

    [Theory]
    [InlineData(Jammer)]
    [InlineData(TrackingLink)]
    [InlineData(Neutralizer)]
    public void HeatInsertionFailureRollsBackBothPoolsEffectsCooldownsAndPublishedEvents(int type)
    {
        using Fixture f = new(type);
        if (f.Kind is Family.Capacitor or Family.Ewar)
        {
            // Preserve rejection coverage at the offline SQL adapter boundary.
            f.Live.Dispose();
        }
        double source = f.SourceCharge;
        double target = f.TargetCharge;
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectSupportHeat BEFORE INSERT ON WorldShipHeat BEGIN SELECT RAISE(ABORT,'test heat failure'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() => f.Pay());
        Assert.Equal(source, f.SourceCharge, 8);
        Assert.Equal(target, f.TargetCharge, 8);
        Assert.Empty(f.Events.Changes);
        Assert.Empty(f.Ewar.Incoming(f.Target));
        Assert.Empty(f.Assistance.Incoming(f.Target));
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            Assert.Empty(world.Set<ActiveFittedModule>());
            Assert.Empty(world.Set<WorldPlayerEwar>());
            Assert.Empty(world.Set<WorldRemoteAssistance>());
            Assert.Empty(world.Set<ModuleCooldown>());
            Assert.Empty(world.Set<SavedShipHeat>());
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectSupportHeat");
        }
        Assert.True(f.Read().Idle);
    }

    [Theory]
    [InlineData(Jammer)]
    [InlineData(TrackingLink)]
    [InlineData(Neutralizer)]
    public void FailedStopKeepsTheStoredEffectAndHeatFromTheSamePaidGeneration(int type)
    {
        using Fixture f = new(type);
        if (f.Kind is Family.Capacitor or Family.Ewar)
        {
            // Preserve rejection coverage at the offline SQL adapter boundary.
            f.Live.Dispose();
        }
        f.Pay();
        var cycle = Assert.Single(f.Read().Cycles);
        f.At(cycle.StartedAt + (cycle.CompletesAt - cycle.StartedAt) / 2);
        var before = f.Read();
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectSupportStop BEFORE DELETE ON WorldActiveModule BEGIN SELECT RAISE(ABORT,'test stop failure'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() => f.Stop());
        Assert.True(HeatCycleRules.SameState(before, f.Read()));
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Equal(f.Module, Assert.Single(saved.Set<ActiveFittedModule>()).ModuleId);
        if (f.Kind == Family.Ewar)
        {
            Assert.Single(saved.Set<WorldPlayerEwar>());
        }
        else if (f.Kind == Family.Assistance)
        {
            Assert.Single(saved.Set<WorldRemoteAssistance>());
        }
        saved.Database.ExecuteSqlRaw("DROP TRIGGER RejectSupportStop");
    }

    [Theory]
    [InlineData(Jammer)]
    [InlineData(TrackingLink)]
    [InlineData(Neutralizer)]
    public void AForeignStopCannotRemoveTheEffectOrItsPaidHeat(int type)
    {
        using Fixture f = new(type);
        f.Pay();
        var before = f.Read();
        switch (f.Kind)
        {
            case Family.Ewar:
                f.Ewar.Stop(f.TargetPilot, f.Ship, f.Module);
                break;
            case Family.Assistance:
                f.Assistance.Stop(f.TargetPilot, f.Ship, f.Module);
                break;
            case Family.Capacitor:
                f.Energy.Stop(f.TargetPilot, f.Ship, f.Module);
                break;
        }
        Assert.True(HeatCycleRules.SameState(before, f.Read()));
        using WorldDbContext world = new(f.Fit.Db.Path);
        Assert.Equal(f.Module, Assert.Single(world.Set<ActiveFittedModule>()).ModuleId);
    }

    [Theory]
    [InlineData(Jammer)]
    [InlineData(TrackingLink)]
    [InlineData(Neutralizer)]
    public void MissingThermodynamicsRejectsTheOverloadedCycleWithoutPayment(int type)
    {
        using Fixture f = new(type);
        f.Fit.Train(OverloadRules.ThermodynamicsSkill, 0);
        double source = f.SourceCharge;
        double target = f.TargetCharge;
        Assert.Throws<InvalidDataException>(() => f.Pay());
        Assert.Equal(source, f.SourceCharge, 8);
        Assert.Equal(target, f.TargetCharge, 8);
        using WorldDbContext world = new(f.Fit.Db.Path);
        Assert.Empty(world.Set<ActiveFittedModule>());
        Assert.Empty(world.Set<ModuleCooldown>());
        Assert.Empty(world.Set<SavedShipHeat>());
    }

    [Theory]
    [InlineData("targetDock")]
    [InlineData("targetSystem")]
    [InlineData("sourceBurnout")]
    public void PhysicalAssistanceCleanupStopsHeatAtCommitEvenWhenTheNextReadArrivesLate(string change)
    {
        using Fixture f = new(TrackingLink);
        f.Pay();
        var cycle = Assert.Single(f.Read().Cycles);
        f.At(cycle.StartedAt + (cycle.CompletesAt - cycle.StartedAt) / 3);
        var before = f.Read();
        if (change == "targetDock")
        {
            f.Fit.Db.Store.SetPosition(f.Target, null);
        }
        else if (change == "targetSystem")
        {
            f.Fit.Db.Store.SetPosition(f.Target, SpacePosition.Zero, NpcEncounterDefinitions.PerimeterSystem);
        }
        else
        {
            using WorldDbContext mutation = new(f.Fit.Db.Path);
            using var transaction = mutation.Database.BeginTransaction();
            using CatalogDbContext catalog = new(f.Fit.CatalogPath);
            var condition = ModuleConditionPersistence.Read(mutation, catalog, mutation.Set<OwnedItem>().Find(f.Module)!);
            Assert.True(
                ModuleConditionPersistence.ApplyDamage(mutation, catalog, f.Module, condition.HitPoints, f.Fit.Clock.Now).Current.BurnedOut
            );
            mutation.SaveChanges();
            transaction.Commit();
        }
        f.At(before.UpdatedAt + (cycle.CompletesAt - cycle.StartedAt) / 3);
        var after = f.Read();
        var rack = before.Racks.Single(r => r.Profile.Rack == cycle.Rack);
        double expected = HeatRules.Calculate(
            rack.Profile,
            rack.Heat,
            0,
            (f.Now - before.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond
        );
        Assert.Empty(after.Cycles);
        Assert.Equal(expected, after.Heat(cycle.Rack), 8);
        Assert.Empty(f.Assistance.Incoming(f.Target));
        using WorldDbContext world = new(f.Fit.Db.Path);
        Assert.Empty(world.Set<ActiveFittedModule>());
        Assert.Empty(world.Set<WorldRemoteAssistance>());
        Assert.Equal(cycle.CompletesAt, Assert.Single(world.Set<ModuleCooldown>()).ReadyAt);
    }

    [Theory]
    [InlineData(Jammer)]
    [InlineData(2108)]
    public void NpcEwarRetirementCleanupRemovesThePaidHeatAndItsActiveMarker(int type)
    {
        using Fixture f = new(type, npc: true);
        var npc = f.Fit.Db.Services.GetRequiredService<INpcEncounters>().List(NpcEncounterDefinitions.PerimeterSystem, f.Now)[0];
        f.Fit.Db.Store.SetPosition(f.Ship, npc.Position + new SpacePosition(1000, 0, 0), npc.SystemId);
        f.Ewar.ActivateOverloaded(
            f.Pilot,
            f.Module,
            f.Effect,
            new TackleTarget(npc.Id, TackleTargetKind.Npc),
            new HashSet<long> { npc.Id },
            f.Now
        );
        var cycle = Assert.Single(f.Read().Cycles);
        f.At(cycle.StartedAt + (cycle.CompletesAt - cycle.StartedAt) / 2);
        double heat = f.Read().Heat(cycle.Rack);
        using (WorldDbContext world = new(f.Fit.Db.Path))
        {
            world.Set<WorldNpc>().Find(npc.Id)!.RetiredAt = f.Now;
            world.SaveChanges();
        }
        f.Ewar.Sweep(f.Now);
        Assert.Empty(f.Read().Cycles);
        Assert.Equal(heat, f.Read().Heat(cycle.Rack), 8);
        using WorldDbContext saved = new(f.Fit.Db.Path);
        Assert.Empty(saved.Set<WorldPlayerEwar>());
        Assert.Empty(saved.Set<ActiveFittedModule>());
    }

    [Theory]
    [InlineData(Jammer)]
    [InlineData(TrackingLink)]
    [InlineData(Neutralizer)]
    public void ExactRepeatsSwitchBetweenNormalAndOverloadedCyclesWithoutLeakingTheBonus(int type)
    {
        using Fixture f = new(type);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            bool overloaded = cycle != 1;
            double duration = (overloaded ? f.Expected() : f.Normal())[DogmaAttributeId.Duration];
            f.Pay(overloaded);
            long end;
            using (WorldDbContext world = new(f.Fit.Db.Path))
            {
                end = Assert.Single(world.Set<ModuleCooldown>()).ReadyAt;
                Assert.Equal(f.Now + (long)Math.Ceiling(duration * TimeSpan.TicksPerMillisecond), end);
                Assert.Single(world.Set<ActiveFittedModule>());
            }
            var state = f.Read();
            if (overloaded)
            {
                Assert.Equal(f.Now, Assert.Single(state.Cycles).StartedAt);
                Assert.Equal(end, Assert.Single(state.Cycles).CompletesAt);
            }
            else
            {
                Assert.Empty(state.Cycles);
            }
            f.At(end);
        }
        Assert.Empty(f.Read().Cycles);
        f.Stop();
    }

    [Theory]
    [InlineData(Jammer)]
    [InlineData(TrackingLink)]
    [InlineData(Neutralizer)]
    public void WarmHeatAndRecipientSnapshotsNeedNoDatabaseOrFittingWork(int type)
    {
        using Fixture f = new(type);
        f.Pay();
        var cycle = Assert.Single(f.Read().Cycles);
        var ewar = f.Ewar.Incoming(f.Target);
        var assistance = f.Assistance.Incoming(f.Target);
        using var metrics = SessionPerformance.Begin("support heat frames", _ => { }, force: true)!;
        for (int frame = 0; frame < 100; frame++)
        {
            f.At(f.Now + TimeSpan.TicksPerMillisecond);
            Assert.Equal(cycle.Key, Assert.Single(f.Read().Cycles).Key);
            Assert.Equal(ewar, f.Ewar.Incoming(f.Target));
            Assert.Equal(assistance, f.Assistance.Incoming(f.Target));
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Theory]
    [InlineData(Jammer)]
    [InlineData(TrackingLink)]
    [InlineData(Neutralizer)]
    public void ReopenedSupportCheckpointRetainsItsPaidDeadlineWithoutRestoringConnectionEffects(int type)
    {
        using Fixture f = new(type);
        f.Pay();
        var cycle = Assert.Single(f.Read().Cycles);
        f.At(cycle.StartedAt + (cycle.CompletesAt - cycle.StartedAt) / 2);
        using var snapshot = Assert.IsType<WorldSnapshot>(f.Live.CaptureSnapshot());
        f.Stop();
        Assert.Empty(f.Read().Cycles);
        snapshot.Project();
        using (WorldDbContext captured = new(snapshot.Database))
        {
            var heat = ShipHeatPersistence.Decode(Assert.Single(captured.Set<SavedShipHeat>()).StateJson);
            Assert.Equal(cycle.Key, Assert.Single(heat.Cycles).Key);
            Assert.Equal(f.Module, Assert.Single(captured.Set<ActiveFittedModule>()).ModuleId);
            Assert.Equal(cycle.CompletesAt, Assert.Single(captured.Set<ModuleCooldown>()).ReadyAt);
        }
        string path = Path.Combine(Path.GetDirectoryName(f.Fit.Db.Path)!, "support-heat-checkpoint.sqlite");
        LiveWorld.PersistSnapshot(snapshot.Database, path);
        f.At(cycle.CompletesAt);
        WorldInitialization.Initialize(new(path), f.Fit.Clock);
        using (WorldDbContext reopened = new(path))
        {
            Assert.Empty(reopened.Set<ActiveFittedModule>());
            Assert.Empty(reopened.Set<WorldPlayerEwar>());
            Assert.Empty(reopened.Set<WorldRemoteAssistance>());
            Assert.Equal(cycle.CompletesAt, Assert.Single(reopened.Set<ModuleCooldown>()).ReadyAt);
        }
        Assert.Empty(new PilotShipHeatStore(new(path)).Read(f.Pilot, f.Ship, f.Now).Cycles);
    }
}
