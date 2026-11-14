using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;

namespace Eden.Protocol.Tests.Fleets;

public sealed partial class FleetRulesTests
{
    private static FleetPilot Pilot(long id, double distance = 0, int system = 30000142) =>
        new(id, 1000044, id + 1000, 601, system, new(distance, 0, 0));

    private static FleetPlacement Member => new(FleetRules.NoGroup, FleetRules.NoGroup, FleetRole.Member);

    private static (FleetWorld World, CooperativeFleet Fleet) Create(int count = 3)
    {
        FleetWorld world = new(100000000);
        for (long id = 1; id <= count; id++)
        {
            world.Update(Guid.NewGuid(), Pilot(id));
        }
        var fleet = world.Create(1);
        for (long id = 2; id <= count; id++)
        {
            world.Invite(1, fleet.Id, id, Member, 0);
            world.Accept(id, fleet.Id, 0);
        }
        for (long id = 1; id <= count; id++)
        {
            world.Drain(id);
        }
        return (world, world.Read(1, fleet.Id));
    }

    [Fact]
    public void MembershipVersionsChangeOnRejoinAndConnectionReplacementButNotRankOrPositionUpdates()
    {
        var (world, fleet) = Create();
        var joined = world.Membership(2)!;
        world.Move(1, fleet.Id, 2, Member);
        Assert.Equal(joined, world.Membership(2));
        Guid replacement = Guid.NewGuid();
        world.Update(replacement, Pilot(2));
        var replaced = world.Membership(2)!;
        Assert.Equal(joined.FleetId, replaced.FleetId);
        Assert.NotEqual(joined.Version, replaced.Version);
        world.Update(replacement, Pilot(2, 100));
        Assert.Equal(replaced, world.Membership(2));
        world.Leave(2, fleet.Id);
        Assert.Null(world.Membership(2));
        world.Invite(1, fleet.Id, 2, Member, 0);
        world.Accept(2, fleet.Id, 0);
        Assert.NotEqual(replaced.Version, world.Membership(2)!.Version);
        world.Update(replacement, null);
        Assert.Null(world.Membership(2));
    }

    [Fact]
    public void InvitationsRequireCurrentIssuerAuthorityAndAcceptanceIsSingleUseAndExpiresAtDeadline()
    {
        var (world, fleet) = Create(1);
        world.Update(Guid.NewGuid(), Pilot(2));
        world.Invite(1, fleet.Id, 2, Member, 0);
        Assert.False(world.CanBind(2, fleet.Id, FleetRules.InvitationLifetime.Ticks));
        Assert.Throws<InvalidDataException>(() => world.Accept(2, fleet.Id, FleetRules.InvitationLifetime.Ticks));
        Assert.Single(world.Read(1, fleet.Id).Members);
        world.Invite(1, fleet.Id, 2, Member, FleetRules.InvitationLifetime.Ticks);
        world.Accept(2, fleet.Id, FleetRules.InvitationLifetime.Ticks);
        Assert.Throws<InvalidDataException>(() => world.Accept(2, fleet.Id, FleetRules.InvitationLifetime.Ticks));
        Assert.Equal(2, world.Read(1, fleet.Id).Members.Count);
        Assert.Throws<InvalidDataException>(() => world.Invite(2, fleet.Id, 1, Member, 0));
        world.Update(Guid.NewGuid(), Pilot(3));
        world.Invite(1, fleet.Id, 3, Member, 0);
        world.MakeBoss(1, fleet.Id, 2);
        world.Move(2, fleet.Id, 1, Member);
        Assert.Throws<InvalidDataException>(() => world.Accept(3, fleet.Id, 1));
        Assert.Null(world.Identity(3));
    }

