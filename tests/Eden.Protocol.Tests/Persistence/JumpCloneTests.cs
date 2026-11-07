using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Protocol.Skills;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class JumpCloneTestData
{
    internal const long PilotId = HomeStationTestData.PilotId;
    internal const long Funds = 2_000_000_000;

    internal static void Prepare(TestWorld db)
    {
        HomeStationTestData.Prepare(db);
        InsuranceTestData.Fund(db, Funds);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        var operation =
            catalog.Set<SourceRecord>().AsTracking().SingleOrDefault(r => r.Dataset == "client/stationoperations" && r.Key == "14")
            ?? catalog.Add(new SourceRecord { Dataset = "client/stationoperations", Key = "14" }).Entity;
        operation.Data = "{\"services\":[24]}";
        var alpha = catalog.Set<SourceRecord>().AsTracking().Single(r => r.Dataset == "client/clonegrades" && r.Key == "\"1\"");
        alpha.Data = JsonSerializer.Serialize(
            new { skills = new[] { new { typeID = 3327, level = 4 }, new { typeID = (int)InfomorphSkill.Psychology, level = 1 } } }
        );
        if (catalog.Set<ItemCategory>().Find(16) is null)
        {
            catalog.Add(new ItemCategory { Id = 16, Name = "Skill" });
        }
        catalog.Add(
            new ItemGroup
            {
                Id = 1220,
                CategoryId = 16,
                Name = "Neural Enhancement",
            }
        );
        foreach (
            var (skill, rank) in new[]
            {
                (InfomorphSkill.Psychology, 1),
                (InfomorphSkill.AdvancedPsychology, 5),
                (InfomorphSkill.ElitePsychology, 12),
                (InfomorphSkill.Synchronizing, 2),
            }
        )
        {
            catalog.Add(
                new ItemType
                {
                    Id = (int)skill,
                    GroupId = 1220,
                    Name = skill.ToString(),
                }
            );
            catalog.Add(
                new TypeAttribute
                {
                    ItemTypeId = (int)skill,
                    AttributeId = (int)DogmaAttributeId.SkillTimeConstant,
                    Value = rank,
                }
            );
            catalog.Add(
                new TypeAttribute
                {
                    ItemTypeId = (int)skill,
                    AttributeId = (int)DogmaAttributeId.PrimaryAttribute,
                    Value = (int)DogmaAttributeId.Charisma,
                }
            );
            catalog.Add(
                new TypeAttribute
                {
                    ItemTypeId = (int)skill,
                    AttributeId = (int)DogmaAttributeId.SecondaryAttribute,
                    Value = (int)DogmaAttributeId.Willpower,
                }
            );
        }
        catalog.SaveChanges();
        Train(db, AccountCloneGrade.Omega, new(2, 0, 0, 2));
    }

    internal static void Train(TestWorld db, AccountCloneGrade grade, JumpCloneSkills levels)
    {
        using WorldDbContext world = new(db.Path);
        world.Set<Account>().Find(1000001L)!.CloneGrade = grade;
        foreach (
            var (skill, rank, level) in new[]
            {
                (InfomorphSkill.Psychology, 1, levels.Psychology),
                (InfomorphSkill.AdvancedPsychology, 5, levels.AdvancedPsychology),
                (InfomorphSkill.ElitePsychology, 12, levels.ElitePsychology),
                (InfomorphSkill.Synchronizing, 2, levels.Synchronizing),
            }
        )
        {
            var saved =
                world.Set<PilotSkill>().Find(PilotId, (int)skill)
                ?? world.Add(new PilotSkill { PilotId = PilotId, TypeId = (int)skill }).Entity;
            saved.Points = level == 0 ? 0 : TrainedSkill.PointsForLevel(rank, level);
        }
        world.SaveChanges();
    }
}

