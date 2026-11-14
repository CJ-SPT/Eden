using Eden.Gameplay.Characters;
using Eden.Gameplay.Notifications;
using Eden.Server;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class InboxTests
{
    private const long Sender = 90000001;
    private const long Recipient = 90000002;

    [Fact]
    public void ContactNotificationsRetainTypedDataReadStateAndDeletionAcrossRestart()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        var inbox = db.Services.GetRequiredService<IPilotInbox>();
        contacts.Add(Sender, Recipient, 5, true, "Étoile 学習 🚀");
        long firstTime = clock.Now;
        clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
        contacts.Edit(Sender, Recipient, -5, false, "Update");
        var rows = inbox.Read(Recipient, new());
        Assert.Equal(new[] { InboxNotificationType.ContactEdited, InboxNotificationType.ContactAdded }, rows.Select(n => n.Type));
        Assert.Equal(new ContactNotificationContent(-5, "Update"), rows[0].Content);
        Assert.Equal(new ContactNotificationContent(5, "Étoile 学習 🚀"), rows[1].Content);
        Assert.Equal(firstTime, rows[1].Created);
        Assert.All(
            rows,
            n =>
            {
                Assert.Equal(Sender, n.SenderId);
                Assert.Equal(Recipient, n.ReceiverId);
                Assert.False(n.Processed);
            }
        );
        Assert.Empty(inbox.Read(Sender, new()));
        Assert.Equal(rows[0], Assert.Single(inbox.Read(Recipient, new(AfterId: rows[1].Id))));
        Assert.Equal(2, inbox.Read(Recipient, new(Group: InboxGroup.Contacts)).Count);
        Assert.Empty(inbox.Read(Recipient, new(Group: InboxGroup.Insurance)));
        inbox.MarkRead(Recipient, [rows[1].Id]);
        Assert.Equal(rows[0], Assert.Single(inbox.Read(Recipient, new(UnreadOnly: true))));
        inbox.Delete(Recipient, [rows[0].Id]);
        inbox.Delete(Recipient, [rows[0].Id]);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        inbox = reopened.GetRequiredService<IPilotInbox>();
        var saved = Assert.Single(inbox.Read(Recipient, new()));
        Assert.True(saved.Processed);
        Assert.Equal(rows[1].Content, saved.Content);
        Assert.Empty(inbox.Read(Recipient, new(UnreadOnly: true)));
        Assert.Equal(rows[0].Id, inbox.Observe(Recipient));
        contacts.Edit(Sender, Recipient, 0, false, "Next");
        Assert.True(inbox.Read(Recipient, new())[0].Id > rows[0].Id);
    }

    [Fact]
    public void ForeignOrMissingIdsCannotPartiallyReadOrDeleteANotificationBatch()
    {
        using TestWorld db = new();
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        var inbox = db.Services.GetRequiredService<IPilotInbox>();
        contacts.Add(Sender, Recipient, 0, false, "To recipient");
        contacts.Add(Recipient, Sender, 0, false, "To sender");
        long owned = Assert.Single(inbox.Read(Recipient, new())).Id;
        long foreign = Assert.Single(inbox.Read(Sender, new())).Id;
        foreach (long invalid in new[] { foreign, long.MaxValue })
        {
            Assert.Throws<InvalidDataException>(() => inbox.MarkRead(Recipient, [owned, invalid]));
            Assert.Throws<InvalidDataException>(() => inbox.Delete(Recipient, [owned, invalid]));
        }
        Assert.False(Assert.Single(inbox.Read(Recipient, new())).Processed);
        Assert.False(Assert.Single(inbox.Read(Sender, new())).Processed);
        inbox.MarkRead(Recipient, [owned, owned]);
        inbox.MarkRead(Recipient, [owned]);
        Assert.True(Assert.Single(inbox.Read(Recipient, new())).Processed);
        inbox.MarkRead(Recipient, []);
        inbox.Delete(Recipient, []);
        Assert.Throws<InvalidDataException>(() => inbox.Read(Recipient, new(AfterId: -1)));
        Assert.Throws<InvalidDataException>(() => inbox.MarkRead(Recipient, [0]));
        Assert.Throws<InvalidDataException>(() => inbox.Delete(Recipient, Enumerable.Repeat(owned, 101).ToArray()));
        Assert.Throws<InvalidDataException>(() => inbox.Read(Recipient, new(Group: (InboxGroup)13)));
    }

    [Fact]
    public void ReadAndDeleteGroupsUseTheRecipientsCurrentUnreadState()
    {
        using TestWorld db = new();
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        var inbox = db.Services.GetRequiredService<IPilotInbox>();
        contacts.Add(Sender, Recipient, 0, false, "First");
        long first = inbox.Read(Recipient, new())[0].Id;
        contacts.Edit(Sender, Recipient, 5, false, "Second");
        inbox.MarkRead(Recipient, [first]);
        Assert.Single(inbox.Read(Recipient, new(Group: InboxGroup.Unread)));
        inbox.DeleteGroup(Recipient, InboxGroup.Insurance);
        Assert.Equal(2, inbox.Read(Recipient, new()).Count);
        inbox.DeleteGroup(Recipient, InboxGroup.Unread);
        Assert.Equal(first, Assert.Single(inbox.Read(Recipient, new())).Id);
        contacts.Edit(Sender, Recipient, 10, false, "Third");
        inbox.MarkGroupRead(Recipient, InboxGroup.Contacts);
        Assert.Empty(inbox.Read(Recipient, new(UnreadOnly: true)));
        contacts.Edit(Sender, Recipient, -10, false, "Fourth");
        inbox.MarkGroupRead(Recipient, null);
        Assert.Empty(inbox.Read(Recipient, new(Group: InboxGroup.Unread)));
        inbox.DeleteGroup(Recipient, InboxGroup.Contacts);
        Assert.Empty(inbox.Read(Recipient, new()));
        contacts.Edit(Sender, Recipient, -5, false, "Fifth");
        inbox.DeleteGroup(Recipient, null);
        Assert.Empty(inbox.Read(Recipient, new()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BlockedMessagesRejectContactChangesAndTaskProgressTogether(bool recipientBlocks)
    {
        using TestWorld db = new();
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        var inbox = db.Services.GetRequiredService<IPilotInbox>();
        contacts.SetBlocked(recipientBlocks ? Recipient : Sender, [recipientBlocks ? Sender : Recipient], true);
        Assert.Throws<InvalidDataException>(() => contacts.Add(Sender, Recipient, 5, true, "Blocked"));
        Assert.Empty(contacts.Read(Sender).Addresses);
        Assert.Equal(0, TaskTrackingTestData.Count(db, TaskEvents.ContactAdded));
        Assert.Empty(inbox.Read(Recipient, new()));
        Assert.Equal(0, inbox.Revision);
        contacts.Add(Sender, Recipient, 0, false);
        Assert.Throws<InvalidDataException>(() => contacts.Edit(Sender, Recipient, 10, true, "Blocked"));
        Assert.Equal(new PersonalContact(Recipient, 0, false, 0), Assert.Single(contacts.Read(Sender).Addresses));
        Assert.Empty(inbox.Read(Recipient, new()));
        Assert.Equal(0, inbox.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedNotificationCreationRollsBackContactEditsAndPublishesNothing(bool edit)
    {
        using TestWorld db = new();
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        var inbox = db.Services.GetRequiredService<IPilotInbox>();
        if (edit)
        {
            contacts.Add(Sender, Recipient, 0, false);
        }
        long before = TaskTrackingTestData.Count(db, TaskEvents.ContactAdded);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectInbox BEFORE INSERT ON WorldInboxNotification " + "BEGIN SELECT RAISE(ABORT,'forced failure'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() =>
        {
            if (edit)
            {
                contacts.Edit(Sender, Recipient, 10, true, "Atomic");
            }
            else
            {
                contacts.Add(Sender, Recipient, 10, true, "Atomic");
            }
        });
        if (edit)
        {
            Assert.Equal(new PersonalContact(Recipient, 0, false, 0), Assert.Single(contacts.Read(Sender).Addresses));
        }
        else
        {
            Assert.Empty(contacts.Read(Sender).Addresses);
        }
        Assert.Equal(before, TaskTrackingTestData.Count(db, TaskEvents.ContactAdded));
        Assert.Empty(inbox.Read(Recipient, new()));
        Assert.Equal(0, inbox.Revision);
    }

    [Fact]
    public void InvalidMessagesAndRepeatedContactAddsCannotLeaveExtraNotifications()
    {
        using TestWorld db = new();
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        var inbox = db.Services.GetRequiredService<IPilotInbox>();
        foreach (string message in new[] { new string('x', 121), "bad\0message", "bad\nmessage" })
        {
            Assert.Throws<InvalidDataException>(() => contacts.Add(Sender, Recipient, 0, false, message));
        }
        Assert.Empty(contacts.Read(Sender).Addresses);
        Assert.Empty(inbox.Read(Recipient, new()));
        contacts.Add(Sender, Recipient, 0, false, new string('x', 120));
        Assert.Throws<InvalidDataException>(() => contacts.Add(Sender, Recipient, 0, false, "Duplicate"));
        Assert.Single(inbox.Read(Recipient, new()));
        contacts.Edit(Sender, Recipient, 5, false, "");
        Assert.Equal("", inbox.Read(Recipient, new())[0].Content.Message);
    }

    [Fact]
    public void FailedBulkMutationKeepsReadAndDeletedStateUnchanged()
    {
        using TestWorld db = new();
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        var inbox = db.Services.GetRequiredService<IPilotInbox>();
        contacts.Add(Sender, Recipient, 0, false, "First");
        contacts.Edit(Sender, Recipient, 5, true, "Second");
        var ids = inbox.Read(Recipient, new()).Select(n => n.Id).ToArray();
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectInboxUpdate BEFORE UPDATE ON WorldInboxNotification "
                    + "WHEN NEW.Id=2 BEGIN SELECT RAISE(ABORT,'forced failure'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() => inbox.MarkRead(Recipient, ids));
        Assert.Throws<DbUpdateException>(() => inbox.Delete(Recipient, ids));
        Assert.Throws<DbUpdateException>(() => inbox.MarkGroupRead(Recipient, null));
        Assert.Throws<DbUpdateException>(() => inbox.DeleteGroup(Recipient, null));
        var rows = inbox.Read(Recipient, new());
        Assert.Equal(2, rows.Count);
        Assert.All(rows, n => Assert.False(n.Processed));
    }

    [Fact]
    public async Task ConcurrentContactAddsCreateOneNotificationAndAStableDeliveryIdentity()
    {
        using TestWorld db = new();
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        var inbox = db.Services.GetRequiredService<IPilotInbox>();
        var errors = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => Task.Run(() => Record.Exception(() => contacts.Add(Sender, Recipient, 5, false, "Once"))))
        );
        Assert.Single(errors, e => e is null);
        Assert.IsType<InvalidDataException>(Assert.Single(errors, e => e is not null));
        Assert.Single(inbox.Read(Recipient, new()));
        Assert.Single(inbox.Changes(Recipient, 0).Items);
        Assert.Equal(1, inbox.Revision);
    }

    [Fact]
    public void VersionFortyEightUpgradeIsAdditiveAndTombstonesStillAdvanceTheCursor()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldInboxNotification; PRAGMA user_version=48");
        }
        Assert.Equal(pilot, WorldInitialization.Open(db.Path).FindPilot(1000001));
        db.Services.GetRequiredService<IPilotContacts>().Add(Sender, Recipient, 5, false, "Saved");
        var inbox = db.Services.GetRequiredService<IPilotInbox>();
        long id = Assert.Single(inbox.Read(Recipient, new())).Id;
        inbox.Delete(Recipient, [id]);
        WorldInitialization.Open(db.Path);
        var changes = inbox.Changes(Recipient, 0);
        Assert.True(Assert.Single(changes.Items).Deleted);
        Assert.Equal(id, changes.Cursor);
        Assert.Empty(inbox.Changes(Recipient, id).Items);
        Assert.Empty(inbox.Read(Recipient, new()));
    }
}
