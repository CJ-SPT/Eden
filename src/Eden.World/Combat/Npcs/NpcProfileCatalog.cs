using System.Collections.Immutable;
using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
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
        var source = ReadAttributes(catalog, typeId);
        var attributes = (NpcMiningRules.Types.Contains(typeId) ? NpcMiningRules.CombatAttributes(source) : source).ToImmutableDictionary();
        return new(
            type,
            attributes,
            ShipHealth.DefenseFrom(attributes),
            NpcEwarRules
                .Profile(
                    FittingCatalog.Effects(catalog, typeId),
                    attributes,
                    type.GroupId is NpcEncounterDefinitions.SentryGroup or NpcEncounterDefinitions.OverseerSentryGroup,
                    concord: typeId == ConcordRules.CaptainType
                )
                .ToImmutableArray()
        )
        {
            StandingThresholds = ReadStandingThresholds(catalog, typeId),
        };
    }

    internal static IReadOnlyDictionary<DogmaAttributeId, double> ReadAttributes(CatalogDbContext catalog, int typeId)
    {
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
        var effects = FittingCatalog.Effects(catalog, typeId);
        bool missile = effects.Contains(DogmaEffect.MissileLaunchingForEntity);
        bool turret = effects.Contains(DogmaEffect.LaserFired);
        if (
            attributes.GetValueOrDefault(DogmaAttributeId.EntityMissileType) > 0 && !missile
            || attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime) > 0 && !turret
        )
        {
            // Some turret-only hulls retain a missile type and a one-millisecond
            // launch duration; support-only drones can retain turret attributes.
            // A weapon's declared effect must enable its attributes.
            return attributes
                .Where(pair => pair.Key != DogmaAttributeId.EntityMissileType || missile)
                .Where(pair => pair.Key != DogmaAttributeId.WeaponCycleTime || turret)
                .ToImmutableDictionary();
        }
        return attributes;
    }

    internal static NpcStandingThresholds? ReadStandingThresholds(CatalogDbContext catalog, int typeId)
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
