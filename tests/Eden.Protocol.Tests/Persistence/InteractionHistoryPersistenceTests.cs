using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Protocol.Tests.Combat;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Loot;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class InteractionHistoryPersistenceTests
{
    private const long PilotId = 90000001;
    private const int FactionId = 500001;

    internal static IPilotInteractionHistory History(TestWorld db) => db.Services.GetRequiredService<IPilotInteractionHistory>();

    internal static void PrepareFaction(PilotCombatTests.Fixture fixture, double fraction = 0.001)
    {
        using WorldDbContext world = new(fixture.Db.Path);
        world.Set<WorldNpc>().Find(fixture.Npc.Id)!.FactionId = FactionId;
        world.SaveChanges();
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO DogmaAttribute VALUES (562,'entityFactionLoss')");
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (30380,562,{fraction})");
    }

    [Fact]
    public void RealTurretKillPersistsCombatLootAndFactionChangeTogetherAcrossRestart()
    {
        using PilotCombatTests.Fixture fixture = new();
        PrepareFaction(fixture);
        fixture.AlmostDefeated();
        Assert.Empty(History(fixture.Db).Combat(PilotId, 25, null));
        var shot = fixture.Fire();
        Assert.True(shot.Destroyed);
        var report = Assert.Single(History(fixture.Db).Combat(PilotId, 25, null));
        Assert.False(report.Historical);
        Assert.Equal(fixture.Clock.Now, report.Time);
        Assert.Equal(fixture.Npc.SystemId, report.SystemId);
        Assert.Null(report.Victim.CharacterId);
        Assert.Equal(fixture.Npc.TypeId, report.Victim.ShipTypeId);
        Assert.Equal(fixture.Npc.Id, report.Victim.EntityId);
        Assert.Equal(PilotId, report.FinalBlow.CharacterId);
        Assert.Equal(601, report.FinalBlow.ShipTypeId);
        Assert.Equal(shot.Weapon.TypeId, report.FinalBlow.WeaponTypeId);
        Assert.Equal(shot.Applied, report.Victim.Damage, 8);
        Assert.Equal(shot.Applied, report.FinalBlow.Damage, 8);
        Assert.NotEmpty(report.Items);
        Assert.All(report.Items, item => Assert.True(item.Dropped > 0 && item.Destroyed == 0));
        var change = Assert.Single(History(fixture.Db).Transactions(PilotId, FactionId));
        Assert.Equal(0, change.Before);
        Assert.Equal(-0.01, change.After, 8);
        Assert.Equal(StandingEvent.CombatShipKill, change.Event);
        Assert.Equal(fixture.Npc.TypeId, change.VictimTypeId);
        Assert.Equal(fixture.Npc.CorporationId, change.VictimCorporationId);
        Assert.Equal(change.After, Assert.Single(History(fixture.Db).Standings(PilotId)).Value);
        Assert.Empty(History(fixture.Db).Combat(90000002, 25, null));
        Assert.Empty(History(fixture.Db).Standings(90000002));
        Assert.Null(History(fixture.Db).Report(90000002, report.Id));
        Assert.Throws<InvalidDataException>(() => fixture.Fire());
        Assert.Single(History(fixture.Db).Combat(PilotId, 25, null));
        Assert.Single(History(fixture.Db).Transactions(PilotId, FactionId));
        _ = WorldInitialization.Open(fixture.Db.Path);
        using var reopened = ServerServices.Build(new(fixture.Db.Path));
        var history = reopened.GetRequiredService<IPilotInteractionHistory>();
        Assert.Equivalent(report, Assert.Single(history.Combat(PilotId, 25, null)));
        Assert.Equal(change, Assert.Single(history.Transactions(PilotId, FactionId)));
        using WorldDbContext saved = new(fixture.Db.Path);
        Assert.Empty(saved.Set<SavedCombatDamage>());
    }

    [Fact]
    public void InvalidFactionInputRollsBackKillBountyWreckAndAllHistory()
    {
        using PilotCombatTests.Fixture fixture = new();
        PrepareFaction(fixture, 1.5);
        fixture.AlmostDefeated();
        Assert.Throws<InvalidDataException>(() => fixture.Fire());
        using WorldDbContext world = new(fixture.Db.Path);
        Assert.Null(world.Set<WorldNpc>().Find(fixture.Npc.Id)!.DestroyedAt);
        Assert.Empty(world.Set<SavedCombatReport>());
        Assert.Empty(world.Set<SavedCombatDamage>());
        Assert.Empty(world.Set<SavedStandingTransaction>());
        Assert.Empty(world.Set<SavedNpcStanding>());
        Assert.Empty(world.Set<NpcBounty>());
        Assert.Empty(world.Set<WorldWreck>());
    }

    [Fact]
    public void RealNpcTurretLossSavesVictimKillerAndItemOutcomesExactlyOnce()
    {
        using NpcCombatTests.Fixture fixture = new();
        fixture.Aggress();
        fixture.AlmostLost();
        fixture.Clock.Milliseconds = 7500;
        var shot = Assert.IsType<NpcShot>(fixture.Fire());
        Assert.NotNull(shot.Capsule);
        var report = Assert.Single(History(fixture.Db).Combat(PilotId, 25, null));
        Assert.Equal(PilotId, report.Victim.CharacterId);
        Assert.Equal(NpcCombatTests.Fixture.ShipId, report.Victim.EntityId);
        Assert.Equal(601, report.Victim.ShipTypeId);
        Assert.Null(report.FinalBlow.CharacterId);
        Assert.Equal(fixture.Npc.Id, report.FinalBlow.EntityId);
        Assert.Equal(fixture.Npc.TypeId, report.FinalBlow.ShipTypeId);
        Assert.Equal(shot.Weapon.TurretTypeId, report.FinalBlow.WeaponTypeId);
        Assert.Equal(shot.Applied, report.Victim.Damage, 8);
        Assert.Equal(shot.Applied, report.FinalBlow.Damage, 8);
        Assert.NotEmpty(report.Items);
        Assert.All(report.Items, item => Assert.Equal(0, item.Destroyed));
        Assert.Null(fixture.Fire());
        Assert.Single(History(fixture.Db).Combat(PilotId, 25, null));
        _ = WorldInitialization.Open(fixture.Db.Path);
        using var reopened = ServerServices.Build(new(fixture.Db.Path));
        Assert.Equivalent(report, Assert.Single(reopened.GetRequiredService<IPilotInteractionHistory>().Combat(PilotId, 25, null)));
    }

    [Fact]
    public void MigrationBackfillsProvenKillsAndLossesOnceWithoutInventingOldStandingChanges()
    {
        using NpcCombatTests.Fixture fixture = new();
        fixture.Aggress();
        fixture.AlmostLost();
        fixture.Clock.Milliseconds = 7500;
        Assert.NotNull(fixture.Fire()!.Capsule);
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Add(
                new NpcBounty
                {
                    PilotId = PilotId,
                    NpcId = fixture.Npc.Id,
                    TypeId = fixture.Npc.TypeId,
                    SystemId = fixture.Npc.SystemId,
                    Time = fixture.Clock.Now - 1,
                    AmountCents = 300000,
                    BalanceCents = 300000,
                }
            );
            world.SaveChanges();
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldCombatDamage; DROP TABLE WorldCombatReport;
                DROP TABLE WorldStandingTransaction; DROP TABLE WorldNpcStanding;
                PRAGMA user_version=70;
                """
            );
        }
        _ = WorldInitialization.Open(fixture.Db.Path);
        var first = History(fixture.Db).Combat(PilotId, 25, null);
        Assert.Equal(2, first.Count);
        Assert.All(first, report => Assert.True(report.Historical));
        Assert.Equal(PilotId, first[0].Victim.CharacterId);
        Assert.Equal(PilotId, first[1].FinalBlow.CharacterId);
        Assert.Null(first[1].FinalBlow.ShipTypeId);
        Assert.Equal(0, first[0].Victim.Damage);
        Assert.NotEmpty(first[0].Items);
        Assert.Empty(History(fixture.Db).Transactions(PilotId, FactionId));
        _ = WorldInitialization.Open(fixture.Db.Path);
        Assert.Equivalent(first, History(fixture.Db).Combat(PilotId, 25, null));
        using WorldDbContext saved = new(fixture.Db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public void MixedCombatPagesRetainBothNativeFiltersAndExactCursorBoundaries()
    {
        using TestWorld db = new();
        using (WorldDbContext world = new(db.Path))
        {
            for (int index = 0; index < 60; index++)
            {
                CombatParty player = new(index + 1, PilotId, 1000044, null, 601, 3638, 0, 10);
                CombatParty npc = new(index + 100, null, 1000127, 500010, 30380, 562, 0, 10);
                PilotInteractionHistoryStore.Save(
                    world,
                    $"fixture:{index}",
                    new(0, PilotId, index, 30000144, index % 2 == 0 ? player : npc, index % 2 == 0 ? npc : player, [], [])
                );
            }
            world.SaveChanges();
        }
        var first = History(db).Combat(PilotId, 25, null);
        Assert.Equal(50, first.Count);
        Assert.Equal(25, first.Count(r => r.Victim.CharacterId == PilotId));
        Assert.Equal(25, first.Count(r => r.FinalBlow.CharacterId == PilotId));
        foreach (bool losses in new[] { false, true })
        {
            var page = first.Where(r => (r.Victim.CharacterId == PilotId) == losses).ToArray();
            long cursor = page.Min(r => r.Id);
            var next = History(db).Combat(PilotId, 25, cursor).Where(r => (r.Victim.CharacterId == PilotId) == losses).ToArray();
            Assert.Equal(5, next.Length);
            Assert.All(next, r => Assert.True(r.Id < cursor));
            Assert.Empty(next.Select(r => r.Id).Intersect(page.Select(r => r.Id)));
            var previous = History(db)
                .Combat(PilotId, 25, page.Max(r => r.Id) + 1)
                .Where(r => (r.Victim.CharacterId == PilotId) == losses)
                .ToArray();
            Assert.Equal(page.Select(r => r.Id), previous.Select(r => r.Id));
        }
        Assert.Empty(History(db).Combat(PilotId, 25, 0));
        Assert.Empty(History(db).Combat(90000002, 25, null));
        Assert.Throws<InvalidDataException>(() => History(db).Combat(PilotId, 0, null));
        Assert.Throws<InvalidDataException>(() => History(db).Combat(PilotId, 25, -1));
    }

    [Fact]
    public void DamageContributorsAccumulateAndSurviveReopenWithoutDuplicateFinalBlow()
    {
        using TestWorld db = new();
        CombatParty first = new(1001, PilotId, 1000044, null, 601, 3638, 0, 0);
        CombatParty final = new(1002, 90000002, 1000044, null, 601, 3638, 0, 0);
        CombatParty victim = new(9999, null, 1000127, 500010, 30380, null, null, 0);
        using (WorldDbContext world = new(db.Path))
        {
            PilotInteractionHistoryStore.Damage(world, victim.EntityId, first, 12.25);
            world.SaveChanges();
        }
        using (WorldDbContext world = new(db.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            PilotInteractionHistoryStore.Damage(world, victim.EntityId, first, 7.75);
            PilotInteractionHistoryStore.Damage(world, victim.EntityId, final, 5);
            PilotInteractionHistoryStore.Destroyed(world, 90000002, "npc:9999", 100, 30000144, victim, final, []);
            world.SaveChanges();
            transaction.Commit();
        }
        var report = Assert.Single(History(db).Combat(90000002, 25, null));
        Assert.Equal(25, report.Victim.Damage);
        Assert.Equal(5, report.FinalBlow.Damage);
        Assert.Equal(20, Assert.Single(report.OtherAttackers).Damage);
        using WorldDbContext saved = new(db.Path);
        Assert.Empty(saved.Set<SavedCombatDamage>());
    }

    [Theory]
    [InlineData(0, 0.001, -0.01)]
    [InlineData(5, 0.1, 3.5)]
    [InlineData(-5, 0.1, -5.5)]
    [InlineData(-10, 0.1, -10)]
    [InlineData(10, 1, -10)]
    [InlineData(1, 0, 1)]
    public void FactionLossUsesDistanceToLowerBound(double current, double fraction, double expected) =>
        Assert.Equal(expected, StandingRules.FactionLoss(current, fraction), 10);

    [Theory]
    [InlineData(double.NaN, 0.1)]
    [InlineData(double.PositiveInfinity, 0.1)]
    [InlineData(-11, 0.1)]
    [InlineData(11, 0.1)]
    [InlineData(0, -0.1)]
    [InlineData(0, 1.1)]
    [InlineData(0, double.NaN)]
    [InlineData(0, double.PositiveInfinity)]
    public void InvalidStandingValuesAndLossFractionsFail(double current, double fraction) =>
        Assert.Throws<InvalidDataException>(() => StandingRules.FactionLoss(current, fraction));
}
