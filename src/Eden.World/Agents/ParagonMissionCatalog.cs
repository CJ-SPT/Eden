using Eden.Data.Catalog;
using Eden.Data.Catalog.Agents;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Redemption;
using Eden.World.Catalog;

namespace Eden.World.Agents;

internal static class ParagonMissionCatalog
{
    private const string Template = "agent.missionTemplatizedContent_HeraldryMission";
    private static readonly CatalogDefinition<IReadOnlyDictionary<int, ClientMissions>> s_missions = new(
        "paragon-missions",
        (catalog, _) =>
            catalog
                .Records(CatalogData.Client.Missions)
                .Where(pair => IsSupported(catalog, pair.Value))
                .ToDictionary(pair => checked((int)pair.Key), pair => pair.Value)
    );

    internal static IReadOnlyDictionary<int, ClientMissions> Read(CatalogDbContext catalog) => catalog.GlobalDefinition(s_missions);

    internal static bool IsAgent(CatalogAgent agent) =>
        agent.Type == AgentType.Heraldry && agent.CorporationId == CurrencyRewardRules.ParagonCorporation && agent.StationId is not null;

    internal static MissionDefinition? Load(CatalogDbContext catalog, ClientMissions mission, int missionId) =>
        IsSupported(catalog, mission) ? new(missionId, 1, 0, false, "build-3662168 client/missions", "3662168", []) { Trade = true } : null;

    private static bool IsSupported(CatalogDbContext catalog, ClientMissions mission) =>
        mission.ContentTemplate == Template
        && mission.AgentTypeID == (int)AgentType.Heraldry
        && mission.CorporationID == CurrencyRewardRules.ParagonCorporation
        && mission.HasStandingRewards == 0
        && mission.KillMission is null
        && mission.ExpirationTime is > 0 and <= 10080
        && mission.FixedLpRewardAlpha > 0
        && mission.FixedLpRewardOmega > 0
        && mission.CourierMission is { ObjectiveSingleton: 0, ObjectiveTypeID: > 0 and <= int.MaxValue, ObjectiveQuantity: 1 } cargo
        && catalog.FittingType(cargo.ObjectiveTypeID.Value)?.CategoryId == (int)InventoryCategory.Ship;
}
