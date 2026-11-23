using Eden.Data.World.Agents;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Server;
using Eden.World.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class EpicArcDeliveryTests
{
    private const int MissionId = 14128;
    private const int SystemId = 30000142;
    private const int SupplyType = 43;
    private const long AgentId = 3019358;
    private const long PilotId = MissionFixture.PilotId;

    [Fact]
    public async Task DeliveringSuppliesConsumesExactlyOneItemAndRetainsTheReceiptAcrossCheckpoint()
    {
        using MissionFixture fixture = new(
            live: true,
            configure: catalog =>
            {
                MissionFixture.Agent(catalog, AgentId, AgentType.EpicArc, 1, MissionRules.SecurityDivision);
                // Synthetic two-object room. Only the IDs used by the author binding match the pinned client.
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Radius) VALUES (10124,'Test entry',12,1),(30783,'Test depot',12,4);
                    INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (43,'Test supplies',12);
                    """
                );
                string type = """{"volume":0.2}""";
                string stage = """
                    {"contentTemplate":"agent.missionTemplatizedContent_EpicArcKillMission","killMission":{"dungeonID":3651}}
                    """;
                string room = """
                    {"entryObjectID":937801,"rooms":{"21428":{"objects":{
                        "937801":{"objectID":937801,"typeID":10124,"position":{"x":10,"y":20,"z":30}},
                        "939749":{"objectID":939749,"typeID":30783,"position":{"x":7010,"y":20,"z":30}}}}}}
                    """;
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','43',{type})");
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','30783',{type})");
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/missions','14128',{stage})");
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/dungeons','3651',{room})");
            }
        );
        var live = LiveWorld.Find(fixture.Db.Path)!;
        long ship;
        long cargo;
        using (var world = live.Economy.Begin(new(pilots: [PilotId])))
        {
            ship = world.Pilots.Find(PilotId)!.ActiveShipId;
            world.Add(
                new SavedMission
                {
                    PilotId = PilotId,
                    AgentId = AgentId,
                    MissionId = MissionId,
                    Level = 1,
                    State = MissionState.Accepted,
                    OfferedAt = fixture.Clock.Now,
                    AcceptedAt = fixture.Clock.Now,
                    ExpiresAt = long.MaxValue,
                    DestinationStation = MissionFixture.StationId,
                    SystemId = SystemId,
                    X = 10_000_000,
                }
            );
            cargo = world.NextItemId();
            world.Add(
                new OwnedItem
                {
                    Id = cargo,
                    OwnerId = PilotId,
                    LocationId = ship,
                    FlagId = (int)InventoryFlag.Cargo,
                    TypeId = SupplyType,
                    Quantity = 2,
                }
            );
            world.Commit();
        }
        var site = Assert.Single(fixture.Missions.Sites(SystemId));
        var container = Assert.Single(site.Objects, body => MissionContainerRules.IsContainer(body.Id));
        string service = $"@missioncontainer:{container.Id}";
        var oneUnit = new Dictionary<string, object?> { ["qty"] = 1L };
        Move(fixture, ship, container.Position + new SpacePosition(9000, 0, 0));
        Assert.IsType<PilotCallResult.Failure>(fixture.Call(service, "Add", [cargo, ship], oneUnit));

        Move(fixture, ship, container.Position);
        Assert.Empty(fixture.Services.GetRequiredService<IPilotMissionContainers>().Read(PilotId, container.Id).Items);
        var binding = fixture.Call("invbroker", "GetInventoryFromId", [container.Id]);
        Assert.True(binding is PilotCallResult.Success, binding.ToString());
        Assert.IsType<PilotCallResult.Failure>(fixture.Call(service, "Add", [cargo, ship]));
        Assert.False(fixture.Missions.Current(PilotId, AgentId)!.ObjectiveComplete);
        Assert.Null(MissionFixture.Reply(fixture.Call(service, "Add", [cargo, ship], oneUnit)));
        Assert.True(fixture.Missions.Current(PilotId, AgentId)!.ObjectiveComplete);
        Assert.Contains(fixture.Context.Notifications, message => message is ItemChanged);
        Assert.IsType<PilotCallResult.Failure>(fixture.Call(service, "Add", [cargo, ship]));

        long receipt = live.Economy.Inspect(world => Assert.Single(world.Filter<OwnedItem>(item => item.LocationId == container.Id)).Id);
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("@ship", "Add", [receipt, container.Id]));
        await live.FlushAsync();
        fixture.StopResident();
        using var reopened = ServerServices.Build(new(fixture.Db.Path), fixture.Clock, liveWorld: true);
        var inventory = reopened.GetRequiredService<IPilotInventory>();
        Assert.Equal(1, Assert.Single(inventory.List(PilotId, ship), item => item.Id == cargo).Quantity);
        var containers = reopened.GetRequiredService<IPilotMissionContainers>();
        Assert.Empty(containers.Read(PilotId, container.Id).Items);
        Assert.Throws<InvalidDataException>(() =>
            containers.Deposit(PilotId, ship, container.Id, new([cargo], ship, (int)InventoryFlag.Cargo, 1))
        );
        Assert.True(reopened.GetRequiredService<IPilotMissions>().Current(PilotId, AgentId)!.ObjectiveComplete);
    }

    private static void Move(MissionFixture fixture, long ship, SpacePosition position)
    {
        fixture.Db.Store.SetPosition(ship, position, SystemId);
        fixture.Context.ChangeShip(fixture.Db.Store.FindPilot(1000001)!);
        fixture.Context.Flight.Snapshot();
    }
}
