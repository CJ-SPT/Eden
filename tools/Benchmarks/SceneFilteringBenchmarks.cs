using BenchmarkDotNet.Attributes;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using ZLinq;

namespace Eden.Protocol.Tests.Benchmarks;

[MemoryDiagnoser]
public class SceneFilteringBenchmarks
{
    private const int VeldsparType = 1230;
    private const int VeldsparGroup = 450;
    private const int OwnerId = 1;
    private const int Spacing = 1000;
    private const float Radius = 100;
    private IReadOnlyList<StaticSpaceObject> _objects = null!;
    private readonly SpacePosition _viewer = SpacePosition.Zero;

    [Params(64, 4096)]
    public int Objects { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _objects = Enumerable
            .Range(0, Objects)
            .Select(index => new StaticSpaceObject(
                index,
                VeldsparType,
                VeldsparGroup,
                AsteroidDefinitions.Category,
                OwnerId,
                new(index * Spacing, 0, 0),
                Radius
            ))
            .ToArray();
        if (!StandardLinq().SequenceEqual(ValueLinq()))
        {
            throw new InvalidOperationException("Visibility and ordering must match before measuring.");
        }
    }

    [Benchmark(Baseline = true)]
    public StaticSpaceObject[] StandardLinq() => _objects.Where(Visible).ToArray();

    [Benchmark]
    public StaticSpaceObject[] ValueLinq() => _objects.AsValueEnumerable().Where(Visible).ToArray();

    private bool Visible(StaticSpaceObject body) =>
        !MiningDefinitions.IsResource(body.CategoryId, body.GroupId)
        || _viewer.DistanceTo(body.Position) <= AsteroidDefinitions.VisibilityRange;
}
