using Eden.Data.World.Bookmarks;
using Eden.Data.World.Calendar;
using Eden.Data.World.Characters;
using Eden.Data.World.Mail;
using Eden.Gameplay.AccessGroups;
using Eden.Gameplay.Bookmarks;
using Eden.Gameplay.Calendar;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Mail;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class ResidentSocialTests
{
    [Fact]
    public void SwappedContactLabelNamesProjectAfterMultipleResidentRenames()
    {
        using TestWorld db = new();
        using LiveWorld live = new(new(db.Path), TimeProvider.System, _ => { });
        var contacts = db.Services.GetRequiredService<IPilotContacts>();
        long owner = CalendarTestData.Owner;
        long first = contacts.CreateLabel(owner, "First", 1);
        long second = contacts.CreateLabel(owner, "Second", 2);
        using (live.EnterShared("contact label baseline"))
        {
            live.Economy.Flush();
        }
        contacts.EditLabel(owner, first, "Temporary", null);
        contacts.EditLabel(owner, second, "First", null);
        contacts.EditLabel(owner, first, "Second", null);
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext saved = new(checkpoint.Database);
        Assert.Equal("Second", saved.Set<SavedContactLabel>().Find(owner, first)!.Name);
        Assert.Equal("First", saved.Set<SavedContactLabel>().Find(owner, second)!.Name);
    }

    [Fact]
    public void MailAndCalendarPublishTogetherWithTheirFeedsAndRetryCheckpoint()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        using LiveWorld live = new(new(db.Path), clock, _ => { });
        live.Economy.Warm();
        var mail = MailTestData.Store(db);
        var lists = MailingListTestData.Store(db);
        var calendar = db.Services.GetRequiredService<IPilotCalendar>();
        long owner = CalendarTestData.Owner;
        long guest = CalendarTestData.Guest;
        Guid origin = MailTestData.Origin;
        long reply;
        long eventId;
        using (var metrics = SessionPerformance.Begin("resident mail and calendar", _ => { }, force: true)!)
        {
            Assert.Empty(mail.Changes(guest, 0).Changes);
            Assert.Empty(lists.Changes(guest, 0).Items);
            long list = lists.Create(owner, "Fleet mail", MailingListTestData.Open, origin);
            lists.SaveWelcome(owner, list, new("Welcome", "Fleet information"), false, origin);
            Assert.Equal(list, lists.Join(guest, "Fleet mail", origin).Id);
            Assert.Single(lists.Changes(guest, 0).Items);
            var welcome = Assert.Single(mail.Changes(guest, 0).Changes);
            Assert.Equal("Welcome", welcome.Header!.Subject);
            reply = mail.Send(owner, MailingListTestData.Broadcast(list), origin);
            var broadcast = Assert.Single(mail.Changes(guest, welcome.Id).Changes);
            Assert.Equal(reply, broadcast.Header!.Id);
            Assert.Equal(origin, broadcast.Origin);
            Assert.Empty(mail.Changes(guest, broadcast.Id).Changes);
            Assert.Equal(MailTestData.Draft.Body, mail.Body(guest, reply, true, origin));

            long revision = calendar.Revision;
            var draft = CalendarTestData.Draft(clock);
            Assert.Throws<InvalidDataException>(() => calendar.Create(owner, draft, [guest, long.MaxValue]));
            Assert.Equal(revision, calendar.Revision);
            Assert.Empty(calendar.Changes(guest, 0).Items);
            eventId = calendar.Create(owner, draft, [guest]);
            Assert.True(calendar.Revision > revision);
            Assert.Equal(eventId, Assert.Single(calendar.Month(guest, 11, 2026)).Id);
            calendar.Respond(guest, eventId, owner, CalendarResponse.Accepted);
            Assert.Equal(CalendarResponse.Accepted, Assert.Single(calendar.Attendance(owner, eventId, owner)).Response);
            Assert.Equal(CalendarChangeKind.Created, Assert.Single(calendar.Changes(guest, 0).Items).Kind);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }

        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        using (WorldDbContext baseline = new(checkpoint.Database))
        {
            baseline.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER RejectCalendarFeed BEFORE INSERT ON WorldCalendarChange
                BEGIN SELECT RAISE(ABORT,'calendar checkpoint failure'); END;
                """
            );
        }
        Assert.Throws<SqliteException>(() => checkpoint.Project());
        using (WorldDbContext failed = new(checkpoint.Database))
        {
            Assert.Empty(failed.Set<SavedMail>());
            Assert.Empty(failed.Set<SavedCalendarEvent>());
            failed.Database.ExecuteSqlRaw("DROP TRIGGER RejectCalendarFeed");
        }
        Assert.Single(calendar.Changes(guest, 0).Items);
        Assert.Equal(MailTestData.Draft.Body, mail.Body(guest, reply, false, origin));
        checkpoint.Project();
        using WorldDbContext saved = new(checkpoint.Database);
        Assert.Equal(origin, saved.Set<SavedMailChange>().First(c => c.MessageId == reply).Origin);
        Assert.Equal(MailStatus.Read, saved.Set<SavedMailEntry>().Find(guest, reply)!.Status);
        Assert.Equal(CalendarResponse.Accepted, saved.Set<SavedCalendarParticipant>().Find(eventId, guest)!.Response);
    }

    [Fact]
    public void SharedBookmarkRevocationAndCrossFolderMovesSurviveResidentCheckpoint()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        using LiveWorld live = new(new(db.Path), clock, _ => { });
        live.Economy.Warm();
        var groups = db.Services.GetRequiredService<IPilotAccessGroups>();
        var bookmarks = db.Services.GetRequiredService<IPilotBookmarks>();
        long owner = CalendarTestData.Owner;
        long guest = CalendarTestData.Guest;
        long bookmarkId;
        long destinationId;
        long folderId;
        using (var metrics = SessionPerformance.Begin("resident shared bookmarks", _ => { }, force: true)!)
        {
            var admin = groups.Create(owner, "Administrators", "");
            var viewers = groups.Create(owner, "Viewers", "");
            groups.ChangeMembers(owner, viewers.Id, [new(guest, AccessGroupRole.Member)]);
            var folder = bookmarks.AddSharedFolder(owner, "Fleet", "", new(admin.Id, null, null, viewers.Id));
            folderId = folder.Id;
            bookmarks.SetFolderState(guest, folder.Id, true);
            var subfolder = bookmarks.AddSubfolder(owner, folder.Id, "Old");
            BookmarkTarget target = new(null, BookmarkRules.SolarSystemTypeId, 30000142, new(1, 2, 3));
            var point = bookmarks.Create(owner, target, folder.Id, subfolder.Id, new("Home", ""), BookmarkExpiry.Never);
            bookmarkId = point.Id;
            clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
            Assert.Equal(point.Id, bookmarks.Find(guest, point.Id).Id);
            groups.Delete(owner, viewers.Id);
            Assert.Empty(bookmarks.Read(guest).Folders);
            Assert.Throws<InvalidDataException>(() => bookmarks.Find(guest, point.Id));
            var destination = bookmarks.AddFolder(owner, "Personal", "");
            destinationId = destination.Id;
            bookmarks.MoveOrCopy(owner, folder.Id, destination.Id, null, [point.Id], copy: false);
            bookmarks.DeleteSubfolder(owner, folder.Id, subfolder.Id);
            Assert.Equal(destination.Id, bookmarks.Find(owner, point.Id).FolderId);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext saved = new(checkpoint.Database);
        Assert.Equal(destinationId, saved.Set<SavedBookmark>().Find(bookmarkId)!.FolderId);
        Assert.Null(saved.Set<SavedBookmarkFolder>().Find(folderId)!.ViewGroupId);
        Assert.Null(saved.Set<SavedBookmarkConnection>().Find(guest, folderId));
        Assert.Empty(saved.Set<SavedBookmarkSubfolder>());
    }
}
