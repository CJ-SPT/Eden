using System.Globalization;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Storage;

namespace Eden.World.Mining;

internal sealed record NpcOwnerMaterials(IReadOnlyDictionary<string, int> OwnerIdToMaterialSetId);

internal sealed record NpcMiningComponents(NpcOwnerMaterials? SofOwnerMapper);

internal sealed record NpcMiningOwner(int CorporationId, int FactionId, int? StrongBoxType);

internal sealed record NpcMiningHull(NpcHullProfile Hull, double Capacity, NpcMiningLaser? Laser);

[Injectable(InjectionType.Singleton)]
internal sealed class NpcMiningCatalog(WorldDatabase database, NpcProfileCatalog profiles)
{
    private readonly Lazy<IReadOnlyList<NpcMiningOwner>> _owners = new(() => ReadOwners(database));
    private readonly Dictionary<int, NpcMiningHull?> _hulls = [];
    internal IReadOnlyList<NpcMiningOwner> Owners => _owners.Value;

    internal NpcMiningHull? Hull(CatalogDbContext catalog, int type)
    {
        if (_hulls.TryGetValue(type, out var cached))
        {
            return cached;
        }
        try
        {
            if (!catalog.Set<ItemType>().Any(t => t.Id == type))
            {
                throw new InvalidDataException("Missing mining fleet type.");
            }
            var hull = profiles.Read(catalog, type);
            var definition = catalog.FindSource("client/types", Key(type))?.Read<ClientTypes>();
            if (
                hull.Type.Mass is not > 0
                || hull.Type.Radius is not > 0
                || !double.IsFinite(hull.Type.Mass.Value + hull.Type.Radius.Value)
                || hull.Type.Group.CategoryId != NpcEncounterDefinitions.EntityCategory
                || hull.Attributes.Values.Any(v => !double.IsFinite(v))
                || hull.Defense.Structure <= 0
                || definition?.WreckTypeID is not long wreck
                || !catalog.Set<ItemType>().Any(t => t.Id == wreck)
            )
            {
                throw new InvalidDataException("Missing mining fleet hull, health or wreck data.");
            }
            _ = NpcMotionRules.SpeedFraction(hull.Attributes);
            NpcMiningLaser? laser = NpcMiningRules.IsMinerType(type) ? NpcMiningRules.Laser(hull.Attributes) : null;
            var result = new NpcMiningHull(hull, hull.Type.Capacity ?? 0, laser);
            if (laser is not null && result.Capacity <= 0)
            {
                throw new InvalidDataException("Missing miner hold capacity.");
            }
            _hulls[type] = result;
            return result;
        }
        catch (InvalidDataException error)
        {
            ServerLog.Console.Warning($"NPC mining: skipped type {type}: {error.Message}");
            _hulls[type] = null;
            return null;
        }
    }

    private static IReadOnlyList<NpcMiningOwner> ReadOwners(WorldDatabase database)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var component = catalog.FindSource("client/spacecomponentsbytype", Key(NpcMiningRules.Venture))?.Read<NpcMiningComponents>();
        List<NpcMiningOwner> owners = [];
        foreach (string key in component?.SofOwnerMapper?.OwnerIdToMaterialSetId.Keys ?? [])
        {
            int id = int.Parse(key, CultureInfo.InvariantCulture);
            var owner = catalog.FindSource("client/npccorporations", key)?.Read<ClientNpccorporations>();
            if (owner?.FactionID is not long faction || NpcMiningRules.Roster(checked((int)faction)) is null)
            {
                continue;
            }
            var corporation = catalog.Set<NpcCorporation>().Find(id);
            if (corporation is null)
            {
                continue;
            }
            // These are actual catalog container names, not fabricated loot IDs.
            string boxName = corporation.Name + " Strong Box";
            int? box = catalog.Set<ItemType>().Where(t => t.Name == boxName).Select(t => (int?)t.Id).SingleOrDefault();
            owners.Add(new(id, checked((int)faction), box));
        }
        return owners.OrderBy(o => o.CorporationId).ToArray();
    }

    internal static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);
}