public sealed class JumpCloneTests
{
    [Fact]
    public void MultipleBodiesNamesFeesAndOwnershipSurviveReopenAndDestroyNeverRefunds()
    {
        using TestWorld db = new();
        JumpCloneTestData.Prepare(db);
        var clones = db.Services.GetRequiredService<IPilotJumpClones>();
        Assert.Null(clones.InstallLimit(JumpCloneTestData.PilotId));
        Assert.Equal(JumpCloneRules.FeeCents, clones.Price(JumpCloneTestData.PilotId));
        clones.Install(JumpCloneTestData.PilotId);
        clones.Install(JumpCloneTestData.PilotId);
        var bodies = clones.Read(JumpCloneTestData.PilotId).Clones;
        Assert.Equal(2, bodies.Count);
        Assert.All(bodies, c => Assert.Equal(HomeStationTestData.Origin, c.StationId));
        Assert.Equal(2, clones.InstallLimit(JumpCloneTestData.PilotId));
        Assert.Throws<InvalidDataException>(() => clones.Install(JumpCloneTestData.PilotId));
        clones.Rename(JumpCloneTestData.PilotId, bodies[0].Id, "  Étude — 学習  ");
        Assert.Throws<InvalidDataException>(() => clones.Rename(90000002, bodies[0].Id, "Stolen"));
        Assert.Throws<InvalidDataException>(() => clones.Destroy(90000002, bodies[0].Id));
        Assert.Throws<InvalidDataException>(() => clones.Rename(JumpCloneTestData.PilotId, bodies[0].Id, new('x', 101)));
        Assert.Throws<InvalidDataException>(() => clones.Rename(JumpCloneTestData.PilotId, bodies[0].Id, "bad\nname"));
        Assert.Equal(HomeStationTestData.Origin, clones.Destroy(JumpCloneTestData.PilotId, bodies[1].Id));
        Assert.Throws<InvalidDataException>(() => clones.Destroy(JumpCloneTestData.PilotId, bodies[1].Id));
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var body = Assert.Single(reopened.GetRequiredService<IPilotJumpClones>().Read(JumpCloneTestData.PilotId).Clones);
        Assert.Equal("Étude — 学習", body.Name);
        Assert.Equal(bodies[0].Id, body.Id);
        var journal = reopened.GetRequiredService<IPilotWalletReader>().Journal(JumpCloneTestData.PilotId);
        Assert.Equal(2, journal.JumpClonePayments.Count);
        Assert.All(journal.JumpClonePayments, p => Assert.Equal(JumpClonePaymentKind.Installation, p.Kind));
        Assert.Equal(
            JumpCloneTestData.Funds - 2 * JumpCloneRules.FeeCents,
            reopened.GetRequiredService<IPilotWalletReader>().Balance(JumpCloneTestData.PilotId)
        );
    }

    [Theory]
    [InlineData(AccountCloneGrade.Alpha, 0, 0, 0, 0, 0, 24)]
    [InlineData(AccountCloneGrade.Alpha, 5, 5, 5, 5, 1, 24)]
    [InlineData(AccountCloneGrade.Omega, 1, 0, 0, 0, 1, 24)]
    [InlineData(AccountCloneGrade.Omega, 5, 5, 5, 5, 15, 19)]
    [InlineData(AccountCloneGrade.Omega, 3, 2, 1, 4, 6, 20)]
    public void CatalogRanksAndAlphaSkillLimitsMatchTheNativeCharacterBrain(
        AccountCloneGrade grade,
        int basic,
        int advanced,
        int elite,
        int sync,
        int capacity,
        int hours
    )
    {
        using TestWorld db = new();
        JumpCloneTestData.Prepare(db);
        JumpCloneTestData.Train(db, grade, new(basic, advanced, elite, sync));
        var skills = db.Services.GetRequiredService<IPilotJumpCloneSkills>().ReadCloneSkills(JumpCloneTestData.PilotId);
        Assert.Equal(capacity, skills.Capacity);
        Assert.Equal(hours, skills.CooldownHours);
        using var scope = db.Services.CreateScope();
        var brain = scope.ServiceProvider.GetRequiredService<PilotBrain>().Read(JumpCloneTestData.PilotId, 1000000000001);
        Assert.Equal(capacity, brain.CharacterEffects.Where(e => e.Attribute == DogmaAttributeId.MaxJumpClones).Sum(e => e.Value));
        Assert.Equal(hours, 24 + brain.CharacterEffects.Where(e => e.Attribute == DogmaAttributeId.CloneJumpCooldown).Sum(e => e.Value));
    }

