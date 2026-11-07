using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Protocol.Tests.Fixtures;

namespace Eden.Protocol.Tests.Flight;

public sealed class CollisionIndexTests
{
    private static readonly FlightProperties Hull = new(0, 50, 1500100, 1, 1000, 0);

    [Fact]
    public void SweptIndexMatchesAllPairsAcrossRandomizedFleetsAndOriginalOrders()
    {
        Random random = new(3662168);
        for (int scene = 0; scene < 120; scene++)
        {
            double origin = (scene % 3) switch
            {
                0 => 0,
                1 => 1e10,
                _ => 1e15,
            };
            var ships = Enumerable
                .Range(1, 48)
                .Select(id =>
                {
                    SpacePosition start = new(
                        origin + random.Next(-2000, 2000),
                        origin + random.Next(-2000, 2000),
                        origin + random.Next(-2000, 2000)
                    );
                    SpacePosition velocity = new(random.Next(-5000, 5000), random.Next(-5000, 5000), random.Next(-5000, 5000));
                    var hull = Hull with
                    {
                        ShipRadius = random.Next(0, 500) / 3f,
                        Mass = 500000 * random.Next(1, 50),
                        Agility = random.Next(1, 20) / 2f,
                    };
                    return new CollisionMotion(
                        id,
                        hull,
                        start,
                        velocity,
                        start + velocity,
                        velocity,
                        Solid: id % 7 != 0,
                        Fixed: id % 5 == 0
                    );
                })
                .OrderBy(_ => random.Next())
                .ToArray();
            Assert.Equal(LegacyShipCollisions.Resolve(ships), ShipCollisions.Resolve(ships));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10000000000d)]
    [InlineData(1000000000000000d)]
    public void FastCrossingsTangencyAndOverlapsPreserveThePairResponse(double origin)
    {
        CollisionMotion[] ships =
        [
            new(1, Hull, new(origin - 10000, 0, 0), new(20000, 0, 0), new(origin + 10000, 0, 0), new(20000, 0, 0)),
            new(2, Hull, new(origin, 100, 0), SpacePosition.Zero, new(origin, 100, 0), SpacePosition.Zero, Fixed: true),
            new(3, Hull, new(origin + 10000, 0, 0), new(-20000, 0, 0), new(origin - 10000, 0, 0), new(-20000, 0, 0)),
            new(4, Hull, new(origin, 0, 0), SpacePosition.Zero, new(origin, 0, 0), SpacePosition.Zero),
            new(5, Hull, new(origin + 1, 0, 0), new(200, 0, 0), new(origin + 100, 0, 0), new(100, 0, 0)),
        ];
        foreach (var ordered in new[] { ships, ships.Reverse().ToArray() })
        {
            var expected = LegacyShipCollisions.Resolve(ordered);
            Assert.Contains(expected, r => r.Bumped);
            Assert.Equal(expected, ShipCollisions.Resolve(ordered));
        }
    }

    [Fact]
    public void SparseFourThousandShipGridAllocatesOnlyLinearWorkingState()
    {
        const int count = 4000;
        var ships = Enumerable
            .Range(0, count)
            .Select(index =>
            {
                SpacePosition position = new(index % 20 * 400, index / 20 % 20 * 400, index / 400 * 400);
                return new CollisionMotion(index, Hull, position, SpacePosition.Zero, position, SpacePosition.Zero);
            })
            .ToArray();
        ShipCollisions.Resolve(ships.Take(64).ToArray());
        long before = GC.GetAllocatedBytesForCurrentThread();
        var resolved = ShipCollisions.Resolve(ships);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, count * 1024L);
        Assert.Equal(ships.Select(s => s.Position), resolved.Select(r => r.Position));
        Assert.All(resolved, r => Assert.False(r.Bumped));
    }

    [Fact]
    public void RoundedFloatRadiusSumsRemainCollisionCandidates()
    {
        CollisionMotion[] ships =
        [
            new(1, Hull with { ShipRadius = 1 }, SpacePosition.Zero, SpacePosition.Zero, SpacePosition.Zero, SpacePosition.Zero),
            new(2, Hull with { ShipRadius = 0.1f }, new(1.10000001, 0, 0), SpacePosition.Zero, new(1.10000001, 0, 0), SpacePosition.Zero),
        ];
        var expected = LegacyShipCollisions.Resolve(ships);
        Assert.All(expected, r => Assert.True(r.Bumped));
        Assert.Equal(expected, ShipCollisions.Resolve(ships));
    }

    [Fact]
    public void NonSolidAndUnrepresentableBoundsRetainLegacyEvaluation()
    {
        Assert.Empty(ShipCollisions.Resolve([]));
        foreach (double origin in new[] { double.NaN, double.PositiveInfinity, double.MaxValue })
        {
            CollisionMotion[] ships =
            [
                new(1, Hull, new(origin, 0, 0), SpacePosition.Zero, new(origin, 0, 0), SpacePosition.Zero),
                new(2, Hull, SpacePosition.Zero, SpacePosition.Zero, SpacePosition.Zero, SpacePosition.Zero, Solid: false),
            ];
            Assert.Equal(LegacyShipCollisions.Resolve(ships), ShipCollisions.Resolve(ships));
        }
    }
}
