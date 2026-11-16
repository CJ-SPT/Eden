using System.Globalization;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Loot;
using Eden.Server;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Loot;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class DataCenterMissionTests
{
    [Fact]
    public void AllFiftyRequiredTagTypesAreObtainableFromTheMatchingPirateFactionAndHullGrades()
    {
        using MissionFixture f = new(live: true);
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var expected = MissionCatalog.Read(catalog).Where(p => !p.Definition.FixedAgentIds.IsEmpty).Select(p => p.CargoType).ToHashSet();
        HashSet<int> actual = [];
        foreach (var faction in Enum.GetValues<NpcPirateFaction>().Where(faction => faction != NpcPirateFaction.RogueDrones))
        {
            foreach (var hull in Enum.GetValues<NpcHullClass>())
            {
                foreach (int seed in new[] { 0, 4 })
                {
                    var drop = NpcTagLootCatalog.Roll(catalog, faction, hull, seed)!;
                    Assert.Equal(1, drop.Quantity);
                    Assert.True(actual.Add(drop.TypeId));
                }
                Assert.Null(NpcTagLootCatalog.Roll(catalog, faction, hull, 1));
            }
        }
        Assert.Equal(50, actual.Count);
        Assert.True(expected.SetEquals(actual));
        Assert.Null(NpcTagLootCatalog.Roll(catalog, NpcPirateFaction.RogueDrones, NpcHullClass.Battleship, 0));
    }

    private sealed record TagCarrier(long Id, int SystemId, SpacePosition Position, NpcLootDrop Tag);

    [Theory]
    [InlineData(875, NpcPirateFaction.Guristas)]
    [InlineData(896, NpcPirateFaction.Angels)]
    [InlineData(1229, NpcPirateFaction.BloodRaiders)]
    [InlineData(1230, NpcPirateFaction.Sansha)]
    [InlineData(1227, NpcPirateFaction.Serpentis)]
    public async Task RealPirateDestructionCreatesLootableCriminalTagsThatSurviveCheckpoint(int mission, NpcPirateFaction faction)
    {
        using MissionFixture f = new(
            division: Eden.Gameplay.Agents.MissionRules.SecurityDivision,
            live: true,
            configure: catalog =>
            {
                string selected = mission.ToString(CultureInfo.InvariantCulture);
                catalog.Database.ExecuteSql(
                    $"""
                    DELETE FROM SourceRecord WHERE Dataset='client/missions' AND Key<>{selected}
                    AND json_extract(Data,'$.contentTemplate')<>'agent.missionTemplatizedContent_StorylineTradeMission'
                    """
                );
            }
        );
        var offer = f.Accept(f.Request());
        foreach (var site in f.Missions.Sites(offer.SystemId))
        {
            f.Missions.Arrive(offer.SystemId, site.Position);
        }
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var carrier = Live(f)
            .Economy.Inspect(world =>
            {
                foreach (var npc in world.Set<WorldNpc>().Where(npc => npc.AnchorKind == NpcAnchorKind.Mission))
                {
                    int groupId = catalog.RequireItem(npc.TypeId).GroupId;
                    var group = NpcPirateGroups
                        .All.Concat(NpcPirateGroups.EncounterLoot)
                        .SingleOrDefault(group => group.GroupId == groupId);
                    if (
                        group is { Commander: false }
                        && group.Faction == faction
                        && NpcTagLootCatalog.Roll(catalog, faction, group.Hull, NpcSpawnRules.Seed(npc.SystemId, npc.Id)) is { } tag
                    )
                    {
                        return new TagCarrier(npc.Id, npc.SystemId, new(npc.X, npc.Y, npc.Z), tag);
                    }
                }
                throw new InvalidDataException("The deterministic fixture needs a tag-carrying native pirate.");
            });
        f.Kill(carrier.Id);
        Move(f, carrier.SystemId, carrier.Position);
        var wrecks = f.Services.GetRequiredService<IWrecks>();
        var wreck = wrecks.List(carrier.SystemId, f.Clock.Now).Single(wreck => wreck.SourceId == carrier.Id);
        var stack = wrecks.Read(MissionFixture.PilotId, wreck.Id, f.Clock.Now).Items.Single(item => item.TypeId == carrier.Tag.TypeId);
        Assert.Equal(1, stack.Quantity);
        long ship = Live(f).Economy.Inspect(world => world.Pilots.Find(MissionFixture.PilotId)!.ActiveShipId);
        var changes = wrecks.Take(MissionFixture.PilotId, ship, wreck.Id, [stack.Id], null, f.Clock.Now);
        Assert.Contains(changes, change => change.Item.TypeId == carrier.Tag.TypeId && change.Item.LocationId == ship);
        await Live(f).FlushAsync();
        f.StopResident();
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        Assert.Equal(
            1,
            Live(f)
                .Economy.Inspect(world =>
                    world.Set<OwnedItem>().Single(item => item.TypeId == carrier.Tag.TypeId && item.LocationId == ship).Quantity
                )
        );
    }
}