    [Fact]
    public void JumpsSwapBodiesRetainNamesAndHomeAndRespectCooldownAcrossRepeatedCyclesAndRestart()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        JumpCloneTestData.Prepare(db);
        var clones = db.Services.GetRequiredService<IPilotJumpClones>();
        var fleet = db.Services.GetRequiredService<IPilotFleet>();
        var pilot = db.Store.FindPilot(1000001)!;
        clones.Install(pilot.PilotId);
        var target = Assert.Single(clones.Read(pilot.PilotId).Clones);
        clones.Rename(pilot.PilotId, target.Id, "Learning");
        HomeStationTestData.Travel(db, HomeStationTestData.Other);
        var pod = fleet.Leave(pilot.PilotId, pilot.ShipId).Pilot;
        var first = clones.Jump(pilot.PilotId, pod.ShipId, target.StationId, target.Id, JumpCloneRules.FeeCents);
        Assert.Equal(HomeStationTestData.Origin, first.Pilot.StationId);
        Assert.NotEqual(pod.ShipId, first.Pilot.ShipId);
        long time = clock.Now;
        Assert.Equal(time, clones.Read(pilot.PilotId).LastJump);
        var outgoing = Assert.Single(clones.Read(pilot.PilotId).Clones);
        Assert.Equal(HomeStationTestData.Other, outgoing.StationId);
        Assert.Equal(HomeStationTestData.Origin, db.Services.GetRequiredService<IPilotHomeStation>().Read(pilot.PilotId).Id);
        Assert.Throws<InvalidDataException>(() => clones.Destroy(pilot.PilotId, target.Id));
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        clones = reopened.GetRequiredService<IPilotJumpClones>();
        Assert.Equal(time, clones.Read(pilot.PilotId).LastJump);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            var current = db.Store.FindPilot(1000001)!;
            var next = Assert.Single(clones.Read(pilot.PilotId).Clones);
            clock.AdvanceWallTime(TimeSpan.FromHours(22) - TimeSpan.FromTicks(1));
            Assert.Throws<InvalidDataException>(() =>
                clones.Jump(pilot.PilotId, current.ShipId, next.StationId, next.Id, JumpCloneRules.FeeCents)
            );
            clock.AdvanceWallTime(TimeSpan.FromTicks(1));
            var swapped = clones.Jump(pilot.PilotId, current.ShipId, next.StationId, next.Id, JumpCloneRules.FeeCents);
            Assert.Equal(next.StationId, swapped.Pilot.StationId);
            Assert.Equal(clock.Now, clones.Read(pilot.PilotId).LastJump);
        }
        Assert.Equal("Learning", Assert.Single(clones.Read(pilot.PilotId).Clones).Name);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(HomeStationTestData.Other, saved.Ships.Find(pilot.ShipId)!.StationId);
        Assert.Equal(2, saved.Ships.Count(s => s.OwnerId == pilot.PilotId));
        Assert.Equal(4, saved.Set<SavedJumpClonePayment>().Count(p => p.Kind == JumpClonePaymentKind.Activation));
    }

    [Fact]
    public void SameStationSwapsBypassButDoNotResetExistingTimerAndLapsedAlphaCanUseStoredBodies()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        JumpCloneTestData.Prepare(db);
        var clones = db.Services.GetRequiredService<IPilotJumpClones>();
        clones.Install(JumpCloneTestData.PilotId);
        clones.Install(JumpCloneTestData.PilotId);
        JumpCloneTestData.Train(db, AccountCloneGrade.Alpha, new(5, 5, 5, 5));
        Assert.Equal(1, clones.InstallLimit(JumpCloneTestData.PilotId));
        var pilot = db.Store.FindPilot(1000001)!;
        var current = db.Services.GetRequiredService<IPilotFleet>().Leave(pilot.PilotId, pilot.ShipId).Pilot;
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(new SavedJumpCloneTimer { PilotId = pilot.PilotId, Time = clock.Now });
            world.SaveChanges();
        }
        for (int cycle = 0; cycle < 3; cycle++)
        {
            var target = clones.Read(pilot.PilotId).Clones[0];
            current = clones.Jump(pilot.PilotId, current.ShipId, target.StationId, target.Id, JumpCloneRules.FeeCents).Pilot;
            Assert.Equal(clock.Now, clones.Read(pilot.PilotId).LastJump);
            Assert.Equal(2, clones.Read(pilot.PilotId).Clones.Count);
        }
        using WorldDbContext saved = new(db.Path);
        Assert.Single(saved.Set<SavedJumpClone>().Where(c => c.PilotId == pilot.PilotId && c.Active));
    }

    [Fact]
    public void InvalidOrFailedJumpsLeaveBodiesWalletTimerAndInventoryUnchanged()
    {
        using TestWorld db = new();
        JumpCloneTestData.Prepare(db);
        var clones = db.Services.GetRequiredService<IPilotJumpClones>();
        clones.Install(JumpCloneTestData.PilotId);
        var target = Assert.Single(clones.Read(JumpCloneTestData.PilotId).Clones);
        var pilot = db.Store.FindPilot(1000001)!;
        Assert.Throws<InvalidDataException>(() =>
            clones.Jump(pilot.PilotId, pilot.ShipId, target.StationId, target.Id, JumpCloneRules.FeeCents)
        );
        var current = db.Services.GetRequiredService<IPilotFleet>().Leave(pilot.PilotId, pilot.ShipId).Pilot;
        Assert.Throws<InvalidDataException>(() =>
            clones.Jump(pilot.PilotId, current.ShipId, HomeStationTestData.Other, target.Id, JumpCloneRules.FeeCents)
        );
        Assert.Throws<InvalidDataException>(() =>
            clones.Jump(90000002, current.ShipId, target.StationId, target.Id, JumpCloneRules.FeeCents)
        );
        Assert.Throws<InvalidDataException>(() =>
            clones.Jump(pilot.PilotId, pilot.ShipId, target.StationId, target.Id, JumpCloneRules.FeeCents)
        );
        Assert.Throws<InvalidDataException>(() => clones.Jump(pilot.PilotId, current.ShipId, target.StationId, target.Id, 0));
        long balance = db.Services.GetRequiredService<IPilotWalletReader>().Balance(pilot.PilotId);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectCloneSwap BEFORE UPDATE OF Active ON WorldJumpClone "
                    + "WHEN NEW.Active=1 BEGIN SELECT RAISE(ABORT,'forced failure'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() =>
            clones.Jump(pilot.PilotId, current.ShipId, target.StationId, target.Id, JumpCloneRules.FeeCents)
        );
        Assert.Equal(current, db.Store.FindPilot(1000001));
        Assert.Equal(balance, db.Services.GetRequiredService<IPilotWalletReader>().Balance(pilot.PilotId));
        Assert.Equal(target, Assert.Single(clones.Read(pilot.PilotId).Clones));
        Assert.Null(clones.Read(pilot.PilotId).LastJump);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(3, saved.Ships.Count());
        Assert.Single(saved.Set<SavedJumpClonePayment>());
        Assert.Empty(saved.Set<SavedJumpClone>().Where(c => c.Active));
    }

    [Fact]
    public void InstallationRequiresFacilitiesSkillsAndFundsButJumpingCanLeaveABodyAtAnOrdinaryStation()
    {
        using TestWorld db = new();
        JumpCloneTestData.Prepare(db);
        var clones = db.Services.GetRequiredService<IPilotJumpClones>();
        var pilot = db.Store.FindPilot(1000001)!;
        InsuranceTestData.Fund(db, JumpCloneRules.FeeCents - 1);
        Assert.Throws<InvalidDataException>(() => clones.Install(pilot.PilotId));
        Assert.Empty(clones.Read(pilot.PilotId).Clones);
        InsuranceTestData.Fund(db, JumpCloneTestData.Funds);
        clones.Install(pilot.PilotId);
        var target = Assert.Single(clones.Read(pilot.PilotId).Clones);
        JumpCloneTestData.Train(db, AccountCloneGrade.Omega, new(0, 0, 0, 0));
        Assert.Equal(0, clones.InstallLimit(pilot.PilotId));
        Assert.Throws<InvalidDataException>(() => clones.Install(pilot.PilotId));
        HomeStationTestData.Travel(db, HomeStationTestData.Other);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Set<Station>().AsTracking().Single(s => s.Id == HomeStationTestData.Other).OperationId = 999;
            catalog.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => clones.InstallLimit(pilot.PilotId));
        Assert.Throws<InvalidDataException>(() => clones.Install(pilot.PilotId));
        Assert.Equal(JumpCloneRules.FeeCents, clones.Price(pilot.PilotId));
        var current = db.Services.GetRequiredService<IPilotFleet>().Leave(pilot.PilotId, pilot.ShipId).Pilot;
        InsuranceTestData.Fund(db, JumpCloneRules.FeeCents - 1);
        Assert.Throws<InvalidDataException>(() =>
            clones.Jump(pilot.PilotId, current.ShipId, target.StationId, target.Id, JumpCloneRules.FeeCents)
        );
        Assert.Equal(current, db.Store.FindPilot(1000001));
        Assert.Null(clones.Read(pilot.PilotId).LastJump);
        InsuranceTestData.Fund(db, JumpCloneTestData.Funds);
        db.Store.SetPosition(current.ShipId, new(1, 2, 3));
        Assert.Throws<InvalidDataException>(() => clones.Price(pilot.PilotId));
        Assert.Throws<InvalidDataException>(() =>
            clones.Jump(pilot.PilotId, current.ShipId, target.StationId, target.Id, JumpCloneRules.FeeCents)
        );
        HomeStationTestData.Travel(db, HomeStationTestData.Other);
        clones.Jump(pilot.PilotId, current.ShipId, target.StationId, target.Id, JumpCloneRules.FeeCents);
        Assert.Equal(HomeStationTestData.Other, Assert.Single(clones.Read(pilot.PilotId).Clones).StationId);
    }

    [Fact]
    public async Task ConcurrentInstallationHonorsTheLimitAndLedgerFailureRollsBackBodyAndWallet()
    {
        using TestWorld db = new();
        JumpCloneTestData.Prepare(db);
        JumpCloneTestData.Train(db, AccountCloneGrade.Alpha, new(1, 0, 0, 0));
        var clones = db.Services.GetRequiredService<IPilotJumpClones>();
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectCloneFee BEFORE INSERT ON WorldJumpClonePayment " + "BEGIN SELECT RAISE(ABORT,'forced failure'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() => clones.Install(JumpCloneTestData.PilotId));
        Assert.Empty(clones.Read(JumpCloneTestData.PilotId).Clones);
        Assert.Equal(JumpCloneTestData.Funds, db.Services.GetRequiredService<IPilotWalletReader>().Balance(JumpCloneTestData.PilotId));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectCloneFee");
        }
        var results = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => Task.Run(() => Record.Exception(() => clones.Install(JumpCloneTestData.PilotId))))
        );
        Assert.Single(results, result => result is null);
        Assert.IsType<InvalidDataException>(Assert.Single(results, result => result is not null));
        Assert.Single(clones.Read(JumpCloneTestData.PilotId).Clones);
        Assert.Equal(
            JumpCloneTestData.Funds - JumpCloneRules.FeeCents,
            db.Services.GetRequiredService<IPilotWalletReader>().Balance(JumpCloneTestData.PilotId)
        );
        Assert.Single(db.Services.GetRequiredService<IPilotWalletReader>().Journal(JumpCloneTestData.PilotId).JumpClonePayments);
    }

    [Theory]
    [InlineData(31000001, 11000001, false)]
    [InlineData(31000005, 11000001, true)]
    [InlineData(30000142, 10000070, false)]
    [InlineData(30000142, 10000002, true)]
    public void RemoteLocationRestrictionsIncludeTheraException(int system, int region, bool allowed)
    {
        var error = Record.Exception(() => JumpCloneRules.CheckLocation(system, region));
        if (allowed)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.IsType<InvalidDataException>(error);
        }
    }

    [Fact]
    public void VersionFortySixUpgradeIsAdditiveAndRepeatedOpeningPreservesSavedBodies()
    {
        using TestWorld db = new();
        JumpCloneTestData.Prepare(db);
        var pilot = db.Store.FindPilot(1000001)!;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "DROP TABLE WorldJumpClonePayment; DROP TABLE WorldJumpCloneTimer; " + "DROP TABLE WorldJumpClone; PRAGMA user_version=46;"
            );
        }
        Assert.Equal(pilot, WorldInitialization.Open(db.Path).FindPilot(1000001));
        var clones = db.Services.GetRequiredService<IPilotJumpClones>();
        clones.Install(pilot.PilotId);
        WorldInitialization.Open(db.Path);
        WorldInitialization.Open(db.Path);
        Assert.Single(clones.Read(pilot.PilotId).Clones);
        Assert.Equal(
            JumpCloneTestData.Funds - JumpCloneRules.FeeCents,
            db.Services.GetRequiredService<IPilotWalletReader>().Balance(pilot.PilotId)
        );
    }

    [Fact]
    public void LeavingPreservesTheHullAndFittingReusesAnExistingCapsuleAndRejectsStaleOrInSpaceRequests()
    {
        using TestWorld db = new();
        JumpCloneTestData.Prepare(db);
        var fleet = db.Services.GetRequiredService<IPilotFleet>();
        var original = db.Store.FindPilot(1000001)!;
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = original.PilotId,
                    LocationId = original.ShipId,
                    TypeId = 3651,
                    FlagId = (int)Eden.Gameplay.Inventory.InventoryFlag.Cargo,
                    Quantity = 3,
                }
            );
            world.SaveChanges();
        }
        long pod;
        using (WorldDbContext world = new(db.Path))
        {
            pod = InventoryPersistence.NextId(world);
            world.Add(
                new Ship
                {
                    Id = pod,
                    OwnerId = original.PilotId,
                    TypeId = FleetDefinitions.CapsuleType,
                    StationId = original.StationId,
                    Name = "Unused capsule",
                }
            );
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => fleet.Leave(90000002, original.ShipId));
        Assert.Throws<InvalidDataException>(() => fleet.Leave(original.PilotId, pod));
        db.Store.SetPosition(original.ShipId, new(1, 2, 3));
        Assert.Throws<InvalidDataException>(() => fleet.Leave(original.PilotId, original.ShipId));
        HomeStationTestData.Travel(db, original.StationId);
        var result = fleet.Leave(original.PilotId, original.ShipId);
        Assert.Equal(pod, result.Pilot.ShipId);
        Assert.Empty(result.Changes);
        Assert.Throws<InvalidDataException>(() => fleet.Leave(original.PilotId, pod));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(original.ShipName, saved.Ships.Find(original.ShipId)!.Name);
        Assert.Equal(original.StationId, saved.Ships.Find(original.ShipId)!.StationId);
        Assert.Equal(
            3,
            Assert
                .Single(
                    saved
                        .Set<OwnedItem>()
                        .Where(i => i.LocationId == original.ShipId && i.FlagId == (int)Eden.Gameplay.Inventory.InventoryFlag.Cargo)
                )
                .Quantity
        );
    }

    [Fact]
    public void HomeCloneDeathRetiresOnlyTheActiveBodyAndKeepsInstalledClonesAndCooldown()
    {
        using TestWorld db = new();
        JumpCloneTestData.Prepare(db);
        var clones = db.Services.GetRequiredService<IPilotJumpClones>();
        HomeStationTestData.Travel(db, HomeStationTestData.Other);
        clones.Install(JumpCloneTestData.PilotId);
        var target = Assert.Single(clones.Read(JumpCloneTestData.PilotId).Clones);
        HomeStationTestData.Travel(db, HomeStationTestData.Origin);
        var pilot = db.Store.FindPilot(1000001)!;
        var capsule = db.Services.GetRequiredService<IPilotFleet>().Leave(pilot.PilotId, pilot.ShipId).Pilot;
        var jumped = clones.Jump(pilot.PilotId, capsule.ShipId, target.StationId, target.Id, JumpCloneRules.FeeCents);
        var before = clones.Read(pilot.PilotId);
        db.Services.GetRequiredService<IPilotHomeStation>().SelfDestruct(pilot.PilotId, jumped.Pilot.ShipId);
        var after = clones.Read(pilot.PilotId);
        Assert.Equal(before.Clones, after.Clones);
        Assert.Equal(before.LastJump, after.LastJump);
        using WorldDbContext saved = new(db.Path);
        Assert.Empty(saved.Set<SavedJumpClone>().Where(c => c.Active));
    }
}
