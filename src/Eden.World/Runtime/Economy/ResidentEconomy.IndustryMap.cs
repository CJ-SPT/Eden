using Eden.Data.World.Economy;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Market;
using Eden.World.Industry;
using Eden.World.Maps;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private readonly IndustryMapHistory _industryMapHistory = new();
    private IReadOnlyList<IndustryMapCostModifier>? _industryMapCosts;

    private void IndustryJobChanged(EconomyIndustryJob? before, EconomyIndustryJob? after)
    {
        if (
            _live is null
            || before?.StartTime == after?.StartTime && before?.StationId == after?.StationId && before?.Activity == after?.Activity
        )
        {
            return;
        }
        if (before is not null)
        {
            _industryMapHistory.Remove(before.Id, before.StartTime);
        }
        long now = _owner.SimulationTime;
        if (after is null || after.StartTime <= now - TimeSpan.TicksPerDay)
        {
            return;
        }
        int system = Structure(after.StationId)?.SystemId ?? _database.Catalog.FindStation(after.StationId)?.SolarSystemId ?? 0;
        if (MapStatistics.IsKnownSpace(system))
        {
            _industryMapHistory.Record(after.Id, after.StartTime, new(system, after.Activity), now);
        }
    }

    internal IReadOnlyList<IndustryMapJobCount> IndustryMapJobs(IndustryActivity? activity)
    {
        using var owner = _owner.EnterPersistence("industry map history");
        if (!_loaded)
        {
            Warm();
        }
        return _industryMapHistory.Read(activity, _owner.SimulationTime);
    }

    internal IReadOnlyList<IndustryMapCostModifier> IndustryMapCosts()
    {
        using var owner = _owner.EnterPersistence("industry map cost indices");
        if (!_loaded)
        {
            Warm();
        }
        if (_industryMapCosts is { } cached)
        {
            return cached;
        }
        var policy = Peek<SavedEconomicPolicy>(WorldEconomySettings.SingletonId)!;
        var indices = (EconomyTable<SavedManufacturingIndex, EconomySavedManufacturingIndex>)_tables[typeof(SavedManufacturingIndex)];
        var systems = _database.Catalog.Systems.Keys.Where(MapStatistics.IsKnownSpace).Order();
        return _industryMapCosts = Array.AsReadOnly(
            systems
                .Select(system => new IndustryMapCostModifier(
                    system,
                    policy.ChargeServiceCosts ? indices.Snapshot(new(system))?.Index ?? policy.DefaultManufacturingIndex : 0
                ))
                .ToArray()
        );
    }
}
