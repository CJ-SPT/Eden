using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using Eden.Gameplay.Agents;
using Eden.Infrastructure.Registration;
using Eden.World.Storage;

namespace Eden.World.Catalog;

[Injectable(InjectionType.Singleton)]
internal sealed class NpcDirectory(WorldDatabase database) : INpcDirectory
{
    private sealed record Snapshot(
        IReadOnlyList<CatalogAgent> Agents,
        IReadOnlyDictionary<long, CatalogAgent> AgentById,
        IReadOnlyDictionary<long, NpcCharacter> Characters
    );

    private static readonly CatalogDefinition<Snapshot> s_snapshot = new(
        "directory/npcs",
        (catalog, _) => Load(catalog),
        Estimate: snapshot => 1024 + snapshot.Characters.Count * 1024L + snapshot.Agents.Count * 256L
    );

    private Snapshot Read()
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return catalog.Definition(s_snapshot, 0);
    }

    public IReadOnlyList<CatalogAgent> Agents() => Read().Agents;

    public CatalogAgent? Agent(long id) => NpcCharacterIds.Contains(id) ? Read().AgentById.GetValueOrDefault(id) : null;

    public NpcCharacter? Character(long id) => NpcCharacterIds.Contains(id) ? Read().Characters.GetValueOrDefault(id) : null;

    public IReadOnlyList<NpcCharacter> Characters(IReadOnlyList<long> ids) =>
        ids.Where(NpcCharacterIds.Contains).Distinct().Order().Select(Character).OfType<NpcCharacter>().ToArray();

    private static Snapshot Load(CatalogDbContext catalog)
    {
        var raw = catalog
            .Records(CatalogData.Client.Npccharacters)
            .Select(record => (Id: record.Key, Data: record.Value))
            .Where(row => row.Data.Deleted != 1)
            .OrderBy(row => row.Id)
            .ToArray();
        var nameKeys = raw.Select(row => row.Data.NameID).OfType<long>().Distinct();
        var names = catalog
            .ReadMany(CatalogData.Client.LocalizationFsdEnUs, nameKeys)
            .ToDictionary(record => record.Key, record => record.Value.Text ?? "");
        var fallbackNames = catalog
            .Records(CatalogData.Sde.NpcCharacters)
            .ToDictionary(record => record.Key, record => record.Value.Name?.En ?? "");
        var stations = catalog.Stations().ToDictionary(station => station.Id, station => station.SolarSystemId);
        var corporations = catalog
            .Records(CatalogData.Client.Npccorporations)
            .ToDictionary(record => checked((int)record.Key), record => checked((int?)record.Value.FactionID));
        var space = catalog
            .Records(CatalogData.Client.Agentsinspace)
            .ToDictionary(record => record.Key, record => checked((int?)record.Value.SolarSystemID));
        List<CatalogAgent> agents = [];
        Dictionary<long, NpcCharacter> characters = [];
        foreach (var (id, data) in raw)
        {
            string name = names.GetValueOrDefault(data.NameID ?? 0, "");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = fallbackNames.GetValueOrDefault(id, "");
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidDataException($"NPC {id} has no pinned name.");
            }
            int corporation = checked((int)(data.CorporationID ?? throw new InvalidDataException($"NPC {id} has no corporation.")));
            long? created = DateTime.TryParse(
                data.StartDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var date
            )
                ? date.ToFileTimeUtc()
                : null;
            characters.Add(
                id,
                new(
                    new(id, name, checked((int)(data.Gender ?? -1)), data.NameID, IsNpc: true),
                    corporation,
                    checked((int?)data.RaceID),
                    checked((int?)data.BloodlineID),
                    checked((int?)data.SchoolID),
                    checked((int?)data.AncestryID),
                    created,
                    data.Description ?? ""
                )
            );
            if (data.Agent is not { AgentType: > (long)AgentType.NonAgent } agent)
            {
                continue;
            }
            int? station =
                data.StationID is > 0 and <= int.MaxValue && stations.ContainsKey((int)data.StationID) ? (int)data.StationID : null;
            int? system = station is int location ? stations[location] : space.GetValueOrDefault(id);
            agents.Add(
                new(
                    id,
                    station,
                    system,
                    corporation,
                    (AgentType)agent.AgentType.Value,
                    corporations.GetValueOrDefault(corporation),
                    checked((int)(agent.AgentDivisionID ?? 0)),
                    checked((int)(agent.AgentLevel ?? 0)),
                    agent.AgentLocateCharacter == 1,
                    checked((int?)data.BloodlineID)
                )
            );
        }
        return new(agents.ToImmutableArray(), agents.ToFrozenDictionary(agent => agent.Id), characters.ToFrozenDictionary());
    }
}
