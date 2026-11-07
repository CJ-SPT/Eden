using Eden.Gameplay.Mail;
using Eden.World.Mail;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Persistence;

public sealed class MailLifecycleTests
{
    [Fact]
    public void FailedDeliveryAndFlagMutationRollBackMessageCopiesFeedAndReplyState()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var store = MailTestData.Store(db);
        long id = store.Send(MailTestData.Sender, MailTestData.Draft, MailTestData.Origin);
        long before = store.Observe(MailTestData.Recipient);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectMail BEFORE INSERT ON WorldMailChange BEGIN SELECT RAISE(ABORT,'test'); END;"
            );
        }
        Assert.Throws<DbUpdateException>(() =>
            store.Send(MailTestData.Recipient, MailTestData.Draft with { Recipients = [MailTestData.Sender], ReplyTo = id }, Guid.NewGuid())
        );
        Assert.Throws<DbUpdateException>(() =>
            store.Change(MailTestData.Recipient, new MailSelection.Ids([id]), MailOperation.Read, 0, Guid.NewGuid())
        );
        Assert.Equal(before, store.Observe(MailTestData.Recipient));
        Assert.Equal(MailStatus.None, Assert.Single(store.Sync(MailTestData.Recipient, null, 0).Status).Status);
        using WorldDbContext saved = new(db.Path);
        Assert.Single(saved.Set<SavedMail>());
        Assert.Equal(2, saved.Set<SavedMailEntry>().Count());
    }

    [Fact]
    public async Task ConcurrentSendsCannotBypassThePerPilotThrottleOrCreatePartialCopies()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var store = MailTestData.Store(db);
        for (int i = 0; i < MailRules.MaximumPerMinute - 1; i++)
        {
            store.Send(MailTestData.Sender, MailTestData.Draft, MailTestData.Origin);
        }
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ => Task.Run(() => Record.Exception(() => store.Send(MailTestData.Sender, MailTestData.Draft, Guid.NewGuid()))))
        );
        Assert.Single(results, r => r is null);
        Assert.IsType<InvalidDataException>(Assert.Single(results, r => r is not null));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(5, saved.Set<SavedMail>().Count());
        Assert.Equal(10, saved.Set<SavedMailEntry>().Count());
        Assert.Equal(10, saved.Set<SavedMailChange>().Count());
    }

    [Fact]
    public void BoundedFeedDrainsInOrderAndDeletedDeliveriesCannotReviveMail()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var store = MailTestData.Store(db);
        List<long> ids = [];
        for (int i = 0; i < MailRules.FeedBatch + 2; i++)
        {
            ids.Add(store.Send(MailTestData.Sender, MailTestData.Draft, MailTestData.Origin));
            clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
        }
        store.Change(MailTestData.Recipient, new MailSelection.Ids([ids[0]]), MailOperation.Trash, 0, Guid.NewGuid());
        store.Change(MailTestData.Recipient, new MailSelection.Trash(), MailOperation.Delete, 0, Guid.NewGuid());
        var first = store.Changes(MailTestData.Recipient, 0);
        Assert.True(first.More);
        Assert.DoesNotContain(first.Changes, c => c.Kind == MailChangeKind.Sent && c.State.Id == ids[0]);
        var second = store.Changes(MailTestData.Recipient, first.Cursor);
        Assert.False(second.More);
        var changes = first.Changes.Concat(second.Changes).ToArray();
        Assert.Equal(changes.Select(c => c.Id).Order(), changes.Select(c => c.Id));
        Assert.Equal(129, changes.Count(c => c.Kind == MailChangeKind.Sent));
        Assert.DoesNotContain(changes, c => c.State.Id == ids[0] && c.Kind != MailChangeKind.Deleted);
        Assert.Equal(store.Observe(MailTestData.Recipient), second.Cursor);
        Assert.Empty(store.Changes(MailTestData.Recipient, second.Cursor).Changes);
    }

    [Fact]
    public void Version61MigrationPreservesSkillsInventoryAndPriorWalletState()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var pilot = db.Store.FindPilot(1000001)!;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "DROP TABLE WorldMailChange; DROP TABLE WorldMailLabel; "
                    + "DROP TABLE WorldMailEntry; DROP TABLE WorldMail; PRAGMA user_version=61"
            );
        }
        WorldInitialization.Initialize(new WorldDatabase(db.Path), clock);
        Assert.Equal(pilot, db.Store.FindPilot(1000001));
        Assert.Empty(MailTestData.Store(db).Sync(MailTestData.Sender, null, 0).Status);
        Assert.True(MailTestData.Store(db).Send(MailTestData.Sender, MailTestData.Draft, MailTestData.Origin) > 0);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }
}
