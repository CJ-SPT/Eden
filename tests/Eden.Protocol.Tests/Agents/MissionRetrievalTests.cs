using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Agents;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Inventory;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionRetrievalTests
{
    private const int SpyStash = 1770;
    private const int ReportType = 22222;

    [Theory]
    [InlineData(2503, false, true)]
    [InlineData(2503.001, false, false)]
    [InlineData(0, true, false)]
    public void ContainerAccessRulesUseSurfaceDistanceAndGuardState(double distance, bool guarded, bool allowed)
    {
        var container = new StaticSpaceObject(1, 22223, 306, (int)InventoryCategory.Entity, 1000044, SpacePosition.Zero, 1);
        void Validate() => MissionContainerRules.Validate(new(distance, 0, 0), 2, container, guarded);
        if (allowed)
        {
            Validate();
        }
        else
        {
            Assert.Throws<InvalidDataException>(Validate);
        }
    }

    [Fact]
    public void ContainerRejectsWrongOwnerShipSystemRangeAndInvalidTransfersWithoutChangingContents()
    {
        using MissionFixture f = new(3, MissionRules.SecurityDivision);
        var mission = Stash(f);
        var container = Container(f, mission);
        var inventory = f.Services.GetRequiredService<IPilotMissionContainers>();
        Assert.Equal("Guristas Officer's Quarters", container.Name);
        var slim = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<BlueInstance>(MissionFixture.Reply(new StaticSlimItem(container))).State
        );
        Assert.Equal(-11d, slim["hostile_response_threshold"]);
        Assert.Equal(11d, slim["friendly_response_threshold"]);
        Assert.Equal("Guristas Officer's Quarters", slim["name"]);
        Clear(f, mission);
        Move(f, mission.SystemId, container.Position);
        var reports = Assert.Single(inventory.Read(MissionFixture.PilotId, container.Id).Items);
        long ship = f.Context.Pilot.ShipId;
        Assert.Throws<InvalidDataException>(() => inventory.Read(90000002, container.Id));
        foreach (
            var request in new InventoryMoveRequest[]
            {
                new([reports.Id, reports.Id], container.Id, (int)InventoryFlag.Cargo, null),
                new([reports.Id], container.Id, (int)InventoryFlag.Cargo, 0),
                new([reports.Id], container.Id, (int)InventoryFlag.Cargo, 6),
                new([reports.Id + 100], container.Id, (int)InventoryFlag.Cargo, null),
                new([reports.Id], container.Id, (int)InventoryFlag.Hangar, null),
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => inventory.Take(MissionFixture.PilotId, ship, request));
            Assert.Equal(reports, Assert.Single(inventory.Read(MissionFixture.PilotId, container.Id).Items));
        }
        Assert.Throws<InvalidDataException>(() =>
            inventory.Take(MissionFixture.PilotId, ship + 1, new([reports.Id], container.Id, (int)InventoryFlag.Cargo, null))
        );
        double edge = MissionContainerRules.TransferRange + container.Radius + f.Context.Pilot.Flight.ShipRadius;
        Move(f, mission.SystemId, container.Position + new SpacePosition(edge + 1, 0, 0));
        Assert.Throws<InvalidDataException>(() => inventory.Read(MissionFixture.PilotId, container.Id));
        Move(f, mission.SystemId, container.Position + new SpacePosition(edge, 0, 0));
        Assert.Equal(reports, Assert.Single(inventory.Read(MissionFixture.PilotId, container.Id).Items));
        Move(f, 30000144, container.Position);
        Assert.Throws<InvalidDataException>(() => inventory.Read(MissionFixture.PilotId, container.Id));
        f.Dock(MissionFixture.StationId);
        Assert.Throws<InvalidDataException>(() => inventory.Read(MissionFixture.PilotId, container.Id));
    }

    [Fact]
    public void ContainerCapacityAndWarpRejectionsPreserveReportsAndRejectOpeningDuringWarp()
    {
        using MissionFixture f = new(3, MissionRules.SecurityDivision);
        var mission = Stash(f);
        var container = Container(f, mission);
        Clear(f, mission);
        Move(f, mission.SystemId, container.Position);
        var inventory = f.Services.GetRequiredService<IPilotMissionContainers>();
        var reports = Assert.Single(inventory.Read(MissionFixture.PilotId, container.Id).Items);
        double capacity = f.Services.GetRequiredService<IPilotFitting>().ShipAttributes(MissionFixture.PilotId)[
            DogmaAttributeId.CargoCapacity
        ];
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = MissionFixture.PilotId,
                    TypeId = ReportType,
                    LocationId = f.Context.Pilot.ShipId,
                    FlagId = (int)InventoryFlag.Cargo,
                    Quantity = checked((int)(capacity / 0.1)),
                }
            );
            world.SaveChanges();
        }
        Assert.IsType<PilotCallResult.NamedFailure>(f.Call("@ship", "Add", [reports.Id, container.Id]));
        Assert.Equal(reports, Assert.Single(inventory.Read(MissionFixture.PilotId, container.Id).Items));
        Assert.Null(f.Context.Flight.CallWire("beyonce", "CmdWarpToStuff", ["item", (long)MissionFixture.StationId]));
        for (int tick = 0; tick < 30 && !f.Context.Flight.InWarp; tick++)
        {
            f.Clock.Seconds++;
            f.Context.Flight.Checkpoint();
        }
        Assert.True(f.Context.Flight.InWarp);
        Assert.IsType<PilotCallResult.Failure>(f.Call("@ship", "Add", [reports.Id, container.Id]));
        Assert.IsType<PilotCallResult.Failure>(f.Call("invbroker", "GetInventoryFromId", [container.Id]));
        Assert.Equal(reports, Assert.Single(inventory.Read(MissionFixture.PilotId, container.Id).Items));
    }

    [Fact]
    public void FailedRetrievalPayoutRollsBackConsumedReportsAndContainerCleanup()
    {
        using MissionFixture f = new(3, MissionRules.SecurityDivision);
        var mission = Stash(f);
        var container = Container(f, mission);
        Clear(f, mission);
        Move(f, mission.SystemId, container.Position);
        var inventory = f.Services.GetRequiredService<IPilotMissionContainers>();
        var reports = Assert.Single(inventory.Read(MissionFixture.PilotId, container.Id).Items);
        inventory.Take(MissionFixture.PilotId, f.Context.Pilot.ShipId, new([reports.Id], container.Id, (int)InventoryFlag.Cargo, null));
        f.Dock(MissionFixture.StationId);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER Reject_Retrieval_Payout BEFORE INSERT ON WorldStandingTransaction
                    BEGIN SELECT RAISE(ABORT,'injected retrieval payout failure'); END;
                """
            );
        }
        Assert.Throws<DbUpdateException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, mission.Id)
        );
        using WorldDbContext check = new(f.Db.Path);
        Assert.Equal(5, check.Set<OwnedItem>().Find(reports.Id)!.Quantity);
        Assert.Equal(MissionState.Accepted, check.Set<SavedMission>().Find(mission.Id)!.State);
        Assert.Empty(check.Set<SavedMarketPayment>());
        Assert.Empty(check.Set<SavedLoyaltyBalance>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpyStashRetrievalUsesNativeContainerInventoryPersistsAndConsumesReportsOnce(bool resident)
    {
        using MissionFixture f = new(3, MissionRules.SecurityDivision, live: resident);
        var mission = Stash(f);
        var container = Container(f, mission);
        var inventory = f.Services.GetRequiredService<IPilotMissionContainers>();
        var initial = Assert.IsType<MissionRetrieval>(mission.Retrieval);
        Assert.Equal(ReportType, initial.TypeId);
        Assert.Equal(5, initial.Quantity);
        Assert.Equal(0.1, initial.Volume);
        Assert.False(initial.HasCargo);
        Assert.Empty(f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.View).Items);
        Assert.DoesNotContain(
            f.Services.GetRequiredService<IPilotInventory>().List(MissionFixture.PilotId, MissionFixture.StationId),
            i => i.TypeId == ReportType
        );

        Move(f, mission.SystemId, container.Position);
        Assert.IsType<PilotCallResult.Failure>(f.Call("invbroker", "GetInventoryFromId", [container.Id]));
        Clear(f, mission);
        var cleared = Current(f);
        Assert.True(cleared.CombatComplete);
        Assert.False(cleared.ObjectiveComplete);
        Assert.False(cleared.Retrieval!.HasCargo);
        var contents = inventory.Read(MissionFixture.PilotId, container.Id);
        var reports = Assert.Single(contents.Items);
        Assert.Equal(5, reports.Quantity);
        Assert.Equal(ReportType, reports.TypeId);
        Assert.Equal(container.TypeId, contents.Container.TypeId);
        Assert.IsType<PilotCallResult.Success>(f.Call("invbroker", "GetInventoryFromId", [container.Id]));
        var listed = Assert.IsType<List<object?>>(
            WireAssertions.Materialize(
                Assert.IsType<PilotCallResult.Success>(f.Call($"@missioncontainer:{container.Id}", "List", [])).Reply
            )
        );
        Assert.Single(listed);
        Assert.Null(MissionFixture.Reply(f.Call("@ship", "Add", [reports.Id, container.Id], new() { ["qty"] = 2L })));
        Assert.False(Current(f).ObjectiveComplete);
        Assert.Equal(3, Assert.Single(inventory.Read(MissionFixture.PilotId, container.Id).Items).Quantity);
        Assert.NotEmpty(f.Context.Notifications.OfType<ItemChanged>());

        if (resident)
        {
            await LiveWorld.Find(f.Db.Path)!.FlushAsync();
            f.StopResident();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        f.Missions.Arrive(mission.SystemId, container.Position);
        Assert.Equal(container.Id, Container(f, mission).Id);
        Assert.Equal(3, Assert.Single(inventory.Read(MissionFixture.PilotId, container.Id).Items).Quantity);
        Move(f, mission.SystemId, container.Position);
        var remainingTake = f.Call("@ship", "MultiAdd", [new List<object?> { reports.Id }, container.Id]);
        Assert.True(remainingTake is PilotCallResult.Success, remainingTake.ToString());
        Assert.Null(MissionFixture.Reply(remainingTake));
        Assert.Empty(inventory.Read(MissionFixture.PilotId, container.Id).Items);
        Assert.True(Current(f).ObjectiveComplete);
        Assert.IsType<PilotCallResult.Failure>(f.Call("@ship", "Add", [reports.Id, container.Id]));
        Assert.Empty(inventory.Read(MissionFixture.PilotId, container.Id).Items);

        f.Dock(MissionFixture.StationId);
        var completed = f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, mission.Id);
        Assert.Equal(MissionState.Completed, completed.Offer!.State);
        Assert.True(completed.Offer.ObjectiveComplete);
        Assert.NotNull(completed.BalanceCents);
        Assert.Equal(5, completed.Items.Sum(i => i.Previous!.Quantity - i.Item.Quantity));
        Assert.All(completed.Items, i => Assert.Equal(ReportType, i.Item.TypeId));
        Assert.Throws<InvalidDataException>(() => inventory.Read(MissionFixture.PilotId, container.Id));
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, mission.Id)
        );
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<OwnedItem>().Where(i => i.TypeId == ReportType));
    }

    [Fact]
    public void RetrievalTurnInRequiresCurrentCargoAndAgentStationAndConsumesOnlyRequiredQuantity()
    {
        using MissionFixture f = new(3, MissionRules.SecurityDivision);
        var mission = Stash(f);
        var container = Container(f, mission);
        Clear(f, mission);
        Move(f, mission.SystemId, container.Position);
        var inventory = f.Services.GetRequiredService<IPilotMissionContainers>();
        long item = Assert.Single(inventory.Read(MissionFixture.PilotId, container.Id).Items).Id;
        inventory.Take(MissionFixture.PilotId, f.Context.Pilot.ShipId, new([item], container.Id, (int)InventoryFlag.Cargo, null));
        Assert.True(Current(f).ObjectiveComplete);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(item)!.LocationId = MissionFixture.DestinationId;
            world.Set<OwnedItem>().Find(item)!.FlagId = (int)InventoryFlag.Hangar;
            world.SaveChanges();
        }
        Assert.False(Current(f).ObjectiveComplete);
        f.Dock(MissionFixture.StationId);
        long balance;
        using (WorldDbContext world = new(f.Db.Path))
        {
            balance =
                world.Set<PilotWallet>().Where(w => w.PilotId == MissionFixture.PilotId).Select(w => (long?)w.Cents).SingleOrDefault() ?? 0;
        }
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, mission.Id)
        );
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(
                balance,
                world.Set<PilotWallet>().Where(w => w.PilotId == MissionFixture.PilotId).Select(w => (long?)w.Cents).SingleOrDefault() ?? 0
            );
            var reports = world.Set<OwnedItem>().Find(item)!;
            Assert.Equal(5, reports.Quantity);
            reports.LocationId = MissionFixture.StationId;
            reports.Quantity = 8;
            world.SaveChanges();
        }
        Assert.True(Current(f).ObjectiveComplete);
        f.Dock(MissionFixture.DestinationId);
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, mission.Id)
        );
        f.Dock(MissionFixture.StationId);
        f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, mission.Id);
        using WorldDbContext check = new(f.Db.Path);
        Assert.Equal(3, check.Set<OwnedItem>().Find(item)!.Quantity);
    }

    [Theory]
    [InlineData(MissionAction.Quit)]
    [InlineData(MissionAction.View)]
    public void ClosingOrExpiringRetrievalRemovesUnclaimedContentsAndRejectsStaleBindings(MissionAction action)
    {
        using MissionFixture f = new(3, MissionRules.SecurityDivision);
        var mission = Stash(f);
        var container = Container(f, mission);
        Clear(f, mission);
        Move(f, mission.SystemId, container.Position);
        var inventory = f.Services.GetRequiredService<IPilotMissionContainers>();
        var item = Assert.Single(inventory.Read(MissionFixture.PilotId, container.Id).Items);
        inventory.Take(MissionFixture.PilotId, f.Context.Pilot.ShipId, new([item.Id], container.Id, (int)InventoryFlag.Cargo, 1));
        if (action == MissionAction.View)
        {
            f.Clock.AdvanceWallTime(MissionRules.MissionLifetime);
        }
        f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, action, mission.Id);
        Assert.Throws<InvalidDataException>(() => inventory.Read(MissionFixture.PilotId, container.Id));
        Assert.IsType<PilotCallResult.Failure>(f.Call($"@missioncontainer:{container.Id}", "List", []));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<OwnedItem>().Where(i => i.LocationId == container.Id));
        Assert.Equal(1, world.Set<OwnedItem>().Where(i => i.TypeId == ReportType).Sum(i => i.Quantity));
    }

    [Fact]
    public void NativeFetchObjectiveIncludesReportQuantityDropoffAndSeparateGuardProgressWithoutAgentGift()
    {
        using MissionFixture f = new(3, MissionRules.SecurityDivision);
        var mission = Stash(f);
        var keywords = BlueRead.Fields(MissionFixture.Reply(new MissionKeywords(mission)));
        Assert.Equal((long)ReportType, keywords["objectiveTypeID"]);
        Assert.Equal(5L, keywords["objectiveQuantity"]);
        Assert.Equal((long)MissionFixture.StationId, keywords["objectiveDestinationID"]);
        foreach (bool hasCargo in new[] { false, true })
        {
            var offer = mission with
            {
                CombatComplete = true,
                ObjectiveComplete = hasCargo,
                Retrieval = mission.Retrieval! with { HasCargo = hasCargo },
            };
            var objective = BlueRead.Fields(MissionFixture.Reply(new MissionObjectives(offer)));
            var fetch = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<List<object?>>(objective["objectives"])));
            Assert.Equal("fetch", BlueMarshal.Text(fetch[0]));
            var values = Assert.IsType<object?[]>(fetch[1]);
            Assert.Equal(MissionFixture.AgentId, values[0]);
            Assert.Equal((long)MissionFixture.StationId, BlueRead.Fields(values[1])["locationID"]);
            var cargo = BlueRead.Fields(values[2]);
            Assert.Equal((long)ReportType, cargo["typeID"]);
            Assert.Equal(5L, cargo["quantity"]);
            Assert.Equal(0.5, cargo["volume"]);
            Assert.Equal(hasCargo, cargo["hasCargo"]);
            Assert.Empty(Assert.IsType<List<object?>>(objective["agentGift"]));
            Assert.Equal(1L, BlueRead.Fields(Assert.Single(Assert.IsType<List<object?>>(objective["dungeons"])))["completionStatus"]);
            Assert.Equal(hasCargo ? 1L : 0L, objective["completionStatus"]);
        }
    }

    private static MissionOffer Stash(MissionFixture f)
    {
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new SavedNpcStanding
                {
                    PilotId = MissionFixture.PilotId,
                    SourceId = 1000044,
                    Value = 8,
                }
            );
            world.SaveChanges();
        }
        for (int i = 0; i < 4; i++)
        {
            var offer = f.Accept(f.Request());
            if (offer.MissionId == SpyStash)
            {
                return offer;
            }
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Quit, offer.Id);
        }
        throw new InvalidOperationException("The retrieval mission is missing.");
    }

    private static MissionOffer Current(MissionFixture f) => f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!;

    private static StaticSpaceObject Container(MissionFixture f, MissionOffer mission) =>
        Assert.Single(
            f.Missions.Sites(mission.SystemId).Where(s => s.Id == mission.Id).SelectMany(s => s.Objects),
            o => MissionContainerRules.IsContainer(o.Id)
        );

    private static void Clear(MissionFixture f, MissionOffer mission)
    {
        var final = f.Missions.Sites(mission.SystemId).Single(s => s.Objects.Any(o => MissionContainerRules.IsContainer(o.Id)));
        f.Missions.Arrive(mission.SystemId, final.Position);
        using WorldDbContext world = new(f.Db.Path);
        var required = world.Set<SavedMissionMember>().Where(m => m.MissionId == mission.Id && m.Required).Select(m => m.NpcId).ToArray();
        Assert.Equal(5, required.Length);
        foreach (long id in required)
        {
            f.Kill(id);
        }
    }

    private static void Move(MissionFixture f, int system, SpacePosition position)
    {
        f.Db.Store.SetPosition(f.Context.Pilot.ShipId, position, system);
        f.Context.ChangeShip(f.Db.Store.FindPilot(1000001)!);
        f.Context.Flight.Snapshot();
    }
}
