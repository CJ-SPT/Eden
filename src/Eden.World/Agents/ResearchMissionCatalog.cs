using Eden.Data.Catalog;
using Eden.Data.Catalog.Agents;
using Eden.Gameplay.Agents;
using Eden.World.Catalog;

namespace Eden.World.Agents;

internal static class ResearchMissionCatalog
{
    // Pinned build 3662168 client/missions. Mineral Acquisition has one record per level.
    private const int FirstMineralAcquisition = 2792;
    private const int LastMineralAcquisition = 2795;
    private const int SpecialDelivery = 2804;
    private const int Difficulty = 500;

    internal static bool Supports(CatalogAgent agent) => agent is { Type: AgentType.Research, StationId: not null, Level: >= 1 and <= 4 };

    internal static bool IsResearch(int missionId) =>
        missionId is >= FirstMineralAcquisition and <= LastMineralAcquisition or SpecialDelivery;

    internal static MissionProfile Next(CatalogDbContext catalog, CatalogAgent agent, int? previousMissionId) =>
        MissionCatalog.Find(
            catalog,
            previousMissionId is >= FirstMineralAcquisition and <= LastMineralAcquisition
                ? SpecialDelivery
                : FirstMineralAcquisition + agent.Level - 1
        );

    internal static MissionDefinition? LoadDefinition(ClientMissions mission, int missionId)
    {
        if (!IsResearch(missionId))
        {
            return null;
        }
        bool trade = missionId != SpecialDelivery;
        string template = trade ? "ResearchTradeMission" : "ResearchCourierMission";
        if (mission.ContentTemplate != $"agent.missionTemplatizedContent_{template}")
        {
            return null;
        }
        string report = trade ? "Mineral_Acquisition" : "The_Special_Delivery";
        return new(
            missionId,
            trade ? missionId - FirstMineralAcquisition + 1 : 1,
            Difficulty,
            false,
            $"https://wiki.eveuniversity.org/{report}",
            "3662168",
            []
        )
        {
            Trade = trade,
        };
    }
}
