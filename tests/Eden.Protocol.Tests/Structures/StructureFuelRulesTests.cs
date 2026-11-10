using Eden.Gameplay.Structures;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureFuelRulesTests
{
    [Theory]
    [InlineData(40, 38, 3, 3)]
    [InlineData(9, 9, 2, 1)]
    [InlineData(0, 0, 1, 1)]
    public void SharedFuelSettlesInDeadlineAndSlotOrderRatherThanStarvingTheLastService(
        int available,
        int consumed,
        int firstOfflineHour,
        int secondOfflineHour
    )
    {
        long hour = TimeSpan.TicksPerHour;
        var plan = StructureFuelRules.Advance([new(1, 164, 1136, 9, hour), new(2, 165, 1136, 10, hour)], [new(1136, available)], 5 * hour);
        Assert.Equal(consumed, plan.Charges.Sum(c => c.Quantity));
        Assert.Equal(firstOfflineHour * hour, plan.Deadlines.Single(d => d.ItemId == 1).OfflineAt);
        Assert.Equal(secondOfflineHour * hour, plan.Deadlines.Single(d => d.ItemId == 2).OfflineAt);
        Assert.All(plan.Deadlines, d => Assert.Null(d.NextFuelAt));
    }

    [Fact]
    public void OffsetDeadlinesAndDifferentFuelGroupsRemainIndependent()
    {
        long hour = TimeSpan.TicksPerHour;
        var plan = StructureFuelRules.Advance(
            [new(1, 164, 10, 9, hour), new(2, 165, 10, 10, hour / 2), new(3, 166, 20, 2, hour)],
            [new(10, 20), new(20, 10)],
            2 * hour
        );
        Assert.Equal(19, plan.Charges.Single(c => c.GroupId == 10).Quantity);
        Assert.Equal(4, plan.Charges.Single(c => c.GroupId == 20).Quantity);
        Assert.Equal(2 * hour, plan.Deadlines.Single(d => d.ItemId == 1).OfflineAt);
        Assert.Equal(3 * hour / 2, plan.Deadlines.Single(d => d.ItemId == 2).OfflineAt);
        Assert.Equal(3 * hour, plan.Deadlines.Single(d => d.ItemId == 3).NextFuelAt);
    }

    [Fact]
    public void FreeServiceSkipsElapsedYearsAndNegativeConsumptionIsRejected()
    {
        long until = TimeSpan.FromDays(3650).Ticks;
        var plan = StructureFuelRules.Advance([new(1, 164, 0, 0, 1)], [], until);
        Assert.Empty(plan.Charges);
        Assert.Equal(until + 1, Assert.Single(plan.Deadlines).NextFuelAt);
        Assert.Null(Assert.Single(plan.Deadlines).OfflineAt);
        Assert.Throws<InvalidDataException>(() => StructureFuelRules.Advance([new(1, 164, 0, -1, 1)], [], until));
    }
}
