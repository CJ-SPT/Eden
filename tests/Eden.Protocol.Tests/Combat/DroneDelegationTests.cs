using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class DroneDelegationTests
{
    private const long Member = 90000002;
    private const long MemberShip = 1000000000002;
    private const int SystemId = 30000144;

    private sealed class Fixture : IDisposable
    {
        internal CombatDroneTests.Fixture Base { get; } = new();
        internal ICooperativeFleets Fleets => Base.Base.Db.Services.GetRequiredService<ICooperativeFleets>();
        internal SharedSpace Space => Base.Base.Db.Services.GetRequiredService<SharedSpace>();
        internal WorldDatabase Database => Base.Base.Db.Services.GetRequiredService<WorldDatabase>();
        internal long[] Ids { get; }
        internal long FleetId { get; }
        internal long Gun { get; }
        internal LiveWorld? Live { get; }

        internal Fixture(bool resident, int droneType = 2488, int quantity = 5)
        {
            using (CatalogDbContext catalog = new(Base.Base.CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    UPDATE TypeAttribute SET Value=1000000 WHERE AttributeId=263;
                    INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000144','{{"securityStatus":0}}');
                    """
                );
            }
            Gun = Base.Base.Fit(3638);
            using (WorldDbContext world = new(Base.Base.Db.Path))
            {
                var module = world.Set<OwnedItem>().Find(Gun)!;
                module.OwnerId = Member;
                module.LocationId = MemberShip;
                var droneSkills = Enum.GetValues<DroneSkill>().Select(s => (int)s).ToHashSet();
                foreach (var skill in world.Set<PilotSkill>().Where(s => s.PilotId == Base.Pilot).ToArray())
                {
                    if (!droneSkills.Contains(skill.TypeId))
                    {
                        var saved =
                            world.Set<PilotSkill>().Find(Member, skill.TypeId)
                            ?? world.Add(new PilotSkill { PilotId = Member, TypeId = skill.TypeId }).Entity;
                        saved.Points = skill.Points;
                    }
                }
                world.Set<WorldNpc>().Find(Base.Npc.Id)!.ShieldCharge = 1000000;
                world.SaveChanges();
            }
            Ids = Base.Launch(droneType, quantity);
            Base.Base.Db.Store.SetPosition(MemberShip, Base.Npc.Position + new SpacePosition(1500, 0, 0), SystemId);
            foreach (long account in new long[] { 1000001, 1000002 })
            {
                var pilot = Base.Base.Db.Store.FindPilot(account)!;
                Fleets.Update(
                    Guid.NewGuid(),
                    new(pilot.PilotId, pilot.CorpId, pilot.ShipId, pilot.ShipTypeId, pilot.SystemId, pilot.Position)
                );
                Space.Publish(new(PilotContact.From(pilot), SpacePosition.Zero, null, 0, null));
            }
            FleetId = Fleets.Create(Base.Pilot).Id;
            Join(Member);
            Base.Drones.ChangeSettings(Base.Pilot, false, false);
            Live = resident ? new(Database, Base.Base.Clock, _ => { }) : null;
        }

        internal void Join(long pilot)
        {
            Fleets.Invite(Base.Pilot, FleetId, pilot, new(FleetRules.NoGroup, FleetRules.NoGroup, FleetRole.Member));
            Fleets.Accept(pilot, FleetId);
        }

        internal void Assign(DroneDelegationMode mode) => Base.Drones.Delegate(Base.Pilot, Ids, mode, Member, Base.Now);

        internal void Fire() =>
            Base.Base.Db.Services.GetRequiredService<IPilotCombat>().Fire(Member, Gun, Base.Npc.Id, SpacePosition.Zero, Base.Now, .01);

        internal void PublishMember()
        {
            var pilot = Base.Base.Db.Store.FindPilot(1000002)!;
            Space.Publish(new(PilotContact.From(pilot), SpacePosition.Zero, null, 0, null));
        }

        internal DroneContact[] Contacts() => Base.Drones.List(SystemId, Base.Now).ToArray();

        public void Dispose()
        {
            Live?.Dispose();
            Base.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssistWaitsForANewPaidMemberActionAndUsesTheLaunchingOwnersProfiles(bool resident)
    {
        using Fixture fixture = new(resident);
        var profiles = fixture.Contacts().Select(d => d.Profile).ToArray();
        fixture.Fire();
        fixture.Assign(DroneDelegationMode.Assist);
        Assert.Empty(fixture.Base.Tick().Hits);
        fixture.Base.Base.Clock.Seconds += 2;
        fixture.Fire();
        var update = fixture.Base.Tick();
        Assert.Equal(5, update.Hits.Count);
        Assert.All(update.Hits, hit => Assert.Equal(fixture.Base.Npc.Id, hit.TargetId));
        var contacts = fixture.Contacts();
        Assert.Equal(profiles, contacts.Select(d => d.Profile));
        Assert.All(
            contacts,
            d =>
            {
                Assert.Equal(fixture.Base.Pilot, d.PilotId);
                Assert.Equal(fixture.Base.Ship, d.ShipId);
                Assert.Equal(Member, d.Delegation!.PilotId);
                Assert.Equal(2, d.Delegation.ReactionRevision);
            }
        );
        Assert.Empty(fixture.Base.Drones.Advance(fixture.Base.Pilot, fixture.Base.Now, 1).Hits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuardRespondsToTheAttackOnItsMemberRatherThanTheMembersOffensiveModule(bool resident)
    {
        using Fixture fixture = new(resident);
        fixture.Assign(DroneDelegationMode.Guard);
        fixture.Fire();
        Assert.Empty(fixture.Base.Tick().Hits);
        fixture.Base.Base.Clock.Seconds += 10;
        fixture.Database.MotionStamps[SystemId] = 1;
        var shot = fixture
            .Base.Base.Db.Services.GetRequiredService<INpcCombat>()
            .Fire(Member, MemberShip, fixture.Base.Npc.Id, SpacePosition.Zero, fixture.Base.Now, .01, 1);
        Assert.Equal(MemberShip, Assert.IsType<NpcShot>(shot).Health.ShipId);
        Assert.Equal(5, fixture.Base.Tick().Hits.Count);
        Assert.All(fixture.Contacts(), d => Assert.Equal(fixture.Base.Npc.Id, d.TargetId));
    }

    [Theory]
    [InlineData(false, "leave")]
    [InlineData(true, "leave")]
    [InlineData(false, "rejoin")]
    [InlineData(true, "rejoin")]
    [InlineData(false, "owner")]
    [InlineData(true, "owner")]
    [InlineData(false, "disconnect")]
    [InlineData(true, "disconnect")]
    [InlineData(false, "dock")]
    [InlineData(true, "dock")]
    [InlineData(false, "system")]
    [InlineData(true, "system")]
    [InlineData(false, "grid")]
    [InlineData(true, "grid")]
    [InlineData(false, "warp")]
    [InlineData(true, "warp")]
    [InlineData(false, "replacement")]
    [InlineData(true, "replacement")]
    [InlineData(false, "ship")]
    [InlineData(true, "ship")]
    public void InvalidMembershipAndShipPresenceCancelDelegationBeforeAnotherShot(bool resident, string change)
    {
        using Fixture fixture = new(resident);
        fixture.Assign(DroneDelegationMode.Assist);
        fixture.Fire();
        Assert.Equal(5, fixture.Base.Tick().Hits.Count);
        if (change is "leave" or "rejoin")
        {
            fixture.Fleets.Leave(Member, fixture.FleetId);
            if (change == "rejoin")
            {
                fixture.Join(Member);
            }
        }
        else if (change == "owner")
        {
            fixture.Fleets.Leave(fixture.Base.Pilot, fixture.FleetId);
        }
        else if (change == "disconnect")
        {
            fixture.Space.Leave(MemberShip);
        }
        else if (change == "replacement")
        {
            var pilot = fixture.Base.Base.Db.Store.FindPilot(1000002)!;
            fixture.Fleets.Update(
                Guid.NewGuid(),
                new(pilot.PilotId, pilot.CorpId, pilot.ShipId, pilot.ShipTypeId, pilot.SystemId, pilot.Position)
            );
        }
        else if (change == "warp")
        {
            var pilot = fixture.Base.Base.Db.Store.FindPilot(1000002)!;
            fixture.Space.Publish(
                new(
                    PilotContact.From(pilot),
                    SpacePosition.Zero,
                    null,
                    0,
                    null,
                    new WarpCourse(pilot.Position!, pilot.Position! + new SpacePosition(1000000000, 0, 0), 1, pilot.Flight, 100)
                )
            );
        }
        else if (change == "ship")
        {
            const long replacement = 7300000000000;
            using WorldDbContext world = new(fixture.Base.Base.Db.Path);
            world.Database.ExecuteSql(
                $"INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name) VALUES({replacement},{Member},601,60003760,{"Replacement"})"
            );
            world.Pilots.Find(Member)!.ActiveShipId = replacement;
            world.SaveChanges();
            fixture.Base.Base.Db.Store.SetPosition(replacement, fixture.Base.Npc.Position, SystemId);
            fixture.PublishMember();
        }
        else if (change == "dock")
        {
            fixture.Base.Base.Db.Store.SetPosition(MemberShip, null);
            fixture.Space.Leave(MemberShip);
        }
        else
        {
            fixture.Base.Base.Db.Store.SetPosition(
                MemberShip,
                fixture.Base.Npc.Position + new SpacePosition(change == "grid" ? FleetRules.CommandRange + 2000 : 1500, 0, 0),
                change == "system" ? 30000142 : SystemId
            );
            fixture.PublishMember();
        }
        Assert.Empty(fixture.Base.Tick(10).Hits);
        Assert.All(
            fixture.Contacts(),
            d =>
            {
                Assert.Null(d.Delegation);
                Assert.Equal(fixture.Base.Ship, d.TargetId);
                Assert.Equal(DroneActivity.Idle, d.Activity);
            }
        );
    }

    [Fact]
    public async Task WarmDelegatedCombatAndObserversRemainSqlFreeAndSaveTheSameAssignment()
    {
        using Fixture fixture = new(true);
        fixture.Assign(DroneDelegationMode.Assist);
        for (int warm = 0; warm < 2; warm++)
        {
            fixture.Fire();
            Assert.Equal(5, fixture.Base.Tick(10).Hits.Count);
        }
        using (var metrics = SessionPerformance.Begin("warm assisted drones", _ => { }, force: true))
        {
            for (int frame = 0; frame < 12; frame++)
            {
                fixture.Fire();
                Assert.Equal(5, fixture.Base.Tick(10).Hits.Count);
                for (int observer = 0; observer < 10; observer++)
                {
                    Assert.All(fixture.Contacts(), d => Assert.NotNull(d.Delegation));
                }
            }
            Assert.Equal(0, metrics!.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        var expected = fixture.Contacts();
        await fixture.Live!.FlushAsync();
        fixture.Live.Dispose();
        Assert.Equal(expected, fixture.Contacts());
        WorldInitialization.Initialize(fixture.Database, fixture.Base.Base.Clock);
        Assert.All(
            fixture.Contacts(),
            d =>
            {
                Assert.Null(d.Delegation);
                Assert.Equal(0, d.ShipId);
                Assert.Equal(0, d.TargetId);
            }
        );
        Assert.Equal(expected.Select(d => d.Position), fixture.Contacts().Select(d => d.Position));
    }
}
