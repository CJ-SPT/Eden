using Eden.Gameplay.Fleets;

namespace Eden.Protocol.Tests.Fleets;

public sealed partial class FleetRulesTests
{
    private static FleetAdvertSettings AdvertSettings() =>
        new(
            "Standing fleet",
            "Recruiting",
            FleetInviteScope.Corporation,
            null,
            true,
            false,
            new(null, null, [], []),
            new(null, null, [], []),
            false,
            false,
            true,
            null
        );

    [Fact]
    public void AdvertAccessSnapshotsEnforceCorporationPublicStandingSecurityAndExclusions()
    {
        var (world, fleet) = Create(1);
        world.Update(Guid.NewGuid(), Pilot(2));
        world.Update(Guid.NewGuid(), Pilot(3) with { CorporationId = 1000001 });
        var settings = AdvertSettings();
        world.SetAdvert(1, settings, 100);
        Assert.Single(world.AvailableAdverts(2, 0));
        Assert.Empty(world.AvailableAdverts(3, 0));
        List<long> allowed = [3];
        var restricted = settings with { Scope = FleetInviteScope.Public, Public = new(5, 0.5, allowed, []) };
        var saved = world.SetAdvert(1, restricted, 200);
        allowed.Clear();
        Assert.Single(saved.Settings.Public.Allowed);
        Assert.Empty(world.AvailableAdverts(2, 5));
        Assert.Empty(world.AvailableAdverts(3, 0.49));
        Assert.Single(world.AvailableAdverts(3, 0.5));
        Assert.Equal(100, saved.CreatedAt);
        Assert.Equal(200, saved.UpdatedAt);
        Assert.Throws<InvalidDataException>(() => world.ApplyToFleet(2, fleet.Id, 5, 200));
        Assert.Single(world.Read(1, fleet.Id).Members);
        world.SetAdvert(1, restricted with { Public = restricted.Public with { Disallowed = new long[] { 3 } } }, 200);
        Assert.Empty(world.AvailableAdverts(3, 10));
        Assert.Throws<InvalidDataException>(() => world.SetAdvert(1, settings with { Name = new('x', 101) }, 200));
        Assert.Throws<InvalidDataException>(() => world.SetAdvert(1, settings with { Description = new('x', 251) }, 200));
        Assert.Throws<InvalidDataException>(() => world.SetAdvert(1, settings with { JoinLimit = 0 }, 200));
        Assert.Throws<InvalidDataException>(() => world.SetAdvert(1, settings with { Public = new(null, double.NaN, [], []) }, 200));
        Assert.Equal(restricted.Name, world.MyAdvert(1)!.Settings.Name);
    }

    [Fact]
    public void AdvertJoinLimitIsRecheckedWhenPendingInvitesAreAcceptedAndRemovalRevokesAdvertInvites()
    {
        var (world, fleet) = Create(1);
        world.Update(Guid.NewGuid(), Pilot(2));
        world.Update(Guid.NewGuid(), Pilot(3));
        world.SetAdvert(1, AdvertSettings() with { JoinLimit = 2 }, 0);
        Assert.False(world.ApplyToFleet(2, fleet.Id, 0, 0));
        Assert.False(world.ApplyToFleet(3, fleet.Id, 0, 0));
        world.Accept(2, fleet.Id, 0);
        Assert.Throws<InvalidDataException>(() => world.Accept(3, fleet.Id, 0));
        Assert.Null(world.Identity(3));
        Assert.Equal(2, world.Read(1, fleet.Id).Members.Count);
        world.Leave(2, fleet.Id);
        world.RemoveAdvert(1);
        Assert.Throws<InvalidDataException>(() => world.Accept(3, fleet.Id, 0));
        Assert.Null(world.Identity(3));
        world.SetAdvert(1, AdvertSettings(), 0);
        world.ApplyToFleet(3, fleet.Id, 1, 0);
        world.SetAdvert(1, AdvertSettings() with { MemberGroups = new(null, 0.5, [], []) }, 0);
        Assert.Throws<InvalidDataException>(() => world.Accept(3, fleet.Id, 0, 0));
        Assert.Null(world.Identity(3));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void AdvertBossChangeAndDisconnectRespectAutoUpdateAndDisbandDropsAdvert(bool update, bool leave)
    {
        var (world, fleet) = Create(2);
        var settings = AdvertSettings() with { UpdateOnBossChange = update };
        world.SetAdvert(1, settings, 0);
        if (leave)
        {
            world.Leave(1, fleet.Id);
        }
        else
        {
            world.MakeBoss(1, fleet.Id, 2);
        }
        Assert.Equal(update, world.Read(2, fleet.Id).Registered);
        if (update)
        {
            Assert.Equal(2, world.MyAdvert(2)!.Leader.Pilot.Id);
        }
        else
        {
            Assert.Null(world.MyAdvert(2));
        }
        world.Disband(2, fleet.Id);
        Assert.Null(world.MyAdvert(2));
        Assert.Empty(world.AvailableAdverts(2, 0));
    }

    [Fact]
    public void ApprovalRequestsAreBoundedDeduplicatedRejectedAndClearedAfterJoiningOrDisconnect()
    {
        var (world, fleet) = Create(1);
        Guid session = Guid.NewGuid();
        world.Update(session, Pilot(2));
        world.SetAdvert(1, AdvertSettings() with { Approval = true }, 0);
        world.Drain(1);
        Assert.True(world.ApplyToFleet(2, fleet.Id, 0, 0));
        Assert.True(world.ApplyToFleet(2, fleet.Id, 0, 0));
        Assert.Single(world.Drain(1), e => e.Event is FleetEvent.JoinRequest);
        Assert.Single(world.JoinRequests(1, fleet.Id));
        world.RejectApplication(1, fleet.Id, 2);
        Assert.Empty(world.JoinRequests(1, fleet.Id));
        world.ApplyToFleet(2, fleet.Id, 0, 0);
        world.Update(session, null);
        Assert.Empty(world.JoinRequests(1, fleet.Id));
        world.Update(session, Pilot(2));
        world.ApplyToFleet(2, fleet.Id, 0, 0);
        world.Invite(1, fleet.Id, 2, Member, 0);
        world.Accept(2, fleet.Id, 0);
        Assert.Empty(world.JoinRequests(1, fleet.Id));
        Assert.Throws<InvalidDataException>(() => world.JoinRequests(2, fleet.Id));
    }

    [Fact]
    public void ApprovalRequestLimitRejectsAdditionalApplicantsWithoutChangingExistingRequests()
    {
        var (world, fleet) = Create(1);
        world.SetAdvert(1, AdvertSettings() with { Approval = true }, 0);
        for (int index = 0; index <= FleetRules.MaximumMembers; index++)
        {
            long pilot = index + 2;
            world.Update(Guid.NewGuid(), Pilot(pilot));
            if (index < FleetRules.MaximumMembers)
            {
                Assert.True(world.ApplyToFleet(pilot, fleet.Id, 0, 0));
            }
            else
            {
                Assert.Throws<InvalidDataException>(() => world.ApplyToFleet(pilot, fleet.Id, 0, 0));
            }
        }
        Assert.Equal(FleetRules.MaximumMembers, world.JoinRequests(1, fleet.Id).Count);
        Assert.Single(world.Read(1, fleet.Id).Members);
    }
}
