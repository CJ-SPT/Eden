using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Skills;

[Injectable]
internal sealed class PilotSkillBookStore(WorldDatabase database, TimeProvider? clock = null) : IPilotSkillBooks
{
    public SkillInjection Inject(long pilotId, IReadOnlyList<long> itemIds)
    {
        if (itemIds.Count == 0 || itemIds.Distinct().Count() != itemIds.Count)
        {
            throw new InvalidDataException("Select distinct skillbooks to inject.");
        }
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        var pilot = world.Filter<Pilot>(p => p.Id == pilotId).Single();
        var ship = pilot.ActiveShip;
        var items = world
            .Filter<OwnedItem>(i =>
                itemIds.Contains(i.Id)
                && i.OwnerId == pilotId
                && (
                    (i.LocationId == ship.Id && i.FlagId == (int)InventoryFlag.Cargo)
                    || (ship.Position == null && i.LocationId == ship.StationId && i.FlagId == (int)InventoryFlag.Hangar)
                )
            )
            .OrderBy(i => i.Id)
            .ToArray();
        if (items.Length != itemIds.Count || items.Select(i => i.TypeId).Distinct().Count() != items.Length)
        {
            throw new InvalidDataException("The skillbooks must be in your current ship's cargo or docked station hangar.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        List<InventoryChange> changes = [];
        foreach (var item in items)
        {
            var previous = PilotInventoryStore.Read(catalog, item);
            var type = catalog.Read(CatalogData.Client.Types, (long)item.TypeId);
            double rank = catalog.FittingAttributes(item.TypeId).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant, 0);
            if (
                previous.CategoryId != (int)InventoryCategory.Skill
                || type?.Published != 1
                || item.Singleton
                || item.Quantity < 1
                || !double.IsFinite(rank)
                || rank <= 0
            )
            {
                throw new InvalidDataException("Only published skillbooks can be injected.");
            }
            if (world.Set<PilotSkill>().Find(pilotId, item.TypeId) is not null)
            {
                throw new InvalidDataException("The pilot already knows this skill.");
            }
            // Injection learns the skill at zero SP. Prerequisites are enforced by
            // the training queue, not by book consumption (CCP's February 2016 change).
            world.Add(new PilotSkill { PilotId = pilotId, TypeId = item.TypeId });
            item.Quantity--;
            changes.Add(new(previous with { Quantity = item.Quantity }, previous));
            if (item.Quantity == 0)
            {
                world.Remove(item);
            }
        }
        PilotTaskTrackingStore.Record(
            world,
            catalog,
            pilotId,
            (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc(),
            TaskEvents.SkillInjected,
            items.Length
        );
        transaction.Commit();
        return new(items.Select(i => i.TypeId).ToArray(), changes);
    }
}
