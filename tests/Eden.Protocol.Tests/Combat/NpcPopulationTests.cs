using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Skills;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcPopulationTests
{
    private const int OtherType = 30381;
    private const int OtherSystem = 30000142;
    private const int OtherCorporation = 1000044;
    private const long Pilot = 90000001;
    private const long Ship = 1000000000001;
    private const long Npc = NpcEncounterDefinitions.FirstNpcId + 1;
    private const long Now = 134000000000000000;

    private static void Prepare(TestWorld db, NpcRole role = NpcRole.Pirate)
    {
        NpcEncounterTests.Prepare(db);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Add(
                new ItemType
                {
                    Id = OtherType,
                    Name = "Other pirate",
                    GroupId = 286,
                    Mass = 2000000,
                    Radius = 60,
                }
            );
            catalog.SaveChanges();
            catalog.Database.ExecuteSqlRaw(
                "INSERT INTO TypeAttribute SELECT 30381,AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=30380"
            );
        }
        using WorldDbContext world = new(db.Path);
        world.Add(
            new WorldNpc
            {
                Id = Npc,
                TypeId = OtherType,
                SystemId = OtherSystem,
                CorporationId = OtherCorporation,
                FactionId = NpcEncounterDefinitions.GuristasFaction,
                Role = role,
                AnchorKind = NpcAnchorKind.Station,
                EncounterId = 60003760,
                HomeX = 1000000,
                HomeY = 0,
                HomeZ = 0,
                X = 1000000,
                ShieldCharge = 20,
                UpdatedAt = Now,
            }
        );
        world.SaveChanges();
    }

    [Fact]
    public void SavedMixedTypesAndStationAnchorsRetainTheirOwnIdentityAndHealthOnReopen()
    {
        using TestWorld db = new();
        Prepare(db);
        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        var contact = Assert.Single(encounters.List(OtherSystem, Now));
        Assert.Equal(OtherType, contact.TypeId);
        Assert.Equal("Other pirate", contact.Name);
        Assert.Equal(OtherCorporation, contact.CorporationId);
        Assert.Equal(60, contact.Radius);
        using (WorldDbContext world = new(db.Path))
        {
            var npc = world.Set<WorldNpc>().Find(Npc)!;
            npc.ArmorDamage = 7;
            npc.Navigation = NpcNavigationState.Returning;
            npc.X += 1000;
            npc.MotionAt = Now;
            world.SaveChanges();
        }
        _ = WorldInitialization.Open(db.Path);
        var reopened = Assert.Single(encounters.List(OtherSystem, Now + TimeSpan.TicksPerSecond));
        Assert.Equal(8, reopened.Health.Remaining.Armor);
        Assert.True(reopened.Position.X < contact.Position.X + 1000);
        Assert.Equal(2, encounters.List(NpcEncounterDefinitions.PerimeterSystem, Now).Count);
    }

    [Theory]
    [InlineData(NpcRole.Pirate, true)]
    [InlineData(NpcRole.Navy, false)]
    [InlineData(NpcRole.Customs, false)]
    public void OnlyPiratesAcquirePeacefulPilotsOutsidePerimeter(NpcRole role, bool attacks)
    {
        using TestWorld db = new();
        Prepare(db, role);
        db.Store.SetPosition(Ship, new(1000100, 0, 0), OtherSystem);
        var combat = db.Services.GetRequiredService<INpcCombat>();
        combat.Acquire([new(Pilot, Ship)], Now);
        Assert.Equal(attacks, combat.Aggressors(Ship).Contains(Npc));
        combat.Acquire([new(Pilot, Ship)], Now + TimeSpan.TicksPerSecond);
        using WorldDbContext world = new(db.Path);
        Assert.Equal(attacks ? 1 : 0, world.Set<NpcAggression>().Count());
    }

    [Fact]
    public void AcquisitionCannotChooseAPilotInAnotherSystem()
    {
        using TestWorld db = new();
        Prepare(db);
        db.Store.SetPosition(Ship, new(1000100, 0, 0), NpcEncounterDefinitions.PerimeterSystem);
        var combat = db.Services.GetRequiredService<INpcCombat>();
        combat.Acquire([new(Pilot, Ship)], Now);
        Assert.Empty(combat.Aggressors(Ship));
    }

    [Fact]
    public void MultipleTypesInOneSystemKeepIndependentCatalogProfiles()
    {
        using TestWorld db = new();
        Prepare(db);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<WorldNpc>().Find(Npc)!.SystemId = NpcEncounterDefinitions.PerimeterSystem;
            world.SaveChanges();
        }
        var contacts = db.Services.GetRequiredService<INpcEncounters>().List(NpcEncounterDefinitions.PerimeterSystem, Now);
        Assert.Equal(3, contacts.Count);
        Assert.Equal(60, contacts.Single(n => n.TypeId == OtherType).Radius);
        Assert.All(contacts.Where(n => n.TypeId != OtherType), n => Assert.Equal(50, n.Radius));
    }

    [Theory]
    [InlineData(NpcRole.Pirate, true)]
    [InlineData(NpcRole.Navy, false)]
    [InlineData(NpcRole.Customs, false)]
    public void KillRewardsUseRoleAndActualCorporation(NpcRole role, bool rewarded)
    {
        using TestWorld db = new();
        Prepare(db, role);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT INTO SourceRecord VALUES ('client/types','30381','{{\"wreckTypeID\":26557}}')");
        }
        using WorldDbContext world = new(db.Path);
        using CatalogDbContext lookup = new(new WorldDatabase(db.Path).CatalogPath);
        using var transaction = world.Database.BeginTransaction();
        var npc = world.Set<WorldNpc>().Find(Npc)!;
        var attributes = PilotFittingStore.ReadAttributes(lookup, OtherType).ToDictionary();
        attributes[DogmaAttributeId.EntityKillBounty] = 3000;
        attributes[DogmaAttributeId.EntitySecurityStatusKillBonus] = 1;
        attributes[DogmaAttributeId.EntitySecurityMaxGain] = 5;
        var result = NpcDamagePersistence.Apply(world, lookup, npc, world.Pilots.Find(Pilot)!, Ship, attributes, new(100000, 0, 0, 0), Now);
        world.SaveChanges();
        transaction.Commit();
        Assert.True(result.Destroyed);
        Assert.Equal(rewarded, result.Reward is not null);
        var pending = world.Set<PendingSecurityReward>().Find(Pilot);
        Assert.Equal(rewarded, pending is not null);
        if (pending is not null)
        {
            Assert.Equal(OtherCorporation, pending.CorporationId);
        }
        Assert.Equal(rewarded ? 1 : 0, world.Set<NpcBounty>().Count());
        Assert.Equal(rewarded ? 7 : 0, world.Set<SavedFreeSkillPoints>().Sum(p => p.Points));
    }

    [Fact]
    public void RetirementHidesNpcWithoutRecordingAKillOrAllowingAcquisition()
    {
        using TestWorld db = new();
        Prepare(db);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<WorldNpc>().Find(Npc)!.RetiredAt = Now;
            world.SaveChanges();
        }
        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        Assert.Empty(encounters.List(OtherSystem, Now));
        Assert.False(encounters.WasDestroyed(Npc));
        db.Store.SetPosition(Ship, new(1000100, 0, 0), OtherSystem);
        var combat = db.Services.GetRequiredService<INpcCombat>();
        combat.Acquire([new(Pilot, Ship)], Now);
        Assert.Empty(combat.Aggressors(Ship));
    }

    [Fact]
    public void Version64MigrationPreservesHistoricalNpcIdentityAndDamage()
    {
        using TestWorld db = new();
        NpcEncounterTests.Prepare(db);
        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        var original = encounters.List(NpcEncounterDefinitions.PerimeterSystem, Now)[0];
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<WorldNpc>().Find(original.Id)!.ArmorDamage = 5;
            world.SaveChanges();
            world.Database.ExecuteSqlRaw(
                """
                DROP INDEX IX_WorldNpc_EncounterId;
                DROP INDEX IX_WorldNpc_SystemId_RetiredAt_DestroyedAt;
                DROP INDEX IX_WorldNpc_EncounterId_SpawnSlot;
                DROP INDEX IX_WorldNpc_EncounterId_DestroyedAt_RetiredAt;
                """
            );
            string[] columns =
            [
                "CorporationId",
                "FactionId",
                "Role",
                "AnchorKind",
                "EncounterId",
                "HomeX",
                "HomeY",
                "HomeZ",
                "RetiredAt",
                "SpawnSlot",
            ];
            foreach (string column in columns)
            {
                string sql = $"ALTER TABLE WorldNpc DROP COLUMN {column}";
                world.Database.ExecuteSqlRaw(sql);
            }
            world.Database.ExecuteSqlRaw("PRAGMA user_version=64");
        }
        _ = WorldInitialization.Open(db.Path);
        var migrated = encounters.List(NpcEncounterDefinitions.PerimeterSystem, Now).Single(n => n.Id == original.Id);
        Assert.Equal(10, migrated.Health.Remaining.Armor);
        Assert.Equal(NpcEncounterDefinitions.GuristasCorporation, migrated.CorporationId);
        Assert.Equal(NpcEncounterDefinitions.GuristasFaction, migrated.FactionId);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(2, saved.Set<WorldNpc>().Count());
        Assert.Empty(saved.Set<NpcBounty>());
    }

    [Fact]
    public void LargeWeaponRangesExpandDetectionAndLeashWithoutChangingRookiePolicy()
    {
        Dictionary<DogmaAttributeId, double> attributes = [];
        Assert.Equal(NpcEncounterDefinitions.DetectionRange, NpcPopulationRules.DetectionRange(attributes));
        Assert.Equal(NpcEncounterDefinitions.ChaseLeash, NpcPopulationRules.Leash(attributes));
        attributes[DogmaAttributeId.EntityAttackRange] = 80000;
        Assert.Equal(80000, NpcPopulationRules.DetectionRange(attributes));
        Assert.Equal(160000, NpcPopulationRules.Leash(attributes));
    }
}
