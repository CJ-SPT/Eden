using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Protocol.Flight;

namespace Eden.Protocol.Tests.Flight;

public sealed class StaticSceneFrameTests
{
    [Fact]
    public void UnchangedAsteroidsReuseSceneObjectsButVisibilityAndSceneReplacementStayCurrent()
    {
        using TestWorld fixture = new();
        var pilot = fixture.Store.FindPilot(1000001)! with { Position = SpacePosition.Zero };
        List<DestinyCommand> updates = [];
        var bodies = Enumerable
            .Range(0, 2000)
            .Select(index => new StaticSpaceObject(index + 10, 0, 15, 3, 1, new(1_000_000 + index, 0, 0), 100))
            .ToArray();
        StaticSpaceObject rock = new(3, 1230, 462, AsteroidDefinitions.Category, 0, new(AsteroidDefinitions.VisibilityRange, 0, 0), 100);
        IReadOnlyList<StaticSpaceObject> asteroids = Array.AsReadOnly(new[] { rock });
        FlightScene scene = new(new(), new TestClock(), bodies, new(updates.Add), updates.Add, _ => { }, null, null, null, null, null);
        FlightSceneContext context = new(pilot, true, true, 1);
        scene.RefreshAsteroids(context, asteroids);
        Assert.Equal(rock, Assert.Single(scene.VisibleAsteroids));
        var initial = scene.Objects;
        updates.Clear();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        const int frames = 20;
        for (int frame = 0; frame < frames; frame++)
        {
            scene.RefreshAsteroids(context, asteroids);
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, frames * 2048L);
        Assert.Same(initial, scene.Objects);
        Assert.Empty(updates);
        var away = context with { Pilot = pilot with { Position = new(-1, 0, 0) } };
        scene.RefreshAsteroids(away, asteroids);
        Assert.Empty(scene.VisibleAsteroids);
        Assert.Equal(rock.Id, Assert.Single(updates.OfType<DestinyCommand.RemoveBall>()).Id);
        updates.Clear();
        scene.RefreshAsteroids(context, asteroids);
        Assert.Equal(rock, Assert.Single(scene.VisibleAsteroids));
        Assert.Single(updates.OfType<DestinyCommand.AddBalls>());
        scene.RefreshAsteroids(context, []);
        Assert.Empty(scene.VisibleAsteroids);
        Assert.DoesNotContain(scene.Objects, body => body.Id == rock.Id);
        scene.RefreshAsteroids(context, asteroids);
        StaticSpaceObject replacement = bodies[0] with { Id = 5000 };
        scene.ReplaceObjects([replacement]);
        scene.RefreshAsteroids(context, asteroids);
        Assert.Equal(new[] { replacement, rock }, scene.Objects);
        var changed = rock with { Radius = rock.Radius + 1 };
        scene.RefreshAsteroids(context, Array.AsReadOnly(new[] { changed }));
        Assert.Equal(changed, scene.Objects.Single(body => body.Id == rock.Id));
    }
}