    [Fact]
    public void ForgedForeignAndOccupiedPositionsRejectWithoutChangingAnEarlierSnapshot()
    {
        var (world, fleet) = Create();
        var before = world.Read(1, fleet.Id);
        Assert.Throws<InvalidDataException>(() => world.Move(2, fleet.Id, 2, new(-1, -1, FleetRole.Commander)));
        Assert.Throws<InvalidDataException>(() => world.Move(1, fleet.Id, 2, new(-1, -1, FleetRole.Commander)));
        Assert.Throws<InvalidDataException>(() => world.Move(1, fleet.Id, 2, new(999, 998, FleetRole.Member)));
        Assert.Throws<InvalidDataException>(() => world.Move(1, fleet.Id, 2, new(-1, -1, (FleetRole)int.MaxValue)));
        Assert.Throws<InvalidDataException>(() => world.Read(999, fleet.Id));
        Assert.Throws<InvalidDataException>(() => world.Disband(2, fleet.Id));
        Assert.Throws<InvalidDataException>(() => world.Kick(2, fleet.Id, 1));
        Assert.Equal(before.Members, world.Read(1, fleet.Id).Members);
        Assert.Empty(world.Drain(1));
        world.Move(1, fleet.Id, 2, new(fleet.Wings[0].Id, fleet.Wings[0].Squads[0].Id, FleetRole.SquadCommander));
        Assert.Equal(FleetRole.Member, before.Members.Single(m => m.Pilot.Id == 2).Placement.Role);
    }

    [Fact]
    public void CommandAuthorityStaysWithinWingAndSquadAndBossTransferIsIndependentOfRank()
    {
        var (world, fleet) = Create(5);
        long wing = fleet.Wings[0].Id;
        long squad = fleet.Wings[0].Squads[0].Id;
        long otherWing = world.CreateWing(1, fleet.Id);
        long otherSquad = world.CreateSquad(1, fleet.Id, otherWing);
        world.Move(1, fleet.Id, 2, new(wing, -1, FleetRole.WingCommander));
        world.Move(1, fleet.Id, 3, new(wing, squad, FleetRole.SquadCommander));
        world.Move(1, fleet.Id, 5, new(otherWing, otherSquad, FleetRole.Member));
        Assert.Throws<InvalidDataException>(() => world.Move(2, fleet.Id, 5, new(wing, squad, FleetRole.Member)));
        Assert.Throws<InvalidDataException>(() => world.Move(3, fleet.Id, 4, new(otherWing, otherSquad, FleetRole.Member)));
        world.Kick(3, fleet.Id, 4);
        Assert.Null(world.Identity(4));
        world.MakeBoss(1, fleet.Id, 5);
        Assert.Equal(FleetRole.Member, world.Identity(5)!.Role);
        Assert.Equal(FleetRules.BossJob, world.Identity(5)!.Job);
        Assert.Equal(FleetRole.Commander, world.Identity(1)!.Role);
        Assert.Equal(0, world.Identity(1)!.Job);
        Assert.Throws<InvalidDataException>(() => world.MakeBoss(1, fleet.Id, 2));
        Assert.Throws<InvalidDataException>(() => world.Kick(1, fleet.Id, 5));
        Assert.Throws<InvalidDataException>(() => world.Move(1, fleet.Id, 5, Member));
        world.CreateWing(5, fleet.Id);
    }

    [Fact]
    public void FreeMoveCannotGrantWingAuthorityOrMoveOtherMembersAndGroupDeletionRequiresEmptyContents()
    {
        var (world, fleet) = Create();
        long wing = fleet.Wings[0].Id;
        long squad = fleet.Wings[0].Squads[0].Id;
        Assert.Throws<InvalidDataException>(() => world.Move(2, fleet.Id, 2, new(wing, squad, FleetRole.SquadCommander)));
        world.SetFreeMove(1, fleet.Id, true);
        world.Move(2, fleet.Id, 2, new(wing, squad, FleetRole.SquadCommander));
        Assert.Throws<InvalidDataException>(() => world.Move(2, fleet.Id, 2, new(wing, -1, FleetRole.WingCommander)));
        Assert.Throws<InvalidDataException>(() => world.ChangeGroup(1, fleet.Id, squad, true, null));
        Assert.Throws<InvalidDataException>(() => world.ChangeGroup(1, fleet.Id, wing, false, null));
        long spare = world.CreateSquad(1, fleet.Id, wing);
        world.ChangeGroup(1, fleet.Id, spare, true, "Industry");
        Assert.Throws<InvalidDataException>(() =>
            world.ChangeGroup(1, fleet.Id, spare, true, new string('a', FleetRules.MaximumNameLength + 1))
        );
        world.ChangeGroup(1, fleet.Id, spare, true, null);
        Assert.DoesNotContain(world.Read(1, fleet.Id).Wings[0].Squads, s => s.Id == spare);
    }

