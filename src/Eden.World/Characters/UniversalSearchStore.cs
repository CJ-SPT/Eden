using System.Collections.Immutable;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

[Injectable]
internal sealed class UniversalSearchStore(WorldDatabase database) : IUniversalSearch
{
    private static readonly CatalogDefinition<ImmutableDictionary<SearchCategory, ImmutableArray<SearchEntry>>> s_index = new(
        "search/universe",
        (catalog, _) => Load(catalog),
        Estimate: index => 1024 + index.Values.Sum(entries => entries.Sum(entry => 96L + entry.Name.Length * 2L))
    );

    public IReadOnlyList<SearchGroup> Search(SearchRequest request)
    {
        UniversalSearchRules.Check(request);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var index = catalog.Definition(s_index, 0);
        using WorldDbContext world = new(database.Path);
        List<SearchGroup> groups = [];
        foreach (var category in request.Categories)
        {
            IEnumerable<SearchEntry> entries = index.GetValueOrDefault(category, []);
            if (category == SearchCategory.Character)
            {
                entries = entries.Concat(world.Pilots.AsNoTracking().Select(pilot => new SearchEntry(category, pilot.Id, pilot.Name)));
            }
            if (category == SearchCategory.Corporation)
            {
                entries = entries.Concat(
                    world
                        .Set<SavedCorporation>()
                        .AsNoTracking()
                        .Select(corporation => new SearchEntry(category, corporation.Id, corporation.Name))
                );
            }
            groups.Add(new(category, UniversalSearchRules.Select(request, entries)));
        }
        return groups;
    }

    public IReadOnlyList<SearchEntry> Owners(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var index = catalog.Definition(s_index, 0);
        var requested = ids.ToHashSet();
        return new[] { SearchCategory.Corporation, SearchCategory.Faction }
            .SelectMany(category => index.GetValueOrDefault(category, []))
            .Where(entry => requested.Contains(entry.Id))
            .OrderBy(entry => entry.Id)
            .ToArray();
    }

    private static ImmutableDictionary<SearchCategory, ImmutableArray<SearchEntry>> Load(CatalogDbContext catalog)
    {
        var definitions = catalog.Definitions;
        List<SearchEntry> entries = [];
        entries.AddRange(definitions.Systems.Values.Select(system => new SearchEntry(SearchCategory.SolarSystem, system.Id, system.Name)));
        entries.AddRange(
            definitions.Constellations.Values.Select(value => new SearchEntry(SearchCategory.Constellation, value.Id, value.Name))
        );
        entries.AddRange(definitions.Regions.Select(region => new SearchEntry(SearchCategory.Region, region.Key, region.Value)));
        entries.AddRange(definitions.StationNames.Select(station => new SearchEntry(SearchCategory.Station, station.Key, station.Value)));
        entries.AddRange(
            definitions.NpcCorporations.Select(corporation => new SearchEntry(
                SearchCategory.Corporation,
                corporation.Key,
                corporation.Value,
                true
            ))
        );
        var published = catalog
            .Records(CatalogData.Client.Types)
            .Where(type => type.Value.Published == 1)
            .Select(type => type.Key)
            .ToHashSet();
        entries.AddRange(
            definitions
                .Items.Values.Where(type => published.Contains(type.Id))
                .Select(type => new SearchEntry(SearchCategory.ItemType, type.Id, type.Name))
        );

        var npcs = catalog.Records(CatalogData.Client.Npccharacters).Where(npc => npc.Value.Deleted != 1).ToArray();
        var factions = catalog.Records(CatalogData.Client.Factions);
        var nameIds = npcs.Select(npc => npc.Value.NameID).Concat(factions.Values.Select(faction => faction.NameID)).OfType<long>();
        var names = catalog.ReadMany(CatalogData.Client.LocalizationFsdEnUs, nameIds);
        var npcFallback = catalog.Records(CatalogData.Sde.NpcCharacters);
        foreach (var (id, npc) in npcs)
        {
            string? name = names.GetValueOrDefault(npc.NameID ?? 0)?.Text;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = npcFallback.GetValueOrDefault(id)?.Name?.En;
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                entries.Add(new(SearchCategory.Character, id, name, true));
                if (npc.Agent is { AgentType: > (long)AgentType.NonAgent })
                {
                    entries.Add(new(SearchCategory.Agent, id, name, true));
                }
            }
        }
        var factionFallback = catalog.Records(CatalogData.Sde.Factions);
        foreach (var (id, faction) in factions)
        {
            string? name = names.GetValueOrDefault(faction.NameID ?? 0)?.Text;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = factionFallback.GetValueOrDefault(id)?.Name?.En;
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                entries.Add(new(SearchCategory.Faction, id, name, true));
            }
        }
        return entries.GroupBy(entry => entry.Category).ToImmutableDictionary(group => group.Key, group => group.ToImmutableArray());
    }
}
