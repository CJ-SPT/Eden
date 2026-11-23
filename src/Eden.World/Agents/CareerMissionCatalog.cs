using System.Collections.Immutable;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.Catalog.Agents;
using Eden.Gameplay.Agents;
using Eden.World.Catalog;

namespace Eden.World.Agents;

internal static class CareerMissionCatalog
{
    private static readonly Lazy<ImmutableDictionary<int, MissionDefinition>> s_definitions = new(() =>
    {
        using var stream = typeof(CareerMissionCatalog).Assembly.GetManifestResourceStream("Eden.World.CareerMissions.json")!;
        var content = JsonSerializer.Deserialize<MissionContent>(stream, JsonSerializerOptions.Web);
        if (content?.Build != CatalogSources.PinnedBuild || content.Missions.Any(m => !m.Career || m.Chain is null))
        {
            throw new InvalidDataException("Invalid career mission content for the pinned client.");
        }
        MissionChainRules.Validate(
            content.Missions.Select(m => new MissionChainNode(m.MissionId, m.Level, m.Storyline, m.Chain!)).ToArray()
        );
        return content.Missions.ToImmutableDictionary(m => m.MissionId);
    });

    private static readonly Lazy<ILookup<long, MissionDefinition>> s_assignments = new(() =>
        s_definitions
            .Value.Values.SelectMany(m => m.FixedAgentIds.Select(id => (Agent: id, Mission: m)))
            .ToLookup(pair => pair.Agent, pair => pair.Mission)
    );

    internal static MissionDefinition? Find(int id) => s_definitions.Value.GetValueOrDefault(id);

    internal static IReadOnlyList<MissionDefinition> ForAgent(CatalogDbContext catalog, CatalogAgent agent)
    {
        if (agent is not { Type: AgentType.Career, StationId: not null, Level: 1 })
        {
            return [];
        }
        var assigned = s_assignments.Value[agent.Id].ToArray();
        var authored = catalog.Read(CatalogData.Client.Npccharacters, agent.Id)?.Agent?.StoryLineMissions ?? [];
        return assigned.Length == authored.Length && assigned.Select(m => (long)m.MissionId).ToHashSet().SetEquals(authored)
            ? assigned
            : [];
    }
}
