using System.Runtime.CompilerServices;

namespace Eden.Gameplay.Flight;

public sealed class StaticCollisionGeometry : IEquatable<StaticCollisionGeometry>
{
    private const int LeafCapacity = 8;
    private const int MaximumClearancePasses = 64;
    private const double Clearance = 1;
    private const int RotationCacheLimit = 256;

    private readonly record struct Bounds(double MinX, double MaxX, double MinY, double MaxY, double MinZ, double MaxZ)
    {
        internal static Bounds Around(SpacePosition start, SpacePosition end, double radius) =>
            new(
                Math.BitDecrement(Math.Min(start.X, end.X) - radius),
                Math.BitIncrement(Math.Max(start.X, end.X) + radius),
                Math.BitDecrement(Math.Min(start.Y, end.Y) - radius),
                Math.BitIncrement(Math.Max(start.Y, end.Y) + radius),
                Math.BitDecrement(Math.Min(start.Z, end.Z) - radius),
                Math.BitIncrement(Math.Max(start.Z, end.Z) + radius)
            );

        internal Bounds Union(Bounds other) =>
            new(
                Math.Min(MinX, other.MinX),
                Math.Max(MaxX, other.MaxX),
                Math.Min(MinY, other.MinY),
                Math.Max(MaxY, other.MaxY),
                Math.Min(MinZ, other.MinZ),
                Math.Max(MaxZ, other.MaxZ)
            );

        internal bool Overlaps(Bounds other) =>
            MinX <= other.MaxX
            && MaxX >= other.MinX
            && MinY <= other.MaxY
            && MaxY >= other.MinY
            && MinZ <= other.MaxZ
            && MaxZ >= other.MinZ;

        internal int LongestAxis =>
            MaxX - MinX >= MaxY - MinY ? (MaxX - MinX >= MaxZ - MinZ ? 0 : 2) : (MaxY - MinY >= MaxZ - MinZ ? 1 : 2);

        internal double Minimum(int axis) =>
            axis switch
            {
                0 => MinX,
                1 => MinY,
                _ => MinZ,
            };
    }

    private readonly record struct Entry(int Index, Bounds Bounds);

    private readonly record struct Node(Bounds Bounds, int Start, int Count, int Left, int Right);

    private readonly StaticCollisionPrimitive[] _primitives;
    private readonly Entry[] _entries;
    private readonly Node[] _nodes;
    private int _nextNode;
    private readonly StaticCollisionGeometry _origin;
    private readonly double _yaw;
    private readonly object _rotationGate = new();
    private readonly Dictionary<double, WeakReference<StaticCollisionGeometry>> _rotations = [];

    public StaticCollisionGeometry(IReadOnlyList<StaticCollisionPrimitive> primitives)
    {
        _origin = this;
        if (primitives.Count is 0 or > ushort.MaxValue)
        {
            throw new ArgumentException("A native compound collider requires between one and 65,535 primitives.", nameof(primitives));
        }
        _primitives = primitives.ToArray();
        _entries = new Entry[_primitives.Length];
        _nodes = new Node[_primitives.Length * 2];
        for (int index = 0; index < _primitives.Length; index++)
        {
            var primitive = _primitives[index];
            if (!Finite(primitive.Start) || !Finite(primitive.End) || !float.IsFinite(primitive.Radius) || primitive.Radius <= 0)
            {
                throw new ArgumentException("Collision primitives require finite coordinates and a positive radius.", nameof(primitives));
            }
            _entries[index] = new(index, Bounds.Around(primitive.Start, primitive.End, MathF.BitIncrement(primitive.Radius)));
        }
        Primitives = Array.AsReadOnly(_primitives);
        Spheres = Array.AsReadOnly(_primitives.Where(primitive => primitive.Sphere).ToArray());
        Capsules = Array.AsReadOnly(_primitives.Where(primitive => !primitive.Sphere).ToArray());
        BoundingRadius = _primitives.Max(primitive =>
            Math.Max(primitive.Start.Length, primitive.End.Length) + MathF.BitIncrement(primitive.Radius)
        );
        Build(0, _primitives.Length);
    }

    private StaticCollisionGeometry(StaticCollisionGeometry origin, double yaw)
        : this(
            origin
                ._primitives.Select(primitive =>
                    primitive with
                    {
                        Start = primitive.Start.RotateYaw(yaw),
                        End = primitive.End.RotateYaw(yaw),
                    }
                )
                .ToArray()
        )
    {
        _origin = origin;
        _yaw = yaw;
    }

    public IReadOnlyList<StaticCollisionPrimitive> Primitives { get; }
    public IReadOnlyList<StaticCollisionPrimitive> Spheres { get; }
    public IReadOnlyList<StaticCollisionPrimitive> Capsules { get; }
    public double BoundingRadius { get; }

    public StaticCollisionGeometry Rotate(double yaw)
    {
        double angle = _yaw + yaw;
        if (!double.IsFinite(angle))
        {
            throw new ArgumentOutOfRangeException(nameof(yaw));
        }
        if (angle == 0)
        {
            return _origin;
        }
        lock (_origin._rotationGate)
        {
            if (_origin._rotations.TryGetValue(angle, out var cached) && cached.TryGetTarget(out var existing))
            {
                return existing;
            }
            var rotated = new StaticCollisionGeometry(_origin, angle);
            if (_origin._rotations.Count >= RotationCacheLimit)
            {
                foreach (
                    double expired in _origin._rotations.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToArray()
                )
                {
                    _origin._rotations.Remove(expired);
                }
                if (_origin._rotations.Count >= RotationCacheLimit)
                {
                    _origin._rotations.Remove(_origin._rotations.Keys.First());
                }
            }
            _origin._rotations[angle] = new(rotated);
            return rotated;
        }
    }

