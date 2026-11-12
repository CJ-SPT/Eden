using System.Text.Json;
using Eden.Data.World.Mining;
using Eden.Gameplay.Mining;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Mining;

public sealed partial class MiningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativePersonalLedgerReflectsCommittedMiningAndRejectsCallerSuppliedOwners(bool liveWorld)
    {
        using Fixture f = new(liveWorld: liveWorld, useTestClock: true);
        var (context, _, mining, module) = f.Client();
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var ledger = f.Db.Services.GetRequiredService<IPersonalMiningLedger>();
        Assert.Empty(Read());
        Assert.Null(mining.CallWire(context, "dogmaIM", "Activate", [module, "miningLaser", Fixture.Asteroid, 100L]));
        long total = 0;
        long critical = 0;
        long wasted = 0;
        for (int i = 0; i < 2; i++)
        {
            context.Notifications.Clear();
            f.Clock.Milliseconds += 10_000;
            mining.Advance(context);
            var yield = Assert.Single(context.Notifications.OfType<OreMined>()).Yield;
            total += yield.Extracted + yield.CriticalBonus;
            critical += yield.CriticalBonus;
            wasted += yield.Residue;
            var fields = BlueRead.Fields(Assert.IsType<BlueInstance>(Assert.Single(Read())).State);
            Assert.Equal(context.Pilot.PilotId, fields["characterID"]);
            Assert.Equal((long)context.Pilot.CorpId, fields["corporationID"]);
            Assert.Equal((long)context.Pilot.SystemId, fields["solarSystemID"]);
            Assert.Equal(fields["solarSystemID"], fields["solarsystemID"]);
            Assert.Equal(1230L, fields["typeID"]);
            Assert.Equal(MiningLedgerRules.Day(f.Clock.Now), fields["eventDate"]);
            Assert.Equal(total, fields["quantity"]);
            Assert.Equal(critical, fields["quantityCritical"]);
            Assert.Equal(wasted, fields["quantityWasted"]);
        }
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(context, "characterMiningLedger", "GetCharacterLogs", new object?[] { 90000002L }, _ => { })
        );
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(
                context,
                "characterMiningLedger",
                "GetCharacterLogs",
                Array.Empty<object?>(),
                _ => { },
                new() { ["characterID"] = 90000002L }
            )
        );
        Assert.Empty(ledger.Read(90000002));
        // Stopping the freshly started next cycle at zero elapsed time adds nothing.
        mining.CallWire(context, "dogmaIM", "Deactivate", [module, "miningLaser"]);
        Assert.Equal(total, Assert.Single(ledger.Read(context.Pilot.PilotId)).Quantity.Collected);
        f.Disconnect(context);
        f.Reopen();
        Assert.Equal(total, Assert.Single(ledger.Read(90000001)).Quantity.Collected);

        List<object?> Read() =>
            Assert.IsType<List<object?>>(
                BlueMarshal.Decode(
                    BlueMarshal.Encode(
                        Assert
                            .IsType<PilotCallResult.Success>(
                                router.Call(context, "characterMiningLedger", "GetCharacterLogs", Array.Empty<object?>(), _ => { })
                            )
                            .Reply
                    )
                )
            );
    }

    [Fact]
    public void PaidMiningCyclesAggregateEveryCommittedOutputIntoOneDailyBucket()
    {
        using Fixture f = new(liveWorld: false, useTestClock: true);
        var (context, _, _, module) = f.Client();
        for (int cycle = 0; cycle < 2; cycle++)
        {
            f.Store.Begin(context.Pilot.PilotId, module, Fixture.Asteroid, f.Clock.Now);
            f.Clock.Seconds += 180;
            var result = f.Store.Collect(context.Pilot.PilotId, module, Fixture.Asteroid, new(1, true, new(.99, .99, .99, .99, .99)));
            Assert.True(result.Extracted > 0);
        }
        var log = Assert.Single(f.Db.Services.GetRequiredService<IPersonalMiningLedger>().Read(context.Pilot.PilotId));
        long cargo = f.Inventory.List(context.Pilot.PilotId, context.Pilot.ShipId).Where(i => i.TypeId == 1230).Sum(i => i.Quantity);
        Assert.True(cargo > 63);
        Assert.Equal(cargo, log.Quantity.Collected);
        Assert.Equal(10_000 - f.Remaining(), cargo + log.Quantity.Wasted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LedgerWriteFailureRollsBackOreCargoRewardsAndPaidCycleThenRetryRecordsOnce(bool updating)
    {
        using Fixture f = new(liveWorld: false, useTestClock: true);
        var (context, _, _, module) = f.Client();
        var ledger = f.Db.Services.GetRequiredService<IPersonalMiningLedger>();
        var collection = new MiningCollection(1, true, new(.99, .99, .99, .99, .99));
        if (updating)
        {
            f.Store.Begin(context.Pilot.PilotId, module, Fixture.Asteroid, f.Clock.Now);
            f.Store.Collect(context.Pilot.PilotId, module, Fixture.Asteroid, collection);
            f.Clock.Milliseconds += 10_000;
        }
        f.Store.Begin(context.Pilot.PilotId, module, Fixture.Asteroid, f.Clock.Now);
        string before = State();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                updating
                    ? """
                    CREATE TRIGGER FailMiningLog BEFORE UPDATE ON WorldPersonalMiningLog
                    BEGIN SELECT RAISE(ABORT,'forced ledger failure'); END;
                    """
                    : """
                    CREATE TRIGGER FailMiningLog BEFORE INSERT ON WorldPersonalMiningLog
                    BEGIN SELECT RAISE(ABORT,'forced ledger failure'); END;
                    """
            );
        }
        Assert.Throws<DbUpdateException>(() => f.Store.Collect(context.Pilot.PilotId, module, Fixture.Asteroid, collection));
        Assert.Equal(before, State());
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TRIGGER FailMiningLog");
        }
        long previous = ledger.Read(context.Pilot.PilotId).Sum(l => l.Quantity.Collected);
        var yielded = f.Store.Collect(context.Pilot.PilotId, module, Fixture.Asteroid, collection);
        long expected = previous + yielded.Extracted + yielded.CriticalBonus;
        Assert.Equal(expected, Assert.Single(ledger.Read(context.Pilot.PilotId)).Quantity.Collected);
        Assert.Throws<InvalidDataException>(() => f.Store.Collect(context.Pilot.PilotId, module, Fixture.Asteroid, collection));
        Assert.Equal(expected, Assert.Single(ledger.Read(context.Pilot.PilotId)).Quantity.Collected);

        string State()
        {
            using WorldDbContext world = new(f.Db.Path);
            return JsonSerializer.Serialize(
                new
                {
                    Rocks = world.Set<WorldAsteroid>().OrderBy(a => a.Id).ToArray(),
                    Items = world.Set<Eden.Data.World.Inventory.OwnedItem>().OrderBy(i => i.Id).ToArray(),
                    Logs = world.Set<SavedPersonalMiningLog>().OrderBy(l => l.Id).ToArray(),
                    Cycles = world.Set<Eden.Data.World.Combat.ModuleCooldown>().OrderBy(c => c.ModuleId).ToArray(),
                    Pilots = world.Pilots.OrderBy(p => p.Id).ToArray(),
                    Points = world.Set<Eden.Data.World.Skills.SavedFreeSkillPoints>().OrderBy(p => p.PilotId).ToArray(),
                }
            );
        }
    }
}
