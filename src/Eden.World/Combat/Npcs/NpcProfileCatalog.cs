using System.Collections.Concurrent;
using System.Collections.Frozen;
using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

internal sealed record NpcHullProfile(
    ItemType Type,
    IReadOnlyDictionary<DogmaAttributeId, double> Attributes,
    HullDefense Defense,
    IReadOnlyList<NpcEwarAbility> ElectronicWarfare
);

public sealed class NpcProfileCatalog(bool cache = true)
{
    private readonly ConcurrentDictionary<int, NpcHullProfile> _profiles = new();

    internal NpcHullProfile Read(CatalogDbContext catalog, int typeId) =>
        cache ? _profiles.GetOrAdd(typeId, id => Load(catalog, id)) : Load(catalog, typeId);

    private static NpcHullProfile Load(CatalogDbContext catalog, int typeId)
    {
        var type = catalog.Set<ItemType>().Include(t => t.Group).Single(t => t.Id == typeId);
        var source = PilotFittingStore.ReadAttributes(catalog, typeId);
        var attributes = (NpcMiningRules.Types.Contains(typeId) ? NpcMiningRules.CombatAttributes(source) : source).ToFrozenDictionary();
        return new(
            type,
            attributes,
            ShipHealth.DefenseFrom(attributes),
            NpcEwarRules.Profile(FittingCatalog.Effects(catalog, typeId), attributes)
        );
    }
}
