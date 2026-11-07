using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat.Weapons;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

internal sealed record NpcSpawnProfile(
    int TypeId,
    string Name,
    int GroupId,
    NpcPirateFaction Faction,
    NpcHullClass Hull,
    int CorporationId,
    double Mass,
    double Radius,
    int WreckTypeId,
    double MissileRange,
    IReadOnlyDictionary<DogmaAttributeId, double> Attributes
);

internal readonly record struct NpcSpawnPoolKey(NpcPirateFaction Faction, NpcHullClass Hull);

internal sealed record NpcSpawnDefinitions(
    ImmutableArray<NpcSpawnProfile> Profiles,
    FrozenDictionary<NpcSpawnPoolKey, ImmutableArray<NpcSpawnProfile>> Pools
);

[Injectable(InjectionType.Singleton)]
internal sealed class NpcSpawnCatalog(WorldDatabase database)
{
    private static readonly CatalogDefinition<NpcSpawnDefinitions> s_profiles = new(
        "npc-spawn-profiles",
        (catalog, _) => Index(Load(catalog)),
        Estimate: values => 128 + values.Profiles.Length * 8192L
    );

    private NpcSpawnDefinitions Definitions
    {
        get
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            return catalog.Definition(s_profiles, 0);
        }
    }

    internal IReadOnlyList<NpcSpawnProfile> Profiles => Definitions.Profiles;

    internal IReadOnlyList<NpcSpawnProfile> Pool(NpcPirateFaction faction, NpcHullClass hull) =>
        Definitions.Pools.GetValueOrDefault(new(faction, hull), []);

    private static NpcSpawnDefinitions Index(ImmutableArray<NpcSpawnProfile> profiles) =>
        new(
            profiles,
            profiles
                .GroupBy(profile => new NpcSpawnPoolKey(profile.Faction, profile.Hull))
                .ToFrozenDictionary(group => group.Key, group => group.OrderBy(profile => profile.TypeId).ToImmutableArray())
        );

    private static ImmutableArray<NpcSpawnProfile> Load(CatalogDbContext catalog)
    {
        var groups = NpcPirateGroups.All.ToDictionary(g => g.GroupId);
        int[] groupIds = groups.Keys.ToArray();
        var types = groupIds.SelectMany(group => catalog.Definitions.TypesInGroup(group)).Select(catalog.RequireItem).OrderBy(t => t.Id);
        List<NpcSpawnProfile> profiles = [];
        foreach (var type in types)
        {
            if (NpcSpecialTypes.Excluded.Contains(type.Id))
            {
                continue;
            }
            try
            {
                var group = groups[type.GroupId];
                var attributes = PilotFittingStore.ReadAttributes(catalog, type.Id);
                var definition = catalog.Read(CatalogData.Client.Types, (long)type.Id);
                var faction = catalog.Read(CatalogData.Client.Factions, (long)(((int)group.Faction)));
                int corporation = checked((int)(faction?.CorporationID ?? 0));
                int wreck = checked((int)(definition?.WreckTypeID ?? 0));
                if (
                    type.Mass is not > 0
                    || type.Radius is not > 0
                    || attributes.Values.Any(v => !double.IsFinite(v))
                    || !double.IsFinite(type.Mass.Value)
                    || !double.IsFinite(type.Radius.Value)
                    || attributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity) <= 0
                    || attributes.GetValueOrDefault(DogmaAttributeId.StructureHitPoints) <= 0
                    || attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity) < 0
                    || attributes.GetValueOrDefault(DogmaAttributeId.ArmorHitPoints) < 0
                    || attributes.GetValueOrDefault(DogmaAttributeId.EntityAttackRange) <= 0
                    || attributes.GetValueOrDefault(DogmaAttributeId.EntityFlyRange) < 0
                    || attributes.GetValueOrDefault(DogmaAttributeId.EntityAttackDelayMinimum) < 0
                    || corporation <= 0
                    || catalog.NpcCorporationName(corporation) is null
                    || catalog.FittingType(wreck)?.GroupId != Eden.Gameplay.Loot.WreckDefinitions.Group
                )
                {
                    throw new InvalidDataException("Incomplete NPC identity, movement, health or wreck definition.");
                }
                _ = NpcMotionRules.SpeedFraction(attributes);
                bool turret = NpcPopulationRules.HasDirectWeapon(attributes);
                bool missile = attributes.GetValueOrDefault(DogmaAttributeId.EntityMissileType) > 0;
                if (
                    turret
                    && (
                        attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime) <= 0
                        || attributes.GetValueOrDefault(DogmaAttributeId.TrackingSpeed) <= 0
                        || attributes.GetValueOrDefault(DogmaAttributeId.WeaponSignatureResolution) <= 0
                        || attributes.GetValueOrDefault(DogmaAttributeId.DamageMultiplier) <= 0
                    )
                )
                {
                    throw new InvalidDataException("Incomplete NPC turret profile.");
                }
                double missileRange = 0;
                if (missile)
                {
                    int charge = checked((int)attributes[DogmaAttributeId.EntityMissileType]);
                    var warhead = PilotFittingStore.ReadAttributes(catalog, charge);
                    missileRange = NpcMissileRules.Drive(MissileChargeCatalog.Drive(catalog, charge, warhead), attributes).NominalRange;
                    _ = NpcMissileRules.Warhead(warhead, attributes);
                    if (attributes.GetValueOrDefault(DogmaAttributeId.EntityMissileCycle) <= 0)
                    {
                        throw new InvalidDataException("Incomplete NPC missile cycle.");
                    }
                }
                if (!turret && !missile)
                {
                    throw new InvalidDataException("NPC has no supported weapon.");
                }
                profiles.Add(
                    new(
                        type.Id,
                        type.Name,
                        type.GroupId,
                        group.Faction,
                        group.Hull,
                        corporation,
                        type.Mass.Value,
                        type.Radius.Value,
                        wreck,
                        missileRange,
                        attributes.ToImmutableDictionary()
                    )
                );
            }
            catch (InvalidDataException error)
            {
                ServerLog.Console.Warning($"NPC population skips type {type.Id}: {error.Message}");
            }
        }
        return profiles.ToImmutableArray();
    }
}
