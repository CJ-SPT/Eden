using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Combat;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class EnforcerCareerTests
{
    private const long Enforcer = 3018935;
    private const int EnforcerStation = 60015027;
    private const long Pilot = MissionFixture.PilotId;
    private const int Civilians = 24441;

    [Fact]
    public void EnforcerRescueTransfersCargoOnlyOnCommittedFinalKill()
    {
        using var fixture = Create(previous: 3122);
        var offer = fixture.Accept(fixture.Request(Enforcer));
        Assert.Equal(3160, offer.MissionId);
        Move(fixture, offer.SystemId, offer.Position);
        fixture.Missions.Arrive(offer.SystemId, offer.Position);
        var initial = Wave(fixture, offer.Id, 0);
        Assert.Equal(3, initial.Length);
        foreach (long id in initial)
        {
            fixture.Kill(id);
        }
        var final = Wave(fixture, offer.Id, 1);
        Assert.Equal(2, final.Length);
        fixture.Kill(final[0]);
        fixture.Kill(final[1], commit: false);
        Assert.Empty(Items(fixture, Civilians));
        Assert.False(fixture.Missions.Current(Pilot, Enforcer)!.ObjectiveComplete);

        fixture.Kill(final[1]);
        var cargo = Assert.Single(Items(fixture, Civilians));
        Assert.Equal(fixture.Context.Pilot.ShipId, cargo.LocationId);
        Assert.Equal((int)InventoryFlag.Cargo, cargo.FlagId);
        Assert.Equal(1, cargo.Quantity);
        Assert.True(fixture.Missions.Current(Pilot, Enforcer)!.ObjectiveComplete);
    }

    [Fact]
    public async Task EnforcerHotelRequiresOwnerProximityAndKeepsAmbushAcrossCheckpoint()
    {
        using var fixture = Create(previous: 3143);
        List<PilotDamage> damage = [];
        var signal = Assert.IsType<PilotDamageSignal>(fixture.Services.GetRequiredService<IPilotDamageEvents>());
        signal.Applied += damage.Add;
        var offer = fixture.Accept(fixture.Request(Enforcer));
        Assert.Equal(3147, offer.MissionId);
        var site = Assert.Single(fixture.Missions.Sites(offer.SystemId));
        var visit = Assert.IsType<MissionVisitPoint>(site.CareerVisit);
        Assert.Contains(site.Objects, body => body.Id == visit.ObjectId && body.Name == "Hotel Kim");

        Move(fixture, offer.SystemId, site.Position);
        Assert.False(fixture.Missions.VisitCareerSite(Pilot, offer.SystemId, offer.Id));
        Assert.Empty(Wave(fixture, offer.Id, 0));
        Move(fixture, offer.SystemId, visit.Position + new SpacePosition(visit.Radius + 500, 0, 0));
        Assert.False(fixture.Missions.VisitCareerSite(Pilot + 1, offer.SystemId, offer.Id));
        Assert.Empty(damage);
        Assert.True(fixture.Missions.VisitCareerSite(Pilot, offer.SystemId, offer.Id));
        Assert.False(fixture.Missions.VisitCareerSite(Pilot, offer.SystemId, offer.Id));
        var hit = Assert.Single(damage);
        Assert.Equal(250, hit.Damage.Thermal);
        Assert.Equal(visit.ObjectId, hit.AttackerShipId);
        Assert.Equal(3, Wave(fixture, offer.Id, 0).Length);
        Assert.DoesNotContain(Assert.Single(fixture.Missions.Sites(offer.SystemId)).Objects, body => body.Id == visit.ObjectId);

        await LiveWorld.Find(fixture.Db.Path)!.FlushAsync();
        fixture.StopResident();
        using var reopened = ServerServices.Build(new(fixture.Db.Path), fixture.Clock, liveWorld: true);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        Assert.True(missions.Current(Pilot, Enforcer)!.ObjectiveComplete);
        Assert.False(missions.VisitCareerSite(Pilot, offer.SystemId, offer.Id));
        Assert.Equal(3, Wave(fixture, offer.Id, 0).Length);
        Assert.DoesNotContain(Assert.Single(missions.Sites(offer.SystemId)).Objects, body => body.Id == visit.ObjectId);
    }

    [Fact]
    public void EnforcerTowerDestructionReleasesTheRequiredPirateCapsule()
    {
        using var fixture = Create(previous: 13868);
        var offer = fixture.Accept(fixture.Request(Enforcer));
        Assert.Equal(3151, offer.MissionId);
        var sites = fixture.Missions.Sites(offer.SystemId);
        Assert.Equal(4, sites.Count);
        Assert.Equal(2, sites.SelectMany(site => site.Gates).Count(gate => gate.RequiresClear));
        var final = sites.Single(site => site.Gates.Count == 0);
        fixture.Missions.Arrive(offer.SystemId, final.Position);
        var guards = Wave(fixture, offer.Id, 5);
        Assert.Equal(3, guards.Length);
        foreach (long guard in guards)
        {
            fixture.Kill(guard);
        }
        Assert.Empty(Wave(fixture, offer.Id, 6));
        Assert.False(fixture.Missions.Current(Pilot, Enforcer)!.ObjectiveComplete);

        long tower = LiveWorld
            .Find(fixture.Db.Path)!
            .Economy.Inspect(world =>
                Assert.Single(world.Filter<SavedMissionStructure>(structure => structure.MissionId == offer.Id)).NpcId
            );
        fixture.Kill(tower);
        Assert.False(fixture.Missions.Current(Pilot, Enforcer)!.ObjectiveComplete);
        long capsule = Assert.Single(Wave(fixture, offer.Id, 6));
        string? name = LiveWorld.Find(fixture.Db.Path)!.Economy.Inspect(world => world.Set<WorldNpc>().Find(capsule)!.Name);
        Assert.Equal("Tahamar's Capsule", name);
        fixture.Kill(capsule);
        Assert.True(fixture.Missions.Current(Pilot, Enforcer)!.ObjectiveComplete);
    }

    private static MissionFixture Create(int previous)
    {
        MissionFixture fixture = new(
            live: true,
            configure: catalog => PassiveCatalogFixture.LoadCareerMissions(catalog.Database.GetDbConnection().DataSource)
        );
        using var world = LiveWorld.Find(fixture.Db.Path)!.Economy.Begin(new(pilots: [Pilot]));
        world.Pilots.Find(Pilot)!.ActiveShip.StationId = EnforcerStation;
        world.Add(
            new SavedMission
            {
                PilotId = Pilot,
                AgentId = Enforcer,
                MissionId = previous,
                Level = 1,
                State = MissionState.Completed,
                OfferedAt = fixture.Clock.Now - 1,
                AcceptedAt = fixture.Clock.Now - 1,
                ClosedAt = fixture.Clock.Now - 1,
                ExpiresAt = long.MaxValue,
            }
        );
        world.Commit();
        return fixture;
    }

    private static long[] Wave(MissionFixture fixture, long mission, int wave) =>
        LiveWorld
            .Find(fixture.Db.Path)!
            .Economy.Inspect(world =>
                world
                    .Filter<SavedMissionMember>(member => member.MissionId == mission && member.Wave == wave)
                    .Select(member => member.NpcId)
                    .ToArray()
            );

    private static OwnedItem[] Items(MissionFixture fixture, int type) =>
        LiveWorld
            .Find(fixture.Db.Path)!
            .Economy.Inspect(world => world.Filter<OwnedItem>(item => item.OwnerId == Pilot && item.TypeId == type).ToArray());

    private static void Move(MissionFixture fixture, int system, SpacePosition position) =>
        fixture.Db.Store.SetPosition(fixture.Context.Pilot.ShipId, position, system);
}
