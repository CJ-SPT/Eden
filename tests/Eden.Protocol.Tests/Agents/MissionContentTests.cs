using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.Catalog.Agents;
using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Server;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionContentTests
{
    private static MissionContent Content()
    {
        using var stream = typeof(MissionCatalog).Assembly.GetManifestResourceStream("Eden.World.Missions.json")!;
        return JsonSerializer.Deserialize<MissionContent>(stream, JsonSerializerOptions.Web)!;
    }

    public static TheoryData<int, int> NewCombatMissions => Cases(combat: true);

    public static TheoryData<int, int> DeliveryMissions => Cases(combat: false);

    private static TheoryData<int, int> Cases(bool combat)
    {
        TheoryData<int, int> cases = new();
        foreach (var mission in Content().Missions.Where(m => combat ? m.OfferOrder > 0 && !m.Waves.IsEmpty : m.Waves.IsEmpty && !m.Mining))
        {
            cases.Add(mission.MissionId, mission.Level);
        }
        return cases;
    }

    [Fact]
    public void ExpandedContentLoadsEveryDefinitionAndCorrectsNuisanceFactionRosters()
    {
        using MissionFixture f = new();
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var profiles = MissionCatalog.Read(catalog);
        Assert.Equal(Content().Missions.Length, profiles.Length);
        Assert.Equal(59, profiles.Count(p => p.Kind == MissionKind.Combat));
        Assert.Equal(261, profiles.Count(p => p.Kind == MissionKind.Courier));
        Assert.Equal(23, profiles.Count(p => p.Kind == MissionKind.Mining));
        foreach (var profile in profiles)
        {
            Assert.NotEqual("Mission", profile.Name);
            Assert.NotEmpty(profile.Briefing);
            Assert.InRange(profile.Definition.Level, 1, 4);
        }
        foreach (var (id, faction, npc) in new[] { (916, 500010, "Pithi Arrogator"), (920, 500011, "Gistii Nomad") })
        {
            var profile = profiles.Single(p => p.Definition.MissionId == id);
            Assert.Equal(faction, profile.EnemyFaction);
            int type = catalog.Set<ItemType>().Single(t => t.Name == npc).Id;
            Assert.Contains(type, profile.Definition.Waves.SelectMany(w => w.Groups).SelectMany(g => g.Types));
        }
    }

    [Theory]
    [MemberData(nameof(NewCombatMissions))]
    public async Task EveryNewCombatVariantHasWorkingObjectivesCargoAndExactlyOnceTurnIn(int id, int level)
    {
        using var f = Create(id, level, MissionRules.SecurityDivision, live: true);
        var offer = f.Accept(f.Request());
        Assert.Equal(id, offer.MissionId);
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var profile = MissionCatalog.Find(catalog, id);
        foreach (var site in f.Missions.Sites(offer.SystemId))
        {
            f.Missions.Arrive(offer.SystemId, site.Position);
            f.Missions.Arrive(offer.SystemId, site.Position);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            int initial = profile.Definition.Waves.GroupBy(w => w.RoomId).Sum(room => room.First().Groups.Sum(g => g.Count));
            Assert.Equal(initial, world.Set<SavedMissionMember>().Count());
            Assert.Equal(profile.Definition.Structures.Length, world.Set<SavedMissionStructure>().Count());
        }
        Assert.False(Current(f).ObjectiveComplete);
        long last = 0;
        if (!profile.Definition.Structures.IsEmpty)
        {
            using (WorldDbContext world = new(f.Db.Path))
            {
                last = world.Set<SavedMissionStructure>().Single(s => s.MissionId == offer.Id && s.CompletesMission).NpcId;
            }
            f.Kill(last);
            Assert.True(Current(f).CombatComplete);
            if (profile.CargoType != 0)
            {
                Assert.False(Current(f).ObjectiveComplete);
                long recovery = MissionContainerRules.Id(offer.Id, 0);
                var obj = f.Missions.Sites(offer.SystemId).SelectMany(s => s.Objects).Single(o => o.Id == recovery);
                Move(f, offer.SystemId, obj.Position);
                var containers = f.Services.GetRequiredService<IPilotMissionContainers>();
                var cargo = Assert.Single(containers.Read(MissionFixture.PilotId, recovery).Items);
                Assert.Equal(profile.CargoQuantity, cargo.Quantity);
                containers.Take(MissionFixture.PilotId, f.Context.Pilot.ShipId, new([cargo.Id], recovery, (int)InventoryFlag.Cargo, null));
                Assert.Empty(containers.Read(MissionFixture.PilotId, recovery).Items);
            }
        }
        else
        {
            long[] required;
            using (WorldDbContext world = new(f.Db.Path))
            {
                required = world
                    .Set<SavedMissionMember>()
                    .Where(m => m.Required || m.CompletesMission)
                    .OrderBy(m => m.NpcId)
                    .Select(m => m.NpcId)
                    .ToArray();
            }
            Assert.NotEmpty(required);
            foreach (long target in required)
            {
                last = target;
                f.Kill(target);
                if (target != required[^1])
                {
                    Assert.False(Current(f).CombatComplete);
                    using WorldDbContext world = new(f.Db.Path);
                    Assert.Empty(world.Set<OwnedItem>().Where(i => i.TypeId == profile.CargoType));
                }
            }
            Assert.True(Current(f).CombatComplete);
            if (profile.CargoType != 0)
            {
                Assert.False(Current(f).ObjectiveComplete);
                var wrecks = f.Services.GetRequiredService<IWrecks>();
                var wreck = wrecks.List(offer.SystemId, f.Clock.Now).Single(w => w.SourceId == last);
                Move(f, offer.SystemId, wreck.Position);
                var cargo = Assert.Single(
                    wrecks.Read(MissionFixture.PilotId, wreck.Id, f.Clock.Now).Items,
                    i => i.TypeId == profile.CargoType
                );
                Assert.Equal(profile.CargoQuantity, cargo.Quantity);
                wrecks.Take(MissionFixture.PilotId, f.Context.Pilot.ShipId, wreck.Id, [cargo.Id], null, f.Clock.Now);
            }
        }
        Assert.True(Current(f).ObjectiveComplete);
        f.Dock(MissionFixture.StationId);
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        f.StopResident();
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        Assert.True(missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.ObjectiveComplete);
        Assert.Equal(
            MissionState.Completed,
            missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id).Offer!.State
        );
        Assert.Throws<InvalidDataException>(() =>
            missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id)
        );
        using WorldDbContext check = new(f.Db.Path);
        Assert.Empty(check.Set<OwnedItem>().Where(i => i.TypeId == profile.CargoType));
        Assert.Empty(missions.Sites(offer.SystemId));
    }

    private static MissionFixture Create(int id, int level, int division, bool live = false)
    {
        var f = new MissionFixture(
            level,
            division,
            live: live,
            configure: catalog =>
            {
                var mission = catalog.Require(CatalogData.Client.Missions, (long)id);
                long corporation =
                    mission.CorporationID
                    ?? (
                        mission.FactionID is > 0
                            ? catalog.Require(CatalogData.Client.Factions, mission.FactionID.Value).CorporationID
                            : null
                    )
                    ?? 1000044;
                string key = MissionFixture.AgentId.ToString(CultureInfo.InvariantCulture);
                string storylineKey = MissionFixture.StorylineId.ToString(CultureInfo.InvariantCulture);
                catalog.Database.ExecuteSql(
                    $"""
                    UPDATE SourceRecord SET Data=json_set(Data,'$.corporationID',{corporation})
                    WHERE Dataset='client/npccharacters' AND Key IN ({key},{storylineKey})
                    """
                );
                string missionKey = id.ToString(CultureInfo.InvariantCulture);
                string prerequisite = Content().Missions.Single(m => m.MissionId == id).Storyline
                    ? (2911 + level).ToString(CultureInfo.InvariantCulture)
                    : missionKey;
                catalog.Database.ExecuteSql(
                    $"""
                    DELETE FROM SourceRecord WHERE Dataset='client/missions' AND Key NOT IN ({missionKey},{prerequisite})
                    """
                );
                catalog.Database.ExecuteSqlRaw("UPDATE ItemType SET Capacity=100000 WHERE Id=601");
            }
        );
        using WorldDbContext world = new(f.Db.Path);
        world.Add(
            new SavedNpcStanding
            {
                PilotId = MissionFixture.PilotId,
                SourceId = MissionFixture.AgentId,
                Value = 8,
            }
        );
        world.SaveChanges();
        return f;
    }

    private static MissionOffer Current(MissionFixture f) => f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!;

    private static void Move(MissionFixture f, int system, SpacePosition position)
    {
        f.Db.Store.SetPosition(f.Context.Pilot.ShipId, position, system);
        f.Context.ChangeShip(f.Db.Store.FindPilot(1000001)!);
    }
}
