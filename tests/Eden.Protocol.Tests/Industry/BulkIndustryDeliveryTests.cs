using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Industry;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Fixture = Eden.Protocol.Tests.Industry.IndustryTests.Fixture;

namespace Eden.Protocol.Tests.Industry;

public sealed class BulkIndustryDeliveryTests
{
    private const long SecondBlueprint = Fixture.BlueprintId + 10;

    private static Fixture Create()
    {
        Fixture f = new();
        using WorldDbContext world = new(f.Db.Path);
        world.Add(
            new PilotSkill
            {
                PilotId = f.Pilot.PilotId,
                TypeId = (int)IndustrySkill.MassProduction,
                Points = 500,
            }
        );
        world.Add(
            new OwnedItem
            {
                Id = SecondBlueprint,
                OwnerId = f.Pilot.PilotId,
                TypeId = 1130,
                LocationId = f.Pilot.StationId,
                FlagId = (int)InventoryFlag.Hangar,
                Quantity = 1,
            }
        );
        world.SaveChanges();
        return f;
    }

    private static (IndustryJobDetails First, IndustryJobDetails Second) Install(Fixture f) =>
        (
            f.Store.Install(f.Pilot.PilotId, f.Request()).Job,
            f.Store.Install(f.Pilot.PilotId, f.Request(2) with { BlueprintId = SecondBlueprint }).Job
        );

