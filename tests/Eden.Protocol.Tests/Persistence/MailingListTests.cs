using Eden.Gameplay.Characters;
using Eden.Gameplay.Mail;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class MailingListTestData
{
    internal const long Owner = CalendarTestData.Owner;
    internal const long Member = CalendarTestData.Guest;
    internal static readonly MailingListDefaults Open = new(MailingListAccess.Allowed, MailingListRole.Member);

    internal static IPilotMailingLists Store(TestWorld db) => db.Services.GetRequiredService<IPilotMailingLists>();

    internal static MailDraft Broadcast(long list) => MailTestData.Draft with { Recipients = [], ListId = list };

    internal static void Corporation(TestWorld db)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Add(
            new SourceRecord
            {
                Dataset = "client/npccorporations",
                Key = "1000044",
                Data = "{}",
            }
        );
        catalog.SaveChanges();
    }
}

public sealed class MailingListTests
{
    [Fact]
    public void CreationJoiningRolesAndNamesPersistAndDeletedNamesAreTombstoned()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var lists = MailingListTestData.Store(db);
        long id = lists.Create(MailingListTestData.Owner, "Fleet  Mail", MailingListTestData.Open, Guid.NewGuid());
        Assert.Equal(MailingListRole.Owner, Assert.Single(lists.Joined(MailingListTestData.Owner)).Role);
        Assert.Empty(lists.Joined(MailingListTestData.Member));
        var joined = lists.Join(MailingListTestData.Member, "FLEETMAIL", Guid.NewGuid());
        Assert.Equal(id, joined.Id);
        Assert.Equal("Fleet Mail", joined.Name.Display);
        Assert.Equal(MailingListRole.Member, joined.Role);
        Assert.Equal(joined, lists.Join(MailingListTestData.Member, "Fleet Mail", Guid.NewGuid()));
        Assert.Throws<InvalidDataException>(() =>
            lists.Create(MailingListTestData.Member, "fleetmail", MailingListTestData.Open, Guid.NewGuid())
        );
        Assert.Throws<InvalidDataException>(() => lists.Leave(MailingListTestData.Owner, id, Guid.NewGuid()));
        Assert.Throws<InvalidDataException>(() => lists.Delete(MailingListTestData.Member, id, Guid.NewGuid()));
        WorldInitialization.Initialize(new WorldDatabase(db.Path), clock);
        Assert.Equal(joined, Assert.Single(lists.Joined(MailingListTestData.Member)));
        lists.Delete(MailingListTestData.Owner, id, Guid.NewGuid());
        Assert.Empty(lists.Joined(MailingListTestData.Member));
        Assert.Equal("Fleet Mail", lists.Info(MailingListTestData.Member, id)!.Name.Display);
        long replacement = lists.Create(MailingListTestData.Member, "FleetMail", MailingListTestData.Open, Guid.NewGuid());
        Assert.NotEqual(id, replacement);
        Assert.Throws<InvalidDataException>(() => lists.Join(MailingListTestData.Owner, "missing", Guid.NewGuid()));
    }

    [Fact]
    public void PrivateEnrollmentCharacterAndCorporationRulesRespectPrecedenceAndKickBlocksRejoin()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        MailingListTestData.Corporation(db);
        var lists = MailingListTestData.Store(db);
        long id = lists.Create(
            MailingListTestData.Owner,
            "Private",
            new(MailingListAccess.Blocked, MailingListRole.Member),
            Guid.NewGuid()
        );
        Assert.Throws<InvalidDataException>(() => lists.Join(MailingListTestData.Member, "Private", Guid.NewGuid()));
        lists.SetAccess(
            MailingListTestData.Owner,
            id,
            new Dictionary<long, MailingListAccess> { [1000044] = MailingListAccess.Allowed },
            Guid.NewGuid()
        );
        lists.Join(MailingListTestData.Member, "Private", Guid.NewGuid());
        lists.ChangeMembers(MailingListTestData.Owner, id, [MailingListTestData.Member], null, Guid.NewGuid());
        Assert.Empty(lists.Joined(MailingListTestData.Member));
        Assert.Throws<InvalidDataException>(() => lists.Join(MailingListTestData.Member, "Private", Guid.NewGuid()));
        lists.ClearAccess(MailingListTestData.Owner, id, MailingListTestData.Member);
        lists.Join(MailingListTestData.Member, "Private", Guid.NewGuid());
        lists.SetAccess(
            MailingListTestData.Owner,
            id,
            new Dictionary<long, MailingListAccess>
            {
                [1000044] = MailingListAccess.Blocked,
                [MailingListTestData.Member] = MailingListAccess.Allowed,
            },
            Guid.NewGuid()
        );
        Assert.Single(lists.Joined(MailingListTestData.Member));
        Assert.Single(lists.Joined(MailingListTestData.Owner));
        Assert.Throws<InvalidDataException>(() =>
            lists.SetAccess(
                MailingListTestData.Owner,
                id,
                new Dictionary<long, MailingListAccess> { [MailingListTestData.Owner] = MailingListAccess.Blocked },
                Guid.NewGuid()
            )
        );
        lists.SetAccess(
            MailingListTestData.Owner,
            id,
            new Dictionary<long, MailingListAccess> { [MailingListTestData.Member] = MailingListAccess.Blocked },
            Guid.NewGuid()
        );
        Assert.Empty(lists.Joined(MailingListTestData.Member));
    }

    [Fact]
    public void ManagerChecksOwnerProtectionAndBulkValidationPreventPartialPrivilegeChanges()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        long other = CalendarTestData.AddPilots(db, 1)[0];
        var lists = MailingListTestData.Store(db);
        long id = lists.Create(MailingListTestData.Owner, "Roles", MailingListTestData.Open, Guid.NewGuid());
        lists.Join(MailingListTestData.Member, "Roles", Guid.NewGuid());
        lists.Join(other, "Roles", Guid.NewGuid());
        Assert.Throws<InvalidDataException>(() => lists.Members(MailingListTestData.Member, id));
        Assert.Throws<InvalidDataException>(() => lists.Settings(MailingListTestData.Member, id));
        Assert.Throws<InvalidDataException>(() => lists.SetDefaults(MailingListTestData.Member, id, MailingListTestData.Open));
        Assert.Throws<InvalidDataException>(() =>
            lists.ChangeMembers(MailingListTestData.Owner, id, [MailingListTestData.Member, 123], MailingListRole.Operator, Guid.NewGuid())
        );
        Assert.Equal(MailingListRole.Member, lists.Members(MailingListTestData.Owner, id)[MailingListTestData.Member]);
        lists.ChangeMembers(MailingListTestData.Owner, id, [MailingListTestData.Member], MailingListRole.Operator, Guid.NewGuid());
        lists.ChangeMembers(MailingListTestData.Member, id, [other], MailingListRole.Muted, Guid.NewGuid());
        Assert.Equal(MailingListRole.Muted, lists.Members(MailingListTestData.Member, id)[other]);
        Assert.Throws<InvalidDataException>(() =>
            lists.ChangeMembers(MailingListTestData.Member, id, [MailingListTestData.Owner], MailingListRole.Member, Guid.NewGuid())
        );
        Assert.Throws<InvalidDataException>(() => lists.Delete(MailingListTestData.Member, id, Guid.NewGuid()));
        lists.ChangeMembers(MailingListTestData.Member, id, [MailingListTestData.Member], MailingListRole.Member, Guid.NewGuid());
        Assert.Throws<InvalidDataException>(() => lists.Members(MailingListTestData.Member, id));
    }

    [Fact]
    public void BroadcastsSnapshotSubscribersWithoutLeakingTheirIdentitiesAndRespectMuteAndPersonalBlocks()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        long other = CalendarTestData.AddPilots(db, 1)[0];
        var lists = MailingListTestData.Store(db);
        var mail = MailTestData.Store(db);
        long id = lists.Create(MailingListTestData.Owner, "Broadcast", MailingListTestData.Open, Guid.NewGuid());
        lists.Join(MailingListTestData.Member, "Broadcast", Guid.NewGuid());
        lists.Join(other, "Broadcast", Guid.NewGuid());
        db.Services.GetRequiredService<IPilotContacts>().SetBlocked(other, [MailingListTestData.Owner], true);
        long message = mail.Send(MailingListTestData.Owner, MailingListTestData.Broadcast(id), Guid.NewGuid());
        var recipient = Assert.Single(mail.Sync(MailingListTestData.Member, null, 0).New);
        Assert.Equal(id, recipient.ListId);
        Assert.Empty(recipient.Recipients);
        Assert.Empty(mail.Sync(other, null, 0).New);
        Assert.Equal(0, Assert.Single(mail.Sync(MailingListTestData.Member, null, 0).Status).Labels);
        Assert.Equal(2, Assert.Single(mail.Sync(MailingListTestData.Owner, null, 0).Status).Labels);
        lists.Leave(MailingListTestData.Member, id, Guid.NewGuid());
        Assert.Equal(message, Assert.Single(mail.Sync(MailingListTestData.Member, null, 0).New).Id);
        mail.Send(MailingListTestData.Owner, MailingListTestData.Broadcast(id), Guid.NewGuid());
        Assert.Single(mail.Sync(MailingListTestData.Member, null, 0).New);
        lists.Join(MailingListTestData.Member, "Broadcast", Guid.NewGuid());
        lists.ChangeMembers(MailingListTestData.Owner, id, [MailingListTestData.Member], MailingListRole.Muted, Guid.NewGuid());
        Assert.Throws<InvalidDataException>(() => mail.Send(MailingListTestData.Member, MailingListTestData.Broadcast(id), Guid.NewGuid()));
        mail.Send(MailingListTestData.Owner, MailingListTestData.Broadcast(id), Guid.NewGuid());
        Assert.Equal(2, mail.Sync(MailingListTestData.Member, null, 0).New.Count);
    }

    [Fact]
    public void DirectAndListAudienceOverlapDeliversOnceAndListSelectorsRemainPrivateAfterLeaving()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var lists = MailingListTestData.Store(db);
        var mail = MailTestData.Store(db);
        long id = lists.Create(MailingListTestData.Owner, "Mixed", MailingListTestData.Open, Guid.NewGuid());
        lists.Join(MailingListTestData.Member, "Mixed", Guid.NewGuid());
        mail.Send(
            MailingListTestData.Owner,
            MailingListTestData.Broadcast(id) with
            {
                Recipients = [MailingListTestData.Member],
            },
            Guid.NewGuid()
        );
        mail.Send(MailingListTestData.Owner, MailTestData.Draft, Guid.NewGuid());
        lists.Leave(MailingListTestData.Member, id, Guid.NewGuid());
        mail.Change(MailingListTestData.Member, new MailSelection.List(id), MailOperation.Read, 0, Guid.NewGuid());
        var states = mail.Sync(MailingListTestData.Member, null, 0).Status;
        Assert.Equal(2, states.Count);
        Assert.Equal(MailStatus.Read, states[0].Status);
        Assert.Equal(1, states[0].Labels);
        Assert.Equal(MailStatus.None, states[1].Status);
        mail.Change(MailingListTestData.Member, new MailSelection.List(id), MailOperation.Trash, 0, Guid.NewGuid());
        Assert.Equal(MailStatus.None, mail.Sync(MailingListTestData.Owner, null, 0).Status[0].Status);
    }

    [Fact]
    public void ClosingEnrollmentAndChangingNewMemberDefaultDoNotEvictOrDemoteExistingSubscribers()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock);
        var lists = MailingListTestData.Store(db);
        long id = lists.Create(MailingListTestData.Owner, "Defaults", MailingListTestData.Open, Guid.NewGuid());
        lists.Join(MailingListTestData.Member, "Defaults", Guid.NewGuid());
        lists.SetDefaults(MailingListTestData.Owner, id, new(MailingListAccess.Blocked, MailingListRole.Muted));
        Assert.Equal(MailingListRole.Member, Assert.Single(lists.Joined(MailingListTestData.Member)).Role);
        MailTestData.Store(db).Send(MailingListTestData.Member, MailingListTestData.Broadcast(id), Guid.NewGuid());
        lists.Leave(MailingListTestData.Member, id, Guid.NewGuid());
        Assert.Throws<InvalidDataException>(() => lists.Join(MailingListTestData.Member, "Defaults", Guid.NewGuid()));
    }
}
