using System.Collections.Frozen;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Combat;

// The index only narrows candidates. Visibility still uses Eden's inclusive distance rule,
// and results retain source order. This does not introduce Carbon's dynamic grid boundaries.
public sealed class NpcSceneIndex
{
    private enum Axis
    {
        X,
        Y,
        Z,
    }

    private readonly NpcSimulationContact[] _members;
    private readonly IReadOnlyList<long> _allIds;
    private readonly int[]? _ordered;
    private readonly Axis _axis;
    private readonly SpacePosition? _minimum;
    private readonly SpacePosition? _maximum;

    public IReadOnlyList<NpcSimulationContact> Members { get; }
    public IReadOnlySet<long> Warping { get; }

    public NpcSceneIndex(IReadOnlyList<NpcSimulationContact> members)
    {
        _members = members.ToArray();
        _allIds = Array.AsReadOnly(_members.Select(n => n.Id).ToArray());
        Members = Array.AsReadOnly(_members);
        Warping = _members.Where(n => n.Warping).Select(n => n.Id).ToFrozenSet();
        if (_members.Length == 0 || _members.Any(n => !Finite(n.Position)))
        {
            return;
        }
        _minimum = new(_members.Min(n => n.Position.X), _members.Min(n => n.Position.Y), _members.Min(n => n.Position.Z));
        _maximum = new(_members.Max(n => n.Position.X), _members.Max(n => n.Position.Y), _members.Max(n => n.Position.Z));
        double x = _maximum.X - _minimum.X;
        double y = _maximum.Y - _minimum.Y;
        double z = _maximum.Z - _minimum.Z;
        _axis = x >= y ? (x >= z ? Axis.X : Axis.Z) : (y >= z ? Axis.Y : Axis.Z);
        if (Math.Max(x, Math.Max(y, z)) > 2 * NpcEncounterDefinitions.VisibilityRange)
        {
            _ordered = Enumerable.Range(0, _members.Length).OrderBy(index => Coordinate(_members[index].Position)).ToArray();
        }
    }

    public IReadOnlyList<long> Find(SpacePosition position)
    {
        if (_members.Length == 0)
        {
            return _allIds;
        }
        if (_minimum is not null && _maximum is not null)
        {
            double x = Math.Max(Math.Abs(position.X - _minimum.X), Math.Abs(position.X - _maximum.X));
            double y = Math.Max(Math.Abs(position.Y - _minimum.Y), Math.Abs(position.Y - _maximum.Y));
            double z = Math.Max(Math.Abs(position.Z - _minimum.Z), Math.Abs(position.Z - _maximum.Z));
            if (Math.Sqrt(x * x + y * y + z * z) <= NpcEncounterDefinitions.VisibilityRange)
            {
                return _allIds;
            }
        }
        if (_ordered is null || !Finite(position))
        {
            return _members.Where(n => Visible(position, n.Position)).Select(n => n.Id).ToArray();
        }
        // Round outward so the candidate interval never rejects an inclusive boundary.
        double minimum = Math.BitDecrement(Coordinate(position) - NpcEncounterDefinitions.VisibilityRange);
        double maximum = Math.BitIncrement(Coordinate(position) + NpcEncounterDefinitions.VisibilityRange);
        int low = 0;
        int high = _ordered.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (Coordinate(_members[_ordered[middle]].Position) < minimum)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        List<int> selected = [];
        for (int index = low; index < _ordered.Length; index++)
        {
            int source = _ordered[index];
            var member = _members[source];
            if (Coordinate(member.Position) > maximum)
            {
                break;
            }
            if (Visible(position, member.Position))
            {
                selected.Add(source);
            }
        }
        selected.Sort();
        return selected.Select(index => _members[index].Id).ToArray();
    }

    private double Coordinate(SpacePosition position) =>
        _axis switch
        {
            Axis.X => position.X,
            Axis.Y => position.Y,
            _ => position.Z,
        };

    private static bool Visible(SpacePosition observer, SpacePosition target) =>
        observer.DistanceTo(target) <= NpcEncounterDefinitions.VisibilityRange;

    private static bool Finite(SpacePosition position) =>
        double.IsFinite(position.X) && double.IsFinite(position.Y) && double.IsFinite(position.Z);
}