    private static PilotCallContext Context(Fixture f)
    {
        var pilot = f.Pilot;
        PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new(), []),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        return context;
    }

    private static PilotCallResult Call(
        Fixture f,
        PilotCallContext context,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    ) =>
        new IndustryHandler(f.Store).Call(
            context,
            "industryManager",
            "CompleteManyJobs",
            BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(args)), args.Length),
            keywords
        );

    private static List<object?> Batch(IndustryJobDetails first, IndustryJobDetails second) =>
        [new object?[] { first.Id, (long)first.Facility.SystemId }, new object?[] { second.Id, (long)second.Facility.SystemId }];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeBulkDeliveryMergesOutputsNotifiesEachJobAndCannotReplayAcrossReopen(bool resident)
    {
        using var f = Create();
        using var live = resident ? ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true) : null;
        var (first, second) = Install(f);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(second.DurationSeconds));
        using var context = Context(f);
        var result = Assert.IsType<PilotCallResult.Success>(Call(f, context, [Batch(first, second)]));
        Assert.Null(BlueMarshal.Decode(BlueMarshal.Encode(result.Reply)));
        Assert.Equal(300, f.Quantity(215));
        Assert.Equal(new[] { first.Id, second.Id }, context.Notifications.OfType<IndustryJobChanged>().Select(n => n.Job.Id));
        Assert.All(
            context.Notifications.OfType<IndustryJobChanged>(),
            notification => Assert.Equal(IndustryJobStatus.Delivered, notification.Job.Status)
        );
        context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Success>(Call(f, context, [Batch(first, second)]));
        Assert.Equal(300, f.Quantity(215));
        live?.Dispose();
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.All(
            f.Store.DeliverMany(f.Pilot.PilotId, [new(first.Id, first.Facility.SystemId), new(second.Id, second.Facility.SystemId)]),
            delivery => Assert.Empty(delivery.Changes)
        );
        Assert.Equal(300, f.Quantity(215));
    }

    [Theory]
    [InlineData("foreign", false)]
    [InlineData("unknown", false)]
    [InlineData("system", false)]
    [InlineData("unfinished", false)]
    [InlineData("cancelled", false)]
    [InlineData("foreign", true)]
    [InlineData("unknown", true)]
    [InlineData("system", true)]
    [InlineData("unfinished", true)]
    [InlineData("cancelled", true)]
    public void RejectedLaterJobLeavesEarlierOutputsJobsAndNotificationsUnchanged(string rejection, bool resident)
    {
        using var f = Create();
        using var live = resident ? ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true) : null;
        var (first, second) = Install(f);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(rejection == "unfinished" ? first.DurationSeconds : second.DurationSeconds));
        if (rejection == "foreign")
        {
            using WorldDbContext world = new(f.Db.Path);
            world.Set<IndustryJob>().Single(job => job.Id == second.Id).PilotId = 90000002;
            world.SaveChanges();
        }
        if (rejection == "cancelled")
        {
            f.Store.Cancel(f.Pilot.PilotId, second.Id, second.Facility.SystemId);
        }
        using var context = Context(f);
        var rows = Batch(first, second);
        if (rejection == "unknown")
        {
            rows[1] = new object?[] { long.MaxValue, (long)second.Facility.SystemId };
        }
        if (rejection == "system")
        {
            rows[1] = new object?[] { second.Id, (long)second.Facility.SystemId + 1 };
        }
        Assert.IsType<PilotCallResult.Failure>(Call(f, context, [rows]));
        Assert.Equal(0, f.Quantity(215));
        Assert.Empty(context.Notifications);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(IndustryJobStatus.Installed, saved.Set<IndustryJob>().Single(job => job.Id == first.Id).Status);
        Assert.Equal(
            rejection == "cancelled" ? IndustryJobStatus.Cancelled : IndustryJobStatus.Installed,
            saved.Set<IndustryJob>().Single(job => job.Id == second.Id).Status
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SqlFailureOnLaterJobRollsBackEarlierDeliveryAndRetryDeliversExactlyOnce(bool resident)
    {
        using var f = Create();
        using var live = resident ? ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true) : null;
        var (first, second) = Install(f);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(second.DurationSeconds));
        using (WorldDbContext world = new(f.Db.Path))
        {
            // 101 is IndustryJobStatus.Delivered; the second installed job has the newest identity.
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER RejectBulkDelivery BEFORE UPDATE OF Status ON WorldIndustryJob
                WHEN NEW.Id=(SELECT MAX(Id) FROM WorldIndustryJob) AND NEW.Status=101
                BEGIN SELECT RAISE(ABORT,'forced bulk failure'); END;
                """
            );
        }
        IndustryDelivery[] deliveries = [new(first.Id, first.Facility.SystemId), new(second.Id, second.Facility.SystemId)];
        Assert.Throws<DbUpdateException>(() => f.Store.DeliverMany(f.Pilot.PilotId, deliveries));
        Assert.Equal(0, f.Quantity(215));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.All(world.Set<IndustryJob>().ToArray(), job => Assert.Equal(IndustryJobStatus.Installed, job.Status));
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectBulkDelivery");
        }
        Assert.Equal(2, f.Store.DeliverMany(f.Pilot.PilotId, deliveries).Count);
        Assert.Equal(300, f.Quantity(215));
        Assert.All(f.Store.DeliverMany(f.Pilot.PilotId, deliveries), result => Assert.Empty(result.Changes));
        Assert.Equal(300, f.Quantity(215));
    }

    [Fact]
    public void MalformedDuplicateAndOversizedRequestsCannotPartiallyDeliverAndEmptyBatchSucceeds()
    {
        using var f = Create();
        var (first, second) = Install(f);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(second.DurationSeconds));
        using var context = Context(f);
        object?[] pair = [first.Id, (long)first.Facility.SystemId];
        foreach (
            object?[] args in new object?[][]
            {
                [],
                [null],
                [new object?[] { pair }],
                [new List<object?> { pair, pair }],
                [
                    new List<object?>
                    {
                        pair,
                        new List<object?> { second.Id, (long)second.Facility.SystemId },
                    },
                ],
                [new List<object?> { pair, new object?[] { second.Id, -1L } }],
                [new List<object?> { pair, new object?[] { second.Id, long.MaxValue } }],
                [Enumerable.Repeat<object?>(pair, IndustryDefinitions.MaximumDeliveryBatch + 1).ToList()],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(Call(f, context, args));
            Assert.Equal(0, f.Quantity(215));
            Assert.Empty(context.Notifications);
        }
        Assert.IsType<PilotCallResult.Failure>(Call(f, context, [Batch(first, second)], new() { ["ownerID"] = 90000002L }));
        Assert.IsType<PilotCallResult.Success>(Call(f, context, [new List<object?>()]));
        Assert.Empty(context.Notifications);
        Assert.Throws<InvalidDataException>(() => IndustryDeliveryRules.Validate([new(first.Id, 0)]));
        Assert.Throws<InvalidDataException>(() => IndustryDeliveryRules.Validate([new(0, first.Facility.SystemId)]));
    }
}
