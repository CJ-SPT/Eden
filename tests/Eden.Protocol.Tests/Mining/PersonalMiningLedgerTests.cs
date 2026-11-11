using System.Text.Json;
using Eden.Data.World.Mining;
using Eden.Gameplay.Mining;
using Eden.Protocol.Tests.Fixtures;
using Eden.World.Mining;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Mining;

public sealed class PersonalMiningLedgerTests
{
    private const long Pilot = 90000001;
    private const long Other = 90000002;
    private const int System = 30000142;
    private const int Resource = 1230;

    [Fact]
    public void TypedDailyRulesCountCriticalOnceRejectInvalidTotalsAndPreserveExactUtcBoundaries()
    {
        long day = new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        Assert.Equal(day, MiningLedgerRules.Day(day + TimeSpan.TicksPerDay - 1));
        Assert.Equal(day + TimeSpan.TicksPerDay, MiningLedgerRules.Day(day + TimeSpan.TicksPerDay));
        Assert.Equal(day - 89 * TimeSpan.TicksPerDay, MiningLedgerRules.EarliestDay(day));
        Assert.Equal(0, MiningLedgerRules.EarliestDay(0));
        Assert.Equal(new(150, 25, 40), MiningLedgerRules.Add(new(100, 20, 30), new(50, 5, 10)));
        foreach (var invalid in new MiningLedgerQuantity[] { new(-1, 0, 0), new(0, -1, 0), new(0, 0, -1), new(1, 2, 0) })
        {
            Assert.Throws<InvalidDataException>(() => MiningLedgerRules.Add(new(0, 0, 0), invalid));
            Assert.Throws<InvalidDataException>(() => MiningLedgerRules.Add(invalid, new(0, 0, 0)));
        }
        Assert.Throws<InvalidDataException>(() => MiningLedgerRules.Day(-1));
        Assert.Throws<OverflowException>(() => MiningLedgerRules.Add(new(long.MaxValue, 0, 0), new(1, 0, 0)));
        Assert.Throws<OverflowException>(() => MiningLedgerRules.Add(new(1, 0, long.MaxValue), new(1, 0, 1)));
    }

    [Fact]
    public void DailyBucketsPreserveCorporationAtMiningTimeAndSeparatePilotsSystemsAndResourcesAcrossReopen()
    {
        TestClock clock = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
        using TestWorld db = new(clock: clock);
        var ledger = db.Services.GetRequiredService<IPersonalMiningLedger>();
        Record(db, Pilot, clock.Now, new(100, 20, 30));
        Record(db, Pilot, clock.Now + 100, new(50, 5, 10));
        Record(db, Pilot, clock.Now, new(1, 0, 0), system: System + 1);
        Record(db, Pilot, clock.Now, new(2, 0, 0), type: Resource + 1);
        Record(db, Other, clock.Now, new(3, 0, 0));
        int originalCorporation;
        using (WorldDbContext world = new(db.Path))
        {
            var pilot = world.Pilots.Find(Pilot)!;
            originalCorporation = pilot.CorporationId;
            pilot.CorporationId = 1000035;
            world.SaveChanges();
        }
        Record(db, Pilot, clock.Now, new(4, 0, 0));
        Assert.Equal(4, ledger.Read(Pilot).Count);
        var old = Assert.Single(
            ledger.Read(Pilot),
            l => l.TypeId == Resource && l.SystemId == System && l.CorporationId == originalCorporation
        );
        Assert.Equal(new(150, 25, 40), old.Quantity);
        Assert.Equal(4, Assert.Single(ledger.Read(Pilot), l => l.CorporationId == 1000035).Quantity.Collected);
        Assert.Equal(3, Assert.Single(ledger.Read(Other)).Quantity.Collected);
        string before = Logs(db);
        WorldInitialization.Open(db.Path);
        WorldInitialization.Open(db.Path);
        Assert.Equal(before, Logs(db));
        Assert.Equal(old, Assert.Single(ledger.Read(Pilot), l => l.Id == old.Id));
        Assert.Throws<InvalidDataException>(() => ledger.Read(long.MaxValue));
    }

