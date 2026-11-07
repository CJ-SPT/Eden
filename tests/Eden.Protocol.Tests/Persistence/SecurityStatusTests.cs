using Eden.Data.Catalog;
using Eden.Data.Characters;
using Eden.Data.Combat;
using Eden.Data.Combat.Npcs;
using Eden.Data.Loot;
using Eden.Data.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Protocol.Tests.Combat;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Loot;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class SecurityTestData
{
    internal const long Pilot = 90000001;

    internal static void CatalogReward(TestWorld db, double percent = 0.005, double cap = 5)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        foreach (
            var (attribute, value) in new[]
            {
                (DogmaAttributeId.EntitySecurityStatusKillBonus, percent),
                (DogmaAttributeId.EntitySecurityMaxGain, cap),
            }
        )
        {
            if (catalog.Set<DogmaAttribute>().Find((int)attribute) is null)
            {
                catalog.Add(new DogmaAttribute { Id = (int)attribute, Name = attribute.ToString() });
            }
            var row = catalog
                .Set<TypeAttribute>()
                .AsTracking()
                .SingleOrDefault(a => a.ItemTypeId == 30380 && a.AttributeId == (int)attribute);
            if (row is null)
            {
                row = catalog.Add(new TypeAttribute { ItemTypeId = 30380, AttributeId = (int)attribute }).Entity;
            }
            row.Value = value;
        }
        catalog.SaveChanges();
    }

    internal static void Record(TestWorld db, long now, long npc = 1, double percent = 1, double cap = 5, int system = 30000144)
    {
        using WorldDbContext world = new(db.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        PilotSecurityStatusStore.RecordKill(
            world,
            catalog,
            world.Pilots.Find(Pilot)!,
            new WorldNpc
            {
                Id = npc,
                TypeId = 30380,
                SystemId = system,
            },
            new Dictionary<DogmaAttributeId, double>
            {
                [DogmaAttributeId.EntitySecurityStatusKillBonus] = percent,
                [DogmaAttributeId.EntitySecurityMaxGain] = cap,
            },
            now
        );
        world.SaveChanges();
        transaction.Commit();
        db.Services.GetRequiredService<SecurityStatusSignal>().Publish();
    }
}

