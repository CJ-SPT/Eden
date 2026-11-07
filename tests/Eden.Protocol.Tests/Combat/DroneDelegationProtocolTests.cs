using Eden.Data.World.Drones;
using Eden.Gameplay.Drones;
using Eden.Protocol.Drones;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class DroneDelegationTests
{
    [Theory]
    [InlineData("CmdAssist", "assist")]
    [InlineData("CmdGuard", "guard")]
    public void NativeCommandsKeepControlAndPublishActivityAfterStateIncludingSnapshotReplay(string method, string activity)
    {
        using Fixture fixture = new(true);
        using var scope = fixture.Base.Base.Db.Services.CreateScope();
        using var context = DroneSynchronizationTests.Context(fixture.Base, scope.ServiceProvider, 1000001);
        context.Flight.Snapshot();
        context.Flight.Updates.Clear();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var selected = fixture.Ids.Cast<object?>().ToList();
        var reply = router.Call(context, "entity", method, new object?[] { Member, selected }, _ => { });
        Assert.IsType<DroneCommandReply>(Assert.IsType<PilotCallResult.Success>(reply).Reply);
        var changed = context.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.DroneActivityChanged>().ToArray();
        Assert.Equal(5, changed.Length);
        foreach (var command in changed)
        {
            var wire = Assert.IsType<object?[]>(command.ToBlue());
            Assert.Equal("OnDroneActivityChange", wire[0]);
            Assert.Equal(new object?[] { command.DroneId, Member, activity }, Assert.IsType<object?[]>(wire[1]));
        }
        Assert.All(
            context.Flight.VisibleDrones,
            d =>
            {
                Assert.Equal(fixture.Base.Ship, d.ShipId);
                Assert.Equal(fixture.Base.Pilot, d.PilotId);
            }
        );
        var before = fixture.Contacts();
        Assert.IsType<PilotCallResult.Failure>(router.Call(context, "entity", method, new object?[] { selected, Member }, _ => { }));
        Assert.IsType<PilotCallResult.Failure>(router.Call(context, "entity", method, new object?[] { Member, selected, 0L }, _ => { }));
        Assert.Equal(before, fixture.Contacts());
        context.Flight.Updates.Clear();
        context.Flight.RefreshDrones();
        Assert.Empty(context.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.DroneActivityChanged>());
        context.Flight.Snapshot();
        var snapshot = context.Flight.Updates.Select(u => u.Command).ToArray();
        int stateIndex = Array.FindIndex(snapshot, command => command is DestinyCommand.SetState);
        int activityIndex = Array.FindIndex(snapshot, command => command is DestinyCommand.DroneActivityChanged);
        Assert.True(stateIndex >= 0 && activityIndex > stateIndex);
        Assert.Equal(5, snapshot.OfType<DestinyCommand.DroneActivityChanged>().Count());
        context.Flight.Updates.Clear();
        Assert.IsType<PilotCallResult.Success>(router.Call(context, "entity", "CmdReturnHome", new object?[] { selected }, _ => { }));
        var cleared = context.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.DroneActivityChanged>().ToArray();
        Assert.Equal(5, cleared.Length);
        Assert.All(cleared, command => Assert.Null(command.Delegation));
        Assert.All(fixture.Contacts(), drone => Assert.Null(drone.Delegation));
    }

    [Theory]
    [InlineData(false, DroneOrder.Engage)]
    [InlineData(true, DroneOrder.Engage)]
    [InlineData(false, DroneOrder.Orbit)]
    [InlineData(true, DroneOrder.Orbit)]
    [InlineData(false, DroneOrder.ReturnToBay)]
    [InlineData(true, DroneOrder.ReturnToBay)]
    [InlineData(false, DroneOrder.Abandon)]
    [InlineData(true, DroneOrder.Abandon)]
    public void DirectOrdersClearDelegationWithoutResettingThePaidShotDeadline(bool resident, DroneOrder order)
    {
        using Fixture fixture = new(resident);
        fixture.Assign(DroneDelegationMode.Assist);
        fixture.Fire();
        Assert.Equal(5, fixture.Base.Tick().Hits.Count);
        using (WorldDbContext world = new(fixture.Base.Base.Db.Path))
        {
            Assert.All(world.Set<WorldDrone>(), drone => Assert.True(drone.ReadyAt > fixture.Base.Now));
        }
        long[] ready;
        using (WorldDbContext world = new(fixture.Base.Base.Db.Path))
        {
            ready = world.Set<WorldDrone>().OrderBy(d => d.ItemId).Select(d => d.ReadyAt).ToArray();
        }
        fixture.Base.Drones.Order(
            fixture.Base.Pilot,
            fixture.Ids,
            order,
            order == DroneOrder.Engage ? fixture.Base.Npc.Id : null,
            new HashSet<long> { fixture.Base.Npc.Id },
            fixture.Base.Now
        );
        Assert.All(fixture.Contacts(), drone => Assert.Null(drone.Delegation));
        using WorldDbContext saved = new(fixture.Base.Base.Db.Path);
        Assert.Equal(ready, saved.Set<WorldDrone>().OrderBy(d => d.ItemId).Select(d => d.ReadyAt));
        Assert.Empty(fixture.Base.Drones.Advance(fixture.Base.Pilot, fixture.Base.Now, 1).Hits);
    }
}
