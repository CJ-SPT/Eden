using System.Collections.ObjectModel;
using System.Globalization;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Protocol;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

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

[Injectable(InjectionType.Singleton)]
internal sealed class NpcSpawnCatalog(WorldDatabase database)
{
    private readonly Lazy<IReadOnlyList<NpcSpawnProfile>> _profiles = new(() => Load(database));

    internal IReadOnlyList<NpcSpawnProfile> Profiles => _profiles.Value;

    internal IReadOnlyList<NpcSpawnProfile> Pool(NpcPirateFaction faction, NpcHullClass hull) =>
        Profiles.Where(p => p.Faction == faction && p.Hull == hull).OrderBy(p => p.TypeId).ToArray();

    private static IReadOnlyList<NpcSpawnProfile> Load(WorldDatabase database)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        catalog.Database.OpenConnection();
        var groups = NpcPirateGroups.All.ToDictionary(g => g.GroupId);
        int[] groupIds = groups.Keys.ToArray();
        var types = catalog.Set<ItemType>().Where(t => groupIds.Contains(t.GroupId)).OrderBy(t => t.Id).ToArray();
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
                var definition = catalog
                    .Set<SourceRecord>()
                    .Find("client/types", type.Id.ToString(CultureInfo.InvariantCulture))
                    ?.Read<ClientTypes>();
                var faction = catalog
                    .Set<SourceRecord>()
                    .Find("client/factions", ((int)group.Faction).ToString(CultureInfo.InvariantCulture))
                    ?.Read<ClientFactions>();
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
                    || !catalog.Set<NpcCorporation>().Any(c => c.Id == corporation)
                    || !catalog.Set<ItemType>().Any(t => t.Id == wreck && t.GroupId == Eden.Gameplay.Loot.WreckDefinitions.Group)
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
                        new ReadOnlyDictionary<DogmaAttributeId, double>(attributes.ToDictionary())
                    )
                );
            }
            catch (InvalidDataException error)
            {
                Console.Error.WriteLine($"NPC population skips type {type.Id}: {error.Message}");
            }
        }
        return profiles.AsReadOnly();
    }
}