public sealed class SecurityStatusTests
{
    [Fact]
    public async Task ConcurrentSettlementReadsApplyOneHistoryEntryAndOneStatusChange()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var security = db.Services.GetRequiredService<IPilotSecurityStatus>();
        SecurityTestData.Record(db, clock.Now);
        clock.AdvanceWallTime(SecurityStatusRules.RewardWindow);
        long revision = security.Revision;
        var views = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => security.Read(SecurityTestData.Pilot))));
        Assert.All(views, view => Assert.Equal(0.1, view.Value, 10));
        Assert.Equal(revision + 1, security.Revision);
        Assert.Single(security.History(SecurityTestData.Pilot));
    }

    [Fact]
    public void ZeroBonusAndAlreadyCappedKillsDoNotOpenSpuriousRewardWindows()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        SecurityTestData.Record(db, clock.Now, percent: 0);
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Empty(world.Set<PendingSecurityReward>());
            world.Add(new SavedSecurityStatus { PilotId = SecurityTestData.Pilot, Value = 5 });
            world.SaveChanges();
        }
        SecurityTestData.Record(db, clock.Now, percent: 1, cap: 5);
        var security = db.Services.GetRequiredService<IPilotSecurityStatus>();
        Assert.Equal(new SecurityStatusView(5, null), security.Read(SecurityTestData.Pilot));
        Assert.Empty(security.History(SecurityTestData.Pilot));
    }

    [Fact]
    public void StrongestEligibleKillWinsOneCharacterWindowAcrossSystemsAndDoesNotMoveItsDeadline()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var security = db.Services.GetRequiredService<IPilotSecurityStatus>();
        SecurityTestData.Record(db, clock.Now, 1, 1);
        long due = security.Read(SecurityTestData.Pilot).PendingAt!.Value;
        clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        SecurityTestData.Record(db, clock.Now, 2, 2, system: 30000142);
        SecurityTestData.Record(db, clock.Now, 3, 0.5);
        Assert.Equal(new SecurityStatusView(0, due), security.Read(SecurityTestData.Pilot));
        clock.AdvanceWallTime(TimeSpan.FromTicks(due - clock.Now - 1));
        Assert.Empty(security.History(SecurityTestData.Pilot));
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Equal(0.2, security.Read(SecurityTestData.Pilot).Value, 10);
        var entry = Assert.Single(security.History(SecurityTestData.Pilot));
        Assert.Equal(2, entry.NpcId);
        Assert.Equal(30000142, entry.LocationId);
        Assert.Equal(due, entry.Time);
        Assert.Equal(0.02, entry.Modification, 10);
        Assert.Null(security.Read(SecurityTestData.Pilot).PendingAt);
        Assert.Equal(0.2, security.Read(SecurityTestData.Pilot).Value, 10);
        Assert.Single(security.History(SecurityTestData.Pilot));
    }

    [Fact]
    public void ACommitAtTheExactDeadlineFinishesTheOldWindowAndOpensOnlyOneNewWindow()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var security = db.Services.GetRequiredService<IPilotSecurityStatus>();
        SecurityTestData.Record(db, clock.Now);
        clock.AdvanceWallTime(SecurityStatusRules.RewardWindow);
        SecurityTestData.Record(db, clock.Now, 2);
        Assert.Equal(0.1, security.Read(SecurityTestData.Pilot).Value, 10);
        Assert.Equal(clock.Now + SecurityStatusRules.RewardWindow.Ticks, security.Read(SecurityTestData.Pilot).PendingAt);
        clock.AdvanceWallTime(SecurityStatusRules.RewardWindow);
        Assert.Equal(0.199, security.Read(SecurityTestData.Pilot).Value, 10);
        Assert.Equal(2, security.History(SecurityTestData.Pilot).Count);
    }

    [Fact]
    public void PendingRewardsSurviveRestartAndSettleOnceEvenWhenThePilotWasOfflinePastTheDeadline()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        SecurityTestData.Record(db, clock.Now);
        WorldInitialization.Open(db.Path);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        using var reopened = ServerServices.Build(new(db.Path), clock);
        var security = reopened.GetRequiredService<IPilotSecurityStatus>();
        Assert.Equal(0.1, security.Read(SecurityTestData.Pilot).Value, 10);
        Assert.Single(security.History(SecurityTestData.Pilot));
        Assert.Empty(security.History(90000002));
        Assert.Throws<InvalidDataException>(() => security.Read(long.MaxValue));
    }

    [Theory]
    [InlineData(AccountCloneGrade.Alpha, 0.1)]
    [InlineData(AccountCloneGrade.Omega, 0.125)]
    public void FastTalkUsesItsPinnedMutatorAndEffectiveCloneGradeLevel(AccountCloneGrade grade, double expected)
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Add(
                new ItemType
                {
                    Id = (int)SecuritySkill.FastTalk,
                    Name = "Fast Talk",
                    GroupId = 257,
                }
            );
            catalog.Add(
                new TypeAttribute
                {
                    ItemTypeId = (int)SecuritySkill.FastTalk,
                    AttributeId = (int)DogmaAttributeId.SkillTimeConstant,
                    Value = 4,
                }
            );
            catalog.Add(new DogmaAttribute { Id = (int)DogmaAttributeId.FastTalkMutator, Name = "fastTalkMutator" });
            catalog.Add(
                new TypeAttribute
                {
                    ItemTypeId = (int)SecuritySkill.FastTalk,
                    AttributeId = (int)DogmaAttributeId.FastTalkMutator,
                    Value = 5,
                }
            );
            var alpha = catalog.Set<SourceRecord>().AsTracking().Single(r => r.Dataset == "client/clonegrades" && r.Key == "\"1\"");
            alpha.Data = "{\"skills\":[{\"typeID\":3358,\"level\":0}]}";
            catalog.SaveChanges();
        }
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<Account>().Find(1000001L)!.CloneGrade = grade;
            world.Add(
                new PilotSkill
                {
                    PilotId = SecurityTestData.Pilot,
                    TypeId = (int)SecuritySkill.FastTalk,
                    Points = 1024000,
                }
            );
            world.SaveChanges();
        }
        SecurityTestData.Record(db, clock.Now);
        clock.AdvanceWallTime(SecurityStatusRules.RewardWindow);
        Assert.Equal(expected, db.Services.GetRequiredService<IPilotSecurityStatus>().Read(SecurityTestData.Pilot).Value, 10);
    }

    [Fact]
    public void GenuineVersion53MigrationStartsNeutralAndRetainsExistingShipAppearance()
    {
        using TestWorld db = new();
        long ship = db.Store.FindPilot(1000001)!.ShipId;
        var dirt = db.Services.GetRequiredService<IPilotShipAppearance>().Read(SecurityTestData.Pilot, ship);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "DROP TABLE WorldSecurityTransaction; DROP TABLE WorldSecurityPending; "
                    + "DROP TABLE WorldPilotSecurity; PRAGMA user_version=53;"
            );
        }
        WorldInitialization.Open(db.Path);
        var security = db.Services.GetRequiredService<IPilotSecurityStatus>();
        Assert.Equal(new SecurityStatusView(0, null), security.Read(SecurityTestData.Pilot));
        Assert.Equal(dirt, db.Services.GetRequiredService<IPilotShipAppearance>().Read(SecurityTestData.Pilot, ship));
        using WorldDbContext verify = new(db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, verify.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public void AFailedHistoryWriteRetainsThePendingRewardAndNeverPartiallyChangesSecurity()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        SecurityTestData.Record(db, clock.Now);
        using var sql = new SqliteConnection($"Data Source={db.Path};Pooling=false");
        sql.Open();
        using var command = sql.CreateCommand();
        command.CommandText =
            "CREATE TRIGGER fail_security BEFORE INSERT ON WorldSecurityTransaction BEGIN SELECT RAISE(ABORT,'history failed'); END";
        command.ExecuteNonQuery();
        clock.AdvanceWallTime(SecurityStatusRules.RewardWindow);
        var security = db.Services.GetRequiredService<IPilotSecurityStatus>();
        long revision = security.Revision;
        Assert.Throws<DbUpdateException>(() => security.Read(SecurityTestData.Pilot));
        Assert.Equal(revision, security.Revision);
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Single(world.Set<PendingSecurityReward>());
            Assert.Empty(world.Set<SavedSecurityStatus>());
            Assert.Empty(world.Set<SavedSecurityTransaction>());
        }
        command.CommandText = "DROP TRIGGER fail_security";
        command.ExecuteNonQuery();
        Assert.Equal(0.1, security.Read(SecurityTestData.Pilot).Value, 10);
        Assert.Single(security.History(SecurityTestData.Pilot));
    }

    [Fact]
    public void RealTurretKillsQueueRewardsAndAQueueFailureRollsBackTheKillBountyAndWreck()
    {
        var clock = CalendarTestData.Clock();
        using PilotCombatTests.Fixture f = new(clock);
        SecurityTestData.CatalogReward(f.Db);
        f.AlmostDefeated();
        using var sql = new SqliteConnection($"Data Source={f.Db.Path};Pooling=false");
        sql.Open();
        using var command = sql.CreateCommand();
        command.CommandText =
            "CREATE TRIGGER fail_pending BEFORE INSERT ON WorldSecurityPending BEGIN SELECT RAISE(ABORT,'pending failed'); END";
        command.ExecuteNonQuery();
        var security = f.Db.Services.GetRequiredService<IPilotSecurityStatus>();
        long revision = security.Revision;
        Assert.Throws<DbUpdateException>(() => f.Fire());
        Assert.Equal(revision, security.Revision);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Null(world.Set<WorldNpc>().Find(f.Npc.Id)!.DestroyedAt);
            Assert.Empty(world.Set<NpcBounty>());
            Assert.Empty(world.Set<WorldWreck>());
            Assert.Empty(world.Set<PendingSecurityReward>());
        }
        command.CommandText = "DROP TRIGGER fail_pending";
        command.ExecuteNonQuery();
        Assert.True(f.Fire().Destroyed);
        Assert.Equal(0, security.Read(SecurityTestData.Pilot).Value);
        clock.AdvanceWallTime(SecurityStatusRules.RewardWindow);
        Assert.Equal(0.0005, security.Read(SecurityTestData.Pilot).Value, 10);
        Assert.Equal(f.Npc.Id, Assert.Single(security.History(SecurityTestData.Pilot)).NpcId);
    }

    [Fact]
    public void RealMissileKillsUseTheSamePendingRewardAndCannotApplyItTwice()
    {
        using var f = MissileCombatTests.Prepare(499, 212);
        long launcher = MissileCombatTests.Load(f, 499, 212);
        SecurityTestData.CatalogReward(f.Db);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            foreach (
                var attribute in new[]
                {
                    DogmaAttributeId.ShieldCapacity,
                    DogmaAttributeId.ArmorHitPoints,
                    DogmaAttributeId.StructureHitPoints,
                }
            )
            {
                double value = attribute == DogmaAttributeId.StructureHitPoints ? 1 : 0;
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (30380,{(int)attribute},{value})");
            }
        }
        var npc = MissileCombatTests.Space(f);
        MissileCombatTests.Launch(f, launcher, npc.Id);
        f.Clock.Milliseconds += 1000;
        MissileCombatTests.Missiles(f).Advance(npc.SystemId, f.Clock.Now);
        Assert.True(Assert.Single(MissileCombatTests.Missiles(f).TakeImpacts(f.PilotId)).Destroyed);
        var security = f.Db.Services.GetRequiredService<IPilotSecurityStatus>();
        Assert.Equal(0, security.Read(f.PilotId).Value);
        f.Clock.AdvanceWallTime(SecurityStatusRules.RewardWindow);
        Assert.Equal(0.0005, security.Read(f.PilotId).Value, 10);
        Assert.Single(security.History(f.PilotId));
        Assert.Single(security.History(f.PilotId));
    }
}
