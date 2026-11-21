using System.Collections;
using Eden.Data.World.Skills;

namespace Eden.World.Runtime.Economy;

internal sealed class EconomySkillRows : IEconomyRows<EconomyPilotSkill>
{
    private readonly Dictionary<long, SkillProfile> _pilots = [];
    private readonly HashSet<SkillProfile> _profiles = [];
    private readonly Dictionary<int, long> _edits = [];
    private long? _editingPilot;

    public string LoadOrder => "PilotId,TypeId";

    public IEnumerable<EconomyKey> Keys
    {
        get
        {
            CompleteWrites();
            return _pilots.SelectMany(pilot => new PilotKeys(pilot.Key, pilot.Value));
        }
    }

    public EconomyPilotSkill? Read(EconomyKey key)
    {
        if (key.Name is not null || key.Part is < int.MinValue or > int.MaxValue)
        {
            return null;
        }
        int type = (int)key.Part;
        long? points =
            _editingPilot == key.Id
                ? _edits.TryGetValue(type, out long edited)
                    ? edited
                    : null
                : _pilots.GetValueOrDefault(key.Id)?.Points(type);
        return points is long saved ? new(key.Id, type, saved) : null;
    }

    public IReadOnlyCollection<EconomyKey>? Candidates(string column, object? value)
    {
        if (column != nameof(PilotSkill.PilotId))
        {
            return null;
        }
        CompleteWrites();
        return value is long pilot && _pilots.TryGetValue(pilot, out var skills) ? new PilotKeys(pilot, skills) : [];
    }

    public void Set(EconomyKey key, EconomyPilotSkill? row)
    {
        int type = checked((int)key.Part);
        if (_editingPilot != key.Id)
        {
            CompleteWrites();
            var existing = _pilots.GetValueOrDefault(key.Id);
            if (existing?.Points(type) == row?.Points)
            {
                return;
            }
            // A batch edits one private copy. Pilots sharing the old profile keep their points.
            _editingPilot = key.Id;
            if (existing is not null)
            {
                foreach (var skill in existing.Skills)
                {
                    _edits.Add(skill.TypeId, skill.Points);
                }
            }
        }
        if (row is null)
        {
            _edits.Remove(type);
        }
        else
        {
            _edits[type] = row.Points;
        }
    }

    public void CompleteWrites()
    {
        if (_editingPilot is not long pilot)
        {
            return;
        }
        if (_pilots.Remove(pilot, out var old) && --old.Pilots == 0)
        {
            _profiles.Remove(old);
        }
        if (_edits.Count > 0)
        {
            var skills = _edits.Select(pair => new Skill(pair.Key, pair.Value)).ToArray();
            Array.Sort(skills);
            SkillProfile profile = new(skills);
            if (!_profiles.TryGetValue(profile, out var shared))
            {
                shared = profile;
                _profiles.Add(profile);
            }
            shared.Pilots++;
            _pilots.Add(pilot, shared);
        }
        _edits.Clear();
        _editingPilot = null;
    }

    public void Clear()
    {
        _pilots.Clear();
        _profiles.Clear();
        _edits.Clear();
        _editingPilot = null;
    }

    private readonly record struct Skill(int TypeId, long Points) : IComparable<Skill>
    {
        public int CompareTo(Skill other) => TypeId.CompareTo(other.TypeId);
    }

    private sealed class SkillProfile(Skill[] skills) : IEquatable<SkillProfile>
    {
        private readonly int _hash = Hash(skills);

        internal Skill[] Skills { get; } = skills;
        internal int Pilots { get; set; }

        internal long? Points(int type)
        {
            int index = Array.BinarySearch(Skills, new Skill(type, 0));
            return index >= 0 ? Skills[index].Points : null;
        }

        public bool Equals(SkillProfile? other) => other is not null && Skills.AsSpan().SequenceEqual(other.Skills);

        public override bool Equals(object? obj) => obj is SkillProfile other && Equals(other);

        public override int GetHashCode() => _hash;

        private static int Hash(Skill[] skills)
        {
            HashCode hash = new();
            foreach (var skill in skills)
            {
                hash.Add(skill);
            }
            return hash.ToHashCode();
        }
    }

    private sealed class PilotKeys(long pilot, SkillProfile profile) : IReadOnlyCollection<EconomyKey>
    {
        public int Count => profile.Skills.Length;

        public IEnumerator<EconomyKey> GetEnumerator()
        {
            foreach (var skill in profile.Skills)
            {
                yield return new(pilot, skill.TypeId);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
