using System.Globalization;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.World.Capsuleers;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerTrafficMigrationTests
{
    private const int SourceSystem = 30000141;
    private const int DestinationSystem = 30000142;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Schema119UpgradeRenamesGeneratedPilotsAndPreservesIdentityAssetsAndCustomNames(bool customShipName)
    {
        using CapsuleerHaulerTests.Fixture f = new();
        CapsuleerProvisioningTests.AddDestinationOre(f.Database);
        new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(DestinationSystem);
        long pilotId;
        long shipId;
        long humanId;
        long seed;
        long[] wallets;
        long[] ships;
        long[] items;
        CapsuleerJob[] jobs;
        string[] preservedNames;
        using (WorldDbContext world = new(f.Database.Path))
        {
            // Version 119 had one slot per career. Remove the newly introduced
            // slots from the registry before recreating that historical schema.
            var extra = world.Set<SavedCapsuleerAgent>().Where(a => a.Slot != 0).Select(a => a.PilotId).ToArray();
            world.Set<SavedCapsuleerJob>().Where(j => extra.Contains(j.PilotId)).ExecuteDelete();
            world.Set<SavedCapsuleerAgent>().Where(a => a.Slot != 0).ExecuteDelete();
            var generated = world
                .Set<SavedCapsuleerAgent>()
                .Single(a => a.CommunitySystemId == DestinationSystem && a.Career == CapsuleerCareer.Hauling);
            pilotId = generated.PilotId;
            shipId = generated.FounderShipId;
            seed = world.Set<SavedCapsuleerProvisioning>().Single().WorldSeed;
            string legacyName = LegacyName(seed, DestinationSystem, generated.Career) + "-2";
            var pilot = world.Pilots.Single(p => p.Id == pilotId);
            pilot.Name = legacyName;
            world.Ships.Single(s => s.Id == shipId).Name = customShipName ? "Quiet Passage" : legacyName;
            world.SaveChanges();
            // A human already owns the first natural candidate, so the upgrade
            // must select a different valid name without altering the human.
            var human = world.Pilots.First(p => p.AccountId > 0);
            humanId = human.Id;
            human.Name = CapsuleerProvisioningRules.Name(seed, DestinationSystem, generated.Career);
            world.SaveChanges();
            preservedNames = world.Pilots.Where(p => p.Id != pilotId).OrderBy(p => p.Id).Select(p => p.Name).ToArray();
            wallets = world.Set<PilotWallet>().OrderBy(w => w.PilotId).Select(w => w.Cents).ToArray();
            ships = world.Ships.OrderBy(s => s.Id).Select(s => s.Id).ToArray();
            items = world.Set<OwnedItem>().OrderBy(i => i.Id).Select(i => i.Id).ToArray();
            jobs = CapsuleerPopulationStore.Jobs(world);
            world.Database.ExecuteSqlRaw(
                """
                DROP INDEX IX_WorldCapsuleerAgent_CommunitySystemId_Career_Slot;
                ALTER TABLE WorldCapsuleerAgent DROP COLUMN Slot;
                CREATE UNIQUE INDEX IX_WorldCapsuleerAgent_CommunitySystemId_Career
                    ON WorldCapsuleerAgent(CommunitySystemId,Career);
                ALTER TABLE WorldCapsuleerHauler DROP COLUMN RouteSystems;
                PRAGMA user_version=119;
                """
            );
        }
        string? migratedName = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            WorldInitialization.Initialize(f.Database, f.Clock);
            using WorldDbContext world = new(f.Database.Path);
            var pilot = world.Pilots.Single(p => p.Id == pilotId);
            migratedName ??= pilot.Name;
            Assert.Equal(migratedName, pilot.Name);
            Assert.Equal(CapsuleerProvisioningRules.Name(seed, DestinationSystem, CapsuleerCareer.Hauling, attempt: 1), pilot.Name);
            Assert.Equal(shipId, pilot.ActiveShipId);
            Assert.Equal(customShipName ? "Quiet Passage" : pilot.Name, world.Ships.Single(s => s.Id == shipId).Name);
            Assert.Equal(
                CapsuleerProvisioningRules.Name(seed, DestinationSystem, CapsuleerCareer.Hauling),
                world.Pilots.Single(p => p.Id == humanId).Name
            );
            Assert.Equal(preservedNames, world.Pilots.Where(p => p.Id != pilotId).OrderBy(p => p.Id).Select(p => p.Name));
            Assert.Equal(wallets, world.Set<PilotWallet>().OrderBy(w => w.PilotId).Select(w => w.Cents));
            Assert.Equal(ships, world.Ships.OrderBy(s => s.Id).Select(s => s.Id));
            Assert.Equal(items, world.Set<OwnedItem>().OrderBy(i => i.Id).Select(i => i.Id));
            Assert.Equal(jobs, CapsuleerPopulationStore.Jobs(world));
            Assert.All(world.Set<SavedCapsuleerAgent>(), a => Assert.Equal(0, a.Slot));
            Assert.All(world.Set<SavedCapsuleerHauler>(), h => Assert.Null(h.RouteSystems));
            Assert.Equal(120, world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        }
    }

    [Fact]
    public void SchemaReconciliationRetainsSlotsAndNullableJsonRoutesAcrossReopen()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        long human;
        using (WorldDbContext world = new(f.Database.Path))
        {
            CapsuleerPopulationStore.Initialize(world);
            CapsuleerProvisioningSchema.Upgrade(world);
            CapsuleerPopulationStore.Initialize(world);
            CapsuleerProvisioningSchema.Upgrade(world);
            var hauler = world.Set<SavedCapsuleerHauler>().Single();
            Assert.Null(hauler.RouteSystems);
            hauler.RouteSystems = [SourceSystem, DestinationSystem];
            human = world.Pilots.First(p => p.AccountId > 0).Id;
            Assert.Throws<SqliteException>(() =>
                CapsuleerPopulationStore.AddAgent(world, new(human, SourceSystem, CapsuleerCareer.Hauling, 0, 0, 0))
            );
            CapsuleerPopulationStore.AddAgent(world, new(human, SourceSystem, CapsuleerCareer.Hauling, 0, 0, 0, Slot: 1));
            world.SaveChanges();
        }
        using WorldDbContext reopened = new(f.Database.Path);
        Assert.Equal(new[] { SourceSystem, DestinationSystem }, reopened.Set<SavedCapsuleerHauler>().Single().RouteSystems);
        Assert.Equal(1, reopened.Set<SavedCapsuleerAgent>().Single(a => a.PilotId == human).Slot);
        Assert.Contains(new CapsuleerPopulationStore(f.Database).Agents(SourceSystem), a => a.PilotId == human && a.Slot == 1);
    }

    [Fact]
    public void NaturalNamesRemainValidAcrossCollisionRetriesAndSlotSchedulesAreDeterministic()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        const int CandidateSampleCount = 8193;
        for (int attempt = 0; attempt < CandidateSampleCount; attempt++)
        {
            string name = CapsuleerProvisioningRules.Name(42, SourceSystem, CapsuleerCareer.Hauling, slot: 3, attempt);
            Assert.True(names.Add(name), name);
            Assert.Equal(CharacterNameResult.Available, CharacterRules.ValidateName(name, false));
            Assert.All(name, c => Assert.True(char.IsAsciiLetter(c) || c == ' '));
        }
        long prior = 0;
        for (int slot = 0; slot < 8; slot++)
        {
            long due = CapsuleerProvisioningRules.FirstDueTicks(0, 42, SourceSystem, CapsuleerCareer.Hauling, slot);
            Assert.True(due > prior);
            Assert.Equal(due, CapsuleerProvisioningRules.FirstDueTicks(0, 42, SourceSystem, CapsuleerCareer.Hauling, slot));
            prior = due;
        }
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CapsuleerProvisioningRules.Name(42, SourceSystem, CapsuleerCareer.Hauling, slot: -1)
        );
        string last = CapsuleerProvisioningRules.Name(
            long.MaxValue,
            DestinationSystem,
            CapsuleerCareer.Hauling,
            slot: int.MaxValue,
            attempt: CapsuleerProvisioningRules.MaximumNameAttempts - 1
        );
        Assert.Equal(CharacterNameResult.Available, CharacterRules.ValidateName(last, false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CapsuleerProvisioningRules.Name(
                42,
                SourceSystem,
                CapsuleerCareer.Hauling,
                attempt: CapsuleerProvisioningRules.MaximumNameAttempts
            )
        );
    }

    private static string LegacyName(long worldSeed, int system, CapsuleerCareer career)
    {
        string[] given = ["Ari", "Taren", "Mira", "Kira", "Dara", "Oren", "Jori", "Sera"];
        string[] family = ["Voss", "Vartio", "Auvinen", "Kade", "Renn", "Sorin", "Arden", "Neris"];
        ulong seed = CapsuleerProvisioningRules.Seed(worldSeed, system, career);
        return $"{given[seed % (ulong)given.Length]} {family[(seed >> 8) % (ulong)family.Length]} "
            + system.ToString(CultureInfo.InvariantCulture)
            + (career == CapsuleerCareer.Mining ? "M" : "H");
    }
}
