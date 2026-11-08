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
)
{
    public NpcStandingThresholds? StandingThresholds { get; init; }
}

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
        )
        {
            StandingThresholds = ReadStandingThresholds(catalog, typeId),
        };
    }

    private static NpcStandingThresholds? ReadStandingThresholds(CatalogDbContext catalog, int typeId)
    {
        var components = catalog.Read(CatalogData.Client.Spacecomponentsbytype, (long)typeId);
        if (components is null || !components.TryGetValue("entityStandings", out var standings))
        {
            return null;
        }
        double Read(string name) =>
            standings.GetValueOrDefault(name) switch
            {
                ClientSpacecomponentsbytypeValueValueChoiceLong value => value.Value,
                ClientSpacecomponentsbytypeValueValueChoiceDouble value when double.IsFinite(value.Value) => value.Value,
                _ => throw new InvalidDataException($"NPC type {typeId} has no finite {name}."),
            };
        return new(Read("hostileResponseThreshold"), Read("friendlyResponseThreshold"));
    }
}
