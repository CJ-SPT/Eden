using System.Collections.Immutable;
using System.Globalization;
using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Storage;

namespace Eden.World.Mining;

internal sealed record NpcMiningOwner(int CorporationId, int FactionId, int? StrongBoxType);

internal sealed record NpcMiningHull(NpcHullProfile Hull, double Capacity, NpcMiningLaser? Laser);

[Injectable(InjectionType.Singleton)]
internal sealed class NpcMiningCatalog(WorldDatabase database, NpcProfileCatalog profiles)
{
    private static readonly CatalogDefinition<ImmutableArray<NpcMiningOwner>> s_owners = new(
        "npc-mining-owners",
        (catalog, _) => ReadOwners(catalog),
        Estimate: values => 128 + values.Length * 128L
    );
    private readonly CatalogDefinition<NpcMiningHull?> _hull = new(
        "npc-mining-hull",
        (catalog, type) => LoadHull(catalog, type, profiles),
        8192
    );

    internal IReadOnlyList<NpcMiningOwner> Owners
    {
        get
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            return catalog.Definition(s_owners, 0);
        }
    }

    internal NpcMiningHull? Hull(CatalogDbContext catalog, int type) => catalog.Definition(_hull, type);

    private static NpcMiningHull? LoadHull(CatalogDbContext catalog, int type, NpcProfileCatalog profiles)
    {
        try
        {
            if (catalog.FittingType(type) is null)
            {
                throw new InvalidDataException("Missing mining fleet type.");
            }
            var hull = profiles.Read(catalog, type);
            var definition = catalog.Read(CatalogData.Client.Types, (long)type);
            if (
                hull.Type.Mass is not > 0
                || hull.Type.Radius is not > 0
                || !double.IsFinite(hull.Type.Mass.Value + hull.Type.Radius.Value)
                || hull.Type.CategoryId != NpcEncounterDefinitions.EntityCategory
                || hull.Attributes.Values.Any(v => !double.IsFinite(v))
                || hull.Defense.Structure <= 0
                || definition?.WreckTypeID is not long wreck
                || catalog.FittingType(wreck) is null
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
            return result;
        }
        catch (InvalidDataException error)
        {
            ServerLog.Console.Warning($"NPC mining: skipped type {type}: {error.Message}");
            return null;
        }
    }

    private static ImmutableArray<NpcMiningOwner> ReadOwners(CatalogDbContext catalog)
    {
        var component = catalog.Read(CatalogData.Client.Spacecomponentsbytype, (long)NpcMiningRules.Venture);
        List<NpcMiningOwner> owners = [];
        var mapping = component?.GetValueOrDefault("sofOwnerMapper")?.GetValueOrDefault("ownerIdToMaterialSetId");
        var ownerIds = mapping is ClientSpacecomponentsbytypeValueValueChoiceMap map ? map.Value.Keys : [];
        foreach (string key in ownerIds)
        {
            int id = int.Parse(key, CultureInfo.InvariantCulture);
            var owner = catalog.Read(CatalogData.Client.Npccorporations, (long)id);
            if (owner?.FactionID is not long faction || NpcMiningRules.Roster(checked((int)faction)) is null)
            {
                continue;
            }
            var corporation = catalog.NpcCorporationName(id);
            if (corporation is null)
            {
                continue;
            }
            // These are actual catalog container names, not fabricated loot IDs.
            string boxName = corporation + " Strong Box";
            int? box = catalog.Definitions.Items.Values.Where(t => t.Name == boxName).Select(t => (int?)t.Id).SingleOrDefault();
            owners.Add(new(id, checked((int)faction), box));
        }
        return owners.OrderBy(o => o.CorporationId).ToImmutableArray();
    }
}
