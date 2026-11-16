using Eden.Data.World.Structures;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureDeploymentDefaultsTests
{
    [Fact]
    public void NativeFirstBoundReinforcementDefaultsRoundTripIntoResidentDeploymentAndCheckpoint()
    {
        using StructureTests.Fixture fixture = new(liveWorld: true);
        using var scope = fixture.Db.Services.CreateScope();
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        object?[] first = ["GetStructureReinforceDefault", Array.Empty<object?>()];
        var result = Assert.IsType<PilotCallResult.Success>(
            router.Call(context, "corpRegistry", "MachoBindObject", new object?[] { (long)fixture.CorporationId, first }, _ => { })
        );
        var reply = Assert.IsType<BoundPilotReply>(result.Reply);
        var defaults = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply)));
        Assert.Equal(new object?[] { 255L, 18L }, defaults);
        Assert.Empty(fixture.Structures.List(fixture.PilotId));
        Assert.Empty(context.Notifications);
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(context, "corpRegistry", "GetStructureReinforceDefault", new object?[] { 1L }, _ => { })
        );
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(
                context,
                "corpRegistry",
                "GetStructureReinforceDefault",
                Array.Empty<object?>(),
                _ => { },
                new() { ["unexpected"] = true }
            )
        );

        long item = fixture.Item(35832);
        object?[] anchor = [item, 0d, 0d, 0d, (long)fixture.CorporationId, "Native defaults", "", defaults[0], defaults[1], null];
        Assert.IsType<PilotCallResult.Success>(router.Call(context, "structureDeployment", "Anchor", anchor, _ => { }));
        var deployed = Assert.Single(fixture.Structures.List(fixture.PilotId));
        Assert.Equal(255, deployed.ReinforceWeekday);
        Assert.Equal(18, deployed.ReinforceHour);
        Assert.DoesNotContain(
            fixture.Db.Services.GetRequiredService<IPilotInventory>().List(fixture.PilotId, fixture.ShipId),
            entry => entry.Id == item
        );
        using var checkpoint = Assert.IsType<WorldSnapshot>(LiveWorld.Find(fixture.Db.Path)!.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        var saved = Assert.Single(reopened.Set<SavedStructure>());
        Assert.Equal(255, saved.ReinforceWeekday);
        Assert.Equal(18, saved.ReinforceHour);
    }

    [Fact]
    public void InvalidReinforcementTimingPreservesThePackagedHull()
    {
        using StructureTests.Fixture fixture = new(liveWorld: true);
        long item = fixture.Item(35832);
        foreach (int weekday in new[] { -1, 7, 254, 256 })
        {
            Assert.Throws<InvalidDataException>(() =>
                fixture.Structures.Deploy(
                    fixture.PilotId,
                    new(item, StructureTests.Fixture.Point, 0, "Invalid timing", ReinforceWeekday: weekday)
                )
            );
        }
        foreach (int hour in new[] { -1, 24 })
        {
            Assert.Throws<InvalidDataException>(() =>
                fixture.Structures.Deploy(
                    fixture.PilotId,
                    new(item, StructureTests.Fixture.Point, 0, "Invalid timing", ReinforceWeekday: 255, ReinforceHour: hour)
                )
            );
        }
        Assert.Empty(fixture.Structures.List(fixture.PilotId));
        var hull = Assert.Single(
            fixture.Db.Services.GetRequiredService<IPilotInventory>().List(fixture.PilotId, fixture.ShipId),
            entry => entry.Id == item
        );
        Assert.Equal(1, hull.Quantity);
    }
}