    [Fact]
    public void ReadsArePrivateAndReadOnlyAndRetentionExpiresAtUtcMidnightWithoutPruningOtherPilots()
    {
        TestClock clock = new(new DateTimeOffset(2026, 11, 11, 23, 59, 59, TimeSpan.Zero));
        using TestWorld db = new(clock: clock);
        var ledger = db.Services.GetRequiredService<IPersonalMiningLedger>();
        long day = MiningLedgerRules.Day(clock.Now);
        Record(db, Pilot, day - 90 * TimeSpan.TicksPerDay, new(1, 0, 0));
        Record(db, Pilot, day - 89 * TimeSpan.TicksPerDay, new(2, 0, 0));
        Record(db, Other, day - 90 * TimeSpan.TicksPerDay, new(3, 0, 0));
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(
                new SavedPersonalMiningLog
                {
                    PilotId = Pilot,
                    Day = day + TimeSpan.TicksPerDay,
                    CorporationId = world.Pilots.Find(Pilot)!.CorporationId,
                    SystemId = System,
                    TypeId = Resource,
                    Quantity = 4,
                }
            );
            world.SaveChanges();
        }
        // Clock rewinds are not pruned by reads; future rows are excluded from replies.
        string before = Logs(db);
        Assert.Equal(2, Assert.Single(ledger.Read(Pilot)).Quantity.Collected);
        Assert.Empty(ledger.Read(Other));
        Assert.Equal(before, Logs(db));
        Record(db, Pilot, clock.Now, new(5, 0, 0));
        Assert.Equal(2, ledger.Read(Pilot).Count);
        using (WorldDbContext world = new(db.Path))
        {
            Assert.False(world.Set<SavedPersonalMiningLog>().Any(l => l.PilotId == Pilot && l.Day == day - 90 * TimeSpan.TicksPerDay));
            Assert.True(world.Set<SavedPersonalMiningLog>().Any(l => l.PilotId == Other));
        }
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        Assert.Equal(new long[] { 4, 5 }, ledger.Read(Pilot).Select(l => l.Quantity.Collected));
    }

    [Fact]
    public void WasteOnlyAndZeroCollectionsNeverInventCollectedOre()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        var ledger = db.Services.GetRequiredService<IPersonalMiningLedger>();
        Record(db, Pilot, clock.Now, new(0, 0, 0));
        Assert.Empty(ledger.Read(Pilot));
        Record(db, Pilot, clock.Now, new(0, 0, 23));
        Assert.Equal(new(0, 0, 23), Assert.Single(ledger.Read(Pilot)).Quantity);
    }

    [Fact]
    public void Version142UpgradeCreatesAnEmptyLedgerAndNewRecordsPersistWithoutBackfillingInventory()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldPersonalMiningLog; PRAGMA user_version=142");
        }
        var pilot = db.Store.FindPilot(1000001);
        WorldInitialization.Open(db.Path);
        var ledger = db.Services.GetRequiredService<IPersonalMiningLedger>();
        Assert.Empty(ledger.Read(Pilot));
        Assert.Equal(pilot, db.Store.FindPilot(1000001));
        Record(db, Pilot, clock.Now, new(17, 2, 3));
        WorldInitialization.Open(db.Path);
        Assert.Equal(new(17, 2, 3), Assert.Single(ledger.Read(Pilot)).Quantity);
        using WorldDbContext check = new(db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, check.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Equal(0, check.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").Single());
    }

    [Fact]
    public void LargeDailyTotalsKeepIntegerWireFormsBeyondTheSigned32BitRange()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        Record(db, Pilot, clock.Now, new(3_000_000_000, 1_000_000_000, 4_000_000_000));
        Record(db, Pilot, clock.Now, new(3_000_000_000, 1_000_000_000, 4_000_000_000));
        var log = Assert.Single(db.Services.GetRequiredService<IPersonalMiningLedger>().Read(Pilot));
        var encoded = Eden.Protocol.Serialization.BlueMarshal.Encode(new Eden.Protocol.Mining.PersonalMiningLogReply(log));
        var reply = Assert.IsType<Eden.Protocol.Serialization.BlueInstance>(Eden.Protocol.Serialization.BlueMarshal.Decode(encoded));
        var fields = Eden.Protocol.Serialization.BlueRead.Fields(reply.State);
        Assert.Equal(6_000_000_000L, fields["quantity"]);
        Assert.Equal(2_000_000_000L, fields["quantityCritical"]);
        Assert.Equal(8_000_000_000L, fields["quantityWasted"]);
    }

    private static void Record(
        TestWorld db,
        long pilotId,
        long now,
        MiningLedgerQuantity quantity,
        int system = System,
        int type = Resource
    )
    {
        using WorldDbContext world = new(db.Path);
        using var transaction = world.Database.BeginTransaction();
        PersonalMiningLedgerStore.Record(world, world.Pilots.Find(pilotId)!, new() { SystemId = system, TypeId = type }, now, quantity);
        world.SaveChanges();
        transaction.Commit();
    }

    private static string Logs(TestWorld db)
    {
        using WorldDbContext world = new(db.Path);
        return JsonSerializer.Serialize(world.Set<SavedPersonalMiningLog>().OrderBy(l => l.Id).ToArray());
    }
}
