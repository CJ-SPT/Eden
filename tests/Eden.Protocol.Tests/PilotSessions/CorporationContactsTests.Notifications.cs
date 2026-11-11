using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Corporations;
using Eden.Protocol.Serialization;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class CorporationContactsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrimedMembersReceiveNativeStandingLabelAndDeletionUpdatesWithoutUnchangedPolling(bool liveWorld)
    {
        using TestWorld db = new(liveWorld: liveWorld);
        int corporation = CorporationAdministrationTests.CreateAndJoin(db);
        using CorporationTestClient ceo = new(db, 1000001);
        using CorporationTestClient member = new(db, 1000002);
        Read(member);
        var monitor = member.Services.GetRequiredService<CorporationContactsMonitor>();
        AssertIdle();
        var store = db.Services.GetRequiredService<ICorporationContacts>();
        store.SetStanding(Ceo, [Navy], 2.5);
        monitor.Advance(member.Context);
        var added = Assert.IsType<OrganizationContactsUpdated>(Assert.Single(member.Context.Notifications));
        var payload = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(added.Arguments)), 2);
        Assert.Equal((long)corporation, payload[0]);
        Assert.Equal(new object?[] { Navy, 2.5, null }, Assert.Single(Assert.IsType<List<object?>>(payload[1])));
        member.Context.Notifications.Clear();
        long label = store.CreateLabel(Ceo, "Friends", 7);
        store.SetStanding(Ceo, [Navy], 5);
        store.SetLabels(Ceo, [Navy], label, true);
        monitor.Advance(member.Context);
        var changed = Assert.IsType<OrganizationContactsUpdated>(Assert.Single(member.Context.Notifications));
        Assert.Equal(2, changed.Updates.Count);
        Assert.Equal(new OrganizationContactUpdate(Navy, 5, null), changed.Updates[0]);
        Assert.Equal(new OrganizationContactUpdate(Navy, null, label), changed.Updates[1]);
        member.Context.Notifications.Clear();
        AssertIdle();
        store.DeleteLabel(Ceo, label);
        monitor.Advance(member.Context);
        Assert.Equal(
            new OrganizationContactUpdate(Navy, null, 0),
            Assert.Single(Assert.IsType<OrganizationContactsUpdated>(Assert.Single(member.Context.Notifications)).Updates)
        );
        member.Context.Notifications.Clear();
        store.Delete(Ceo, [Navy]);
        monitor.Advance(member.Context);
        Assert.Equal(
            new OrganizationContactUpdate(Navy, null, null),
            Assert.Single(Assert.IsType<OrganizationContactsUpdated>(Assert.Single(member.Context.Notifications)).Updates)
        );
        member.Context.Notifications.Clear();
        AssertIdle();

        void AssertIdle()
        {
            using var measured = SessionPerformance.Begin("warm corporation contacts", _ => { }, force: true)!;
            for (int i = 0; i < 10; i++)
            {
                monitor.Advance(member.Context);
            }
            Assert.Equal(0, measured.Commands);
            Assert.Equal(0, measured.Connections);
            Assert.Equal(0, measured.FittingCalculations);
            Assert.Empty(member.Context.Notifications);
        }
    }

    [Fact]
    public void ChangedSavedMembershipPreventsNotificationsAndWritesForTheFormerCorporation()
    {
        using TestWorld db = new();
        int corporation = CorporationAdministrationTests.CreateAndJoin(db);
        SetRoles(db, (long)CorporationRole.Diplomat);
        using CorporationTestClient member = new(db, 1000002);
        Read(member);
        using (WorldDbContext world = new(db.Path))
        {
            world.Pilots.Find(Member)!.CorporationId = 1000044;
            world.SaveChanges();
        }
        var store = db.Services.GetRequiredService<ICorporationContacts>();
        store.SetStanding(Ceo, [Navy], 10);
        member.Services.GetRequiredService<CorporationContactsMonitor>().Advance(member.Context);
        Assert.Empty(member.Context.Notifications);
        Assert.Empty(Read(member));
        Assert.Throws<InvalidDataException>(() => store.SetStanding(Member, [Navy], -10));
        Assert.Equal(corporation, store.Read(Ceo).CorporationId);
        Assert.Equal(10, Assert.Single(store.Read(Ceo).Contacts).Standing);
    }
}
