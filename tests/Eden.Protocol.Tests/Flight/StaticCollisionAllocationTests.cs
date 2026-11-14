using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;

namespace Eden.Protocol.Tests.Flight;

public sealed class StaticCollisionAllocationTests
{
    private static readonly FlightProperties Hull = new(0, 47, 1_163_000, 4.5f, 295, 3);
    private const int DistantCount = 2000;
    private const int Repeats = 20;

    [Fact]
    public void DistantStaticBodiesDoNotAllocateCollisionRecordsPerFrame()
    {
        CollisionMotion ship = new(1, Hull, SpacePosition.Zero, new(100, 0, 0), new(100, 0, 0), new(100, 0, 0));
        var obstacles = Enumerable.Range(0, DistantCount).Select(index => Body(index + 2, new(1_000_000 + index, 0, 0))).ToArray();
        var expected = ShipCollisions.ResolveStatic(ship, []);
        Assert.Equal(expected, ShipCollisions.ResolveStatic(ship, obstacles));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int repeat = 0; repeat < Repeats; repeat++)
        {
            Assert.Equal(expected, ShipCollisions.ResolveStatic(ship, obstacles));
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, Repeats * 1024L);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_000_000_000)]
    public void StaticCandidatesPreserveOverlapTangencyAndEndOfStepContacts(double origin)
    {
        SpacePosition start = new(origin, origin, origin);
        CollisionMotion ship = new(1, Hull, start, new(100, 0, 0), start + new SpacePosition(100, 0, 0), new(100, 0, 0));
        double radius = Hull.ShipRadius + Hull.ShipRadius;
        foreach (
            var offset in new[]
            {
                SpacePosition.Zero,
                new SpacePosition(radius, 0, 0),
                new SpacePosition(100 + radius, 0, 0),
                new SpacePosition(Math.BitIncrement(100 + radius), 0, 0),
                new SpacePosition(50, radius, 0),
                new SpacePosition(50, Math.BitIncrement(radius), 0),
                new SpacePosition(-100 - radius, 0, 0),
            }
        )
        {
            var body = Body(2, start + offset);
            Assert.Equal(Reference(ship, [body]), ShipCollisions.ResolveStatic(ship, [body]));
        }
    }

    [Fact]
    public void StaticCandidatesKeepMixedContactOrderingAndNonSolidExclusions()
    {
        Random random = new(1701);
        for (int sample = 0; sample < 100; sample++)
        {
            CollisionMotion ship = new(1, Hull, SpacePosition.Zero, new(200, 30, 0), new(180, 25, 0), new(160, 20, 0));
            var obstacles = Enumerable
                .Range(0, 12)
                .Select(index =>
                    Body(index + 2, new(random.Next(-200, 400), random.Next(-100, 100), random.Next(-50, 50))) with
                    {
                        Solid = index % 3 != 0,
                    }
                )
                .ToArray();
            Assert.Equal(Reference(ship, obstacles), ShipCollisions.ResolveStatic(ship, obstacles));
            Array.Reverse(obstacles);
            Assert.Equal(Reference(ship, obstacles), ShipCollisions.ResolveStatic(ship, obstacles));
            Assert.Equal(
                Reference(ship with { Solid = false }, obstacles),
                ShipCollisions.ResolveStatic(ship with { Solid = false }, obstacles)
            );
        }
    }

    private static StaticSpaceObject Body(long id, SpacePosition position) => new(id, 0, 15, 3, 1, position, Hull.ShipRadius);

    private static CollisionResult Reference(CollisionMotion ship, IReadOnlyList<StaticSpaceObject> bodies) =>
        ShipCollisions.Resolve([
            ship,
            .. bodies.Select(body => new CollisionMotion(
                body.Id,
                ship.Hull with
                {
                    ShipRadius = body.Radius,
                },
                body.Position,
                SpacePosition.Zero,
                body.Position,
                SpacePosition.Zero,
                Solid: body.Solid,
                Fixed: true
            )),
        ])[0];
}
