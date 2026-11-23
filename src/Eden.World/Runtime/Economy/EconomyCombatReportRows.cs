using System.Text.Json;
using Eden.Gameplay.Characters;

namespace Eden.World.Runtime.Economy;

internal sealed class EconomyCombatReportRows(Func<long, KillboardPartyKind> pilotKind) : IEconomyRows<EconomySavedCombatReport>
{
    private readonly Dictionary<EconomyKey, EconomySavedCombatReport> _rows = [];
    private readonly Dictionary<long, IndexedCombatReport> _reports = [];
    private readonly Dictionary<(KillboardPartyKind Killer, KillboardPartyKind Victim), SortedSet<(long Time, long Id)>> _byParties = [];

    public string LoadOrder => "Id";
    public IEnumerable<EconomyKey> Keys => _rows.Keys;

    public EconomySavedCombatReport? Read(EconomyKey key) => _rows.GetValueOrDefault(key);

    public IReadOnlyCollection<EconomyKey>? Candidates(string column, object? value) => null;

    public void Set(EconomyKey key, EconomySavedCombatReport? row)
    {
        if (_reports.Remove(key.Id, out var previous))
        {
            _byParties[(previous.Killer.Kind, previous.Victim.Kind)].Remove((previous.Cursor.Time, previous.Cursor.Id));
        }
        if (row is null)
        {
            _rows.Remove(key);
            return;
        }
        _rows[key] = row;
        // Capsuleer reports have participant copies. The victim's report and the original NPC kill each represent one event.
        if (
            !(row.IsLoss && row.SourceKey.StartsWith("ship:", StringComparison.Ordinal))
            && !row.SourceKey.StartsWith("npc:", StringComparison.Ordinal)
        )
        {
            return;
        }
        var summary = JsonSerializer.Deserialize<ReportSummary>(row.Data);
        if (summary is not { Victim: { } victim, FinalBlow: { } killer })
        {
            return;
        }
        var entry = new IndexedCombatReport(new(row.Time, row.Id), summary.SystemId, Party(victim), Party(killer));
        _reports.Add(row.Id, entry);
        var parties = (entry.Killer.Kind, entry.Victim.Kind);
        if (!_byParties.TryGetValue(parties, out var order))
        {
            _byParties.Add(parties, order = []);
        }
        order.Add((row.Time, row.Id));
    }

    internal IEnumerable<IndexedCombatReport> Page(KillboardFilter filter, int count)
    {
        var upper = filter.Before is { } before ? (before.Time, before.Id - 1) : (long.MaxValue, long.MaxValue);
        return _byParties
            .Where(pair =>
                (filter.Killer is null || pair.Key.Killer == filter.Killer) && (filter.Victim is null || pair.Key.Victim == filter.Victim)
            )
            .SelectMany(pair => pair.Value.GetViewBetween((long.MinValue, long.MinValue), upper).Reverse().Take(count))
            .OrderDescending()
            .Take(count)
            .Select(key => _reports[key.Id]);
    }

    private IndexedCombatParty Party(CombatParty party) =>
        new(party.CharacterId is long pilot ? pilotKind(pilot) : KillboardPartyKind.Npc, party.CharacterId, party.ShipTypeId);

    public void CompleteWrites() { }

    public void Clear()
    {
        _rows.Clear();
        _reports.Clear();
        _byParties.Clear();
    }

    private sealed record ReportSummary(int SystemId, CombatParty? Victim, CombatParty? FinalBlow);
}

internal readonly record struct IndexedCombatParty(KillboardPartyKind Kind, long? CharacterId, int? ShipTypeId);

internal sealed record IndexedCombatReport(KillboardCursor Cursor, int SystemId, IndexedCombatParty Victim, IndexedCombatParty Killer);
