using Eden.Data.World.Agents;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionNpcWeaponTests
{
    // Build 3662168 missileLaunchingForEntity. Keep the literal in this regression
    // independent of the production enum so it checks the actual catalog binding.
    private const DogmaEffect EntityMissileEffect = (DogmaEffect)569;

    [Fact]
    public void SupportOnlySpiderDroneCannotFireAPlaceholderTurret()
    {
        const int spiderDrone = 16210;
        using MissionFixture fixture = new(live: true);
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
        Assert.True(NpcPopulationRules.HasDirectWeapon(PilotFittingStore.ReadAttributes(catalog, spiderDrone)));
        Assert.DoesNotContain(DogmaEffect.LaserFired, FittingCatalog.Effects(catalog, spiderDrone));
        var profile = new NpcProfileCatalog().Read(catalog, spiderDrone);
        Assert.NotEmpty(profile.ElectronicWarfare);
        Assert.False(NpcPopulationRules.HasDirectWeapon(profile.Attributes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissionTurretsKeepTheirCooldownAndOnlyDeclaredLaunchersCreateProjectiles(bool launcher)
    {
        using MissionFixture fixture = new(
            level: 2,
            division: MissionRules.SecurityDivision,
            live: true,
            configure: catalog =>
            {
                catalog.Database.ExecuteSqlRaw("DELETE FROM SourceRecord WHERE Dataset='client/missions' AND Key<>'923'");
            }
        );
        var live = LiveWorld.Find(fixture.Db.Path)!;
        using (var action = live.Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Add(
                new Eden.Data.World.Characters.SavedNpcStanding
                {
                    PilotId = MissionFixture.PilotId,
                    SourceId = MissionFixture.AgentId,
                    Value = 8,
                }
            );
            action.Commit();
        }
        var mission = fixture.Accept(fixture.Request());
        foreach (var site in fixture.Missions.Sites(mission.SystemId))
        {
            fixture.Missions.Arrive(mission.SystemId, site.Position);
        }
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
        var npc = live.Economy.Inspect(action =>
            action
                .Set<WorldNpc>()
                .Where(n => n.EncounterId == MissionRules.FirstSiteId + mission.Id)
                .AsEnumerable()
                .First(n => FittingCatalog.Effects(catalog, n.TypeId).Contains(EntityMissileEffect) == launcher)
        );
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        fixture.Db.Store.SetPosition(pilot.ShipId, new SpacePosition(npc.X + 1000, npc.Y, npc.Z), mission.SystemId);
        long now = fixture.Clock.Now;
        using (var action = live.Economy.Begin(new(pilots: [pilot.PilotId])))
        {
            var aggression = action.Add(NpcAggression.Start(npc.Id, pilot.ShipId, 0, now)).Entity;
            if (!launcher)
            {
                var raw = PilotFittingStore.ReadAttributes(catalog, npc.TypeId);
                Assert.Equal(1, raw[DogmaAttributeId.EntityMissileCycle]);
                Assert.False(
                    NpcMissileStore.Launch(
                        action,
                        catalog,
                        action.Set<WorldNpc>().Find(npc.Id)!,
                        aggression,
                        raw,
                        action.Pilots.Find(pilot.PilotId)!,
                        SpacePosition.Zero,
                        now,
                        1
                    )
                );
                Assert.Equal(now, aggression.MissileReadyAt);
                Assert.Empty(action.Set<WorldMissile>());
            }
            action.Commit();
        }
        fixture.Context.Flight.Snapshot();
        var combat = fixture.Services.GetRequiredService<INpcCombat>();
        NpcShot? Fire() =>
            combat.Fire(
                pilot.PilotId,
                pilot.ShipId,
                npc.Id,
                SpacePosition.Zero,
                fixture.Clock.Now,
                0.5,
                fixture.Context.Flight.CurrentStamp
            );
        var shot = Fire();
        long expectedReady = now;
        if (launcher)
        {
            // This room's Pithi Plunderer/Wrecker hulls use missiles only.
            Assert.Null(shot);
        }
        else
        {
            Assert.NotNull(shot);
            Assert.False(shot.IsMissile);
            Assert.True(shot.Applied > 0);
            expectedReady += (long)(shot.Weapon.Duration * TimeSpan.TicksPerMillisecond);
        }
        Assert.Null(Fire());
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(1));
        Assert.Null(Fire());
        await live.FlushAsync();
        fixture.StopResident();
        using WorldDbContext saved = new(fixture.Db.Path);
        Assert.Equal(launcher ? 1 : 0, saved.Set<WorldMissile>().Count(m => m.SourceShipId == npc.Id));
        Assert.Equal(expectedReady, saved.Set<NpcAggression>().Find(npc.Id)!.ReadyAt);
    }

    [Fact]
    public void EveryMissionHullRequiresItsDeclaredMissileEffectBeforeEnablingMissiles()
    {
        using MissionFixture fixture = new(live: true);
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
        var types = MissionCatalog
            .Read(catalog)
            .SelectMany(p => p.Definition.Waves)
            .SelectMany(w => w.Groups)
            .SelectMany(g => g.Types)
            .Distinct()
            .ToArray();
        Assert.NotEmpty(types);
        List<string> invalid = [];
        var profiles = new NpcProfileCatalog();
        foreach (int type in types)
        {
            var raw = PilotFittingStore.ReadAttributes(catalog, type);
            bool expected =
                raw.GetValueOrDefault(DogmaAttributeId.EntityMissileType) > 0
                && FittingCatalog.Effects(catalog, type).Contains(EntityMissileEffect);
            var profile = profiles.Read(catalog, type);
            bool enabled = profile.Attributes.GetValueOrDefault(DogmaAttributeId.EntityMissileType) > 0;
            bool direct = NpcPopulationRules.HasDirectWeapon(raw) && FittingCatalog.Effects(catalog, type).Contains(DogmaEffect.LaserFired);
            if (expected != enabled || direct != NpcPopulationRules.HasDirectWeapon(profile.Attributes))
            {
                invalid.Add($"{type} ({profile.Type.Name}), missile cycle {raw.GetValueOrDefault(DogmaAttributeId.EntityMissileCycle)} ms");
            }
        }
        Assert.True(invalid.Count == 0, string.Join(Environment.NewLine, invalid));
    }
}
