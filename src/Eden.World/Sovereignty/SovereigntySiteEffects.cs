using System.Collections.Concurrent;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Sovereignty;

internal sealed record SovereigntyOreEffect(int UpgradeTypeId, int OreTypeId, int NameId, int Tier);

internal sealed record SovereigntySiteEffects(int Minor, int Major, int Drones, int Exploration, IReadOnlyList<SovereigntyOreEffect> Ores)
{
    internal static SovereigntySiteEffects Empty { get; } = new(0, 0, 0, 0, []);
}

internal static class SovereigntySites
{
    private static readonly ConcurrentDictionary<(string World, int System), long> s_versions = new();

    internal static long Version(string world, int system) => s_versions.GetValueOrDefault((world, system));

    internal static void Invalidate(WorldAction world, int system)
    {
        world.AfterCommit(() => s_versions.AddOrUpdate((world.WorldPath, system), 1, (_, version) => version + 1));
        Eden.World.Exploration.CombatAnomalyStore.Invalidate(world, system);
        Eden.World.Exploration.CombatAnomalyStore.Invalidate(world, -1);
    }

    internal static SovereigntySiteEffects Read(string worldPath, CatalogDbContext catalog, int system) =>
        LiveWorld.Find(worldPath) is { } live ? live.Economy.Inspect(world => Read(world, catalog, system)) : SovereigntySiteEffects.Empty;

    internal static SovereigntySiteEffects Read(WorldAction world, CatalogDbContext catalog, int system)
    {
        if (world is not EconomyTransaction)
        {
            return SovereigntySiteEffects.Empty;
        }
        var running = world
            .Filter<SavedSovereigntyUpgrade>(row => row.SystemId == system && row.PowerState == SovereigntyUpgradePower.Online)
            .Select(row => row.TypeId)
            .ToHashSet();
        if (running.Count == 0)
        {
            return SovereigntySiteEffects.Empty;
        }
        int minor = 0,
            major = 0,
            drones = 0,
            exploration = 0;
        List<SovereigntyOreEffect> ores = [];
        foreach (var definition in SovereigntyResourceCatalog.Upgrades(catalog).Where(row => running.Contains(row.TypeId)))
        {
            // The export gives distribution IDs, not server rosters. Eden's additional supported site slots use the upgrade tier.
            int tier = definition.Name.Length > 0 && definition.Name[^1] is >= '1' and <= '3' ? definition.Name[^1] - '0' : 1;
            switch (definition.ExclusiveGroup)
            {
                case "PvE_A":
                    minor = tier;
                    break;
                case "PvE_B":
                    major = tier;
                    break;
                case "PvE_C":
                    drones = tier;
                    break;
                case "Exploration":
                    exploration = tier;
                    break;
                default:
                    string? ore = definition.ExclusiveGroup switch
                    {
                        "Mining_A" => "Veldspar",
                        "Mining_B" => "Mordunium",
                        "Mining_C" => "Kylixium",
                        "Mining_D" => "Griemeer",
                        "Mining_E" => "Nocxite",
                        "Mining_F" => "Hezorime",
                        "Mining_G" => "Ueganite",
                        _ => null,
                    };
                    if (ore is null)
                    {
                        break;
                    }
                    var type = catalog.Definitions.Items.Values.Single(row => row.Name == ore);
                    int name = checked(
                        (int)(
                            catalog.Require(CatalogData.Client.Types, (long)type.Id).TypeNameID
                            ?? throw new InvalidDataException("The upgrade ore has no native name.")
                        )
                    );
                    ores.Add(new(definition.TypeId, type.Id, name, tier));
                    break;
            }
        }
        return new(minor, major, drones, exploration, ores);
    }
}
