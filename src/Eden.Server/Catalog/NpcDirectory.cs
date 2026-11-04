using System.Globalization;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Protocol;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Catalog;

[Injectable(InjectionType.Singleton)]
internal sealed class NpcDirectory(WorldDatabase database) : INpcDirectory
{
    // The pinned catalog is immutable until restart, as with StationDirectory.
    private sealed record Snapshot(
        IReadOnlyList<CatalogAgent> Agents,
        IReadOnlyDictionary<long, CatalogAgent> AgentById,
        IReadOnlyDictionary<long, NpcCharacter> Characters
    );

    private readonly Lazy<Snapshot> _snapshot = new(() => Load(database.CatalogPath));

    public IReadOnlyList<CatalogAgent> Agents() => _snapshot.Value.Agents;

    public CatalogAgent? Agent(long id) => NpcCharacterIds.Contains(id) ? _snapshot.Value.AgentById.GetValueOrDefault(id) : null;

    public NpcCharacter? Character(long id) => NpcCharacterIds.Contains(id) ? _snapshot.Value.Characters.GetValueOrDefault(id) : null;

    public IReadOnlyList<NpcCharacter> Characters(IReadOnlyList<long> ids) =>
        ids.Where(NpcCharacterIds.Contains).Distinct().Order().Select(Character).OfType<NpcCharacter>().ToArray();

    private static Snapshot Load(string path)
    {
        using CatalogDbContext catalog = new(path);
        var raw = catalog
            .Set<SourceRecord>()
            .Where(record => record.Dataset == "client/npccharacters")
            .AsEnumerable()
            .Select(record => (Id: long.Parse(record.Key, CultureInfo.InvariantCulture), Data: record.Read<ClientNpccharacters>()))
            .Where(row => row.Data.Deleted != 1)
            .OrderBy(row => row.Id)
            .ToArray();
        var nameKeys = raw.Select(row => row.Data.NameID?.ToString(CultureInfo.InvariantCulture)).OfType<string>().Distinct().ToArray();
        var names = catalog
            .Set<SourceRecord>()
            .Where(record => record.Dataset == "client/localization_fsd_en-us" && nameKeys.Contains(record.Key))
            .AsEnumerable()
            .ToDictionary(
                record => long.Parse(record.Key, CultureInfo.InvariantCulture),
                record => record.Read<ClientLocalizationFsdDe>().Text ?? ""
            );
        var fallbackNames = catalog
            .Set<SourceRecord>()
            .Where(record => record.Dataset == "sde/npcCharacters")
            .AsEnumerable()
            .ToDictionary(
                record => long.Parse(record.Key, CultureInfo.InvariantCulture),
                record => record.Read<SdeNpcCharacters>().Name?.En ?? ""
            );
        var stations = catalog.Set<Station>().AsNoTracking().ToDictionary(station => station.Id, station => station.SolarSystemId);
        var corporations = catalog
            .Set<SourceRecord>()
            .Where(record => record.Dataset == "client/npccorporations")
            .AsEnumerable()
            .ToDictionary(
                record => int.Parse(record.Key, CultureInfo.InvariantCulture),
                record => checked((int?)record.Read<ClientNpccorporations>().FactionID)
            );
        var space = catalog
            .Set<SourceRecord>()
            .Where(record => record.Dataset == "client/agentsinspace")
            .AsEnumerable()
            .ToDictionary(
                record => long.Parse(record.Key, CultureInfo.InvariantCulture),
                record => checked((int?)record.Read<ClientAgentsinspace>().SolarSystemID)
            );
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
                    agent.AgentLocateCharacter == 1
                )
            );
        }
        return new(agents, agents.ToDictionary(agent => agent.Id), characters);
    }
}
