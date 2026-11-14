using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Mining;

[Injectable]
internal sealed partial class PilotAutoMiningStore(
    WorldDatabase database,
    IPilotCharacterSettings settings,
    IPilotFitting fitting,
    TimeProvider clock
) : IPilotAutoMining, IPilotAutoMiningHauling
{
    private const string FilterKey = "eden.automining.oreFilter";

    public AutoMiningFilter ReadFilter(long pilotId) =>
        AutoMiningFilter.Parse((settings.Read(pilotId, FilterKey)?.Value as CharacterSettingValue.Text)?.Value ?? "");

    public void SaveFilter(long pilotId, AutoMiningFilter filter) =>
        settings.Save(pilotId, new(FilterKey, new CharacterSettingValue.Text(filter.Text)));

    public IReadOnlyList<AutoMiningModule> Modules(long pilotId, long shipId)
    {
        IReadOnlyList<AutoMiningModule> Load()
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            using WorldActionScope action = new(database, new(pilots: [pilotId]));
            List<AutoMiningModule> modules = [];
            foreach (var item in PilotInventoryStore.List(action.World, catalog, pilotId, shipId))
            {
                if (
                    !item.Online
                    || !item.Singleton
                    || !InventoryDefinitions.IsHighSlot(item.FlagId)
                    || !InventoryDefinitions.IsOreMiningGroup(item.GroupId)
                )
                {
                    continue;
                }
                var attributes = catalog.FittingAttributes(item.TypeId);
                if (MiningDefinitions.Family(item.GroupId, attributes) != MiningFamily.Ore)
                {
                    continue;
                }
                double range = PilotFittingStore
                    .ReadModuleAttributes(action.World, catalog, pilotId, item)
                    .GetValueOrDefault(DogmaAttributeId.MaxRange);
                if (range <= 0 || !double.IsFinite(range))
                {
                    continue;
                }
                InventoryTypeList? targets = null;
                if (PilotAmmunitionStore.Contents(action.World, item.Id) is { } crystal)
                {
                    var type = catalog.RequireItem(crystal.TypeId);
                    if (!MiningCrystalCatalog.Supported(catalog, crystal.TypeId, type.GroupId))
                    {
                        continue;
                    }
                    var crystalAttributes = catalog.FittingAttributes(crystal.TypeId);
                    targets = CatalogTypeLists.Read(catalog, checked((long)crystalAttributes[DogmaAttributeId.MiningCrystalTypeList]));
                }
                modules.Add(new(item.Id, range, MiningDefinitions.IsDeepCore(attributes), targets));
            }
            action.Commit();
            return modules.AsReadOnly();
        }

        return LiveReadModels.Find(database.Path) is { } resident ? resident.AutoMiningModules(pilotId, shipId, Load) : Load();
    }
}
