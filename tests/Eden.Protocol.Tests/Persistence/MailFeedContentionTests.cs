using Eden.Data.World.Mail;
using Eden.Gameplay.Mail;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class MailFeedContentionTests
{
    [Fact]
    public async Task FeedHeadsFollowCommitRollbackDeletionAndReopenedCheckpoint()
    {
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var mail = MailTestData.Store(fixture);
        var lists = MailingListTestData.Store(fixture);
        const long Pilot = MailTestData.Sender;
        long mailCursor = mail.Observe(Pilot);
        long listCursor = lists.Observe(Pilot);
        AssertIdle();
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(new SavedMailChange { PilotId = Pilot, Kind = MailChangeKind.Deleted });
            action.Add(new SavedMailingListChange { PilotId = Pilot, Kind = MailingListChangeKind.Deleted });
        }
        AssertIdle();
        mail.CreateLabel(Pilot, "Fleet", null, Guid.NewGuid());
        lists.Create(Pilot, "Fleet", MailingListTestData.Open, Guid.NewGuid());
        Assert.True(live.Economy.HasMailChanges(Pilot, mailCursor));
        Assert.True(live.Economy.HasMailingListChanges(Pilot, listCursor));
        var mailBatch = mail.Changes(Pilot, mailCursor);
        var listBatch = lists.Changes(Pilot, listCursor);
        var mailChange = Assert.Single(mailBatch.Changes);
        var listChange = Assert.Single(listBatch.Items);
        mailCursor = mailBatch.Cursor;
        listCursor = listBatch.Cursor;
        AssertIdle();
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Remove(action.Set<SavedMailChange>().Find(mailChange.Id)!);
            action.Remove(action.Set<SavedMailingListChange>().Find(listChange.Id)!);
            action.Commit();
        }
        Assert.Equal(0, mail.Observe(Pilot));
        Assert.Equal(0, lists.Observe(Pilot));
        AssertIdle();
        mail.CreateLabel(Pilot, "After deletion", null, Guid.NewGuid());
        lists.Create(Pilot, "After deletion", MailingListTestData.Open, Guid.NewGuid());
        Assert.Single(mail.Changes(Pilot, mailCursor).Changes);
        Assert.Single(lists.Changes(Pilot, listCursor).Items);
        await live.FlushAsync();
        using var file = WorldConnections.FileConnection(fixture.Path);
        using WorldDbContext reopened = new(file);
        Assert.NotNull(reopened.Set<SavedMailChange>().Find(mail.Observe(Pilot)));
        Assert.NotNull(reopened.Set<SavedMailingListChange>().Find(lists.Observe(Pilot)));

        void AssertIdle()
        {
            using var metrics = SessionPerformance.Begin("idle feeds", _ => { }, force: true)!;
            Assert.False(live.Economy.HasMailChanges(Pilot, mailCursor));
            Assert.False(live.Economy.HasMailingListChanges(Pilot, listCursor));
            Assert.Empty(mail.Changes(Pilot, mailCursor).Changes);
            Assert.Empty(lists.Changes(Pilot, listCursor).Items);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
    }
}
