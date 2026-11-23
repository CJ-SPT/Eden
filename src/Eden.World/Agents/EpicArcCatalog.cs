using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.Catalog.Agents;
using Eden.Gameplay.Agents;
using Eden.World.Catalog;

namespace Eden.World.Agents;

internal sealed record EpicArcNode(int Id, long AgentId, ImmutableArray<int> Successors, int? FailureMissionId);

internal sealed record EpicArc(int Id, long ReplayTicks, ImmutableDictionary<int, EpicArcNode> Nodes, ImmutableArray<EpicArcNode> Roots);

internal static class EpicArcCatalog
{
    private const int VisionsOfGreatness = 132;
    private static readonly Lazy<ImmutableDictionary<int, MissionDefinition>> s_content = new(() =>
    {
        using var stream = typeof(EpicArcCatalog).Assembly.GetManifestResourceStream("Eden.World.EpicArcMissions.json")!;
        var content = JsonSerializer.Deserialize<MissionContent>(stream, JsonSerializerOptions.Web);
        if (content?.Build != CatalogSources.PinnedBuild || content.Missions.Any(m => m.EpicArcId is null || m.FixedAgentIds.Length != 1))
        {
            throw new InvalidDataException("Invalid epic arc content for the pinned client.");
        }
        return content.Missions.ToImmutableDictionary(m => m.MissionId);
    });

    private static readonly Lazy<ILookup<long, int>> s_agents = new(() =>
        s_content
            .Value.Values.GroupBy(m => m.FixedAgentIds[0])
            .SelectMany(g => g.Select(m => m.EpicArcId!.Value).Distinct().Select(arc => (Agent: g.Key, Arc: arc)))
            .ToLookup(pair => pair.Agent, pair => pair.Arc)
    );

    private static readonly Lazy<IReadOnlySet<long>> s_agentIds = new(() => s_agents.Value.Select(agents => agents.Key).ToHashSet());
    private static readonly CatalogDefinition<EpicArc> s_arc = new("epic-arc", Read);
    private static readonly CatalogDefinition<ImmutableArray<EpicArcMessage>> s_messages = new("epic-arc-messages", ReadMessages);

    internal static IReadOnlySet<long> AgentIds => s_agentIds.Value;

    internal static MissionDefinition? Find(int missionId) => s_content.Value.GetValueOrDefault(missionId);

    internal static bool IsSimulation(int missionId) => Find(missionId)?.EpicArcId == VisionsOfGreatness;

    internal static IEnumerable<EpicArc> ForAgent(CatalogDbContext catalog, long agentId) =>
        s_agents.Value[agentId].Select(id => Get(catalog, id));

    internal static EpicArc Get(CatalogDbContext catalog, int id) => catalog.Definition(s_arc, id);

    internal static IReadOnlyList<EpicArcMessage> Messages(CatalogDbContext catalog) => catalog.Definition(s_messages, 0);

    private static EpicArc Read(CatalogDbContext catalog, int id)
    {
        var native = catalog.Require(CatalogData.Client.Epicarcs, id);
        var compiled = s_content.Value.Values.Where(m => m.EpicArcId == id).ToDictionary(m => m.MissionId);
        if (native.Missions is null || native.Missions.Count != compiled.Count || native.ArcRestartInterval is not > 0)
        {
            throw new InvalidDataException("Epic arc manifest does not match the pinned graph.");
        }
        var nodes = native.Missions.ToImmutableDictionary(
            pair => int.Parse(pair.Key, CultureInfo.InvariantCulture),
            pair =>
            {
                int mission = int.Parse(pair.Key, CultureInfo.InvariantCulture);
                var stage = pair.Value;
                if (
                    !compiled.TryGetValue(mission, out var definition)
                    || definition.FixedAgentIds[0] != stage.AgentID
                    || definition.CargoLossRetryMissionId is int target && (!definition.FailOnCargoLoss || !compiled.ContainsKey(target))
                )
                {
                    throw new InvalidDataException("Epic arc stage does not match its pinned agent.");
                }
                return new EpicArcNode(
                    mission,
                    stage.AgentID!.Value,
                    (stage.NextMissions ?? []).Select(m => checked((int)m)).ToImmutableArray(),
                    checked((int?)stage.FailMissionID)
                );
            }
        );
        var successors = nodes.Values.SelectMany(m => m.Successors).ToHashSet();
        if (
            successors.Any(next => !nodes.ContainsKey(next))
            || nodes.Values.Any(n => n.FailureMissionId is int failed && !nodes.ContainsKey(failed))
        )
        {
            throw new InvalidDataException("Epic arc stage refers to a mission outside the pinned graph.");
        }
        var roots = nodes.Values.Where(n => !successors.Contains(n.Id)).OrderBy(n => n.Id).ToImmutableArray();
        if (roots.IsEmpty)
        {
            throw new InvalidDataException("Epic arc has no entry point.");
        }
        return new(checked((int)id), checked(native.ArcRestartInterval.Value * TimeSpan.TicksPerMinute), nodes, roots);
    }

    private static ImmutableArray<EpicArcMessage> ReadMessages(CatalogDbContext catalog, int _)
    {
        const string prefix = "messages.epicMission.journalText.";
        return catalog
            .ReadMany(CatalogData.Client.Missions, s_content.Value.Keys.Select(id => (long)id))
            .OrderBy(pair => pair.Key)
            .SelectMany(pair =>
                pair.Value.Messages?.Where(m => m.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(m => new EpicArcMessage(checked((int)pair.Key), m.Key, checked((int)m.Value)))
                ?? []
            )
            .ToImmutableArray();
    }
}
