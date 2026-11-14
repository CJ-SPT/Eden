using Eden.Data.World.Mining;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Mining;

[Injectable]
internal sealed partial class PilotAutoMiningStore(WorldDatabase database, TimeProvider clock) : IPilotAutoMining, IPilotAutoMiningHauling
{
    private const string FilterKey = "eden.automining.oreFilter";
    private const string OptionsKey = "eden.automining.options";

    public AutoMiningOptions ReadOptions(long pilotId)
    {
        var saved = ReadSetting(pilotId, OptionsKey);
        return
            saved?.Split(':') is [var family, var order, var approach, var jettison]
            && Enum.TryParse(family, out MiningFamily parsedFamily)
            && Enum.IsDefined(parsedFamily)
            && Enum.TryParse(order, out AutoMiningTargetOrder parsedOrder)
            && Enum.IsDefined(parsedOrder)
            && bool.TryParse(approach, out bool parsedApproach)
            && bool.TryParse(jettison, out bool parsedJettison)
            ? new(parsedFamily, parsedOrder, parsedApproach, parsedJettison)
            : AutoMiningOptions.Default;
    }

    public void SaveOptions(long pilotId, AutoMiningOptions options) => SaveSetting(pilotId, OptionsKey, FormatOptions(options));

    private static string FormatOptions(AutoMiningOptions options) =>
        $"{options.Family}:{options.TargetOrder}:{options.Approach}:{options.Jettison}";

    public void SaveSettings(long pilotId, int systemId, AutoMiningFilter filter, AutoMiningOptions options, AutoMiningHaulSettings hauling)
    {
        using var action = RequireResidentWorld().Economy.Begin(new(pilots: [pilotId]));
        if (hauling.StationId != ParseHauling(ReadSetting(action, pilotId, HaulKey)).StationId)
        {
            CheckHaulStation(systemId, hauling);
        }
        SaveSetting(action, pilotId, FilterKey, filter.Text);
        SaveSetting(action, pilotId, OptionsKey, FormatOptions(options));
        SaveSetting(action, pilotId, HaulKey, FormatHauling(hauling));
        action.Commit();
    }

    public IReadOnlyDictionary<long, double> Survey(long pilotId, int systemId, IReadOnlyList<long> visibleTargets)
    {
        var live = RequireResidentWorld();
        using var ownership = live.EnterShared("automatic mining survey");
        InformationRules.RequireMiningSurvey(ShipAttributes(pilotId));
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Inspect(world =>
        {
            var ship = world.Pilots.Find(pilotId)?.ActiveShip;
            if (ship?.Position is not { } origin || (origin.SystemId ?? catalog.RequireStation(ship.StationId).SolarSystemId) != systemId)
            {
                return new Dictionary<long, double>();
            }
            var position = new SpacePosition(origin.X, origin.Y, origin.Z);
            Dictionary<long, double> volumes = [];
            var rocks = world.Set<WorldAsteroid>();
            foreach (long id in visibleTargets)
            {
                if (
                    rocks.Find(id) is { Quantity: > 0 } rock
                    && rock.SystemId == systemId
                    && position.DistanceTo(new(rock.X, rock.Y, rock.Z)) <= InformationRules.MiningScanRange
                )
                {
                    volumes[id] = rock.Quantity * PilotInventoryStore.Volume(catalog, rock.TypeId);
                }
            }
            return volumes;
        });
    }

    public AutoMiningFilter ReadFilter(long pilotId) => AutoMiningFilter.Parse(ReadSetting(pilotId, FilterKey) ?? "");

    public void SaveFilter(long pilotId, AutoMiningFilter filter) => SaveSetting(pilotId, FilterKey, filter.Text);

    private string? ReadSetting(long pilotId, string key) =>
        RequireResidentWorld().Economy.Inspect(world => ReadSetting(world, pilotId, key));

    private static string? ReadSetting(WorldAction world, long pilotId, string key) =>
        (PilotCharacterSettingsStore.Read(world, pilotId, key)?.Value as CharacterSettingValue.Text)?.Value;

    public IReadOnlyDictionary<DogmaAttributeId, double> ShipAttributes(long pilotId)
    {
        var live = RequireResidentWorld();
        IReadOnlyDictionary<DogmaAttributeId, double> Load()
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            return live.Economy.Read(world => PilotFittingStore.ShipAttributes(world, catalog, pilotId), new(pilots: [pilotId]));
        }
        return live.Reads is { } reads ? reads.Fit(pilotId, Load) : Load();
    }

    private void SaveSetting(long pilotId, string key, string value)
    {
        using var action = RequireResidentWorld().Economy.Begin(new(pilots: [pilotId]));
        SaveSetting(action, pilotId, key, value);
        action.Commit();
    }

    private static void SaveSetting(WorldAction world, long pilotId, string key, string value) =>
        PilotCharacterSettingsStore.Save(world, pilotId, new(key, new CharacterSettingValue.Text(value)));

    public IReadOnlyList<AutoMiningModule> Modules(long pilotId, long shipId)
    {
        var live = RequireResidentWorld();
        IReadOnlyList<AutoMiningModule> Load()
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            using var action = live.Economy.Begin(new(pilots: [pilotId]));
            List<AutoMiningModule> modules = [];
            foreach (var item in PilotInventoryStore.List(action, catalog, pilotId, shipId))
            {
                if (
                    !item.Online
                    || !item.Singleton
                    || !InventoryDefinitions.IsHighSlot(item.FlagId)
                    || !InventoryDefinitions.IsMiningGroup(item.GroupId)
                )
                {
                    continue;
                }
                var attributes = catalog.FittingAttributes(item.TypeId);
                var family = MiningDefinitions.Family(item.GroupId, attributes);
                double range = PilotFittingStore
                    .ReadModuleAttributes(action, catalog, pilotId, item)
                    .GetValueOrDefault(DogmaAttributeId.MaxRange);
                if (range <= 0 || !double.IsFinite(range))
                {
                    continue;
                }
                InventoryTypeList? targets = null;
                if (family == MiningFamily.Ore && PilotAmmunitionStore.Contents(action, item.Id) is { } crystal)
                {
                    var type = catalog.RequireItem(crystal.TypeId);
                    if (!MiningCrystalCatalog.Supported(catalog, crystal.TypeId, type.GroupId))
                    {
                        continue;
                    }
                    var crystalAttributes = catalog.FittingAttributes(crystal.TypeId);
                    targets = CatalogTypeLists.Read(catalog, checked((long)crystalAttributes[DogmaAttributeId.MiningCrystalTypeList]));
                }
                modules.Add(new(item.Id, range, family, MiningDefinitions.IsDeepCore(attributes), targets));
            }
            action.Commit();
            return modules.AsReadOnly();
        }

        return LiveReadModels.Find(database.Path) is { } resident ? resident.AutoMiningModules(pilotId, shipId, Load) : Load();
    }
}
