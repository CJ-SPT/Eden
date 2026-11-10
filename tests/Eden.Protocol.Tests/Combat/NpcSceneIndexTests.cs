using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcSceneIndexTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DispersedQueriesMatchInclusiveDistanceAndSourceOrder(int axis)
    {
        const double origin = -100_000_000_000_000;
        double range = NpcEncounterDefinitions.VisibilityRange;
        SpacePosition Position(double value) =>
            axis switch
            {
                0 => new(origin + value, origin, origin),
                1 => new(origin, origin + value, origin),
                _ => new(origin, origin, origin + value),
            };
        var members = new[] { range, range + 1, -range, -range - 1, 0, 20 * range, -20 * range }
            .Select((distance, index) => new NpcSimulationContact(100 - index, Position(distance), index == 5))
            .ToArray();
        NpcSceneIndex scene = new(members);
        foreach (double offset in new[] { 0, range, -range, 20 * range, -20 * range })
        {
            var position = Position(offset);
            var expected = members.Where(n => position.DistanceTo(n.Position) <= range).Select(n => n.Id);
            Assert.Equal(expected, scene.Find(position));
        }
        Assert.Equal(new[] { members[5].Id }, scene.Warping);
    }

    [Fact]
    public void MixedCloudQueriesMatchTheLegacyScanAndKeepTheirInputSnapshot()
    {
        Random random = new(42);
        var members = Enumerable
            .Range(0, 2048)
            .Select(index => new NpcSimulationContact(
                index,
                new(random.Next(-2_000_000, 2_000_000), random.Next(-2_000_000, 2_000_000), random.Next(-2_000_000, 2_000_000)),
                index % 31 == 0
            ))
            .ToArray();
        NpcSceneIndex scene = new(members);
        for (int sample = 0; sample < 32; sample++)
        {
            var position = members[sample].Position;
            Assert.Equal(
                members.Where(n => position.DistanceTo(n.Position) <= NpcEncounterDefinitions.VisibilityRange).Select(n => n.Id),
                scene.Find(position)
            );
        }
        var first = scene.Members[0];
        members[0] = members[0] with { Position = new(double.NaN, 0, 0), Warping = false };
        Assert.Equal(first, scene.Members[0]);
        Assert.Contains(first.Id, scene.Find(first.Position));
        Assert.Contains(first.Id, scene.Warping);
    }

    [Fact]
    public void DenseEmptyAndNonfiniteInputsRetainTheDistanceRule()
    {
        var members = new[]
        {
            new NpcSimulationContact(1, SpacePosition.Zero, false),
            new NpcSimulationContact(2, new(double.PositiveInfinity, 0, 0), true),
            new NpcSimulationContact(3, new(1, 0, 0), false),
        };
        NpcSceneIndex scene = new(members);
        Assert.Equal(new long[] { 1, 3 }, scene.Find(SpacePosition.Zero));
        Assert.Empty(scene.Find(new(double.NaN, 0, 0)));
        Assert.Empty(new NpcSceneIndex([]).Find(SpacePosition.Zero));
        NpcSceneIndex dense = new([members[0], members[2]]);
        var visible = dense.Find(SpacePosition.Zero);
        Assert.Equal(new long[] { 1, 3 }, visible);
        Assert.Same(visible, dense.Find(new(1, 0, 0)));
    }
}
