using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.World.Catalog;

namespace Eden.World.Capsuleers;

internal static class CapsuleerWorkRegions
{
    internal const string TransitOnlyReason = "This region allows bots to pass through only.";
    internal static readonly TimeSpan HoldInterval = TimeSpan.FromMinutes(1);

    internal static CapsuleerJob Retry(CapsuleerJob job, long elapsed) =>
        job.Next(checked(Math.Max(job.DueTicks, elapsed) + HoldInterval.Ticks), job.Action);

    internal static bool Unrestricted(this CapsuleerPopulationPolicy policy) =>
        policy.DefaultRegionAccess == CapsuleerRegionAccess.WorkAndTransit && policy.RegionAccess.Count == 0;

    internal static bool CanWork(this CapsuleerPopulationPolicy policy, CatalogProvider catalog, int system) =>
        policy.Unrestricted()
        || catalog.Systems.TryGetValue(system, out var location) && CapsuleerPopulationRules.AllowsWork(policy, location.RegionId);

    internal static bool CanWorkAtStation(this CapsuleerPopulationPolicy policy, CatalogProvider catalog, int station) =>
        policy.Unrestricted() || catalog.FindStation(station) is { } stop && policy.CanWork(catalog, stop.SolarSystemId);

    internal static bool CanUse(this CapsuleerPopulationPolicy policy, CatalogProvider catalog, CapsuleerTrafficRoute route) =>
        policy.CanWorkAtStation(catalog, route.SourceStationId) && policy.CanWorkAtStation(catalog, route.DestinationStationId);
}
