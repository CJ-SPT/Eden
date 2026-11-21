using Eden.Gameplay.FactionWarfare;

namespace Eden.World.Runtime;

internal sealed partial class LiveWorld
{
    private const int MaximumMilitiaNoticesPerAccount = 32;
    private static readonly TimeSpan MilitiaNoticeLifetime = TimeSpan.FromMinutes(2);
    private readonly Dictionary<long, Queue<MilitiaAdvantageContribution>> _militiaContributions = [];

    internal void PublishMilitiaContribution(long account, MilitiaAdvantageContribution contribution)
    {
        lock (_militiaContributions)
        {
            foreach (var key in _militiaContributions.Keys.ToArray())
            {
                var pending = _militiaContributions[key];
                while (pending.TryPeek(out var entry) && contribution.At - entry.At >= MilitiaNoticeLifetime.Ticks)
                {
                    pending.Dequeue();
                }
                if (pending.Count == 0)
                {
                    _militiaContributions.Remove(key);
                }
            }
            if (!_militiaContributions.TryGetValue(account, out var notices))
            {
                notices = new();
                _militiaContributions.Add(account, notices);
            }
            if (notices.Count >= MaximumMilitiaNoticesPerAccount)
            {
                notices.Dequeue();
            }
            notices.Enqueue(contribution);
        }
    }

    internal IReadOnlyList<MilitiaAdvantageContribution> MilitiaContributions(long account, long now)
    {
        lock (_militiaContributions)
        {
            return _militiaContributions.Remove(account, out var notices)
                ? notices.Where(entry => now - entry.At < MilitiaNoticeLifetime.Ticks).ToArray()
                : [];
        }
    }
}
