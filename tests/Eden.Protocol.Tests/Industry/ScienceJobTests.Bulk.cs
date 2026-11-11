using Eden.Gameplay.Industry;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Industry;

public sealed partial class ScienceJobTests
{
    [Fact]
    public void OneBulkDeliveryCompletesManufacturingAndCopyingWithTheirNormalOutputs()
    {
        using var f = Create();
        var copy = f.Store.Install(f.Pilot.PilotId, Request(f, IndustryActivity.Copying, licensed: 2)).Job;
        var manufacturing = f
            .Store.Install(f.Pilot.PilotId, f.ModuleRequest(Eden.Protocol.Tests.Fixtures.IndustryBlueprintType.MinerI))
            .Job;
        long before = f.Quantity(manufacturing.ProductTypeId);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(Math.Max(copy.DurationSeconds, manufacturing.DurationSeconds)));
        var results = f.Store.DeliverMany(
            f.Pilot.PilotId,
            [new(copy.Id, copy.Facility.SystemId), new(manufacturing.Id, manufacturing.Facility.SystemId)]
        );
        Assert.All(results, result => Assert.Equal(IndustryJobStatus.Delivered, result.Job.Status));
        Assert.Equal(before + manufacturing.OutputQuantity, f.Quantity(manufacturing.ProductTypeId));
        Assert.Contains(
            f.Store.Blueprints(f.Pilot.PilotId),
            blueprint => blueprint.Item.TypeId == copy.BlueprintTypeId && blueprint.RunsRemaining == 2
        );
        Assert.All(
            f.Store.DeliverMany(
                f.Pilot.PilotId,
                [new(copy.Id, copy.Facility.SystemId), new(manufacturing.Id, manufacturing.Facility.SystemId)]
            ),
            result => Assert.Empty(result.Changes)
        );
    }
}
