using Eden.Gameplay.Mail;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Persistence;

public sealed class MailingListLifecycleTests
{
    [Fact]
    public async Task ConcurrentJoinsSendOneWelcomeAndDeliveryFailureRollsBackTheSubscription()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var lists = MailingListTestData.Store(db);
        long id = lists.Create(MailingListTestData.Owner, "Welcome", MailingListTestData.Open, Guid.NewGuid());
        lists.SaveWelcome(MailingListTestData.Owner, id, new("Welcome aboard", "<b>Hello 学習</b>"), false, Guid.NewGuid());
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER FailWelcome BEFORE INSERT ON WorldMailChange BEGIN SELECT RAISE(ABORT,'test'); END;"
            );
        }
        Assert.Throws<DbUpdateException>(() => lists.Join(MailingListTestData.Member, "Welcome", Guid.NewGuid()));
        Assert.Empty(lists.Joined(MailingListTestData.Member));
        Assert.Empty(MailTestData.Store(db).Sync(MailingListTestData.Member, null, 0).New);
        Assert.Equal(0, lists.Observe(MailingListTestData.Member));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TRIGGER FailWelcome");
        }
        var results = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => Task.Run(() => lists.Join(MailingListTestData.Member, "Welcome", Guid.NewGuid())))
        );
        Assert.All(results, info => Assert.Equal(id, info.Id));
        var mail = Assert.Single(MailTestData.Store(db).Sync(MailingListTestData.Member, null, 0).New);
        Assert.Equal(id, mail.SenderId);
        Assert.Equal(id, mail.ListId);
        Assert.Equal("<b>Hello 学習</b>", MailTestData.Store(db).Body(MailingListTestData.Member, mail.Id, false, Guid.NewGuid()));
        Assert.Equal(MailStatus.Automated, Assert.Single(MailTestData.Store(db).Sync(MailingListTestData.Member, null, 0).Status).Status);
        Assert.Single(lists.Changes(MailingListTestData.Member, 0).Items);
    }

    [Fact]
    public void FailedAccessRemovalFeedRollsBackBothAclAndMembership()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var lists = MailingListTestData.Store(db);
        long id = lists.Create(MailingListTestData.Owner, "Rollback", MailingListTestData.Open, Guid.NewGuid());
        lists.Join(MailingListTestData.Member, "Rollback", Guid.NewGuid());
        long cursor = lists.Observe(MailingListTestData.Member);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER FailListChange BEFORE INSERT ON WorldMailingListChange BEGIN SELECT RAISE(ABORT,'test'); END;"
            );
        }
        Assert.Throws<DbUpdateException>(() =>
            lists.SetAccess(
                MailingListTestData.Owner,
                id,
                new Dictionary<long, MailingListAccess> { [MailingListTestData.Member] = MailingListAccess.Blocked },
                Guid.NewGuid()
            )
        );
        Assert.Single(lists.Joined(MailingListTestData.Member));
        Assert.Empty(lists.Settings(MailingListTestData.Owner, id).Access);
        Assert.Equal(cursor, lists.Observe(MailingListTestData.Member));
    }

    [Fact]
    public async Task ConcurrentNameClaimsCreateOnlyOneOwnerAndUnicodeAliasesPersist()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var lists = MailingListTestData.Store(db);
        var results = await Task.WhenAll(
            new[] { MailingListTestData.Owner, MailingListTestData.Member }.Select(pilot =>
                Task.Run(() =>
                    Record.Exception(() =>
                        lists.Create(
                            pilot,
                            pilot == MailingListTestData.Owner ? "Σ Fleet" : "ςfleet",
                            MailingListTestData.Open,
                            Guid.NewGuid()
                        )
                    )
                )
            )
        );
        Assert.Single(results, error => error is null);
        Assert.IsType<InvalidDataException>(Assert.Single(results, error => error is not null));
        WorldInitialization.Initialize(new WorldDatabase(db.Path), clock);
        using WorldDbContext world = new(db.Path);
        Assert.Single(world.Set<SavedMailingList>());
        Assert.Equal(MailingListRole.Owner, Assert.Single(world.Set<SavedMailingListMember>()).Role);
    }

    [Fact]
    public void WelcomeBroadcastSaveClearAndLargeAudiencePreserveRecipientPrivacy()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var extra = CalendarTestData.AddPilots(db, 51);
        var lists = MailingListTestData.Store(db);
        long id = lists.Create(MailingListTestData.Owner, "Large", MailingListTestData.Open, Guid.NewGuid());
        foreach (long member in extra.Append(MailingListTestData.Member))
        {
            lists.Join(member, "Large", Guid.NewGuid());
        }
        long message = MailTestData.Store(db).Send(MailingListTestData.Owner, MailingListTestData.Broadcast(id), Guid.NewGuid());
        Assert.All(
            extra,
            member =>
            {
                var header = Assert.Single(MailTestData.Store(db).Sync(member, null, 0).New);
                Assert.Equal(message, header.Id);
                Assert.Empty(header.Recipients);
            }
        );
        lists.SaveWelcome(MailingListTestData.Owner, id, new("Broadcast welcome", "All current members"), true, Guid.NewGuid());
        Assert.Equal(2, MailTestData.Store(db).Sync(MailingListTestData.Member, null, 0).New.Count);
        lists.SaveWelcome(MailingListTestData.Owner, id, null, false, Guid.NewGuid());
        Assert.Null(lists.Welcome(MailingListTestData.Owner, id));
        WorldInitialization.Initialize(new WorldDatabase(db.Path), clock);
        Assert.Equal(53, lists.Members(MailingListTestData.Owner, id).Count);
        Assert.Null(lists.Welcome(MailingListTestData.Owner, id));
    }

    [Fact]
    public void MembershipAndBulkUpdateLimitsAreEnforcedAgainstTheSavedRows()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var pilots = CalendarTestData.AddPilots(db, 2999);
        var lists = MailingListTestData.Store(db);
        long id = lists.Create(MailingListTestData.Owner, "Full", MailingListTestData.Open, Guid.NewGuid());
        using (WorldDbContext world = new(db.Path))
        {
            world.AddRange(
                pilots.Select(pilot => new SavedMailingListMember
                {
                    ListId = id,
                    PilotId = pilot,
                    Role = MailingListRole.Member,
                })
            );
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => lists.Join(MailingListTestData.Member, "Full", Guid.NewGuid()));
        lists.ChangeMembers(MailingListTestData.Owner, id, pilots.Take(1000).ToArray(), MailingListRole.Muted, Guid.NewGuid());
        Assert.Equal(1000, lists.Members(MailingListTestData.Owner, id).Count(m => m.Value == MailingListRole.Muted));
        Assert.Throws<InvalidDataException>(() =>
            lists.ChangeMembers(MailingListTestData.Owner, id, pilots.Take(1001).ToArray(), MailingListRole.Operator, Guid.NewGuid())
        );
        lists.Leave(pilots[0], id, Guid.NewGuid());
        lists.Join(MailingListTestData.Member, "Full", Guid.NewGuid());
        Assert.Equal(3000, lists.Members(MailingListTestData.Owner, id).Count);
    }

    [Fact]
    public void Version62UpgradePreservesPersonalMailAndAddsEmptyListState()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        long message = MailTestData.Store(db).Send(MailingListTestData.Owner, MailTestData.Draft, Guid.NewGuid());
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "DROP INDEX IX_WorldMail_ListId; ALTER TABLE WorldMail DROP COLUMN ListId; "
                    + "DROP TABLE WorldMailingListChange; DROP TABLE WorldMailingListAccess; "
                    + "DROP TABLE WorldMailingListMember; DROP TABLE WorldMailingList; PRAGMA user_version=62"
            );
        }
        WorldInitialization.Initialize(new WorldDatabase(db.Path), clock);
        Assert.Empty(MailingListTestData.Store(db).Joined(MailingListTestData.Owner));
        var header = Assert.Single(MailTestData.Store(db).Sync(MailingListTestData.Member, null, 0).New);
        Assert.Equal(message, header.Id);
        Assert.Null(header.ListId);
        Assert.Equal(MailTestData.Draft.Body, MailTestData.Store(db).Body(MailingListTestData.Member, message, false, Guid.NewGuid()));
    }
}
