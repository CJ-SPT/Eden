using Eden.Data.World.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.Protocol.Flight;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class PatrolHullTests
{
    private const int Away = 30000142;
    private const long Human = 90000002;

    private static readonly Dictionary<int, int[]> s_patrolHulls = new()
    {
        [RoamingGangTests.Fixture.FrigateSeed] = Enum.GetValues<GangFrigate>().Select(hull => (int)hull).ToArray(),
        [RoamingGangTests.Fixture.DestroyerSeed] =
        [
            CapsuleerPatrolFit.Cormorant,
            CapsuleerPatrolFit.Catalyst,
            CapsuleerPatrolFit.Thrasher,
            CapsuleerPatrolFit.Corax,
        ],
        [RoamingGangTests.Fixture.CruiserSeed] =
        [
            CapsuleerPatrolFit.Caracal,
            CapsuleerPatrolFit.Stabber,
            CapsuleerPatrolFit.Moa,
            CapsuleerPatrolFit.Thorax,
        ],
        [RoamingGangTests.Fixture.HeavySeed] =
        [
            CapsuleerPatrolFit.Drake,
            CapsuleerPatrolFit.Hurricane,
            CapsuleerPatrolFit.Raven,
            CapsuleerPatrolFit.Megathron,
        ],
    };

    [Theory]
    [InlineData(RoamingGangTests.Fixture.FrigateSeed)]
    [InlineData(RoamingGangTests.Fixture.DestroyerSeed)]
    [InlineData(RoamingGangTests.Fixture.CruiserSeed)]
    [InlineData(RoamingGangTests.Fixture.HeavySeed)]
    public async Task SnapshotAndSpecialistGangCoexistAndKeepTheirOriginalHullsOnReopen(int seed)
    {
        using var fixture = new RoamingGangTests.Fixture(regional: true, patrolSeed: seed);
        var gang = fixture.Form();
        var expected = s_patrolHulls[seed].Order();
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

    public static TheoryData<int, int, bool> PatrolEncounters(bool includeFrigates)
    {
        var encounters = new TheoryData<int, int, bool>();
        foreach (var (seed, hulls) in s_patrolHulls)
        {
            if (!includeFrigates && seed == RoamingGangTests.Fixture.FrigateSeed)
            {
                continue;
            }
            foreach (int hull in hulls)
            {
                encounters.Add(seed, hull, false);
                encounters.Add(seed, hull, true);
            }
        }
        return encounters;
    }

    [Theory]
    [MemberData(nameof(PatrolEncounters), true)]
    public void FittedGangMemberLocksPointsAndDamagesAPlayerOrRival(int seed, int hull, bool rival)
    {
        using var fixture = new RoamingGangTests.Fixture(regional: true, patrolSeed: seed);
        var gang = fixture.Form();
        var member = Assert.Single(gang.Members, ship => fixture.Live.Economy.Peek<Ship>(ship.ShipId)!.TypeId == hull);
        var ships = fixture.Host.GetRequiredService<SqliteWorld>();
        DockedPilot attacker;
        using (var catalog = new CatalogDbContext(fixture.Database.CatalogPath))
        using (var action = fixture.Live.Economy.Begin())
        {
            var gate = SystemObjects.Geometry(catalog, Away).First(body => body.DestinationGateId is not null);
            SqliteWorld.SetPosition(action, catalog, member.ShipId, gate.Position, Away, fixture.Clock.Now);
            attacker = ships.FindPilot(action, action.Pilots.Find(member.PilotId)!.AccountId, member.PilotId)!;
            action.Commit();
        }
        var victim = PlaceTarget(fixture, attacker, rival, gang.DestinationStationId);
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

    [Theory]
    [MemberData(nameof(PatrolEncounters), false)]
    public void SoloPatrolUndocksAndEngagesAVisiblePlayerOrRival(int seed, int hull, bool rival)
    {
        using var fixture = new RoamingGangTests.Fixture(regional: true, patrolSeed: seed, soloHull: hull);
        long pilot = fixture.Live.Economy.Read(world =>
            world
                .Set<SavedCapsuleerHauler>()
                .AsEnumerable()
                .Single(row => world.Set<SavedCapsuleerSuspension>().Find(row.PilotId) is null)
                .PilotId
        );
        for (int turn = 0; turn < 300 && fixture.Scheduler.Hauler(pilot)?.Work.Ratting?.Phase != RattingPhase.Searching; turn++)
        {
            fixture.Tick();
        }
        var arrival = fixture.Scheduler.Hauler(pilot)!;
        Assert.Null(arrival.Work.Gang);
        Assert.Equal(PatrolKind.Roam, arrival.Work.Ratting?.Patrol?.Kind);
        Assert.Equal(RattingPhase.Searching, arrival.Work.Ratting?.Phase);
        Assert.Equal(hull, arrival.Pilot.ShipTypeId);
        var victim = PlaceTarget(fixture, arrival.Pilot, rival, arrival.Work.Ratting!.StationId, solo: true);
        var tackle = fixture.Host.GetRequiredService<IPilotTackle>();
        var health = fixture.Host.GetRequiredService<IPilotHealth>();
        double Remaining()
        {
            var status = health.Read(victim.PilotId, fixture.Clock.Now).Remaining;
            return status.Shield + status.Armor + status.Structure;
        }
        double before = Remaining();
        bool pointed = false;
        for (int turn = 0; turn < 45 && (!pointed || Remaining() >= before); turn++)
        {
            fixture.Tick();
            var work = fixture.Live.HaulerCheckpoints[pilot].Work;
            Assert.Null(work.Gang);
            pointed |=
                work.Ratting?.TargetId == victim.ShipId
                && tackle.Incoming(victim.ShipId).Any(effect => effect.SourceShipId == arrival.Pilot.ShipId && effect.Module.Strength > 0);
        }
        Assert.True(pointed);
        Assert.True(Remaining() < before);
        Assert.Equal(0, fixture.Scheduler.Read().Failures);
    }

    private static DockedPilot PlaceTarget(
        RoamingGangTests.Fixture fixture,
        DockedPilot attacker,
        bool rival,
        int station,
        bool solo = false
    )
    {
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
            var hullClass = (HullClass)attacker.ShipGroupId;
            bool heavy = hullClass is HullClass.CombatBattlecruiser or HullClass.Battleship;
            target.ActiveShip.TypeId = heavy ? CapsuleerPatrolFit.Moa : (int)TechOneHullType.Merlin;
            if (rival)
            {
                action.Remove(action.Set<SavedCapsuleerSuspension>().Find(victimId)!);
            }
            // Medium and large turrets need room to track a crossing target on arrival.
            int separation = heavy || solo && hullClass == HullClass.Cruiser ? 5_000 : 1_000;
            var position = attacker.Position! + new SpacePosition(separation, 0, 0);
            SqliteWorld.SetPosition(action, catalog, target.ActiveShipId, position, attacker.SystemId, fixture.Clock.Now);
            victim = fixture.Host.GetRequiredService<SqliteWorld>().FindPilot(action, target.AccountId, victimId)!;
            action.Commit();
        }
        if (rival)
        {
            fixture.Scheduler.PopulationPrepared(Away);
            // Let the rival finish its normal wait on grid so that larger hulls have time to lock before it starts its trip.
            fixture.Live.HaulerCheckpoints[victim.PilotId] = new(
                victim.PilotId,
                new(
                    HaulerActivity.Waiting,
                    0,
                    0,
                    victim.ShipId,
                    station,
                    0,
                    new NavigationState().Capture(),
                    DueTicks: TimeSpan.FromSeconds(fixture.Clock.Seconds + 60).Ticks
                )
            );
        }
        else
        {
            fixture.Host.GetRequiredService<SharedSpace>().Publish(new(PilotContact.From(victim), SpacePosition.Zero, null, 0, null));
        }
        return victim;
    }
}
