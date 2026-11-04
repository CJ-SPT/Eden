using Eden.Gameplay.Characters;
using Eden.Gameplay.Mail;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class MailTestData
{
    internal const long Sender = CalendarTestData.Owner;
    internal const long Recipient = CalendarTestData.Guest;
    internal static readonly Guid Origin = Guid.Parse("4c74845b-2021-4daa-85ca-43f78df750a1");
    internal static MailDraft Draft => new([Recipient], "Étoile 学習", "<b>Hello</b><br>学習 🚀", null, null);

    internal static IPilotMail Store(TestWorld world) => world.Services.GetRequiredService<IPilotMail>();
}

public sealed class MailTests
{
    [Fact]
    public void MailboxCopiesBodiesStatusesAndReplyFlagsArePrivateAndPersistent()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        long other = CalendarTestData.AddPilots(db, 1)[0];
        var store = MailTestData.Store(db);
        long id = store.Send(MailTestData.Sender, MailTestData.Draft, MailTestData.Origin);
        Assert.Equal(id, Assert.Single(store.Sync(MailTestData.Sender, null, 0).New).Id);
        Assert.Equal(MailStatus.Read, Assert.Single(store.Sync(MailTestData.Sender, null, 0).Status).Status);
        Assert.Equal(2, Assert.Single(store.Sync(MailTestData.Sender, null, 0).Status).Labels);
        Assert.Equal(MailStatus.None, Assert.Single(store.Sync(MailTestData.Recipient, null, 0).Status).Status);
        Assert.Equal(1, Assert.Single(store.Sync(MailTestData.Recipient, null, 0).Status).Labels);
        Assert.Empty(store.Headers(other, [id]));
        Assert.Empty(store.Sync(other, null, 0).Status);
        Assert.Throws<InvalidDataException>(() => store.Body(other, id, true, Guid.NewGuid()));
        Assert.Equal(MailTestData.Draft.Body, store.Body(MailTestData.Recipient, id, true, Guid.NewGuid()));
        Assert.Equal(MailStatus.Read, Assert.Single(store.Sync(MailTestData.Recipient, null, 0).Status).Status);
        long reply = store.Send(
            MailTestData.Recipient,
            MailTestData.Draft with
            {
                Recipients = [MailTestData.Sender],
                ReplyTo = id,
            },
            Guid.NewGuid()
        );
        Assert.True(store.Sync(MailTestData.Recipient, null, 0).Status.Single(s => s.Id == id).Status.HasFlag(MailStatus.Replied));
        Assert.False(store.Sync(MailTestData.Sender, null, 0).Status.Single(s => s.Id == id).Status.HasFlag(MailStatus.Replied));
        WorldInitialization.Initialize(new WorldDatabase(db.Path), clock);
        Assert.Equal(MailTestData.Draft.Body, store.Body(MailTestData.Sender, reply, false, Guid.NewGuid()));
        Assert.Equal(2, store.Sync(MailTestData.Recipient, null, 0).New.Count);
    }

    [Fact]
    public void SelfMailIsOneUnreadCopyWithInboxAndSentLabels()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var store = MailTestData.Store(db);
        store.Send(MailTestData.Sender, MailTestData.Draft with { Recipients = [MailTestData.Sender] }, MailTestData.Origin);
        var row = Assert.Single(store.Sync(MailTestData.Sender, null, 0).Status);
        Assert.Equal(3, row.Labels);
        Assert.Equal(MailStatus.None, row.Status);
        Assert.Single(store.Changes(MailTestData.Sender, 0).Changes);
    }

    [Fact]
    public void RecipientFailureBlockAndForeignReplySourceLeaveEveryMailboxAndSourceFlagUnchanged()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        long other = CalendarTestData.AddPilots(db, 1)[0];
        var store = MailTestData.Store(db);
        long privateId = store.Send(MailTestData.Recipient, MailTestData.Draft with { Recipients = [other] }, MailTestData.Origin);
        Assert.Throws<InvalidDataException>(() =>
            store.Send(MailTestData.Sender, MailTestData.Draft with { Recipients = [MailTestData.Recipient, 123] }, Guid.NewGuid())
        );
        Assert.Throws<InvalidDataException>(() =>
            store.Send(MailTestData.Sender, MailTestData.Draft with { ReplyTo = privateId }, Guid.NewGuid())
        );
        db.Services.GetRequiredService<IPilotContacts>().SetBlocked(MailTestData.Recipient, [MailTestData.Sender], true);
        Assert.Throws<InvalidDataException>(() => store.Send(MailTestData.Sender, MailTestData.Draft, Guid.NewGuid()));
        Assert.Empty(store.Sync(MailTestData.Sender, null, 0).Status);
        using WorldDbContext saved = new(db.Path);
        Assert.Single(saved.Set<SavedMail>());
        Assert.Equal(MailStatus.Read, saved.Set<SavedMailEntry>().Find(MailTestData.Recipient, privateId)!.Status);
    }

    [Fact]
    public void ReadUnreadTrashRestoreAndPermanentDeleteAffectOnlyTheSelectedOwnedCopies()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var store = MailTestData.Store(db);
        long first = store.Send(MailTestData.Sender, MailTestData.Draft, MailTestData.Origin);
        long second = store.Send(MailTestData.Sender, MailTestData.Draft, MailTestData.Origin);
        var selected = new MailSelection.Ids([first]);
        store.Change(MailTestData.Recipient, new MailSelection.All(), MailOperation.Read, 0, Guid.NewGuid());
        store.Change(MailTestData.Recipient, selected, MailOperation.Unread, 0, Guid.NewGuid());
        Assert.Equal(MailStatus.None, store.Sync(MailTestData.Recipient, null, 0).Status.Single(s => s.Id == first).Status);
        Assert.Equal(MailStatus.Read, store.Sync(MailTestData.Recipient, null, 0).Status.Single(s => s.Id == second).Status);
        Assert.Throws<InvalidDataException>(() => store.Change(MailTestData.Recipient, selected, MailOperation.Delete, 0, Guid.NewGuid()));
        store.Change(MailTestData.Recipient, selected, MailOperation.Trash, 0, Guid.NewGuid());
        store.Change(MailTestData.Recipient, new MailSelection.All(), MailOperation.Unread, 0, Guid.NewGuid());
        store.Change(MailTestData.Recipient, new MailSelection.Trash(), MailOperation.Restore, 0, Guid.NewGuid());
        Assert.Equal(MailStatus.None, store.Sync(MailTestData.Recipient, null, 0).Status.Single(s => s.Id == first).Status);
        store.Change(MailTestData.Recipient, selected, MailOperation.Trash, 0, Guid.NewGuid());
        store.Change(MailTestData.Recipient, new MailSelection.Trash(), MailOperation.Delete, 0, Guid.NewGuid());
        Assert.Equal(second, Assert.Single(store.Sync(MailTestData.Recipient, null, 0).Status).Id);
        Assert.Throws<InvalidDataException>(() => store.Body(MailTestData.Recipient, first, false, Guid.NewGuid()));
        Assert.Equal(MailTestData.Draft.Body, store.Body(MailTestData.Sender, first, false, Guid.NewGuid()));
    }

    [Fact]
    public void MixedOwnershipBulkMutationsAndUnknownLabelsRollbackWithoutChangingValidRows()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var store = MailTestData.Store(db);
        long first = store.Send(MailTestData.Sender, MailTestData.Draft, MailTestData.Origin);
        long privateId = store.Send(
            MailTestData.Sender,
            MailTestData.Draft with
            {
                Recipients = [MailTestData.Sender],
            },
            MailTestData.Origin
        );
        Assert.Throws<InvalidDataException>(() =>
            store.Change(MailTestData.Recipient, new MailSelection.Ids([first, privateId]), MailOperation.Read, 0, Guid.NewGuid())
        );
        Assert.Throws<InvalidDataException>(() =>
            store.Change(MailTestData.Recipient, new MailSelection.Ids([first]), MailOperation.AssignLabels, 16, Guid.NewGuid())
        );
        Assert.Equal(new(first, MailStatus.None, 1), Assert.Single(store.Sync(MailTestData.Recipient, null, 0).Status));
    }

    [Fact]
    public void LabelsAreIndependentHaveAllTwentyFiveBitsAndClearDeletedBitsBeforeReuse()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var store = MailTestData.Store(db);
        long id = store.Send(MailTestData.Sender, MailTestData.Draft, MailTestData.Origin);
        List<long> labels = [];
        for (int i = 0; i < MailRules.MaximumLabels; i++)
        {
            labels.Add(store.CreateLabel(MailTestData.Recipient, $"Label {i}", i, Guid.NewGuid()));
        }
        Assert.Equal(1L << 28, labels[^1]);
        Assert.Throws<InvalidDataException>(() => store.CreateLabel(MailTestData.Recipient, "Extra", null, Guid.NewGuid()));
        Assert.Throws<InvalidDataException>(() => store.CreateLabel(MailTestData.Recipient, "label 0", null, Guid.NewGuid()));
        long mask = labels[0] | labels[1];
        store.Change(MailTestData.Recipient, new MailSelection.Ids([id]), MailOperation.AssignLabels, mask, Guid.NewGuid());
        Assert.Equal(mask | 1, Assert.Single(store.Sync(MailTestData.Recipient, null, 0).Status).Labels);
        store.Change(MailTestData.Recipient, new MailSelection.Label(labels[0]), MailOperation.Read, 0, Guid.NewGuid());
        store.Change(MailTestData.Recipient, new MailSelection.Ids([id]), MailOperation.RemoveLabels, labels[1], Guid.NewGuid());
        store.EditLabel(MailTestData.Recipient, labels[0], "Renamed", 7, Guid.NewGuid());
        Assert.Equal("Renamed", store.Labels(MailTestData.Recipient).Single(l => l.Id == labels[0]).Name);
        store.DeleteLabel(MailTestData.Recipient, labels[0], Guid.NewGuid());
        Assert.Equal(1, Assert.Single(store.Sync(MailTestData.Recipient, null, 0).Status).Labels);
        Assert.Equal(labels[0], store.CreateLabel(MailTestData.Recipient, "Reused", null, Guid.NewGuid()));
        Assert.Empty(store.Labels(MailTestData.Sender));
        Assert.Throws<InvalidDataException>(() => store.DeleteLabel(MailTestData.Recipient, 1, Guid.NewGuid()));
        Assert.Throws<InvalidDataException>(() => store.EditLabel(MailTestData.Recipient, 1, "Fake", null, Guid.NewGuid()));
        WorldInitialization.Initialize(new WorldDatabase(db.Path), clock);
        Assert.Equal(25, store.Labels(MailTestData.Recipient).Count);
    }

    [Fact]
    public void SyncRangesProvideNewOldAndCurrentStatusWithoutExposingPermanentlyDeletedHeaders()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var store = MailTestData.Store(db);
        long first = store.Send(MailTestData.Sender, MailTestData.Draft, MailTestData.Origin);
        long second = store.Send(MailTestData.Sender, MailTestData.Draft, MailTestData.Origin);
        long third = store.Send(MailTestData.Sender, MailTestData.Draft, MailTestData.Origin);
        var snapshot = store.Sync(MailTestData.Recipient, second, second);
        Assert.Equal(third, Assert.Single(snapshot.New).Id);
        Assert.Equal(first, Assert.Single(snapshot.Old).Id);
        Assert.Equal(3, snapshot.Status.Count);
        store.Change(MailTestData.Recipient, new MailSelection.Ids([second]), MailOperation.Trash, 0, Guid.NewGuid());
        store.Change(MailTestData.Recipient, new MailSelection.Trash(), MailOperation.Delete, 0, Guid.NewGuid());
        Assert.Empty(store.Headers(MailTestData.Recipient, [second]));
        Assert.Equal(2, store.Sync(MailTestData.Recipient, null, 0).New.Count);
    }

    [Fact]
    public void ThrottleCountsPersistedSendsAndAllowsTheExactMinuteBoundary()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var store = MailTestData.Store(db);
        for (int i = 0; i < MailRules.MaximumPerMinute; i++)
        {
            store.Send(MailTestData.Sender, MailTestData.Draft, MailTestData.Origin);
        }
        WorldInitialization.Initialize(new WorldDatabase(db.Path), clock);
        clock.AdvanceWallTime(TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1));
        Assert.Throws<InvalidDataException>(() => store.Send(MailTestData.Sender, MailTestData.Draft, Guid.NewGuid()));
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        store.Send(MailTestData.Sender, MailTestData.Draft, Guid.NewGuid());
        Assert.Equal(6, store.Sync(MailTestData.Recipient, null, 0).New.Count);
    }

    [Fact]
    public void MaximumRecipientBatchDeliversEveryCopyAndOrganizationLabelForgeryRejectsAtomically()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        long[] recipients = CalendarTestData.AddPilots(db, 49).Append(MailTestData.Recipient).ToArray();
        var store = MailTestData.Store(db);
        long id = store.Send(MailTestData.Sender, MailTestData.Draft with { Recipients = recipients }, Guid.NewGuid());
        Assert.All(recipients, recipient => Assert.Equal(id, Assert.Single(store.Sync(recipient, null, 0).Status).Id));
        Assert.Throws<InvalidDataException>(() =>
            store.Change(
                MailTestData.Recipient,
                new MailSelection.Ids([id]),
                MailOperation.AssignLabels,
                (long)SystemMailLabel.Corporation,
                Guid.NewGuid()
            )
        );
        Assert.Equal(1, Assert.Single(store.Sync(MailTestData.Recipient, null, 0).Status).Labels);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(51, saved.Set<SavedMailEntry>().Count());
    }
}
