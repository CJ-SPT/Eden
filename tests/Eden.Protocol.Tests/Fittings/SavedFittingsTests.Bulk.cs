using Eden.Data.World.Fittings;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Fittings;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Fittings;

public sealed partial class SavedFittingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeBulkImportMapsTemporaryIdsAndDeleteReturnsCommittedIdsAcrossReopen(bool liveWorld)
    {
        using TestWorld db = new(liveWorld: liveWorld);
        using Client client = new(db);
        string assets = Assets(db);
        Assert.Empty(client.List());
        var mappings = Assert.IsType<List<object?>>(client.Success("SaveManyFittings", [Owner, Batch()]));
        Assert.Equal(2, mappings.Count);
        var ids = mappings.Select(row => BlueRead.Fields(Assert.IsType<BlueInstance>(row).State)).ToArray();
        Assert.Equal(new long[] { -1, -2 }, ids.Select(row => Assert.IsType<long>(row["tempFittingID"])));
        var real = ids.Select(row => Assert.IsType<long>(row["realFittingID"])).ToArray();
        Assert.Equal(2, real.Distinct().Count());
        Assert.All(real, id => Assert.True(id > 0));
        Assert.Equal(real, client.Context.Notifications.OfType<FittingAddedNotification>().Select(n => n.FittingId));
        client.Context.Notifications.Clear();
        client.AdvanceFittings();
        Assert.Empty(client.Context.Notifications);
        if (!liveWorld)
        {
            WorldInitialization.Open(db.Path);
        }
        using var reopened = ServerServices.Build(new(db.Path));
        var store = reopened.GetRequiredService<IPilotSavedFittings>();
        Assert.Equal(real, store.Read(Owner).Select(f => f.Id));
        Assert.Equal(new[] { "First", "Second" }, store.Read(Owner).Select(f => f.Plan.Name));
        var deleted = Assert.IsType<List<object?>>(client.Success("DeleteManyFittings", [Owner, real.Cast<object?>().ToList()]));
        Assert.Equal(real, deleted.Cast<long>());
        var notice = Assert.IsType<FittingsDeletedNotification>(Assert.Single(client.Context.Notifications));
        Assert.Equal(
            real,
            Assert.IsType<List<object?>>(BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(notice.Arguments)), 2)[1]).Cast<long>()
        );
        client.Dispose();
        db.Services.Dispose();
        WorldInitialization.Open(db.Path);
        Assert.Empty(store.Read(Owner));
        Assert.Equal(assets, Assets(db));
    }

    [Fact]
    public void InvalidLateImportsAndForeignDeletesDoNotPartiallyChangeTemplatesOrNotify()
    {
        using TestWorld db = new();
        using Client client = new(db);
        var invalid = Batch();
        invalid[-2] = NativePlan(hull: 999999, name: "Unknown");
        Assert.IsType<PilotCallResult.Failure>(client.Call("SaveManyFittings", [Owner, invalid]));
        Assert.Empty(client.List());
        Assert.Empty(client.Context.Notifications);
        var store = db.Services.GetRequiredService<IPilotSavedFittings>();
        long mine = store.Save(Owner, Plan("Mine"));
        long foreign = store.Save(Other, Plan("Foreign"));
        client.List();
        client.Context.Notifications.Clear();
        string before = SavedRows(db);
        long revision = store.Revision(Owner);
        foreach (
            object? invalidIds in new object?[]
            {
                new List<object?> { mine, foreign },
                new List<object?> { mine, long.MaxValue },
                new List<object?> { mine, mine },
                new List<object?> { mine, true },
                new object?[] { mine },
                new List<object?> { 0L },
                null,
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(client.Call("DeleteManyFittings", [Owner, invalidIds]));
            Assert.Equal(before, SavedRows(db));
            Assert.Equal(revision, store.Revision(Owner));
            Assert.Empty(client.Context.Notifications);
        }
    }

    [Fact]
    public void MalformedBulkRequestsAndWrongOwnersLeaveSavedPlansUnchanged()
    {
        using TestWorld db = new();
        using Client client = new(db);
        object?[] malformed =
        [
            null,
            new List<object?> { NativePlan() },
            new Dictionary<long, object?> { [1] = NativePlan() },
            new Dictionary<long, object?> { [-1] = NativePlan(owner: Other) },
            new Dictionary<long, object?> { [-1] = NativePlan(), [-2] = null },
            Enumerable.Range(1, SavedFittingRules.MaximumCorporationFittings + 1).ToDictionary(i => -(long)i, _ => (object?)NativePlan()),
        ];
        foreach (var payload in malformed)
        {
            Assert.IsType<PilotCallResult.Failure>(client.Call("SaveManyFittings", [Owner, payload]));
            Assert.Empty(client.List());
            Assert.Empty(client.Context.Notifications);
        }
        Assert.IsType<PilotCallResult.Failure>(client.Call("SaveManyFittings", [Other, Batch()]));
        Assert.IsType<PilotCallResult.Failure>(client.Call("SaveManyFittings", [Owner, Batch()], new() { ["unknown"] = 1L }));
        Assert.Empty(Assert.IsType<List<object?>>(client.Success("SaveManyFittings", [Owner, new Dictionary<long, object?>()])));
        Assert.Empty(Assert.IsType<List<object?>>(client.Success("DeleteManyFittings", [Owner, new List<object?>()])));
        Assert.Empty(client.Context.Notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DatabaseFailureRollsBackEveryImportedOrDeletedFittingAndRevision(bool deleting)
    {
        using TestWorld db = new();
        var store = db.Services.GetRequiredService<IPilotSavedFittings>();
        var initial = store.SaveMany(Owner, Owner, [new(-1, Plan("First")), new(-2, Plan("Second"))]);
        string before = SavedRows(db);
        long revision = store.Revision(Owner);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                deleting
                    ? """
                    CREATE TRIGGER FailFittingBatch BEFORE DELETE ON WorldSavedFitting WHEN OLD.Name='Second'
                    BEGIN SELECT RAISE(ABORT,'forced fitting failure'); END;
                    """
                    : """
                    CREATE TRIGGER FailFittingBatch BEFORE INSERT ON WorldSavedFitting WHEN NEW.Name='Second'
                    BEGIN SELECT RAISE(ABORT,'forced fitting failure'); END;
                    """
            );
        }
        if (deleting)
        {
            Assert.Throws<DbUpdateException>(() => store.DeleteMany(Owner, Owner, initial.Select(f => f.FittingId).ToArray()));
        }
        else
        {
            Assert.Throws<DbUpdateException>(() => store.SaveMany(Owner, Owner, [new(-1, Plan("First")), new(-2, Plan("Second"))]));
        }
        Assert.Equal(before, SavedRows(db));
        Assert.Equal(revision, store.Revision(Owner));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TRIGGER FailFittingBatch");
        }
        WorldInitialization.Open(db.Path);
        Assert.Equal(2, store.Read(Owner).Count);
    }

    [Fact]
    public void BulkQuotaCountsTheWholeBatchAndUpdatesRemainAvailableAtCapacity()
    {
        using TestWorld db = new();
        var store = db.Services.GetRequiredService<IPilotSavedFittings>();
        using (WorldDbContext world = new(db.Path))
        {
            world.AddRange(
                Enumerable
                    .Range(1, SavedFittingRules.MaximumFittings - 1)
                    .Select(i => new SavedFittingRecord
                    {
                        OwnerId = Owner,
                        Name = "Fit " + i,
                        ShipTypeId = 601,
                    })
            );
            world.SaveChanges();
        }
        string before = SavedRows(db);
        Assert.Throws<InvalidDataException>(() => store.SaveMany(Owner, Owner, [new(-1, Plan("First")), new(-2, Plan("Second"))]));
        Assert.Equal(before, SavedRows(db));
        long id = Assert.Single(store.SaveMany(Owner, Owner, [new(-1, Plan("Last"))])).FittingId;
        Assert.Equal(id, store.Save(Owner, Plan("Updated"), id));
        Assert.Throws<InvalidDataException>(() => store.SaveMany(Owner, Owner, [new(-1, Plan("Over"))]));
        store.DeleteMany(Owner, Owner, [id]);
        Assert.Single(store.SaveMany(Owner, Owner, [new(-1, Plan("Replacement"))]));
    }

    private static Dictionary<long, object?> Batch(long owner = Owner) =>
        new() { [-1] = NativePlan(name: "First", owner: owner), [-2] = NativePlan(name: "Second", owner: owner) };

    private static FittingPlan Plan(string name) => new(name, "", 601, [new(3651, InventoryFlag.HighSlot0, 1)]);
}
