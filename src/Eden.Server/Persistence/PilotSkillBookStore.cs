using System.Globalization;
using Eden.Gameplay.Dogma;
using Eden.Protocol;
using Eden.Protocol.Docking;
using Eden.Protocol.Skills;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotSkillBookStore(WorldDatabase database) : IPilotSkillBooks
{
    public SkillInjection Inject(long pilotId, IReadOnlyList<long> itemIds)
    {
        if (itemIds.Count == 0 || itemIds.Distinct().Count() != itemIds.Count)
        {
            throw new InvalidDataException("Select distinct skillbooks to inject.");
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var ship = pilot.ActiveShip;
        var items = world
            .Set<OwnedItem>()
            .Where(i =>
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
            var type = catalog
                .Set<SourceRecord>()
                .Find("client/types", item.TypeId.ToString(CultureInfo.InvariantCulture))
                ?.Read<ClientTypes>();
            double rank = catalog.Set<TypeAttribute>().Find(item.TypeId, (int)DogmaAttributeId.SkillTimeConstant)?.Value ?? 0;
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
        world.SaveChanges();
        transaction.Commit();
        return new(items.Select(i => i.TypeId).ToArray(), changes);
    }
}
