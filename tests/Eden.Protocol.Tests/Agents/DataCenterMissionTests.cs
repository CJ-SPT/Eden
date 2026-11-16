using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.Catalog.Agents;
using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class DataCenterMissionTests
{
    private const long FirstAgent = 3018480;

    private static MissionContent Content()
    {
        using var stream = typeof(MissionCatalog).Assembly.GetManifestResourceStream("Eden.World.Missions.json")!;
        return JsonSerializer.Deserialize<MissionContent>(stream, JsonSerializerOptions.Web)!;
    }

    public static IEnumerable<object[]> Missions =>
        Content()
            .Missions.Where(m => !m.FixedAgentIds.IsEmpty)
            .SelectMany(m => m.FixedAgentIds.Select(agent => new object[] { m.MissionId, agent }));

    [Theory]
    [MemberData(nameof(Missions))]
    public async Task EveryDataCenterMissionUsesItsNativeAssignmentAndPaysOnceAcrossCheckpoint(int missionId, long agentId)
    {
        using MissionFixture f = new(live: true);
        var agent = Directory(f).Agent(agentId)!;
        Assert.NotNull(agent.Space);
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var native = catalog.Require(CatalogData.Client.Missions, (long)missionId).CourierMission!;
        var assignment = catalog.Require(CatalogData.Client.Npccharacters, agentId).Agent!;
        Assert.Equal(missionId, Assert.Single(assignment.StoryLineMissions!.Value));
        Assert.Throws<InvalidDataException>(() => f.Request(agentId));
        Prepare(f, agent);
        Assert.True(f.Missions.Access(MissionFixture.PilotId, agentId).Allowed);
        var offer = f.Request(agentId);
        Assert.Equal(missionId, offer.MissionId);
        Assert.Equal(offer.Id, f.Request(agentId).Id);
        Assert.Equal(assignment.AgentLevel, offer.Level);
        Assert.Equal(MissionKind.Trade, offer.Kind);
        Assert.True(offer.Storyline);
        Assert.Null(offer.ItemReward);
        Assert.Equal(0, offer.Rewards.LoyaltyPoints);
        Assert.Equal(native.ObjectiveTypeID, offer.Retrieval!.TypeId);
        Assert.Equal(native.ObjectiveQuantity, offer.Retrieval!.Quantity);
        Assert.Null(offer.Cargo);
        Assert.Null(offer.DungeonId);
        Assert.True(f.Missions.Information(MissionFixture.PilotId, agentId).MissionService);
        Assert.False(f.Missions.Information(MissionFixture.PilotId, agentId).MissionAvailable);
        offer = f.Accept(offer);
        Assert.False(offer.ObjectiveComplete);
        Assert.Throws<InvalidDataException>(() => Complete(f, offer));
        Assert.Empty(Live(f).Economy.Inspect(world => world.Set<OwnedItem>().Where(i => i.TypeId == offer.Retrieval!.TypeId).ToArray()));
        var point = BlueRead.Fields(MissionFixture.Reply(f.Call($"@agent:{agentId}", "GetAgentLocationWrap", [])));
        Assert.Equal(agentId, point["locationID"]);
        Assert.Equal((long)agent.Space!.TypeId, point["typeID"]);
        Assert.Equal((long)agent.Space.SystemId, point["solarsystemID"]);
        var objectives = BlueRead.Fields(MissionFixture.Reply(f.Call($"@agent:{agentId}", "GetMissionObjectiveInfo", [])));
        Assert.Empty(Assert.IsType<List<object?>>(objectives["agentGift"]));
        Assert.Empty(Assert.IsType<List<object?>>(objectives["dungeons"]));
        Assert.Equal(
            "fetch",
            BlueMarshal.Text(Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(objectives["objectives"])))[0])
        );
        AddTags(f, offer, offer.Retrieval!.Quantity + 1);
        var ready = f.Missions.Current(MissionFixture.PilotId, agentId)!;
        var tracker = MissionTrackingRules.Read(new(ready, []), agent.Space.SystemId, null, agent.Space.Position);
        Assert.Equal(MissionObjectiveKind.Complete, tracker.Current.Kind);
        long balance = Live(f).WalletBalance(MissionFixture.PilotId);
        var completed = Complete(f, offer).Offer!;
        Assert.Equal(MissionState.Completed, completed.State);
        Assert.Equal(balance + offer.Rewards.IskCents + offer.Bonus!.IskCents, Live(f).WalletBalance(MissionFixture.PilotId));
        Assert.Equal(1, Live(f).Economy.Inspect(world => world.Set<OwnedItem>().Single(i => i.TypeId == offer.Retrieval!.TypeId).Quantity));
        Assert.Throws<InvalidDataException>(() => Complete(f, offer));
        Assert.Throws<InvalidDataException>(() => f.Request(agentId));
        Assert.False(f.Missions.Access(MissionFixture.PilotId, agentId).Allowed);
        Assert.False(f.Missions.Information(MissionFixture.PilotId, agentId).MissionAvailable);
        Assert.Empty(Live(f).Economy.Inspect(world => world.Set<SavedMissionProgress>().ToArray()));
        Assert.True(
            Live(f).Economy.Inspect(world => world.Set<SavedNpcStanding>().Find(MissionFixture.PilotId, agent.FactionId!.Value)!.Value > 0)
        );
        await Live(f).FlushAsync();
        f.StopResident();
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock, liveWorld: true);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        Assert.Equal(MissionState.Completed, missions.Current(MissionFixture.PilotId, agentId)!.State);
        Assert.Throws<InvalidDataException>(() => missions.Act(MissionFixture.PilotId, agentId, MissionAction.Request));
    }

    [Fact]
    public void AllFiftyDefinitionsAndFiftySevenNativeAgentsHaveDistinctUsableContactsAndNoOrdinaryOffers()
    {
        using MissionFixture f = new(live: true);
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var definitions = Content().Missions.Where(m => !m.FixedAgentIds.IsEmpty).ToArray();
        Assert.Equal(50, definitions.Length);
        var ids = definitions.SelectMany(m => m.FixedAgentIds).ToArray();
        Assert.Equal(57, ids.Distinct().Count());
        foreach (long id in ids)
        {
            var agent = Directory(f).Agent(id)!;
            Assert.Null(agent.StationId);
            var space = agent.Space!;
            var objects = SystemObjects.Geometry(catalog, space.SystemId);
            var contact = Assert.Single(objects, body => body.AgentId == id);
            Assert.Equal(space.Position, contact.Position);
            Assert.False(contact.IsGlobal);
            Assert.False(contact.Solid);
            var slim = BlueRead.Fields(
                Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(new StaticSlimItem(contact)))).State
            );
            Assert.Equal(id, slim["charID"]);
            Assert.DoesNotContain(MissionCatalog.ForLevel(catalog, agent.Level, true), p => p.Definition.FixedAgentIds.Contains(id));
        }
    }

    private static INpcDirectory Directory(MissionFixture f) => f.Services.GetRequiredService<INpcDirectory>();

    private static LiveWorld Live(MissionFixture f) => LiveWorld.Find(f.Db.Path)!;

    private static MissionUpdate Complete(MissionFixture f, MissionOffer offer) =>
        f.Missions.Act(MissionFixture.PilotId, offer.Agent.Id, MissionAction.Complete, offer.Id);

    private static void Prepare(MissionFixture f, CatalogAgent agent)
    {
        using (var action = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId])))
        {
            action.Add(
                new SavedNpcStanding
                {
                    PilotId = MissionFixture.PilotId,
                    SourceId = agent.Id,
                    Value = 8,
                }
            );
            action.Commit();
        }
        Move(f, agent.Space!.SystemId, agent.Space.Position);
    }

    private static void Move(MissionFixture f, int systemId, SpacePosition position)
    {
        using var action = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId]));
        long ship = action.Pilots.Find(MissionFixture.PilotId)!.ActiveShipId;
        var row = action.Positions.Find(ship) ?? action.Add(new FlightPosition { ShipId = ship }).Entity;
        (row.SystemId, row.X, row.Y, row.Z) = (systemId, position.X, position.Y, position.Z);
        action.Commit();
    }

    private static void AddTags(MissionFixture f, MissionOffer offer, int quantity, long? location = null, long? owner = null)
    {
        using var action = Live(f).Economy.Begin(new(pilots: [MissionFixture.PilotId]));
        action.Add(
            new OwnedItem
            {
                Id = action.NextItemId(),
                OwnerId = owner ?? MissionFixture.PilotId,
                TypeId = offer.Retrieval!.TypeId,
                LocationId = location ?? action.Pilots.Find(MissionFixture.PilotId)!.ActiveShipId,
                FlagId = location is null ? (int)InventoryFlag.Cargo : (int)InventoryFlag.Hangar,
                Quantity = quantity,
            }
        );
        action.Commit();
    }
}
