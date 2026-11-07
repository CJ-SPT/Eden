using System.Collections.Immutable;
using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

internal sealed record NpcHullProfile(
    CatalogItemDefinition Type,
    IReadOnlyDictionary<DogmaAttributeId, double> Attributes,
    HullDefense Defense,
    IReadOnlyList<NpcEwarAbility> ElectronicWarfare
);

public sealed class NpcProfileCatalog(bool cache = true)
{
    private static readonly CatalogDefinition<NpcHullProfile> s_profile = new("npc-hull", Load, 8192);

    internal NpcHullProfile Read(CatalogDbContext catalog, int typeId) =>
        cache ? catalog.Definition(s_profile, typeId) : Load(catalog, typeId);

    private static NpcHullProfile Load(CatalogDbContext catalog, int typeId)
    {
        var type = catalog.RequireItem(typeId);
        var source = PilotFittingStore.ReadAttributes(catalog, typeId);
        var attributes = (NpcMiningRules.Types.Contains(typeId) ? NpcMiningRules.CombatAttributes(source) : source).ToImmutableDictionary();
        return new(
            type,
            attributes,
            ShipHealth.DefenseFrom(attributes),
            NpcEwarRules.Profile(FittingCatalog.Effects(catalog, typeId), attributes).ToImmutableArray()
        );
    }
}