    [Theory]
    [InlineData(FleetBroadcastScope.All, 3)]
    [InlineData(FleetBroadcastScope.Down, 1)]
    [InlineData(FleetBroadcastScope.Up, 2)]
    public void BroadcastDirectionUsesTheHierarchyAndRejectsInvalidAndExcessiveTraffic(FleetBroadcastScope scope, int recipients)
    {
        var (world, fleet) = Create();
        world.Broadcast(2, fleet.Id, FleetBroadcastKind.TravelTo, scope, FleetBroadcastRange.Universe, 30000144, null, 0);
        int count = Enumerable.Range(1, 3).Sum(id => world.Drain(id).Count(d => d.Event is FleetEvent.Broadcast));
        Assert.Equal(recipients, count);
        Assert.Throws<InvalidDataException>(() =>
            world.Broadcast(
                2,
                fleet.Id,
                FleetBroadcastKind.Location,
                scope,
                FleetBroadcastRange.Universe,
                null,
                null,
                FleetRules.BroadcastInterval.Ticks - 1
            )
        );
        world.Broadcast(
            2,
            fleet.Id,
            FleetBroadcastKind.Location,
            scope,
            FleetBroadcastRange.Universe,
            null,
            null,
            FleetRules.BroadcastInterval.Ticks
        );
        Assert.Throws<InvalidDataException>(() =>
            world.Broadcast(
                2,
                fleet.Id,
                (FleetBroadcastKind)int.MaxValue,
                scope,
                FleetBroadcastRange.Universe,
                null,
                null,
                2 * FleetRules.BroadcastInterval.Ticks
            )
        );
    }

    [Theory]
    [InlineData(FleetRules.CommandRange, true)]
    [InlineData(FleetRules.CommandRange + 1, false)]
    public void GroupWarpUsesSameSystemRangeAndFreshMembershipShipAndOptOutState(double distance, bool eligible)
    {
        var (world, fleet) = Create();
        world.Update(Guid.NewGuid(), Pilot(2, distance));
        world.Update(Guid.NewGuid(), Pilot(3, 0, 30000144));
        world.Warp(1, fleet.Id, 42, 0, null, 0);
        Assert.Empty(world.Drain(3));
        var orders = world.Drain(2).Where(d => d.Event is FleetEvent.Warp).ToArray();
        Assert.Equal(eligible ? 1 : 0, orders.Length);
        if (!eligible)
        {
            return;
        }
        var order = Assert.IsType<FleetEvent.Warp>(orders[0].Event).Order;
        Assert.True(world.CanExecuteWarp(2, order, 0));
        Assert.False(world.CanExecuteWarp(2, order, order.ExpiresAt));
        world.SetTakesWarp(2, fleet.Id, false);
        Assert.False(world.CanExecuteWarp(2, order, 0));
        world.SetTakesWarp(2, fleet.Id, true);
        world.Update(Guid.NewGuid(), Pilot(2, distance) with { ShipId = 999 });
        Assert.False(world.CanExecuteWarp(2, order, 0));
        world.Update(Guid.NewGuid(), Pilot(2, distance));
        world.Move(1, fleet.Id, 1, Member);
        Assert.False(world.CanExecuteWarp(2, order, 0));
        Assert.Throws<InvalidDataException>(() => world.Warp(1, fleet.Id, 42, 0, null, 0));
        world.Leave(2, fleet.Id);
        Assert.False(world.CanExecuteWarp(2, order, 0));
    }

