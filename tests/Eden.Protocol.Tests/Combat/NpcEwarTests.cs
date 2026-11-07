using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class NpcEwarTests
{
    private sealed class Roll : INpcEwarRoll
    {
        public double Value { get; set; }
        public int Count { get; private set; }
        public Queue<double> Values { get; } = new();

        public double Next()
        {
            Count++;
            return Values.Count > 0 ? Values.Dequeue() : Value;
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal NpcCombatTests.Fixture Combat { get; }
        internal Roll Roll { get; } = new();
        internal NpcEwarStore Warfare { get; }
        internal IPilotFitting Fitting => Combat.Db.Services.GetRequiredService<IPilotFitting>();
        internal long Now => Combat.Clock.Now;

        internal Fixture(int typeId, bool resident = false)
        {
            Combat = new(liveWorld: resident, useTestClock: true);
            PassiveCatalogFixture.LoadNpcPopulation(new WorldDatabase(Combat.Db.Path).CatalogPath);
            using (CatalogDbContext catalog = new(new WorldDatabase(Combat.Db.Path).CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT OR IGNORE INTO DogmaAttribute VALUES (211,'scanGravimetricStrength');
                    INSERT OR REPLACE INTO TypeAttribute VALUES (601,211,10),(601,482,1000),(601,55,1e20);
                    """
                );
            }
            using (WorldDbContext world = new(Combat.Db.Path))
            {
                var npc = world.Set<WorldNpc>().Find(Combat.Npc.Id)!;
                npc.TypeId = typeId;
                world.SaveChanges();
            }
            Combat.Aggress();
            using (WorldDbContext world = new(Combat.Db.Path))
            {
                world.Set<NpcAggression>().Find(Combat.Npc.Id)!.ReadyAt = Now;
                world.SaveChanges();
            }
            Warfare = new(new(Combat.Db.Path), Roll, new(cache: false));
        }

        internal IReadOnlyList<NpcEwarContact> Advance() =>
            Warfare.Advance(NpcCombatTests.Fixture.PilotId, NpcCombatTests.Fixture.ShipId, Now);

        public void Dispose() => Combat.Dispose();
    }

    [Theory]
    [InlineData(2385, NpcEwarKind.Jamming, 20000, 28000, 1)]
    [InlineData(10015, NpcEwarKind.TargetPainting, 10000, 25000, 0.25)]
    [InlineData(10027, NpcEwarKind.TrackingDisruption, 30000, 50000, 0.05)]
    [InlineData(10284, NpcEwarKind.SensorDampening, 10000, 25000, 0.05)]
    [InlineData(11901, NpcEwarKind.EnergyNeutralization, 10000, 10000, 0.25)]
    [InlineData(11894, NpcEwarKind.Webifying, 5000, 10000, 0.075)]
    [InlineData(13032, NpcEwarKind.WarpDisruption, 10000, 20000, 0.25)]
    public void CatalogProfilesKeepDeclaredAbilitiesAndTheirOwnValues(
        int type,
        NpcEwarKind kind,
        double duration,
        double range,
        double chance
    )
    {
        using TestWorld db = new();
        PassiveCatalogFixture.LoadNpcPopulation(new WorldDatabase(db.Path).CatalogPath);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        var profile = new NpcProfileCatalog(cache: false).Read(catalog, type);
        var ability = Assert.Single(profile.ElectronicWarfare, e => e.Kind == kind);
        Assert.Equal(duration, ability.DurationMilliseconds);
        Assert.Equal(range, ability.Optimal);
        Assert.Equal(chance, ability.Chance);
        Assert.Contains(ability.Effect, FittingCatalog.Effects(catalog, type));
        switch (kind)
        {
            case NpcEwarKind.Jamming:
                Assert.Equal(new NpcJammingStrength(4.2, 4.2, 4.2, 4.2), ability.Power.Jamming);
                break;
            case NpcEwarKind.TargetPainting:
                Assert.Equal(27.5, ability.Power.SignaturePercent);
                Assert.Equal(50000, ability.Falloff);
                break;
            case NpcEwarKind.TrackingDisruption:
                Assert.Equal(-10, ability.Power.OptimalPercent);
                Assert.Equal(-10, ability.Power.FalloffPercent);
                Assert.Equal(0, ability.Power.TrackingPercent);
                break;
            case NpcEwarKind.SensorDampening:
                Assert.Equal(0.85, ability.Power.TargetRangeMultiplier);
                Assert.Equal(0.85, ability.Power.ScanResolutionMultiplier);
                break;
            case NpcEwarKind.EnergyNeutralization:
                Assert.Equal(50, ability.Power.CapacitorDrain);
                break;
            case NpcEwarKind.Webifying:
                Assert.Equal(-37.5, ability.Power.WebPercent);
                break;
            case NpcEwarKind.WarpDisruption:
                Assert.Equal(1, ability.Power.WarpStrength);
                break;
        }
    }

    [Fact]
    public void EveryAdmittedPirateAndGuardProfileCanLoadWithoutGrantingUndeclaredAbilities()
    {
        using TestWorld db = new(regionalNpcs: true);
        RegionalNpcTests.Prepare(db);
        var types = db
            .Services.GetRequiredService<NpcSpawnCatalog>()
            .Profiles.Select(p => p.TypeId)
            .Concat(Enum.GetValues<NpcGuardType>().Select(g => (int)g))
            .ToArray();
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        catalog.Database.OpenConnection();
        NpcProfileCatalog profiles = new(cache: false);
        HashSet<NpcEwarKind> kinds = [];
        foreach (int type in types)
        {
            var profile = profiles.Read(catalog, type);
            var effects = FittingCatalog.Effects(catalog, type);
            Assert.All(
                profile.ElectronicWarfare,
                ability =>
                {
                    Assert.Contains(ability.Effect, effects);
                    kinds.Add(ability.Kind);
                }
            );
        }
        Assert.Equal(
            new[]
            {
                NpcEwarKind.Jamming,
                NpcEwarKind.SensorDampening,
                NpcEwarKind.TrackingDisruption,
                NpcEwarKind.TargetPainting,
                NpcEwarKind.EnergyNeutralization,
                NpcEwarKind.Webifying,
                NpcEwarKind.WarpDisruption,
            }.Order(),
            kinds.Order()
        );
        Assert.Empty(NpcEwarRules.Profile(new HashSet<DogmaEffect>(), new Dictionary<DogmaAttributeId, double>()));
    }

    private static NpcEwarAbility Ability(NpcEwarKind kind = NpcEwarKind.TargetPainting) =>
        new(kind, DogmaEffect.NpcTargetPainting, 10000, 10000, 5000, 0.5, new(SignaturePercent: 30));

    [Theory]
    [InlineData(10000, 1)]
    [InlineData(15000, 0.5)]
    [InlineData(20000, 0.0625)]
    public void FalloffAndProbabilityUseExactBoundaries(double range, double factor)
    {
        var ability = Ability();
        Assert.Equal(factor, NpcEwarRules.RangeFactor(ability, range), 10);
        double threshold = factor * ability.Chance;
        Assert.NotNull(NpcEwarRules.Attempt(ability, range, new Dictionary<DogmaAttributeId, double>(), threshold - 0.000001));
        Assert.Null(NpcEwarRules.Attempt(ability, range, new Dictionary<DogmaAttributeId, double>(), threshold));
        Assert.Null(NpcEwarRules.Attempt(ability with { Falloff = 0 }, 10001, new Dictionary<DogmaAttributeId, double>(), 0));
    }

    [Fact]
    public void EcmUsesTheTargetsRacialSensorStrengthResistanceAndJammerException()
    {
        var ability = Ability(NpcEwarKind.Jamming) with { Chance = 1, Power = new(Jamming: new(2, 4, 6, 8)) };
        Dictionary<DogmaAttributeId, double> target = new() { [DogmaAttributeId.GravimetricStrength] = 20 };
        Assert.NotNull(NpcEwarRules.Attempt(ability, 0, target, 0.399999));
        Assert.Null(NpcEwarRules.Attempt(ability, 0, target, 0.4));
        target[DogmaAttributeId.GravimetricStrength] = 40;
        Assert.Null(NpcEwarRules.Attempt(ability, 0, target, 0.2));
        target[DogmaAttributeId.EcmResistance] = 0;
        Assert.Null(NpcEwarRules.Attempt(ability, 0, target, 0));
        NpcEwarContact contact = new(100, 2385, 200, ability, ability.Power, 1, 2);
        Assert.True(NpcEwarRules.CanTarget([contact], 100));
        Assert.False(NpcEwarRules.CanTarget([contact], 101));
        Assert.True(NpcEwarRules.CanTarget([], 101));
    }

    [Fact]
    public void NeutralizerFalloffAndResistanceScaleDrainedEnergy()
    {
        var ability = Ability(NpcEwarKind.EnergyNeutralization) with { Chance = 1, Power = new(CapacitorDrain: 50) };
        Dictionary<DogmaAttributeId, double> target = new() { [DogmaAttributeId.EnergyWarfareResistance] = 0.5 };
        Assert.Equal(12.5, NpcEwarRules.Attempt(ability, 15000, target, 0.9)!.CapacitorDrain);
        target[DogmaAttributeId.EnergyWarfareResistance] = 0;
        Assert.Equal(0, NpcEwarRules.Attempt(ability, 0, target, 0)!.CapacitorDrain);
    }

    [Theory]
    [InlineData(2385, NpcEwarKind.Jamming, false)]
    [InlineData(2385, NpcEwarKind.Jamming, true)]
    [InlineData(10015, NpcEwarKind.TargetPainting, false)]
    [InlineData(10015, NpcEwarKind.TargetPainting, true)]
    [InlineData(10027, NpcEwarKind.TrackingDisruption, false)]
    [InlineData(10027, NpcEwarKind.TrackingDisruption, true)]
    [InlineData(10284, NpcEwarKind.SensorDampening, false)]
    [InlineData(10284, NpcEwarKind.SensorDampening, true)]
    [InlineData(11901, NpcEwarKind.EnergyNeutralization, false)]
    [InlineData(11901, NpcEwarKind.EnergyNeutralization, true)]
    [InlineData(11894, NpcEwarKind.Webifying, false)]
    [InlineData(11894, NpcEwarKind.Webifying, true)]
    [InlineData(13032, NpcEwarKind.WarpDisruption, false)]
    [InlineData(13032, NpcEwarKind.WarpDisruption, true)]
    public void PaidAbilitiesChangeActualTargetStateAndCancelCleanly(int type, NpcEwarKind kind, bool resident)
    {
        using Fixture f = new(type, resident);
        var before = f.Fitting.ShipAttributes(NpcCombatTests.Fixture.PilotId);
        var gun = f
            .Combat.Db.Services.GetRequiredService<IPilotInventory>()
            .List(NpcCombatTests.Fixture.PilotId, NpcCombatTests.Fixture.ShipId)
            .Single(i => i.Id == f.Combat.Module);
        var weaponBefore = f.Combat.Weapons.ModuleAttributes(NpcCombatTests.Fixture.PilotId, gun);
        var effect = Assert.Single(f.Advance(), e => e.Ability.Kind == kind);
        var timers = f
            .Combat.Db.Services.GetRequiredService<IPilotCrimewatch>()
            .Read(NpcCombatTests.Fixture.PilotId, f.Combat.Pilot.SystemId)
            .Timers;
        Assert.True(timers.Npc.Active(f.Now));
        Assert.True(timers.Npc.ExpiresAt >= effect.ReadyAt + CrimewatchRules.NpcDuration.Ticks);
        var after = f.Fitting.ShipAttributes(NpcCombatTests.Fixture.PilotId);
        switch (kind)
        {
            case NpcEwarKind.Jamming:
                Assert.False(NpcEwarRules.CanTarget([effect], 123));
                break;
            case NpcEwarKind.SensorDampening:
                Assert.Equal(before[DogmaAttributeId.MaxTargetRange] * 0.85, after[DogmaAttributeId.MaxTargetRange], 6);
                Assert.Equal(before[DogmaAttributeId.ScanResolution] * 0.85, after[DogmaAttributeId.ScanResolution], 6);
                break;
            case NpcEwarKind.TargetPainting:
                Assert.Equal(before[DogmaAttributeId.SignatureRadius] * 1.275, after[DogmaAttributeId.SignatureRadius], 6);
                break;
            case NpcEwarKind.TrackingDisruption:
                var weaponAfter = f.Combat.Weapons.ModuleAttributes(NpcCombatTests.Fixture.PilotId, gun);
                Assert.Equal(weaponBefore[DogmaAttributeId.MaxRange] * 0.9, weaponAfter[DogmaAttributeId.MaxRange], 6);
                Assert.Equal(weaponBefore[DogmaAttributeId.AccuracyFalloff] * 0.9, weaponAfter[DogmaAttributeId.AccuracyFalloff], 6);
                Assert.Equal(weaponBefore[DogmaAttributeId.TrackingSpeed], weaponAfter[DogmaAttributeId.TrackingSpeed]);
                break;
            case NpcEwarKind.EnergyNeutralization:
                using (WorldDbContext world = new(f.Combat.Db.Path))
                {
                    Assert.Equal(950, world.Set<ShipEnergy>().Find(NpcCombatTests.Fixture.ShipId)!.Charge, 8);
                }
                break;
            case NpcEwarKind.Webifying:
                Assert.Equal(before[DogmaAttributeId.MaxVelocity] * 0.625, after[DogmaAttributeId.MaxVelocity], 6);
                break;
            case NpcEwarKind.WarpDisruption:
                Assert.Equal(1, after[DogmaAttributeId.WarpScrambleStatus]);
                break;
        }
        var rolls = f.Roll.Count;
        Assert.Equal(f.Advance(), f.Warfare.Incoming(NpcCombatTests.Fixture.ShipId));
        Assert.Equal(rolls, f.Roll.Count);
        f.Combat.Combat.Disengage(NpcCombatTests.Fixture.ShipId);
        Assert.Empty(f.Warfare.Incoming(NpcCombatTests.Fixture.ShipId));
        var restored = f.Fitting.ShipAttributes(NpcCombatTests.Fixture.PilotId);
        Assert.Equal(
            before.GetValueOrDefault(DogmaAttributeId.MaxTargetRange),
            restored.GetValueOrDefault(DogmaAttributeId.MaxTargetRange)
        );
        Assert.Equal(before.GetValueOrDefault(DogmaAttributeId.MaxVelocity), restored.GetValueOrDefault(DogmaAttributeId.MaxVelocity));
        Assert.Equal(
            before.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
            restored.GetValueOrDefault(DogmaAttributeId.SignatureRadius)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentAndDelayedCyclesDrainOnceWithoutOfflineBursts(bool resident)
    {
        using Fixture f = new(11901, resident);
        var first = Assert.Single(f.Advance());
        f.Combat.Clock.Seconds = 10;
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(f.Advance)));
        Assert.All(results, contacts => Assert.Equal(first.ReadyAt, Assert.Single(contacts).StartedAt));
        Assert.Equal(2, f.Roll.Count);
        using (WorldDbContext world = new(f.Combat.Db.Path))
        {
            Assert.Equal(900, world.Set<ShipEnergy>().Find(NpcCombatTests.Fixture.ShipId)!.Charge, 8);
        }
        f.Combat.Clock.Seconds = 1000;
        var delayed = Assert.Single(f.Advance());
        Assert.Equal(f.Now + 10 * TimeSpan.TicksPerSecond, delayed.ReadyAt);
        Assert.Equal(3, f.Roll.Count);
        using WorldDbContext saved = new(f.Combat.Db.Path);
        Assert.Equal(850, saved.Set<ShipEnergy>().Find(NpcCombatTests.Fixture.ShipId)!.Charge, 8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedCyclesEndEffectsAndReactionRangeAndLeashAreEnforced(bool resident)
    {
        using Fixture f = new(10015, resident);
        using (WorldDbContext world = new(f.Combat.Db.Path))
        {
            world.Set<NpcAggression>().Find(f.Combat.Npc.Id)!.ReadyAt = f.Now + TimeSpan.TicksPerSecond;
            world.SaveChanges();
        }
        Assert.Empty(f.Advance());
        f.Combat.Clock.Seconds = 1;
        Assert.Single(f.Advance());
        f.Roll.Value = 0.999999;
        f.Combat.Clock.Seconds = 11;
        Assert.Empty(f.Advance());
        Assert.Empty(f.Warfare.Incoming(NpcCombatTests.Fixture.ShipId));
        f.Roll.Value = 0;
        f.Combat.Clock.Seconds = 21;
        Assert.Single(f.Advance());
        f.Combat.Db.Store.SetPosition(
            NpcCombatTests.Fixture.ShipId,
            f.Combat.Npc.Position + new SpacePosition(1_000_000, 0, 0),
            f.Combat.Npc.SystemId
        );
        Assert.Empty(f.Advance());
        Assert.Empty(f.Warfare.Incoming(NpcCombatTests.Fixture.ShipId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointsRetainSpentCapacitorAndRestartClearsAllLiveEffects(bool resident)
    {
        using Fixture f = new(11901, resident);
        Assert.Single(f.Advance());
        if (resident)
        {
            await LiveWorld.Find(f.Combat.Db.Path)!.FlushAsync();
        }
        using (WorldDbContext world = new(f.Combat.Db.Path))
        {
            Assert.Single(world.Set<WorldNpcEwar>());
            Assert.Equal(950, world.Set<ShipEnergy>().Find(NpcCombatTests.Fixture.ShipId)!.Charge, 8);
        }
        string checkpoint = Path.Combine(Path.GetDirectoryName(f.Combat.Db.Path)!, "npc-ewar-checkpoint.sqlite");
        using (var source = WorldConnections.FileConnection(f.Combat.Db.Path))
        using (SqliteConnection target = new($"Data Source={checkpoint};Pooling=False"))
        {
            source.Open();
            target.Open();
            source.BackupDatabase(target);
        }
        using (WorldDbContext copy = new(checkpoint))
        {
            Assert.Single(copy.Set<WorldNpcEwar>());
            Assert.Equal(950, copy.Set<ShipEnergy>().Find(NpcCombatTests.Fixture.ShipId)!.Charge, 8);
        }
        WorldInitialization.Initialize(new(checkpoint), f.Combat.Clock);
        using (WorldDbContext reopened = new(checkpoint))
        {
            Assert.Empty(reopened.Set<WorldNpcEwar>());
            Assert.Equal(950, reopened.Set<ShipEnergy>().Find(NpcCombatTests.Fixture.ShipId)!.Charge, 8);
        }
        f.Combat.Clock.Seconds = 1000;
        WorldInitialization.Initialize(new(f.Combat.Db.Path), f.Combat.Clock);
        Assert.Empty(f.Advance());
        using WorldDbContext saved = new(f.Combat.Db.Path);
        Assert.Empty(saved.Set<WorldNpcEwar>());
        Assert.Equal(950, saved.Set<ShipEnergy>().Find(NpcCombatTests.Fixture.ShipId)!.Charge, 8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EcmClearsOtherLocksAndPreventsNewLocksWhileAllowingTheJammer(bool resident)
    {
        using Fixture f = new(2385, resident);
        var encounters = f.Combat.Db.Services.GetRequiredService<INpcEncounters>();
        using PilotCallContext context = new(
            1000001,
            f.Combat.Pilot.Account,
            [f.Combat.Pilot],
            p => new(f.Combat.Db.Store, p, f.Combat.Clock, new(), [], encounters: encounters),
            10,
            SignedSettings.Empty
        );
        context.Select(f.Combat.Pilot);
        context.Flight.Snapshot();
        using TargetingHandler targeting = new(f.Combat.Clock, f.Fitting, new(), f.Warfare);
        var other = encounters.List(f.Combat.Npc.SystemId, f.Now).First(n => n.Id != f.Combat.Npc.Id);
        // Move the second belt contact onto this grid without moving the victim.
        using (WorldDbContext world = new(f.Combat.Db.Path))
        {
            var row = world.Set<WorldNpc>().Find(other.Id)!;
            (row.X, row.Y, row.Z) = (f.Combat.Npc.Position.X + 500, f.Combat.Npc.Position.Y, f.Combat.Npc.Position.Z);
            world.SaveChanges();
        }
        context.Flight.RefreshNpcs();
        Assert.IsType<TargetLockReply>(
            Assert.IsType<PilotCallResult.Success>(targeting.Call(context, "dogmaIM", "AddTarget", [other.Id])).Reply
        );
        f.Combat.Clock.Seconds = 20;
        targeting.Advance(context);
        Assert.True(targeting.IsLocked(other.Id));
        Assert.Single(f.Advance());
        targeting.Advance(context);
        Assert.False(targeting.IsLocked(other.Id));
        Assert.Contains(
            context.Notifications,
            n => n is TargetChanged { Change: TargetChange.Lost } changed && changed.TargetId == other.Id
        );
        Assert.IsType<PilotCallResult.Failure>(targeting.Call(context, "dogmaIM", "AddTarget", [other.Id]));
        Assert.IsType<TargetLockReply>(
            Assert.IsType<PilotCallResult.Success>(targeting.Call(context, "dogmaIM", "AddTarget", [f.Combat.Npc.Id])).Reply
        );
    }

    [Fact]
    public void VersionEightyOneMigrationCreatesEmptyEwarStateWithoutChangingNpcHealth()
    {
        using NpcCombatTests.Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldNpcEwar; PRAGMA user_version=81;");
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<WorldNpcEwar>());
        Assert.Equal(f.Npc.Health.Remaining.Shield, saved.Set<WorldNpc>().Find(f.Npc.Id)!.ShieldCharge);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VictimNotificationsAndObserverSnapshotsKeepWebAndDisruptionDistinctAcrossRenewals(bool resident)
    {
        using Fixture f = new(13032, resident);
        var encounters = f.Combat.Db.Services.GetRequiredService<INpcEncounters>();
        var tackle = f.Combat.Db.Services.GetRequiredService<IPilotTackle>();
        using PilotCallContext context = new(
            1000001,
            f.Combat.Pilot.Account,
            [f.Combat.Pilot],
            p => new(f.Combat.Db.Store, p, f.Combat.Clock, new(), [], encounters: encounters, tackle: tackle),
            10,
            SignedSettings.Empty
        );
        context.Select(f.Combat.Pilot);
        context.Flight.Snapshot();
        context.Flight.Updates.Clear();
        NpcEwarHandler handler = new(
            f.Combat.Clock,
            f.Warfare,
            f.Combat.Db.Services.GetRequiredService<SupportModuleWire>(),
            f.Combat.Db.Services.GetRequiredService<IPilotShipResources>()
        );
        handler.Advance(context);
        var starts = context.NotificationsBeforeFlight.OfType<NpcEwarChanged>().ToArray();
        Assert.Equal(2, starts.Length);
        Assert.All(
            starts,
            e =>
            {
                Assert.True(e.Active);
                Assert.Equal(f.Combat.Npc.Id, e.Arguments[0]);
                Assert.Equal(f.Combat.Npc.Id, e.Arguments[1]);
                Assert.Equal(NpcCombatTests.Fixture.ShipId, e.Arguments[2]);
            }
        );
        Assert.Equal(new[] { "warpScrambler", "webify" }, starts.Select(e => (string)e.Arguments[3]!).Order().ToArray());
        Assert.Equal(
            f.Fitting.ShipAttributes(NpcCombatTests.Fixture.PilotId)[DogmaAttributeId.MaxVelocity],
            context.Flight.Pilot.Flight.MaxSpeed,
            4
        );
        Assert.IsType<PilotCallResult.Failure>(context.Flight.CallWire("beyonce", "CmdWarpToStuff", ["item", f.Combat.Npc.Id]));
        using FlightSession observer = new(f.Combat.Db.Store, f.Combat.Pilot, f.Combat.Clock, new(), [], encounters: encounters);
        observer.Snapshot();
        Assert.Equal(
            2,
            observer
                .Updates.Select(u => u.Command)
                .OfType<DestinyCommand.TimedSpecialEffect>()
                .Count(e => e.Id == f.Combat.Npc.Id && e.TargetId == NpcCombatTests.Fixture.ShipId)
        );
        context.NotificationsBeforeFlight.Clear();
        context.Flight.Updates.Clear();
        f.Combat.Clock.Seconds = 5;
        handler.Advance(context);
        Assert.Empty(context.NotificationsBeforeFlight.OfType<NpcEwarChanged>());
        Assert.Contains(
            context.Flight.Updates.Select(u => u.Command),
            command => command is DestinyCommand.TimedSpecialEffect { Graphic: "effects.ModifyTargetSpeed", Start: true }
        );
        Assert.NotEmpty(BlueMarshal.Encode(context.Flight.WireUpdates()));
        f.Combat.Combat.Disengage(NpcCombatTests.Fixture.ShipId);
        handler.Advance(context);
        Assert.Equal(2, context.NotificationsBeforeFlight.OfType<NpcEwarChanged>().Count(e => !e.Active));
        Assert.Equal(0, f.Fitting.ShipAttributes(NpcCombatTests.Fixture.PilotId).GetValueOrDefault(DogmaAttributeId.WarpScrambleStatus));
        Assert.Empty(context.Flight.VisibleNpcs.SelectMany(n => n.ElectronicWarfare ?? []));
    }

    [Theory]
    [InlineData("dock", false)]
    [InlineData("dock", true)]
    [InlineData("system", false)]
    [InlineData("system", true)]
    [InlineData("retire", false)]
    [InlineData("retire", true)]
    [InlineData("destroy", false)]
    [InlineData("destroy", true)]
    public void InvalidTargetsAndSourcesEndEveryActiveEffect(string transition, bool resident)
    {
        using Fixture f = new(13032, resident);
        Assert.Equal(2, f.Advance().Count);
        if (transition is "dock" or "system")
        {
            f.Combat.Db.Store.SetPosition(NpcCombatTests.Fixture.ShipId, transition == "dock" ? null : f.Combat.Npc.Position, 30000142);
        }
        else
        {
            using WorldDbContext world = new(f.Combat.Db.Path);
            var npc = world.Set<WorldNpc>().Find(f.Combat.Npc.Id)!;
            if (transition == "retire")
            {
                npc.RetiredAt = f.Now;
            }
            else
            {
                npc.DestroyedAt = f.Now;
            }
            world.SaveChanges();
        }
        Assert.Empty(f.Advance());
        Assert.Empty(f.Warfare.Incoming(NpcCombatTests.Fixture.ShipId));
        var attributes = f.Fitting.ShipAttributes(NpcCombatTests.Fixture.PilotId);
        Assert.Equal(0, attributes.GetValueOrDefault(DogmaAttributeId.WarpScrambleStatus));
        Assert.Equal(295, attributes[DogmaAttributeId.MaxVelocity], 4);
    }

    [Fact]
    public void WarmNpcCombatWithoutDeclaredEwarAddsNoSqlOrFittingWork()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        f.Aggress();
        NpcEwarStore store = new(new(f.Db.Path), new Roll(), new(cache: false));
        Assert.Empty(store.Advance(NpcCombatTests.Fixture.PilotId, NpcCombatTests.Fixture.ShipId, f.Clock.Now));
        using var measure = SessionPerformance.Begin("empty NPC EWAR", _ => { }, force: true)!;
        for (int second = 1; second <= 12; second++)
        {
            f.Clock.Seconds = second;
            Assert.Empty(store.Advance(NpcCombatTests.Fixture.PilotId, NpcCombatTests.Fixture.ShipId, f.Clock.Now));
            Assert.Empty(store.Incoming(NpcCombatTests.Fixture.ShipId));
        }
        Assert.Equal(0, measure.Commands);
        Assert.Equal(0, measure.Connections);
        Assert.Equal(0, measure.FittingCalculations);
    }

    [Fact]
    public void DampeningStackingAndResistanceUseCatalogOperatorsAndOnlyTurretsReceiveDisruption()
    {
        const long Ship = 1;
        const long Turret = 2;
        const long Launcher = 3;
        Dictionary<DogmaAttributeId, double> hull = new()
        {
            [DogmaAttributeId.MaxTargetRange] = 10000,
            [DogmaAttributeId.ScanResolution] = 100,
            [DogmaAttributeId.SensorDampenerResistance] = 0.5,
        };
        Dictionary<DogmaAttributeId, double> weapon = new()
        {
            [DogmaAttributeId.MaxRange] = 10000,
            [DogmaAttributeId.AccuracyFalloff] = 5000,
            [DogmaAttributeId.TrackingSpeed] = 1,
        };
        var damp = Ability(NpcEwarKind.SensorDampening) with { Power = new(TargetRangeMultiplier: 0.8, ScanResolutionMultiplier: 0.8) };
        var disrupt = Ability(NpcEwarKind.TrackingDisruption) with
        {
            Power = new(OptimalPercent: -10, FalloffPercent: -20, TrackingPercent: -30),
        };
        NpcEwarContact[] contacts =
        [
            new(10, 10284, Ship, damp, damp.Power, 0, 1),
            new(11, 10284, Ship, damp, damp.Power, 0, 1),
            new(12, 10027, Ship, disrupt, disrupt.Power, 0, 1),
        ];
        Dictionary<DogmaAttributeId, DogmaAttributeDefinition> definitions = new()
        {
            [DogmaAttributeId.MaxTargetRange] = new(DogmaAttributeId.MaxTargetRange, 0, false),
            [DogmaAttributeId.ScanResolution] = new(DogmaAttributeId.ScanResolution, 0, false),
            [DogmaAttributeId.WeaponDisruptionResistance] = new(DogmaAttributeId.WeaponDisruptionResistance, 1),
        };
        DogmaGraph graph = new(
            [new(Ship, hull), new(Turret, weapon), new(Launcher, weapon)],
            NpcEwarRules.Modifiers(Ship, [Turret], contacts).ToArray(),
            definitions
        );
        double secondPenalty = Math.Exp(-Math.Pow(1 / 2.67, 2));
        Assert.Equal(10000 * 0.9 * (1 - 0.1 * secondPenalty), graph.Read(Ship, DogmaAttributeId.MaxTargetRange), 6);
        Assert.Equal(100 * 0.9 * (1 - 0.1 * secondPenalty), graph.Read(Ship, DogmaAttributeId.ScanResolution), 6);
        Assert.Equal(9000, graph.Read(Turret, DogmaAttributeId.MaxRange));
        Assert.Equal(4000, graph.Read(Turret, DogmaAttributeId.AccuracyFalloff));
        Assert.Equal(0.7, graph.Read(Turret, DogmaAttributeId.TrackingSpeed), 8);
        Assert.Equal(10000, graph.Read(Launcher, DogmaAttributeId.MaxRange));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultipleNeutralizersClampToAvailableChargeAndAnInvalidLaterDrawRollsBackEarlierDrain(bool resident)
    {
        using Fixture f = new(11901, resident);
        using (WorldDbContext world = new(f.Combat.Db.Path))
        {
            var second = world.Set<WorldNpc>().Single(n => n.Id != f.Combat.Npc.Id);
            second.TypeId = 11901;
            (second.X, second.Y, second.Z) = (f.Combat.Npc.Position.X + 500, f.Combat.Npc.Position.Y, f.Combat.Npc.Position.Z);
            (second.HomeX, second.HomeY, second.HomeZ) = (second.X, second.Y, second.Z);
            world.Add(NpcAggression.Start(second.Id, NpcCombatTests.Fixture.ShipId, 0, f.Now));
            var capacitor = world.Set<ShipEnergy>().Find(NpcCombatTests.Fixture.ShipId)!;
            capacitor.Charge = 25;
            capacitor.UpdatedAt = f.Now;
            world.SaveChanges();
        }
        f.Roll.Values.Enqueue(0);
        f.Roll.Values.Enqueue(double.NaN);
        Assert.Throws<InvalidDataException>(() => f.Advance());
        using (WorldDbContext unchanged = new(f.Combat.Db.Path))
        {
            Assert.Equal(25, unchanged.Set<ShipEnergy>().Find(NpcCombatTests.Fixture.ShipId)!.Charge, 8);
            Assert.Empty(unchanged.Set<WorldNpcEwar>());
        }
        var contacts = f.Advance();
        Assert.Equal(2, contacts.Count);
        Assert.Equal(25, contacts.Sum(e => e.Applied.CapacitorDrain), 8);
        using WorldDbContext saved = new(f.Combat.Db.Path);
        Assert.Equal(0, saved.Set<ShipEnergy>().Find(NpcCombatTests.Fixture.ShipId)!.Charge, 8);
    }
}
