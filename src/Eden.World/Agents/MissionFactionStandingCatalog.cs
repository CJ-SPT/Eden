using System.Collections.Immutable;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.Catalog.Agents;
using Eden.World.Catalog;

namespace Eden.World.Agents;

internal sealed record MissionFactionRelationship(int FactionId, double Standing, double SizeRatio);

internal static class MissionFactionStandingCatalog
{
    private static readonly Lazy<ILookup<int, NpcFactionStanding>> s_relations = new(ReadRelations);
    private static readonly CatalogDefinition<ImmutableArray<MissionFactionRelationship>> s_relationships = new(
        "agent-storyline-faction-standings",
        Load,
        Estimate: rows => 128L + rows.Length * 40L
    );

    internal static ImmutableArray<MissionFactionRelationship> ForFaction(CatalogDbContext catalog, int faction) =>
        catalog.Definition(s_relationships, faction);

    private static ImmutableArray<MissionFactionRelationship> Load(CatalogDbContext catalog, int faction)
    {
        double? sourceSize = catalog.Read(CatalogData.Client.Factions, (long)faction)?.SizeFactor;
        if (sourceSize is not > 0)
        {
            return [];
        }
        var relationships = s_relations.Value[faction];
        var factions = catalog.ReadMany(CatalogData.Client.Factions, relationships.Select(row => (long)row.FromId));
        return relationships
            .Where(row => factions.GetValueOrDefault(row.FromId)?.SizeFactor is > 0)
            .Select(row => new MissionFactionRelationship(
                row.FromId,
                row.Standing,
                sourceSize.Value / factions[row.FromId].SizeFactor!.Value
            ))
            .ToImmutableArray();
    }

    private static ILookup<int, NpcFactionStanding> ReadRelations()
    {
        // Factual Tranquility standingMgr.GetNPCNPCStandings cache rows recorded in the EVE.js reference.
        using var stream =
            typeof(NpcFactionStanding).Assembly.GetManifestResourceStream("Eden.Data.NpcFactionStandings.json")
            ?? throw new InvalidDataException("The NPC faction standings snapshot is missing.");
        var rows = JsonSerializer.Deserialize<ImmutableArray<NpcFactionStanding>>(stream, JsonSerializerOptions.Web);
        return rows.Where(row => row.Standing != 0).ToLookup(row => row.ToId);
    }
}
