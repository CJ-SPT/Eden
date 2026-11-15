using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Inventory;

[Injectable]
internal sealed class MutaplasmidStore(WorldDatabase database, TimeProvider clock) : IMutaplasmids
{
    private LiveWorld Live =>
        LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Mutaplasmids require the resident world.");

    public MutatedItem? Read(long itemId) =>
        Live.Economy.Inspect(world =>
            world.Set<OwnedItem>().Find(itemId) is not null && world.Set<SavedMutatedItem>().Find(itemId) is { } saved
                ? Decode(saved)
                : null
        );

    internal static MutatedItem Decode(SavedMutatedItem saved) =>
        JsonSerializer.Deserialize<MutatedItem>(saved.Payload) ?? throw new InvalidDataException("Invalid saved mutation.");

    internal static IReadOnlyDictionary<DogmaAttributeId, double> Attributes(WorldAction world, CatalogDbContext catalog, long id, int type)
    {
        var basis = PilotFittingStore.ReadAttributes(catalog, type);
        if (world is not EconomyTransaction)
        {
            return basis;
        }
        if (world.Set<SavedMutatedItem>().Find(id) is { } saved)
        {
            return Decode(saved).Attributes;
        }
        if (MutationCatalog.BaseType(catalog, type) != type)
        {
            throw new InvalidDataException("This Abyssal module has no saved mutation.");
        }
        return basis;
    }

    public MutationResult Create(long pilotId, long mutatorId, long sourceId)
    {
        using var tx = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = tx.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
        if (pilot.ActiveShip.Position is not null || mutatorId == sourceId)
        {
            throw new InvalidDataException("Mutate modules in your docked personal hangar.");
        }
        var mutator = Require(mutatorId);
        var source = Require(sourceId);
        if (tx.Set<SavedMutatedItem>().Find(sourceId) is not null || tx.Filter<OwnedItem>(i => i.LocationId == sourceId).Any())
        {
            throw new InvalidDataException("An already mutated module or loaded module cannot be mutated.");
        }
        var definition =
            catalog.Read(CatalogData.Client.Dynamicitemattributes, (long)mutator.TypeId)
            ?? throw new InvalidDataException("This item is not a mutaplasmid.");
        var mapping =
            definition.InputOutputMapping?.SingleOrDefault(m => m.ApplicableTypes?.Contains(source.TypeId) == true)
            ?? throw new InvalidDataException("This mutaplasmid is not compatible with the selected module.");
        int output = checked((int)mapping.ResultingType!.Value);
        if (
            catalog.RequireItem(source.TypeId).CategoryId != (int)InventoryCategory.Module
            || FittingCatalog.Definition(catalog, source.TypeId) is null
            || FittingCatalog.Definition(catalog, output) is null
        )
        {
            throw new InvalidDataException("This mutation result is not a supported fitted module.");
        }
        var ranges = (
            definition.AttributeIDs ?? ImmutableDictionary<string, Eden.Data.Catalog.ClientDynamicitemattributesAttributeIDsValue>.Empty
        )
            .OrderBy(p => int.Parse(p.Key, CultureInfo.InvariantCulture))
            .Select(p => new MutationRange(
                (DogmaAttributeId)int.Parse(p.Key, CultureInfo.InvariantCulture),
                p.Value.Min ?? double.NaN,
                p.Value.Max ?? double.NaN
            ))
            .ToArray();
        const int Resolution = 1 << 24;
        var rolls = ranges
            .Select(r => new MutationRoll(r.Attribute, RandomNumberGenerator.GetInt32(Resolution) / (double)(Resolution - 1)))
            .ToArray();
        var basis = catalog.FittingAttributes(source.TypeId).ToDictionary();
        foreach (var pair in catalog.RawFittingAttributes(output))
        {
            basis[pair.Key] = pair.Value;
        }
        var attributes = MutationRules.Apply(basis, ranges, rolls);
        long id = InventoryPersistence.NextId(tx);
        MutatedItem mutation = new(
            id,
            output,
            source.TypeId,
            source.Id,
            mutator.TypeId,
            mutator.Id,
            pilotId,
            clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
            attributes,
            rolls.ToImmutableDictionary(r => r.Attribute, r => r.Fraction)
        );
        List<InventoryChange> changes = [];
        Consume(mutator);
        Consume(source);
        var result = tx.Add(
            new OwnedItem
            {
                Id = id,
                OwnerId = pilotId,
                LocationId = pilot.ActiveShip.DockedLocationId,
                FlagId = (int)InventoryFlag.Hangar,
                TypeId = output,
                Singleton = true,
                Quantity = 1,
            }
        ).Entity;
        tx.Add(new SavedMutatedItem { ItemId = id, Payload = JsonSerializer.Serialize(mutation) });
        changes.Add(new(PilotInventoryStore.Read(catalog, result), null));
        tx.Commit();
        return new(mutation, changes);

        OwnedItem Require(long itemId)
        {
            var item = tx.Set<OwnedItem>().Find(itemId);
            if (
                item is null
                || item.OwnerId != pilotId
                || item.LocationId != pilot.ActiveShip.DockedLocationId
                || item.FlagId != (int)InventoryFlag.Hangar
                || item.Quantity < 1
                || item.Online
            )
            {
                throw new InvalidDataException("Both inputs must be available in your personal station hangar.");
            }
            return item;
        }
        void Consume(OwnedItem item)
        {
            var before = PilotInventoryStore.Read(catalog, item);
            item.Quantity--;
            changes.Add(new(before with { Quantity = item.Quantity }, before));
            if (item.Quantity == 0)
            {
                tx.Remove(item);
            }
        }
    }
}
