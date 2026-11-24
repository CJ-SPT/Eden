using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Exploration;

internal sealed record IncursionReport(IncursionFocus Focus) : BlueModel
{
    // Build 3662168 localization identifies UI/TaleDistributions/SanshaIncursion.
    internal const int TemplateNameId = 507300;

    // Client build 3662168 talecommon.const and Journal's state labels.
    private const int TemplateClassId = 2;
    private const int Established = 2;

    public override object ToBlue() =>
        new BlueInstance(
            "util.KeyVal",
            new Dictionary<string, object?>
            {
                ["taleID"] = Focus.TaleId,
                ["templateClassID"] = TemplateClassId,
                ["templateNameID"] = TemplateNameId,
                ["stagingSolarSystemID"] = Focus.StagingSystemId,
                ["aggressorFactionID"] = (int)NpcPirateFaction.Sansha,
                ["state"] = Established,
                // The native bar uses its blue (CONCORD) width and displays the complement as pirate influence.
                ["influence"] = 1 - Focus.Influence,
                ["lastUpdated"] = Focus.LastUpdated,
                ["graceTime"] = 0,
                ["decayRate"] = 0d,
                ["hasFinalEncounter"] = Focus.HasFinalEncounter,
                ["effects"] = BlueEmpty.List,
                ["rewardGroupID"] = Focus.TaleId,
                ["incursedSystems"] = new BlueList<int>(Focus.Systems.Select(system => system.SystemId).ToArray()),
                ["severity"] = (int)IncursionScene.Staging,
                ["hasChat"] = false,
                ["chatAnnouncementMessageId"] = 0,
                ["musicState"] = null,
            }
        );
}

internal sealed record IncursionSystemList(IReadOnlyList<IncursionSystem> Systems) : BlueModel
{
    private static readonly BlueList<string> s_columns = new(["locationID", "sceneType", "templateNameID"]);

    public override object ToBlue() =>
        new BlueInstance(
            "eve.common.script.sys.rowset.Rowset",
            new Dictionary<string, object?>
            {
                ["header"] = s_columns,
                ["columns"] = s_columns,
                // Rowset.__getitem__ in build 3662168 constructs each yielded row via this class.
                ["RowClass"] = new BlueGlobal("util.Row"),
                ["lines"] = new BlueList<IncursionSystemRow>(Systems.Select(system => new IncursionSystemRow(system)).ToArray()),
            }
        );
}

internal sealed record IncursionSystemRow(IncursionSystem System) : BlueModel
{
    public override object ToBlue() => new object?[] { System.SystemId, (int)System.SceneType, IncursionReport.TemplateNameId };
}

internal sealed record IncursionDelayedReward(IncursionPendingLoyalty Loyalty) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<long, object?> { [Loyalty.TaleId] = new BlueList<IncursionLoyaltyAmount>([new(Loyalty.Amount)]) };
}

internal sealed record IncursionLoyaltyAmount(long Amount) : BlueModel
{
    // grouprewards in client build 3662168 assigns loyalty points reward type 1.
    private const int LoyaltyPoints = 1;

    public override object ToBlue() =>
        new BlueInstance("util.KeyVal", new Dictionary<string, object?> { ["rewardTypeID"] = LoyaltyPoints, ["rewardQuantity"] = Amount });
}
