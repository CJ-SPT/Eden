using System.Collections.Immutable;
using Eden.Gameplay.Maps;
using Eden.Gameplay.Sovereignty;
using Eden.World.Catalog;

namespace Eden.World.Sovereignty;

internal sealed record SovereigntySystemResources(
    int SystemId,
    long StarPower,
    ImmutableArray<PlanetResourceDefinition> Planets,
    ImmutableDictionary<int, IReadOnlyList<int>> Neighbors
);

internal static class SovereigntyResourceCatalog
{
    private static readonly CatalogDefinition<ImmutableArray<SovereigntyUpgradeDefinition>> s_upgrades = new(
        "sovereignty-resource-upgrades",
        (catalog, _) =>
            catalog
                .Records(CatalogData.Sde.SovereigntyUpgrades)
                .Where(row => catalog.Read(CatalogData.Client.Types, row.Key)?.Published == 1)
                .OrderBy(row => row.Key)
                .Select(row =>
                {
                    var value = row.Value;
                    return new SovereigntyUpgradeDefinition(
                        checked((int)row.Key),
                        catalog.RequireItem(checked((int)row.Key)).Name,
                        value.MutuallyExclusiveGroup ?? "",
                        value.PowerAllocation ?? 0,
                        value.WorkforceAllocation ?? 0,
                        checked((int)(value.Fuel?.TypeId ?? 0)),
                        value.Fuel?.StartupCost ?? 0,
                        value.Fuel?.HourlyUpkeep ?? 0,
                        value.PowerProduction ?? 0,
                        value.WorkforceProduction ?? 0
                    );
                })
                .ToImmutableArray()
    );
    private static readonly CatalogDefinition<ImmutableDictionary<int, IReadOnlyList<int>>> s_neighbors = new(
        "sovereignty-resource-neighbors",
        (catalog, _) =>
            catalog
                .Records(CatalogData.Sde.MapStargates)
                .Values.Where(row => row.SolarSystemID is > 0 && row.Destination?.SolarSystemID is > 0)
                .GroupBy(row => checked((int)row.SolarSystemID!.Value))
                .ToImmutableDictionary(
                    group => group.Key,
                    group =>
                        (IReadOnlyList<int>)
                            group.Select(row => checked((int)row.Destination!.SolarSystemID!.Value)).Distinct().Order().ToImmutableArray()
                )
    );
    private static readonly CatalogDefinition<SovereigntySystemResources> s_system = new(
        "sovereignty-system-resources",
        (catalog, system) =>
        {
            var source = catalog.Require(CatalogData.Sde.MapSolarSystems, (long)system);
            PlanetResourceDefinition Planet(long id)
            {
                var data = catalog.Read(CatalogData.Sde.PlanetResources, id);
                var reagent = data?.Reagent;
                var planet = new PlanetResourceDefinition(
                    id,
                    data?.Power ?? 0,
                    data?.Workforce ?? 0,
                    reagent is null
                        ? null
                        : new(
                            checked((int)(reagent.TypeId ?? 0)),
                            reagent.AmountPerCycle ?? 0,
                            reagent.CyclePeriod ?? 0,
                            reagent.SecuredCapacity ?? 0,
                            reagent.UnsecuredCapacity ?? 0
                        )
                );
                if (
                    planet.Power < 0
                    || planet.Workforce < 0
                    || planet.Reagent is { } harvest
                        && (
                            harvest.TypeId <= 0
                            || harvest.AmountPerCycle is < 0 or > uint.MaxValue
                            || harvest.CycleSeconds <= 0
                            || harvest.CycleSeconds > long.MaxValue / TimeSpan.TicksPerSecond
                            || harvest.SecuredCapacity is < 0 or > uint.MaxValue
                            || harvest.UnsecuredCapacity is < 0 or > uint.MaxValue
                        )
                )
                {
                    throw new InvalidDataException("Invalid catalog sovereignty resources.");
                }
                return planet;
            }
            return new(
                system,
                source.StarID is long star ? catalog.Read(CatalogData.Sde.PlanetResources, star)?.Power ?? 0 : 0,
                (source.PlanetIDs ?? []).Select(Planet).ToImmutableArray(),
                catalog.Definition(s_neighbors, 0)
            );
        }
    );

    internal static ImmutableArray<SovereigntyUpgradeDefinition> Upgrades(CatalogDbContext catalog) => catalog.Definition(s_upgrades, 0);

    internal static SovereigntySystemResources System(CatalogDbContext catalog, int system) => catalog.Definition(s_system, system);
}