    [Fact]
    public void OfflineAndDepartedLeadersTransferBossDeterministicallyAndLastDepartureDestroysTheFleet()
    {
        FleetWorld world = new(1000);
        Guid first = Guid.NewGuid(),
            second = Guid.NewGuid();
        world.Update(first, Pilot(1));
        world.Update(second, Pilot(2));
        long fleet = world.Create(1).Id;
        world.Invite(1, fleet, 2, Member, 0);
        world.Accept(2, fleet, 0);
        world.Update(first, null);
        Assert.Null(world.Identity(1));
        Assert.Equal(FleetRules.BossJob, world.Identity(2)!.Job);
        var succession = Assert.Single(world.Drain(2), d => d.Event is FleetEvent.Changed);
        Assert.True(Assert.IsType<FleetEvent.Changed>(succession.Event).OnlyMember);
        world.Update(second, null);
        Assert.False(world.CanBind(2, fleet, 0));
        Assert.Throws<InvalidDataException>(() => world.Read(2, fleet));
        world.Update(first, Pilot(1));
        Assert.True(world.Create(1).Id > fleet);
    }

    [Fact]
    public void FleetAndHierarchyLimitsAreEnforcedAndEventBacklogsStayBounded()
    {
        var (world, fleet) = Create(FleetRules.MaximumMembers);
        world.Update(Guid.NewGuid(), Pilot(FleetRules.MaximumMembers + 1));
        Assert.Throws<InvalidDataException>(() => world.Invite(1, fleet.Id, FleetRules.MaximumMembers + 1, Member, 0));
        for (int index = 1; index < FleetRules.MaximumWings; index++)
        {
            world.CreateWing(1, fleet.Id);
        }
        Assert.Throws<InvalidDataException>(() => world.CreateWing(1, fleet.Id));
        for (int index = 1; index < FleetRules.MaximumSquads; index++)
        {
            world.CreateSquad(1, fleet.Id, fleet.Wings[0].Id);
        }
        Assert.Throws<InvalidDataException>(() => world.CreateSquad(1, fleet.Id, fleet.Wings[0].Id));
        for (int index = 0; index < 2 * FleetRules.MaximumPendingEvents; index++)
        {
            world.SetFreeMove(1, fleet.Id, index % 2 == 0);
        }
        var deliveries = world.Drain(2);
        Assert.True(deliveries.Count <= FleetRules.MaximumPendingEvents);
        var resync = Assert.Single(deliveries, d => d.Event is FleetEvent.Resync);
        Assert.Equal(2, Assert.IsType<FleetEvent.Resync>(resync.Event).Member!.Pilot.Id);
        Assert.All(deliveries, d => Assert.Equal(fleet.Id, d.FleetId));
    }

    [Fact]
    public void IssuedWarpRetainsItsOriginWhenTheCommanderStartsMovingAndNewerManualControlsInvalidateIt()
    {
        var (world, fleet) = Create();
        world.Warp(1, fleet.Id, 42, 0, null, 0);
        var order = Assert.IsType<FleetEvent.Warp>(Assert.Single(world.Drain(2)).Event).Order;
        world.Update(Guid.NewGuid(), Pilot(1, FleetRules.CommandRange * 2));
        Assert.True(world.CanExecuteWarp(2, order, 0));
        world.CancelWarpOrders(2);
        Assert.False(world.CanExecuteWarp(2, order, 0));
        world.Update(Guid.NewGuid(), Pilot(1));
        world.Warp(1, fleet.Id, 42, 0, null, 0);
        var current = Assert.IsType<FleetEvent.Warp>(Assert.Single(world.Drain(2)).Event).Order;
        Assert.True(world.CanExecuteWarp(2, current, 0));
        Assert.Throws<InvalidDataException>(() => world.Warp(1, fleet.Id, 42, double.NaN, null, 0));
        Assert.Throws<InvalidDataException>(() => world.Warp(1, fleet.Id, 42, FleetRules.MaximumWarpRange + 1, null, 0));
        Assert.Throws<InvalidDataException>(() => world.Warp(1, fleet.Id, 42, 0, new(double.NaN, 0, 0), 0));
    }
}