    public bool Equals(StaticCollisionGeometry? other) =>
        other is not null && ReferenceEquals(_origin, other._origin) && _yaw.Equals(other._yaw);

    public override bool Equals(object? obj) => obj is StaticCollisionGeometry other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(_origin), _yaw);

    public void Find(SpacePosition start, SpacePosition end, double radius, List<int> candidates)
    {
        candidates.Clear();
        Collect(0, Bounds.Around(start, end, MathF.BitIncrement((float)radius)), candidates);
        candidates.Sort();
    }

    public bool Intersects(SpacePosition point, double radius, bool includeBoundary = true) =>
        Intersects(0, Bounds.Around(point, point, radius), point, radius, includeBoundary);

    public SpacePosition Unstick(SpacePosition point, double radius)
    {
        List<int> candidates = [];
        for (int pass = 0; pass < MaximumClearancePasses; pass++)
        {
            Find(point, point, radius, candidates);
            bool moved = false;
            foreach (int index in candidates)
            {
                var primitive = _primitives[index];
                var centre = primitive.ClosestPoint(point);
                var radial = point - centre;
                double surface = primitive.Radius + radius;
                if (radial.Length >= surface)
                {
                    continue;
                }
                var normal = radial.Length > 0 ? radial.Unit : Perpendicular(primitive.End - primitive.Start);
                point = centre + normal * (surface + Clearance);
                moved = true;
                break;
            }
            if (!moved)
            {
                return point;
            }
        }
        // Severely embedded legacy positions can intersect many touching surface primitives.
        return (point.Length > 0 ? point.Unit : new SpacePosition(1, 0, 0)) * (BoundingRadius + radius + Clearance);
    }

    public SpacePosition ClearAlongRay(SpacePosition point, SpacePosition direction, double radius)
    {
        List<int> candidates = [];
        for (int pass = 0; pass < MaximumClearancePasses; pass++)
        {
            Find(point, point, radius, candidates);
            double distance = 0;
            foreach (int index in candidates)
            {
                var primitive = _primitives[index];
                if ((point - primitive.ClosestPoint(point)).Length < primitive.Radius + radius)
                {
                    distance = Math.Max(distance, primitive.ExitDistance(point, direction, radius) + Clearance);
                }
            }
            if (distance == 0)
            {
                return point;
            }
            point += direction * distance;
        }
        throw new InvalidDataException("The authored undock ray could not clear the hull collision geometry.");
    }

    private static bool Finite(SpacePosition point) => double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);

    private static SpacePosition Perpendicular(SpacePosition axis)
    {
        var normal = axis.Cross(new(0, 1, 0));
        return normal.Length > 0 ? normal.Unit : new(1, 0, 0);
    }

    private int Build(int start, int count)
    {
        int node = _nextNode++;
        var bounds = _entries[start].Bounds;
        for (int index = start + 1; index < start + count; index++)
        {
            bounds = bounds.Union(_entries[index].Bounds);
        }
        if (count <= LeafCapacity)
        {
            _nodes[node] = new(bounds, start, count, 0, 0);
            return node;
        }
        int axis = bounds.LongestAxis;
        Array.Sort(
            _entries,
            start,
            count,
            Comparer<Entry>.Create(
                (a, b) =>
                {
                    int order = a.Bounds.Minimum(axis).CompareTo(b.Bounds.Minimum(axis));
                    return order == 0 ? a.Index.CompareTo(b.Index) : order;
                }
            )
        );
        int firstCount = count / 2;
        int left = Build(start, firstCount);
        int right = Build(start + firstCount, count - firstCount);
        _nodes[node] = new(bounds, start, 0, left, right);
        return node;
    }

    private void Collect(int index, Bounds bounds, List<int> candidates)
    {
        var node = _nodes[index];
        if (!node.Bounds.Overlaps(bounds))
        {
            return;
        }
        if (node.Count > 0)
        {
            for (int entry = node.Start; entry < node.Start + node.Count; entry++)
            {
                if (_entries[entry].Bounds.Overlaps(bounds))
                {
                    candidates.Add(_entries[entry].Index);
                }
            }
            return;
        }
        Collect(node.Left, bounds, candidates);
        Collect(node.Right, bounds, candidates);
    }

    private bool Intersects(int index, Bounds bounds, SpacePosition point, double radius, bool includeBoundary)
    {
        var node = _nodes[index];
        if (!node.Bounds.Overlaps(bounds))
        {
            return false;
        }
        if (node.Count == 0)
        {
            return Intersects(node.Left, bounds, point, radius, includeBoundary)
                || Intersects(node.Right, bounds, point, radius, includeBoundary);
        }
        for (int entry = node.Start; entry < node.Start + node.Count; entry++)
        {
            var primitive = _primitives[_entries[entry].Index];
            var radial = point - primitive.ClosestPoint(point);
            double surface = radius + primitive.Radius;
            double squared = radial.Dot(radial);
            if (includeBoundary ? squared <= surface * surface : squared < surface * surface)
            {
                return true;
            }
        }
        return false;
    }
}
