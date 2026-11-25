using Eden.Data.World.Skills;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class CooperativeFleetTests
{
    [Fact]
    public void NativeFleetCompositionProvidesCommandDetailsAndMapLocationsToCurrentMembers()
    {
        using TestWorld world = new(liveWorld: true);
        using FleetClient boss = new(world, 1000001);
        using FleetClient member = new(world, 1000002);
        long fleet = Join(boss, member);
        using (var action = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None))
        {
            action.Add(
                new PilotSkill
                {
                    PilotId = member.PilotId,
                    TypeId = (int)FleetLeadershipSkill.Leadership,
                    Points = TrainedSkill.PointsForLevel(1, 3),
                }
            );
            action.Commit();
        }

        var commandView = Composition(boss);
        var memberRow = commandView[member.PilotId];
        Assert.Equal((long)member.Context.Pilot.SystemId, memberRow["solarSystemID"]);
        Assert.Equal(member.Context.Pilot.DockedLocationId, memberRow["stationID"]);
        Assert.Equal((long)member.Context.Pilot.ShipTypeId, memberRow["shipTypeID"]);
        Assert.Equal(new object?[] { 3L, 0L, 0L }, Assert.IsType<List<object?>>(memberRow["skills"]));
        Assert.Equal(new object?[] { 3348L, 11574L, 24764L }, Assert.IsType<List<object?>>(memberRow["skillIDs"]));

        using (var metrics = SessionPerformance.Begin("fleet composition lookup", _ => { }, force: true)!)
        {
            var memberView = Composition(member);
            Assert.Equal((long)boss.Context.Pilot.SystemId, memberView[boss.PilotId]["solarSystemID"]);
            Assert.Null(memberView[boss.PilotId]["stationID"]);
            Assert.Null(memberView[boss.PilotId]["shipTypeID"]);
            Assert.Empty(Assert.IsType<List<object?>>(memberView[boss.PilotId]["skills"]));
            Assert.Equal(new object?[] { 3L, 0L, 0L }, Assert.IsType<List<object?>>(memberView[member.PilotId]["skills"]));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }

        Assert.Null(member.ServiceReply("ship", "Undock", [member.Context.Pilot.ShipId]));
        member.Context.Flight.Snapshot();
        member.Advance();
        Assert.Null(Composition(boss)[member.PilotId]["stationID"]);
        Assert.Null(member.Reply("LeaveFleet", []));
        Assert.Single(Composition(boss));
        Assert.IsType<PilotCallResult.Failure>(member.CallBound(fleet, "GetFleetComposition", []));
    }

    private static Dictionary<long, Dictionary<string, object?>> Composition(FleetClient client) =>
        Assert
            .IsType<List<object?>>(client.Reply("GetFleetComposition", []))
            .Select(entry => BlueRead.Fields(Assert.IsType<BlueInstance>(entry).State))
            .ToDictionary(fields => Assert.IsType<long>(fields["characterID"]), fields => fields);
}
