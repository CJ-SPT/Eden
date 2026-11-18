using Eden.Data.World.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Propulsion;
using Eden.Protocol.Flight;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class GangFrigateTests
{
    private const int Away = 30000142;
    private const long Human = 90000002;

    [Fact]
    public async Task SnapshotAndSpecialistGangCoexistAndKeepTheirOriginalHullsOnReopen()
    {
        using var fixture = new RoamingGangTests.Fixture(regional: true, gangFrigates: true);
        var gang = fixture.Form();
        var expected = Enum.GetValues<GangFrigate>().Select(hull => (int)hull).Order();
        int[] Hulls() => gang.Members.Select(member => fixture.Live.Economy.Peek<Ship>(member.ShipId)!.TypeId).Order().ToArray();
        Assert.Equal(expected, Hulls());
        Assert.NotEmpty(fixture.Database.Traffic.Profiles);
        var identities = fixture.Live.Economy.Read(world => world.Set<SavedTrafficIdentity>().ToArray());
        Assert.NotEmpty(identities);
        Assert.True(identities.Length < fixture.Database.Traffic.Profiles.Count);
        Assert.DoesNotContain(identities, identity => gang.Members.Any(member => member.PilotId == identity.PilotId));
        foreach (var member in gang.Members)
        {
            var fit = fixture.Host.GetRequiredService<CapsuleerDefense>().Profile(member.PilotId);
            Assert.True(fit is { Combat: true, HasDamage: true, TackleRange: > 0 });
            Assert.All(fit.Modules, module => Assert.True(module.Item.Online));
        }
        await fixture.Live.FlushAsync();
        fixture.Restart();
        _ = fixture.Scheduler.Jobs();
        Assert.Equal(expected, Hulls());
        Assert.Equal(gang.Members, fixture.Live.HaulerCheckpoints[gang.Id].Work.Gang!.Members);
    }

    [Theory]
    [InlineData(GangFrigate.Stiletto, false)]
    [InlineData(GangFrigate.Stiletto, true)]
    [InlineData(GangFrigate.Crow, false)]
    [InlineData(GangFrigate.Crow, true)]
    [InlineData(GangFrigate.Wolf, false)]
    [InlineData(GangFrigate.Wolf, true)]
    [InlineData(GangFrigate.Harpy, false)]
    [InlineData(GangFrigate.Harpy, true)]
    public void FittedGangMemberLocksPointsAndDamagesAPlayerOrRival(GangFrigate hull, bool rival)
    {
        using var fixture = new RoamingGangTests.Fixture(regional: true, gangFrigates: true);
        var gang = fixture.Form();
        var member = Assert.Single(gang.Members, ship => fixture.Live.Economy.Peek<Ship>(ship.ShipId)!.TypeId == (int)hull);
        var ships = fixture.Host.GetRequiredService<SqliteWorld>();
        DockedPilot attacker;
        DockedPilot victim;
        using (var catalog = new CatalogDbContext(fixture.Database.CatalogPath))
        using (var action = fixture.Live.Economy.Begin())
        {
            long victimId = rival
                ? action
                    .Set<SavedCapsuleerAgent>()
                    .Where(agent => agent.CommunitySystemId == Away)
                    .AsEnumerable()
                    .First(agent => action.Set<SavedCapsuleerSuspension>().Find(agent.PilotId) is not null)
                    .PilotId
                : Human;
            var target = action.Pilots.Find(victimId)!;
            target.ActiveShip.TypeId = 603;
            if (rival)
            {
                action.Remove(action.Set<SavedCapsuleerSuspension>().Find(victimId)!);
            }
            var anchor = SystemObjects.Geometry(catalog, Away).First(body => body.DestinationGateId is not null).Position;
            SqliteWorld.SetPosition(action, catalog, member.ShipId, anchor, Away, fixture.Clock.Now);
            SqliteWorld.SetPosition(action, catalog, target.ActiveShipId, anchor + new SpacePosition(1_000, 0, 0), Away, fixture.Clock.Now);
            attacker = ships.FindPilot(action, action.Pilots.Find(member.PilotId)!.AccountId, member.PilotId)!;
            victim = ships.FindPilot(action, target.AccountId, victimId)!;
            action.Commit();
        }
        if (rival)
        {
            fixture.Scheduler.PopulationPrepared(Away);
            fixture.Live.HaulerCheckpoints[victim.PilotId] = new(
                victim.PilotId,
                new(HaulerActivity.Waiting, 0, 0, victim.ShipId, gang.DestinationStationId, 0, new NavigationState().Capture())
            );
        }
        else
        {
            fixture.Host.GetRequiredService<SharedSpace>().Publish(new(PilotContact.From(victim), SpacePosition.Zero, null, 0, null));
        }
        var defense = fixture.Host.GetRequiredService<CapsuleerDefense>();
        var tackle = fixture.Host.GetRequiredService<IPilotTackle>();
        var health = fixture.Host.GetRequiredService<IPilotHealth>();
        var contact = Assert.Single(defense.Observe(attacker, 0), sight => sight.ShipId == victim.ShipId);
        Assert.Equal(rival ? gang.Regional!.OpponentGroupId : null, contact.RegionalGroupId);
        Assert.True(RegionalConflictStore.CanTarget(fixture.Database, gang.Regional!, contact, 0));
        double Remaining()
        {
            var status = health.Read(victim.PilotId, fixture.Clock.Now).Remaining;
            return status.Shield + status.Armor + status.Structure;
        }
        var guns = defense.Profile(member.PilotId).RattingWeapons.Select(weapon => weapon.Weapon.ItemId).ToArray();
        Assert.NotEmpty(guns);
        long Loaded() =>
            fixture.Live.Economy.Read(world =>
                world.Set<OwnedItem>().Where(item => Enumerable.Contains(guns, item.LocationId)).Sum(item => (long)item.Quantity)
            );
        double before = Remaining();
        long loaded = Loaded();
        var course = new NavigationState();
        CapsuleerDefenseState? state = null;
        bool pointed = false;
        for (int turn = 0; turn < 45 && (!pointed || Remaining() >= before || Loaded() >= loaded); turn++)
        {
            fixture.Clock.Seconds++;
            var target = Assert.IsType<CapsuleerAttacker>(
                defense.PatrolTarget(attacker, victim.ShipId, attacker.Position!, fixture.Clock.Now)
            );
            var result = defense.Advance(
                attacker,
                course,
                state,
                fixture.Clock.Now,
                turn,
                [],
                proactive: target,
                pvpRole: member.Role,
                gang: gang
            );
            attacker = result.Pilot;
            state = result.State;
            pointed |= tackle.Incoming(victim.ShipId).Any(effect => effect.SourceShipId == member.ShipId && effect.Module.Strength > 0);
        }
        Assert.True(pointed);
        Assert.True(Loaded() < loaded);
        Assert.True(Remaining() < before);
    }
}
