using System.Collections;
using Eden.Data.World.Skills;

namespace Eden.World.Runtime.Economy;

// The pilot bucket is both the primary lookup and the index for reading a pilot's skills.
// Keep just the skill type and point balance per row. Snapshots are reconstructed on demand.
internal sealed class EconomySkillRows : IEconomyRows<EconomyPilotSkill>
{
    private readonly Dictionary<long, Dictionary<int, long>> _pilots = [];

    public IEnumerable<EconomyKey> Keys => _pilots.SelectMany(pilot => new PilotKeys(pilot.Key, pilot.Value));

    public EconomyPilotSkill? Read(EconomyKey key) =>
        key.Name is null
        && key.Part is >= int.MinValue and <= int.MaxValue
        && _pilots.TryGetValue(key.Id, out var skills)
        && skills.TryGetValue((int)key.Part, out long points)
            ? new(key.Id, (int)key.Part, points)
            : null;

    public IReadOnlyCollection<EconomyKey>? Candidates(string column, object? value)
    {
        if (column != nameof(PilotSkill.PilotId))
        {
            return null;
        }
        return value is long pilot && _pilots.TryGetValue(pilot, out var skills) ? new PilotKeys(pilot, skills) : [];
    }

    public void Set(EconomyKey key, EconomyPilotSkill? row)
    {
        if (row is not null)
        {
            if (!_pilots.TryGetValue(row.PilotId, out var skills))
            {
                skills = [];
                _pilots.Add(row.PilotId, skills);
            }
            skills[row.TypeId] = row.Points;
        }
        else if (_pilots.TryGetValue(key.Id, out var skills))
        {
            skills.Remove(checked((int)key.Part));
            if (skills.Count == 0)
            {
                _pilots.Remove(key.Id);
            }
        }
    }

    public void Clear() => _pilots.Clear();

    private sealed class PilotKeys(long pilot, Dictionary<int, long> skills) : IReadOnlyCollection<EconomyKey>
    {
        public int Count => skills.Count;

        public IEnumerator<EconomyKey> GetEnumerator()
        {
            foreach (int type in skills.Keys)
            {
                yield return new(pilot, type);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
