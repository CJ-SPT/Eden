namespace Eden.Gameplay.Flight;

// Conservative swept boxes select candidates. The existing pair response still
// resolves them in the caller's original order, including equal-time contacts.
internal sealed class CollisionIndex
{
    private const int LeafCapacity = 8;
    private const float MaximumIndexedRadius = float.MaxValue / 2;

    private enum Axis
    {
        X,
        Y,
        Z,
    }

    private readonly record struct Bounds(double MinX, double MaxX, double MinY, double MaxY, double MinZ, double MaxZ)
    {
        internal bool Finite =>
            double.IsFinite(MinX)
            && double.IsFinite(MaxX)
            && double.IsFinite(MinY)
            && double.IsFinite(MaxY)
            && double.IsFinite(MinZ)
            && double.IsFinite(MaxZ);

        internal static Bounds Swept(CollisionMotion ship)
        {
            // Potential adds two float radii before converting to double. Expand
            // each radius by a float ULP as well as rounding the box outward.
            double radius = MathF.BitIncrement(ship.Hull.ShipRadius);
            return new(
                Math.BitDecrement(Math.Min(ship.Start.X, ship.Position.X) - radius),
                Math.BitIncrement(Math.Max(ship.Start.X, ship.Position.X) + radius),
                Math.BitDecrement(Math.Min(ship.Start.Y, ship.Position.Y) - radius),
                Math.BitIncrement(Math.Max(ship.Start.Y, ship.Position.Y) + radius),
                Math.BitDecrement(Math.Min(ship.Start.Z, ship.Position.Z) - radius),
                Math.BitIncrement(Math.Max(ship.Start.Z, ship.Position.Z) + radius)
            );
        }

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

        internal Axis LongestAxis =>
            MaxX - MinX >= MaxY - MinY ? (MaxX - MinX >= MaxZ - MinZ ? Axis.X : Axis.Z) : (MaxY - MinY >= MaxZ - MinZ ? Axis.Y : Axis.Z);

        internal double Minimum(Axis axis) =>
            axis switch
            {
                Axis.X => MinX,
                Axis.Y => MinY,
                _ => MinZ,
            };
    }

    private readonly record struct Entry(int Index, Bounds Bounds);

    private readonly record struct Node(Bounds Bounds, int Start, int Count, int Left, int Right);

    private readonly Entry[] _entries;
    private readonly Node[] _nodes;
    private int _nextNode;

    private CollisionIndex(Entry[] entries)
    {
        _entries = entries;
        _nodes = new Node[entries.Length * 2];
        if (entries.Length > 0)
        {
            Build(0, entries.Length);
        }
    }

    internal static CollisionIndex? Create(IReadOnlyList<CollisionMotion> ships)
    {
        List<Entry> entries = [];
        for (int index = 0; index < ships.Count; index++)
        {
            var ship = ships[index];
            if (!ship.Solid)
            {
                continue;
            }
            var bounds = Bounds.Swept(ship);
            if (!bounds.Finite || ship.Hull.ShipRadius < 0 || ship.Hull.ShipRadius > MaximumIndexedRadius)
            {
                // Preserve pair evaluation for unrepresentable/invalid inputs.
                return null;
            }
            entries.Add(new(index, bounds));
        }
        return new(entries.ToArray());
    }

    internal void Find(CollisionMotion ship, List<int> candidates)
    {
        candidates.Clear();
        if (ship.Solid && _entries.Length > 0)
        {
            Collect(0, Bounds.Swept(ship), candidates);
            candidates.Sort();
        }
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
        var axis = bounds.LongestAxis;
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
}
