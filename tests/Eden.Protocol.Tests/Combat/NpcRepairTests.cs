using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcRepairTests
{
    private const long Now = 134000000000000000;
    private static readonly NpcRepairProfile s_profile = new(
        new(7, 5 * TimeSpan.TicksPerSecond, 0),
        new(4, 10 * TimeSpan.TicksPerSecond, 0)
    );

    private static LiveHealth Damaged()
    {
        Dictionary<DogmaAttributeId, double> attributes = new()
        {
            [DogmaAttributeId.ShieldCapacity] = 20,
            [DogmaAttributeId.ArmorHitPoints] = 15,
            [DogmaAttributeId.StructureHitPoints] = 20,
            [DogmaAttributeId.ShieldRechargeTime] = 1e20,
        };
        return new(ShipHealth.DefenseFrom(attributes), new(0, 1, 17), Now, ArmorDamage: 14, StructureDamage: 3);
    }

    [Fact]
    public void IndependentCyclesRespectDeadlinesClampAndNeverReplayMissedAttempts()
    {
        int draws = 0;
        double Draw()
        {
            draws++;
            return 0.5;
        }
        var initial = NpcRepairRules.Advance(s_profile, new(), Damaged(), true, Now, Draw);
        Assert.Equal(0, draws);
        Assert.Equal(Now + 5 * TimeSpan.TicksPerSecond, initial.State.ShieldReadyAt);
        Assert.Equal(Now + 10 * TimeSpan.TicksPerSecond, initial.State.ArmorReadyAt);
        var early = NpcRepairRules.Advance(s_profile, initial.State, initial.Health, true, initial.State.ShieldReadyAt - 1, Draw);
        Assert.Equal(initial, early);
        var shield = NpcRepairRules.Advance(s_profile, early.State, early.Health, true, initial.State.ShieldReadyAt, Draw);
        Assert.Equal(7, shield.Health.Remaining.Shield, 8);
        Assert.Equal(1, shield.Health.Remaining.Armor);
        Assert.Equal(3, shield.Health.StructureDamage);
        Assert.Equal(1, draws);
        Assert.Equal(shield, NpcRepairRules.Advance(s_profile, shield.State, shield.Health, true, initial.State.ShieldReadyAt, Draw));
        var late = NpcRepairRules.Advance(s_profile, shield.State, shield.Health, true, Now + TimeSpan.TicksPerHour, Draw);
        Assert.Equal(14, late.Health.Remaining.Shield, 8);
        Assert.Equal(5, late.Health.Remaining.Armor);
        Assert.Equal(3, draws);
        Assert.Equal(Now + TimeSpan.TicksPerHour + 5 * TimeSpan.TicksPerSecond, late.State.ShieldReadyAt);
        var cappedProfile = new NpcRepairProfile(s_profile.Shield! with { Amount = 100 }, s_profile.Armor! with { Amount = 100 });
        var capped = NpcRepairRules.Advance(cappedProfile, late.State, late.Health, true, Now + 2 * TimeSpan.TicksPerHour, Draw);
        Assert.Equal(20, capped.Health.Remaining.Shield);
        Assert.Equal(15, capped.Health.Remaining.Armor);
        Assert.Equal(17, capped.Health.Remaining.Structure);
        Assert.Equal(new NpcRepairState(Revision: 3), capped.State);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0.5, 0.499999, false)]
    [InlineData(0.5, 0.5, true)]
    [InlineData(1, 0.999999, false)]
    public void DelayChanceUsesTheCatalogProbabilityBoundary(double chance, double roll, bool repaired)
    {
        NpcRepairProfile profile = new(s_profile.Shield! with { DelayChance = chance }, null);
        var result = NpcRepairRules.Advance(profile, new(Now), Damaged(), true, Now, () => roll);
        Assert.Equal(repaired ? 7 : 0, result.Health.Remaining.Shield);
        Assert.Equal(repaired ? 1 : 0, result.State.Revision);
        Assert.Equal(Now + 5 * TimeSpan.TicksPerSecond, result.State.ShieldReadyAt);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void UnavailableAndDestroyedNpcsCannotRepair(bool available, bool destroyed)
    {
        var health = Damaged();
        if (destroyed)
        {
            health = health with { Remaining = health.Remaining with { Structure = 0 } };
        }
        var result = NpcRepairRules.Advance(s_profile, new(Now, Now), health, available, Now, () => throw new Exception());
        Assert.Equal(health, result.Health);
        Assert.Equal(new NpcRepairState(), result.State);
    }

    [Fact]
    public void BackwardTimeAndUnsupportedRepairsCannotGrantHealth()
    {
        var health = Damaged();
        var state = new NpcRepairState(Now, Now);
        Assert.Equal(
            new NpcRepairResult(health, state),
            NpcRepairRules.Advance(s_profile, state, health, true, Now - 1, () => throw new Exception())
        );
        var profile = NpcRepairRules.Profile(new Dictionary<DogmaAttributeId, double>());
        var result = NpcRepairRules.Advance(profile, state, health, true, Now, () => throw new Exception());
        Assert.Equal(health, result.Health);
        Assert.Equal(new NpcRepairState(), result.State);
    }

    [Fact]
    public void ProfilesUseSizeSpecificProbabilitiesMultipliersAndPinnedDefaults()
    {
        Dictionary<DogmaAttributeId, double> attributes = new()
        {
            [DogmaAttributeId.EntityShieldBoostAmount] = 17.5,
            [DogmaAttributeId.EntityShieldBoostDuration] = 5000,
            [DogmaAttributeId.EntityShieldBoostDelayChance] = 0.6,
            [DogmaAttributeId.EntityShieldBoostDelayChanceSmall] = 0.18,
            [DogmaAttributeId.EntityShieldBoostDelayChanceSmallMultiplier] = 0.5,
            [DogmaAttributeId.EntityArmorRepairAmount] = 140,
            [DogmaAttributeId.EntityArmorRepairDuration] = 10000,
            [DogmaAttributeId.EntityArmorRepairDelayChance] = 0.42,
        };
        var profile = NpcRepairRules.Profile(attributes);
        Assert.Equal(new NpcRepairCycle(17.5, 5 * TimeSpan.TicksPerSecond, 0.09), profile.Shield);
        Assert.Equal(new NpcRepairCycle(140, 10 * TimeSpan.TicksPerSecond, 0.42), profile.Armor);
        attributes.Remove(DogmaAttributeId.EntityShieldBoostDuration);
        attributes.Remove(DogmaAttributeId.EntityShieldBoostDelayChance);
        attributes.Remove(DogmaAttributeId.EntityShieldBoostDelayChanceSmall);
        Assert.Equal(new NpcRepairCycle(17.5, 10 * TimeSpan.TicksPerSecond, 1), NpcRepairRules.Profile(attributes).Shield);
    }

    [Theory]
    [InlineData(DogmaAttributeId.EntityShieldBoostAmount, -1)]
    [InlineData(DogmaAttributeId.EntityShieldBoostAmount, double.NaN)]
    [InlineData(DogmaAttributeId.EntityShieldBoostDuration, 0)]
    [InlineData(DogmaAttributeId.EntityShieldBoostDelayChance, 1.1)]
    [InlineData(DogmaAttributeId.EntityShieldBoostDelayChance, double.PositiveInfinity)]
    public void InvalidCatalogProfilesAreRejected(DogmaAttributeId attribute, double value)
    {
        Dictionary<DogmaAttributeId, double> attributes = new() { [DogmaAttributeId.EntityShieldBoostAmount] = 7 };
        attributes[attribute] = value;
        Assert.Throws<InvalidDataException>(() => NpcRepairRules.Profile(attributes));
    }

    [Fact]
    public void PinnedRegionalAndGuardProfilesRetainTheirOwnRepairValues()
    {
        using TestWorld db = new(regionalNpcs: true);
        RegionalNpcTests.Prepare(db);
        var profiles = db.Services.GetRequiredService<NpcSpawnCatalog>().Profiles;
        Assert.All(profiles, p => NpcRepairRules.Profile(p.Attributes));
        var invader = NpcRepairRules.Profile(profiles.Single(p => p.TypeId == 2383).Attributes);
        Assert.Equal(new NpcRepairCycle(17.5, 5 * TimeSpan.TicksPerSecond, 0.18), invader.Shield);
        var drone = NpcRepairRules.Profile(profiles.Single(p => p.TypeId == 25632).Attributes);
        Assert.Equal(new NpcRepairCycle(140, 10 * TimeSpan.TicksPerSecond, 0.42), drone.Armor);
        Assert.Null(drone.Shield);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        Assert.Equal(10000, catalog.FindSource("client/dogmaattributes", "636")!.Read<ClientDogmaattributes>().DefaultValue);
        Assert.Equal(0, catalog.FindSource("client/dogmaattributes", "638")!.Read<ClientDogmaattributes>().DefaultValue);
        Assert.Equal(1, catalog.FindSource("client/dogmaattributes", "639")!.Read<ClientDogmaattributes>().DefaultValue);
        foreach (var guard in Enum.GetValues<NpcGuardType>())
        {
            _ = NpcRepairRules.Profile(PilotFittingStore.ReadAttributes(catalog, (int)guard));
        }
        var moa = NpcRepairRules.Profile(PilotFittingStore.ReadAttributes(catalog, (int)NpcGuardType.CaldariMoa));
        Assert.Equal(new NpcRepairCycle(160, 10 * TimeSpan.TicksPerSecond, 0.6), moa.Shield);
    }

    private static void Configure(NpcCombatTests.Fixture f)
    {
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            DogmaAttributeId[] ids =
            [
                DogmaAttributeId.EntityShieldBoostAmount,
                DogmaAttributeId.EntityShieldBoostDuration,
                DogmaAttributeId.EntityShieldBoostDelayChance,
                DogmaAttributeId.EntityArmorRepairAmount,
                DogmaAttributeId.EntityArmorRepairDuration,
                DogmaAttributeId.EntityArmorRepairDelayChance,
            ];
            foreach (var id in ids)
            {
                catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO DogmaAttribute VALUES ({(int)id},{id.ToString()})");
            }
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO TypeAttribute VALUES
                    (30380,637,7),(30380,636,5000),(30380,639,0),
                    (30380,631,4),(30380,630,10000),(30380,638,0),(30380,479,1e20);
                """
            );
        }
        using WorldDbContext world = new(f.Db.Path);
        var npc = world.Set<WorldNpc>().Find(f.Npc.Id)!;
        npc.ShieldCharge = 0;
        npc.ArmorDamage = 14;
        npc.StructureDamage = 3;
        npc.UpdatedAt = f.Clock.Now;
        world.SaveChanges();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SharedReadsRepairOnceAndCheckpointPreservesHealthAndDeadlines(bool resident, bool simulation)
    {
        using NpcCombatTests.Fixture f = new(liveWorld: resident, simulationObjects: simulation, useTestClock: true);
        Configure(f);
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        NpcContact Read() => encounters.List(f.Npc.SystemId, f.Clock.Now).Single(n => n.Id == f.Npc.Id);
        var initial = Read();
        f.Clock.Milliseconds = 4999;
        Assert.Equal(initial.Health.Remaining.Armor, Read().Health.Remaining.Armor);
        f.Clock.Milliseconds = 5000;
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(Read)));
        Assert.All(concurrent, n => Assert.Equal(7, n.Health.Remaining.Shield, 8));
        Assert.All(concurrent, n => Assert.Equal(1, n.RepairRevision));
        f.Clock.Milliseconds = 10000;
        var healed = Read();
        Assert.Equal(14, healed.Health.Remaining.Shield, 8);
        Assert.Equal(5, healed.Health.Remaining.Armor);
        Assert.Equal(17, healed.Health.Remaining.Structure);
        Assert.Equal(2, healed.RepairRevision);
        if (resident && simulation)
        {
            using var measure = SessionPerformance.Begin("warm NPC repair", _ => { }, force: true)!;
            f.Clock.Milliseconds = 15000;
            Assert.Equal(20, Read().Health.Remaining.Shield);
            Assert.Equal(20, Read().Health.Remaining.Shield);
            Assert.Equal(0, measure.Commands);
            Assert.Equal(0, measure.Connections);
            healed = Read();
        }
        if (resident)
        {
            await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        }
        string checkpoint = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "npc-repair-checkpoint.sqlite");
        if (resident)
        {
            File.Copy(f.Db.Path, checkpoint);
        }
        else
        {
            using WorldDbContext source = new(f.Db.Path);
            source.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE)");
            File.Copy(f.Db.Path, checkpoint);
        }
        using WorldDbContext saved = new(checkpoint);
        var row = saved.Set<WorldNpc>().Find(f.Npc.Id)!;
        Assert.Equal(healed.Health.Remaining.Shield, row.ShieldCharge, 8);
        Assert.Equal(10, row.ArmorDamage);
        Assert.Equal(3, row.StructureDamage);
        Assert.Equal(healed.RepairRevision, row.RepairRevision);
        Assert.Equal(initial.Health.ShipId, row.Id);
        Assert.Equal(initial.RepairRevision + (resident && simulation ? 3 : 2), row.RepairRevision);
        Assert.Equal(f.Clock.UtcNow.AddMilliseconds(resident && simulation ? 5000 : 10000).UtcDateTime.ToFileTimeUtc(), row.ArmorRepairAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestartRetainsDamageButRestartsRepairIntervals(bool resident)
    {
        using NpcCombatTests.Fixture f = new(liveWorld: resident, useTestClock: true);
        Configure(f);
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        NpcContact Read() => encounters.List(f.Npc.SystemId, f.Clock.Now).Single(n => n.Id == f.Npc.Id);
        _ = Read();
        f.Clock.Seconds = 500;
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        var restart = Read();
        Assert.Equal(0, restart.Health.Remaining.Shield, 8);
        Assert.Equal(1, restart.Health.Remaining.Armor);
        Assert.Equal(0, restart.RepairRevision);
        f.Clock.Seconds = 505;
        Assert.Equal(7, Read().Health.Remaining.Shield, 8);
        Assert.Equal(1, Read().Health.Remaining.Armor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShieldRepairsPreserveUntouchedHistoricalArmorDamageAboveCapacity(bool resident)
    {
        using NpcCombatTests.Fixture f = new(liveWorld: resident, useTestClock: true);
        Configure(f);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=0 WHERE ItemTypeId=30380 AND AttributeId=631");
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldNpc>().Find(f.Npc.Id)!.ArmorDamage = 500;
            world.SaveChanges();
        }
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        _ = encounters.List(f.Npc.SystemId, f.Clock.Now);
        f.Clock.Seconds = 5;
        var healed = encounters.List(f.Npc.SystemId, f.Clock.Now).Single(n => n.Id == f.Npc.Id);
        Assert.Equal(7, healed.Health.Remaining.Shield, 8);
        Assert.Equal(0, healed.Health.Remaining.Armor);
        if (resident)
        {
            LiveWorld.Find(f.Db.Path)!.Combat!.Flush();
        }
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(500, saved.Set<WorldNpc>().Find(f.Npc.Id)!.ArmorDamage);
        Assert.Equal(3, saved.Set<WorldNpc>().Find(f.Npc.Id)!.StructureDamage);
    }

    [Fact]
    public void VersionEightyUpgradePreservesDamageAndAddsEmptyRepairTimers()
    {
        using NpcCombatTests.Fixture f = new();
        Configure(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldNpc DROP COLUMN ShieldRepairAt;
                ALTER TABLE WorldNpc DROP COLUMN ArmorRepairAt;
                ALTER TABLE WorldNpc DROP COLUMN RepairRevision;
                PRAGMA user_version=80;
                """
            );
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext saved = new(f.Db.Path);
        var row = saved.Set<WorldNpc>().Find(f.Npc.Id)!;
        Assert.Equal(14, row.ArmorDamage);
        Assert.Equal(3, row.StructureDamage);
        Assert.Equal(0, row.ShieldRepairAt);
        Assert.Equal(0, row.ArmorRepairAt);
        Assert.Equal(0, row.RepairRevision);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TurretDamageAtTheRepairDeadlineUsesRepairedHealthWithoutRepeatingTheCycle(bool resident)
    {
        using NpcCombatTests.Fixture f = new(liveWorld: resident, useTestClock: true);
        Configure(f);
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        _ = encounters.List(f.Npc.SystemId, f.Clock.Now);
        f.Clock.Seconds = 5;
        var shot = f.Weapons.Fire(NpcCombatTests.Fixture.PilotId, f.Module, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, 0.5);
        Assert.True(shot.Applied > 0);
        Assert.Equal(7 - shot.Applied, shot.Health.Remaining.Shield, 8);
        var contact = encounters.List(f.Npc.SystemId, f.Clock.Now).Single(n => n.Id == f.Npc.Id);
        Assert.Equal(1, contact.RepairRevision);
        Assert.Equal(shot.Health.Remaining.Shield, contact.Health.Remaining.Shield, 8);
        Assert.Equal(shot.Health.Remaining.Armor, contact.Health.Remaining.Armor);
        Assert.Equal(shot.Health.Remaining.Structure, contact.Health.Remaining.Structure);
        Assert.Throws<InvalidDataException>(() =>
            f.Weapons.Fire(NpcCombatTests.Fixture.PilotId, f.Module, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, 0.5)
        );
        var afterRejection = encounters.List(f.Npc.SystemId, f.Clock.Now).Single(n => n.Id == f.Npc.Id);
        Assert.Equal(contact.Health, afterRejection.Health);
        Assert.Equal(contact.RepairRevision, afterRejection.RepairRevision);
    }

    [Fact]
    public void MissileDamageSettlementUsesRepairedHealthAndFatalDamageCancelsBothCycles()
    {
        using NpcCombatTests.Fixture f = new();
        Configure(f);
        using WorldDbContext world = new(f.Db.Path);
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        using var transaction = world.Database.BeginTransaction();
        var npc = world.Set<WorldNpc>().Find(f.Npc.Id)!;
        var attributes = PilotFittingStore.ReadAttributes(catalog, npc.TypeId);
        NpcRepairPersistence.Advance(npc, attributes, f.Clock.Now);
        f.Clock.Seconds = 5;
        var pilot = world.Pilots.Find(NpcCombatTests.Fixture.PilotId)!;
        var damage = new DamageVector(1, 1, 1, 1);
        var expected = CombatDamage.Apply(new(7, 1, 17), ShipHealth.DefenseFrom(attributes), damage);
        var hit = NpcDamagePersistence.Apply(world, catalog, npc, pilot, pilot.ActiveShipId, attributes, damage, f.Clock.Now);
        Assert.Equal(expected.Remaining.Shield, hit.Health.Remaining.Shield, 8);
        Assert.Equal(1, npc.RepairRevision);
        var fatal = NpcDamagePersistence.Apply(
            world,
            catalog,
            npc,
            pilot,
            pilot.ActiveShipId,
            attributes,
            new(10000, 10000, 10000, 10000),
            f.Clock.Now
        );
        Assert.True(fatal.Destroyed);
        Assert.Equal(0, npc.ShieldRepairAt);
        Assert.Equal(0, npc.ArmorRepairAt);
        Assert.Equal(1, npc.RepairRevision);
        Assert.Throws<InvalidDataException>(() =>
            NpcDamagePersistence.Apply(world, catalog, npc, pilot, pilot.ActiveShipId, attributes, damage, f.Clock.Now)
        );
        world.SaveChanges();
        transaction.Commit();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObserversReceiveOneHealthUpdateForAShieldBoostAndSnapshotsContainRepairedHealth(bool resident)
    {
        using NpcCombatTests.Fixture f = new(liveWorld: resident, useTestClock: true);
        Configure(f);
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        using FlightSession first = new(f.Db.Store, f.Pilot, f.Clock, new(), [], encounters: encounters);
        using FlightSession second = new(f.Db.Store, f.Pilot, f.Clock, new(), [], encounters: encounters);
        first.Snapshot();
        second.Snapshot();
        first.Updates.Clear();
        second.Updates.Clear();
        f.Clock.Seconds = 5;
        first.RefreshNpcs();
        second.RefreshNpcs();
        foreach (var flight in new[] { first, second })
        {
            var health = Assert.Single(flight.Updates.Select(u => u.Command).OfType<DestinyCommand.OnDamageStateChange>());
            Assert.Equal(f.Npc.Id, health.Id);
            Assert.Equal(7, flight.VisibleNpcs.Single(n => n.Id == f.Npc.Id).Health.Remaining.Shield, 8);
            flight.RefreshNpcs();
            Assert.Single(flight.Updates.Select(u => u.Command).OfType<DestinyCommand.OnDamageStateChange>());
        }
        using FlightSession joined = new(f.Db.Store, f.Pilot, f.Clock, new(), [], encounters: encounters);
        joined.Snapshot();
        Assert.Equal(7, joined.VisibleNpcs.Single(n => n.Id == f.Npc.Id).Health.Remaining.Shield, 8);
    }
}
