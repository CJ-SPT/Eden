using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Server;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionSpawnClearanceTests
{
    private sealed record SavedSpawn(long Id, int TypeId, SpacePosition Position);

    [Fact]
    public async Task EmbeddedAngelSpiesSpawnWithoutCollisionImpulseAndKeepPositionsAcrossCheckpoints()
    {
        const int missionId = 897;
        using MissionFixture fixture = new(
            level: 2,
            division: MissionRules.SecurityDivision,
            live: true,
            configure: catalog =>
            {
                catalog.Database.ExecuteSqlRaw("DELETE FROM SourceRecord WHERE Dataset='client/missions' AND Key<>'897'");
            }
        );
        var live = LiveWorld.Find(fixture.Db.Path)!;
        using (var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Add(
                new SavedNpcStanding
                {
                    PilotId = MissionFixture.PilotId,
                    SourceId = MissionFixture.AgentId,
                    Value = 8,
                }
            );
            action.Commit();
        }
        var mission = fixture.Accept(fixture.Request());
        Assert.Equal(missionId, mission.MissionId);
        fixture.Missions.Arrive(mission.SystemId, mission.Position);
        var spawns = Snapshot(live);
        Assert.NotEmpty(spawns);
        var objects = fixture.Missions.Sites(mission.SystemId).SelectMany(s => s.Objects).ToArray();
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath))
        {
            foreach (var spawn in spawns)
            {
                var type = catalog.RequireItem(spawn.TypeId);
                FlightProperties hull = new(0, (float)(type.Radius ?? 0), 1000000, 1, 100, 3);
                var collision = ShipCollisions.ResolveStatic(
                    new(spawn.Id, hull, spawn.Position, SpacePosition.Zero, spawn.Position, SpacePosition.Zero),
                    objects
                );
                Assert.False(collision.Bumped, $"NPC {spawn.Id} starts inside scenery.");
                Assert.Equal(SpacePosition.Zero, collision.Velocity);
                Assert.Equal(spawn.Position, collision.Position);
            }
        }
        fixture.Missions.Arrive(mission.SystemId, mission.Position);
        Assert.Equal(spawns, Snapshot(live));
        await live.FlushAsync();
        fixture.StopResident();
        using var reopened = ServerServices.Build(new(fixture.Db.Path), fixture.Clock, liveWorld: true);
        reopened.GetRequiredService<IPilotMissions>().Arrive(mission.SystemId, mission.Position);
        Assert.Equal(spawns, Snapshot(LiveWorld.Find(fixture.Db.Path)!));

        SavedSpawn[] Snapshot(LiveWorld world) =>
            world.Economy.Inspect(action =>
                action
                    .Set<WorldNpc>()
                    .Where(n => n.EncounterId == MissionRules.FirstSiteId + mission.Id)
                    .OrderBy(n => n.Id)
                    .Select(n => new SavedSpawn(n.Id, n.TypeId, new(n.X, n.Y, n.Z)))
                    .ToArray()
            );
    }

    [Fact]
    public void EveryCombatWaveAndHullVariantSpawnsOutsideSolidScenery()
    {
        using MissionFixture fixture = new(live: true);
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
        List<string> overlaps = [];
        var profiles = MissionCatalog.Read(catalog).Where(p => p.Kind == MissionKind.Combat).ToArray();
        Assert.NotEmpty(profiles);
        foreach (var profile in profiles)
        {
            for (int wave = 0; wave < profile.Definition.Waves.Length; wave++)
            {
                var room = profile.Rooms.Single(r => r.Id == profile.WaveRoom(wave));
                var groups = profile.Definition.Waves[wave].Groups;
                int variants = groups.Max(g => g.Types.Length);
                for (int variant = 0; variant < variants; variant++)
                {
                    var members = groups
                        .SelectMany(
                            (group, index) =>
                            {
                                int typeId = group.Types[variant % group.Types.Length];
                                var type = catalog.RequireItem(typeId);
                                return Enumerable.Repeat(
                                    new MissionSpawnMember(typeId, index, group.Range, type.GroupId == NpcEncounterDefinitions.SentryGroup)
                                    {
                                        Radius = (float)(type.Radius ?? 0),
                                    },
                                    group.Count
                                );
                            }
                        )
                        .ToArray();
                    bool initial = !Enumerable.Range(0, wave).Any(w => profile.WaveRoom(w) == room.Id);
                    var obstacles = profile
                        .Rooms.SelectMany(r => r.Objects)
                        .Select(o => o with { Position = o.Position - room.Position })
                        .ToArray();
                    var placements = MissionSpawnRules.Place(initial, members, room.SpawnAnchors, obstacles);
                    for (int index = 0; index < members.Length; index++)
                    {
                        if (members[index].Sentry)
                        {
                            continue;
                        }
                        double radius = catalog.RequireItem(members[index].TypeId).Radius ?? 0;
                        var point = room.Position + placements[index].Position;
                        foreach (var obstacle in profile.Rooms.SelectMany(r => r.Objects).Where(o => o.Solid))
                        {
                            if (point.DistanceTo(obstacle.Position) <= radius + obstacle.Radius)
                            {
                                overlaps.Add(
                                    $"Mission {profile.Definition.MissionId} ({profile.Name}), wave {wave}, "
                                        + $"hull {members[index].TypeId}, object {obstacle.Id} (type {obstacle.TypeId})."
                                );
                            }
                        }
                    }
                }
            }
        }
        Assert.True(overlaps.Count == 0, $"{overlaps.Count} overlapping placements:\n" + string.Join("\n", overlaps.Take(20)));
    }
}
