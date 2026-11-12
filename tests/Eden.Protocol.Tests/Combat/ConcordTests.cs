using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Insurance;
using Eden.Data.World.Market;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Combat;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.Persistence;
using Eden.Server.Maintenance;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Insurance;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Combat.NpcCombatTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class ConcordTests
{
    private const long Victim = 90000002;
    private const long VictimShip = 1000000000002;
    private const int SystemId = 30000144;

    internal static void Catalog(Fixture f, double security = .8)
    {
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            string system = JsonSerializer.Serialize(new { securityStatus = security });
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000144',{system})");
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (301,'CONCORD Drone',11);
                INSERT OR IGNORE INTO NpcCorporation(Id,Name) VALUES (1000125,'CONCORD');
                INSERT INTO ItemType(Id,Name,GroupId,Mass,Radius) VALUES
                    (3885,'CONCORD Police Captain',301,10100000,150),(11125,'CONCORD Police Commander',301,20000000,600);
                INSERT INTO TypeAttribute SELECT 3885,AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=30380;
                INSERT INTO TypeAttribute SELECT 11125,AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=30380;
                INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES
                    (20,'speedFactor'),(97,'energyNeutralizerAmount'),(98,'energyNeutralizerRangeOptimal'),
                    (103,'warpScrambleRange'),(105,'warpScrambleStrength'),(504,'entityWarpScrambleChance'),
                    (512,'modifyTargetSpeedChance'),(513,'modifyTargetSpeedDuration'),(514,'modifyTargetSpeedRange'),
                    (929,'ECMDuration'),(930,'ECMEntityChance'),(931,'energyNeutralizerEntityChance'),
                    (936,'ECMRangeOptimal'),(942,'energyNeutralizerDuration'),(953,'ECMRangeFalloff');
                INSERT OR REPLACE INTO TypeAttribute VALUES
                    (3885,9,100000),(3885,263,100000),(3885,265,100000),(3885,51,5000),
                    (3885,20,-95),(3885,97,1000000),(3885,98,200000),(3885,103,250000),(3885,105,10),
                    (3885,504,1),(3885,512,1),(3885,513,5000),(3885,514,250000),
                    (3885,929,5000),(3885,930,1),(3885,931,1),(3885,936,200000),(3885,942,5000),(3885,953,50000),
                    (11125,9,100000),(11125,263,100000),(11125,265,100000),(11125,245,574);
                """
            );
            int[] effects = [10, 3661, 3710, 3713, 3714, 6691];
            string dogma = JsonSerializer.Serialize(
                new { dogmaEffects = effects.Select(effect => new { effectID = effect, isDefault = 0 }) }
            );
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/typedogma','3885',{dogma})");
        }
        using WorldDbContext world = new(f.Db.Path);
        world.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Omega;
        world.Pilots.Find(Fixture.PilotId)!.SafetyLevel = ShipSafetyLevel.Disabled;
        world.SaveChanges();
        f.Db.Store.SetPosition(VictimShip, f.Pilot.Position! + new SpacePosition(500, 0, 0), SystemId);
    }

    private static IConcord Store(Fixture f) => f.Db.Services.GetRequiredService<IConcord>();

    private static void Attack(Fixture f) =>
        f.Weapons.FirePlayerBank(Fixture.PilotId, f.Module, new(VictimShip, SpacePosition.Zero), SpacePosition.Zero, f.Clock.Now, .5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CriminalAttackSpawnsNativeSquadAndDestroysOnlyTheOffenderOnceWithoutInsurance(bool live)
    {
        using Fixture f = new(liveWorld: live, useTestClock: true);
        Catalog(f);
        long now = f.Clock.Now;
        Attack(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var response = Assert.Single(world.Set<SavedConcordResponse>());
            Assert.Equal(now + TimeSpan.FromSeconds(7).Ticks, response.RespondAt);
            Assert.Equal(response.RespondAt + ConcordRules.CommanderDelay.Ticks, response.DestroyAt);
            Assert.Equal(Fixture.ShipId, response.ShipId);
            Assert.Equal(CrimewatchFlag.Criminal, world.Set<SavedCrimewatch>().Find(Fixture.PilotId)!.Timers().Flag(now));
        }
        Assert.Null(Store(f).Advance(Fixture.PilotId, now + TimeSpan.FromSeconds(7).Ticks - 1));
        Assert.Null(Store(f).Advance(Fixture.PilotId, now + TimeSpan.FromSeconds(7).Ticks));
        using (WorldDbContext spawned = new(f.Db.Path))
        {
            var squad = spawned.Set<WorldNpc>().Where(n => n.Role == NpcRole.Concord).ToArray();
            Assert.Equal(3, squad.Length);
            Assert.Equal(2, squad.Count(n => n.TypeId == ConcordRules.CaptainType));
            Assert.All(squad, n => Assert.Equal(ConcordRules.Corporation, n.CorporationId));
            Assert.Equal(2, spawned.Set<NpcAggression>().Count(a => a.TargetShipId == Fixture.ShipId));
        }
        long deadline = now + TimeSpan.FromSeconds(22).Ticks;
        Assert.Null(Store(f).Advance(Fixture.PilotId, deadline - 1));
        var loss = Assert.IsType<NpcShot>(Store(f).Advance(Fixture.PilotId, deadline));
        Assert.Equal(ConcordRules.CommanderType, loss.TypeId);
        Assert.Equal(FleetDefinitions.CapsuleGroup, loss.Capsule!.ShipGroupId);
        Assert.Null(loss.Insurance);
        Assert.Null(Store(f).Advance(Fixture.PilotId, deadline));
        Assert.Null(Store(f).Advance(Fixture.PilotId, deadline + TimeSpan.TicksPerMinute));
        using (WorldDbContext saved = new(f.Db.Path))
        {
            Assert.Equal(Fixture.ShipId, Assert.Single(saved.Set<ShipLoss>()).ShipId);
            Assert.Empty(saved.Set<SavedInsuranceLedger>().Where(l => l.IsPayout));
            Assert.Equal(VictimShip, saved.Pilots.Find(Victim)!.ActiveShipId);
            Assert.NotNull(Assert.Single(saved.Set<SavedConcordResponse>()).CompletedAt);
            Assert.All(saved.Set<WorldNpc>().Where(n => n.Role == NpcRole.Concord), n => Assert.NotNull(n.RetiredAt));
        }
        if (live)
        {
            await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        }
    }

    [Theory]
    [InlineData(.45, 19)]
    [InlineData(.54999, 19)]
    [InlineData(.55, 14)]
    [InlineData(.65, 10)]
    [InlineData(.75, 7)]
    [InlineData(.85, 6)]
    [InlineData(1, 6)]
    public void ResponseUsesDisplayedSecurity(double security, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), ConcordRules.ResponseDelay(security));

    [Fact]
    public void CaptainSpecialEffectsJamAllTargetsAndDrainCapacitorUsingNativeContacts()
    {
        using Fixture f = new(useTestClock: true);
        Catalog(f);
        Attack(f);
        long arrival = f.Clock.Now + TimeSpan.FromSeconds(7).Ticks;
        Store(f).Advance(Fixture.PilotId, arrival);
        var warfare = f.Db.Services.GetRequiredService<INpcElectronicWarfare>();
        var contacts = warfare.Advance(Fixture.PilotId, Fixture.ShipId, arrival);
        Assert.Equal(8, contacts.Count);
        Assert.Contains(contacts, c => c.Ability.Effect == DogmaEffect.ConcordTargetJam);
        Assert.Contains(contacts, c => c.Ability.Kind == NpcEwarKind.EnergyNeutralization && c.Applied.CapacitorDrain > 0);
        Assert.Contains(contacts, c => c.Applied.WebPercent == -95);
        Assert.Contains(contacts, c => c.Applied.WarpStrength == 10);
        Assert.False(NpcEwarRules.CanTarget(contacts, contacts[0].SourceId));
        Assert.False(NpcEwarRules.CanTarget(contacts, VictimShip));
        Assert.Contains(
            NpcEwarRules.Modifiers(Fixture.ShipId, [], contacts),
            modifier => modifier.Attribute == DogmaAttributeId.DroneBandwidth && modifier.Source is DogmaValue.Constant { Value: 0 }
        );
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(0, saved.Set<ShipEnergy>().Find(Fixture.ShipId)!.Charge);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuietAndPendingResidentMonitoringPerformNoSqlAfterWarmup(bool pending)
    {
        using Fixture f = new(liveWorld: true, useTestClock: true);
        Catalog(f);
        if (pending)
        {
            Attack(f);
        }
        Store(f).Advance(Fixture.PilotId, f.Clock.Now);
        Store(f).Sweep(f.Clock.Now);
        using (var measure = SessionPerformance.Begin("CONCORD warm reads", _ => { }, force: true)!)
        {
            for (int i = 0; i < 3; i++)
            {
                Assert.Null(Store(f).Advance(Fixture.PilotId, f.Clock.Now + i));
                Store(f).Sweep(f.Clock.Now + i);
            }
            Assert.Equal(0, measure.Commands);
            Assert.Equal(0, measure.Connections);
            Assert.Equal(0, measure.FittingCalculations);
        }
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedResponseSurvivesRestartAndExpiredFlagAndSettlesOfflineExactlyOnce(bool arrived)
    {
        using Fixture f = new(useTestClock: true);
        Catalog(f);
        Attack(f);
        long deadline;
        using (WorldDbContext world = new(f.Db.Path))
        {
            deadline = Assert.Single(world.Set<SavedConcordResponse>()).DestroyAt;
        }
        if (arrived)
        {
            Store(f).Advance(Fixture.PilotId, deadline - ConcordRules.CommanderDelay.Ticks);
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(20));
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        if (arrived)
        {
            using WorldDbContext restored = new(f.Db.Path);
            Assert.Equal(2, restored.Set<NpcAggression>().Count(a => a.TargetShipId == Fixture.ShipId));
        }
        Store(f).Sweep(f.Clock.Now);
        Store(f).Sweep(f.Clock.Now);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(deadline, Assert.Single(saved.Set<SavedConcordResponse>()).DestroyAt);
        Assert.Equal(Fixture.ShipId, Assert.Single(saved.Set<ShipLoss>()).ShipId);
        Assert.Equal(FleetDefinitions.CapsuleGroup, f.Pilot.ShipGroupId);
        Assert.Equal(3, saved.Set<WorldNpc>().Count(n => n.Role == NpcRole.Concord));
        Assert.Equal(0, saved.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").Single());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.44999)]
    public void LowAndNullSecurityNeverDispatchConcordEvenWithACriminalFlag(double security)
    {
        using Fixture f = new(useTestClock: true);
        Catalog(f, security);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new SavedCrimewatch { PilotId = Fixture.PilotId, CriminalUntil = f.Clock.Now + CrimewatchRules.FlagDuration.Ticks });
            world.SaveChanges();
        }
        Store(f).Advance(Fixture.PilotId, f.Clock.Now);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<SavedConcordResponse>());
        Assert.DoesNotContain(saved.Set<WorldNpc>(), n => n.Role == NpcRole.Concord);
    }

    [Fact]
    public void RepeatsDoNotPostponeEnforcementAndShipTravelIsBlockedWhileCapsuleCanWarp()
    {
        using Fixture f = new(useTestClock: true);
        Catalog(f);
        Attack(f);
        long deadline;
        using (WorldDbContext world = new(f.Db.Path))
        {
            deadline = Assert.Single(world.Set<SavedConcordResponse>()).DestroyAt;
        }
        var crimewatch = f.Db.Services.GetRequiredService<IPilotCrimewatch>();
        foreach (var travel in Enum.GetValues<CrimewatchTravel>())
        {
            Assert.Throws<InvalidDataException>(() => crimewatch.CheckTravel(Fixture.PilotId, travel));
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(5));
        Attack(f);
        using (WorldDbContext saved = new(f.Db.Path))
        {
            Assert.Equal(deadline, Assert.Single(saved.Set<SavedConcordResponse>()).DestroyAt);
        }
        Store(f).Advance(Fixture.PilotId, deadline);
        crimewatch.CheckTravel(Fixture.PilotId, CrimewatchTravel.Warp);
        Assert.Throws<InvalidDataException>(() => crimewatch.CheckTravel(Fixture.PilotId, CrimewatchTravel.Dock));
    }

    [Fact]
    public void CriminalArrivalAndAssistanceUseTheSamePersistentResponse()
    {
        using Fixture f = new(useTestClock: true);
        Catalog(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new SavedCrimewatch { PilotId = Victim, CriminalUntil = f.Clock.Now + CrimewatchRules.FlagDuration.Ticks });
            world.SaveChanges();
        }
        // An undock/system entry commits dispatch before a session tick.
        f.Db.Store.SetPosition(VictimShip, null);
        f.Db.Store.SetPosition(VictimShip, f.Pilot.Position, SystemId);
        using (WorldDbContext world = new(f.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath))
        {
            var source = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == Fixture.PilotId);
            var target = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == Victim);
            AssistancePersistence.Apply(world, catalog, source, target, f.Clock.Now, f.Clock.Now + TimeSpan.TicksPerSecond);
            world.SaveChanges();
            transaction.Commit();
        }
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(2, saved.Set<SavedConcordResponse>().Count());
        Assert.Equal(CrimewatchFlag.Criminal, saved.Set<SavedCrimewatch>().Find(Fixture.PilotId)!.Timers().Flag(f.Clock.Now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedCriminalAttackRollsBackAllCostsTimersAndDispatch(bool missingCatalog)
    {
        using Fixture f = new(useTestClock: true);
        Catalog(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            if (!missingCatalog)
            {
                world.Database.ExecuteSqlRaw(
                    "CREATE TRIGGER RejectConcord BEFORE INSERT ON WorldConcordResponse BEGIN SELECT RAISE(ABORT,'test'); END"
                );
            }
        }
        if (missingCatalog)
        {
            using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw("DELETE FROM TypeAttribute WHERE ItemTypeId=3885");
        }
        var before = f.Health.Read(Victim, f.Clock.Now);
        Assert.ThrowsAny<Exception>(() => Attack(f));
        Assert.Equal(before, f.Health.Read(Victim, f.Clock.Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<SavedConcordResponse>());
        Assert.Empty(saved.Set<SavedCrimewatch>());
        Assert.Empty(saved.Set<ModuleCooldown>());
        Assert.Empty(saved.Set<ShipEnergy>());
    }

    [Fact]
    public void NativeCombatHandlerReplacesDestroyedShipAndEmitsDamageNotification()
    {
        using Fixture f = new(useTestClock: true);
        Catalog(f);
        using var scope = f.Db.Services.CreateScope();
        using var context = new PilotCallContext(
            1000001,
            f.Pilot.Account,
            [f.Pilot],
            p => new(f.Db.Store, p, f.Clock, new(), [], encounters: f.Db.Services.GetRequiredService<INpcEncounters>(), health: f.Health),
            10,
            SignedSettings.Empty
        );
        context.Select(f.Pilot);
        context.Flight.Snapshot();
        using var handler = ActivatorUtilities.CreateInstance<NpcCombatHandler>(scope.ServiceProvider, f.Clock);
        handler.Enter(context);
        Attack(f);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(22));
        handler.Advance(context);
        Assert.Equal(FleetDefinitions.CapsuleGroup, context.Pilot.ShipGroupId);
        Assert.Contains(context.NotificationsBeforeFlight, n => n is DamageMessageNotification);
        Assert.DoesNotContain(context.Notifications, n => n.Name == "OnAccountChange");
        Assert.NotEqual(Fixture.ShipId, context.Flight.Pilot.ShipId);
    }

    [Fact]
    public void CaptainShipLossVoidsPaidInsuranceAndCompletesThePendingResponse()
    {
        using Fixture f = new(useTestClock: true);
        Catalog(f);
        InsuranceTestData.Prepare(f.Db);
        InsuranceTestData.Fund(f.Db);
        var position = f.Pilot.Position!;
        f.Db.Store.SetPosition(Fixture.ShipId, null);
        new PilotInsuranceStore(new(f.Db.Path), f.Clock).Insure(Fixture.PilotId, f.Pilot.StationId, Fixture.ShipId, 30000, false);
        f.Db.Store.SetPosition(Fixture.ShipId, position, SystemId);
        Attack(f);
        long arrival = f.Clock.Now + TimeSpan.FromSeconds(7).Ticks;
        Store(f).Advance(Fixture.PilotId, arrival);
        f.AlmostLost();
        long captain;
        long balance;
        using (WorldDbContext world = new(f.Db.Path))
        {
            captain = Assert.Single(world.Set<SavedConcordResponse>()).FirstCaptainId!.Value;
            balance = world.Set<PilotWallet>().Find(Fixture.PilotId)!.Cents;
        }
        var shot = Assert.IsType<NpcShot>(f.Combat.Fire(Fixture.PilotId, Fixture.ShipId, captain, SpacePosition.Zero, arrival, 0, 8));
        Assert.NotNull(shot.Capsule);
        Assert.Null(shot.Insurance);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(balance, saved.Set<PilotWallet>().Find(Fixture.PilotId)!.Cents);
        Assert.Empty(saved.Set<SavedInsuranceLedger>().Where(l => l.IsPayout));
        Assert.Equal(Eden.Gameplay.Insurance.InsurancePolicyStatus.HullRemoved, Assert.Single(saved.Set<SavedInsurancePolicy>()).Status);
        Assert.NotNull(Assert.Single(saved.Set<SavedConcordResponse>()).CompletedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingEnforcementAndFailedLossAreAtomicAndRetryTheOriginalDeadline(bool resident)
    {
        using Fixture f = new(liveWorld: resident, useTestClock: true);
        Catalog(f);
        Attack(f);
        long deadline = f.Clock.Now + TimeSpan.FromSeconds(22).Ticks;
        const string reject = "CREATE TRIGGER RejectLoss BEFORE INSERT ON WorldShipLoss BEGIN SELECT RAISE(ABORT,'test'); END";
        var live = LiveWorld.Find(f.Db.Path);
        if (live is null)
        {
            using WorldDbContext world = new(f.Db.Path);
            world.Database.ExecuteSqlRaw(reject);
            Assert.Throws<DbUpdateException>(() => Store(f).Advance(Fixture.PilotId, deadline));
            CheckUnchanged(world);
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectLoss");
        }
        else
        {
            // The compatibility read records the pending response in the relational image
            // used as the base for the detached checkpoint.
            using WorldDbContext world = new(f.Db.Path);
            CheckUnchanged(world);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => Store(f).Advance(Fixture.PilotId, deadline))));
        Assert.Single(results.OfType<NpcShot>());
        using var snapshot = live?.CaptureSnapshot();
        if (snapshot is not null)
        {
            using (WorldDbContext world = new(snapshot.Database))
            {
                world.Database.ExecuteSqlRaw(reject);
                Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => snapshot.Project());
                CheckUnchanged(world);
                world.Database.ExecuteSqlRaw("DROP TRIGGER RejectLoss");
            }
            Assert.Null(Store(f).Advance(Fixture.PilotId, deadline));
            snapshot.Project();
        }
        using var saved = snapshot is null ? new WorldDbContext(f.Db.Path) : new WorldDbContext(snapshot.Database);
        Assert.Single(saved.Set<ShipLoss>());
        Assert.Equal(deadline, Assert.Single(saved.Set<SavedConcordResponse>()).DestroyAt);

        static void CheckUnchanged(WorldDbContext world)
        {
            Assert.Null(Assert.Single(world.Set<SavedConcordResponse>()).CommanderId);
            Assert.DoesNotContain(world.Set<WorldNpc>(), n => n.Role == NpcRole.Concord);
            Assert.Equal(Fixture.ShipId, world.Pilots.Find(Fixture.PilotId)!.ActiveShipId);
        }
    }

    [Fact]
    public void AttackingConcordChecksSafetyAndCannotDestroyOrRewardAnEnforcementShip()
    {
        using Fixture f = new(liveWorld: true, useTestClock: true);
        Catalog(f);
        Attack(f);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(7));
        Store(f).Advance(Fixture.PilotId, f.Clock.Now);
        long commander;
        using (WorldDbContext world = new(f.Db.Path))
        {
            commander = Assert.Single(world.Set<SavedConcordResponse>()).CommanderId!.Value;
            world.Pilots.Find(Fixture.PilotId)!.SafetyLevel = ShipSafetyLevel.Full;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Weapons.Fire(Fixture.PilotId, f.Module, commander, SpacePosition.Zero, f.Clock.Now, 0));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Pilots.Find(Fixture.PilotId)!.SafetyLevel = ShipSafetyLevel.Disabled;
            world.SaveChanges();
        }
        var shot = f.Weapons.Fire(Fixture.PilotId, f.Module, commander, SpacePosition.Zero, f.Clock.Now, 0);
        Assert.Equal(0, shot.Applied);
        Assert.False(shot.Destroyed);
        Assert.Null(shot.Reward);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<SavedConcordResponse>());
        Assert.Empty(saved.Set<NpcBounty>());
    }

    [Fact]
    public void PaidMissileImpactCannotRecheckSafetyOrFlagTheReplacementCapsule()
    {
        using Fixture f = new(useTestClock: true);
        Catalog(f);
        Attack(f);
        Store(f).Advance(Fixture.PilotId, f.Clock.Now + TimeSpan.FromSeconds(7).Ticks);
        using WorldDbContext world = new(f.Db.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var source = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == Fixture.PilotId);
        source.SafetyLevel = ShipSafetyLevel.Full;
        var target = world.Set<WorldNpc>().Single(n => n.TypeId == ConcordRules.CommanderType);
        var result = NpcDamagePersistence.Apply(
            world,
            catalog,
            target,
            source,
            Fixture.ShipId,
            PilotFittingStore.ReadAttributes(catalog, target.TypeId),
            new(1, 1, 1, 1),
            f.Clock.Now,
            paidProjectile: true
        );
        Assert.Equal(0, result.Applied);
        Assert.False(result.Destroyed);
        Assert.Single(world.Set<SavedConcordResponse>());
    }

    [Fact]
    public void PreviousVersionMigrationIsIdempotentAndPreservesPilotsAndInventory()
    {
        using Fixture f = new(useTestClock: true);
        var before = f.Pilot;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldConcordResponse; PRAGMA user_version=125");
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(before, f.Pilot);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<SavedConcordResponse>());
        Assert.NotNull(saved.Set<Eden.Data.World.Inventory.OwnedItem>().Find(f.Module));
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public void HostMaintenanceSettlesDisconnectedOffendersAtTheSavedDeadline()
    {
        using Fixture f = new(liveWorld: true, useTestClock: true);
        Catalog(f);
        Attack(f);
        f.Db.Services.GetRequiredService<IPilotCrimewatch>().Disconnect(Fixture.PilotId, Fixture.ShipId);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(22));
        f.Db.Services.GetRequiredService<ConcordMaintenance>().Tick();
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(Fixture.ShipId, Assert.Single(saved.Set<ShipLoss>()).ShipId);
        Assert.NotNull(Assert.Single(saved.Set<SavedConcordResponse>()).CompletedAt);
    }
}
