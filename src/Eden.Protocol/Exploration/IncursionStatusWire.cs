using Eden.Gameplay.Exploration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Exploration;

internal sealed record LocalIncursionStatus(IncursionAgencyUpdate Focus, IncursionSystem System);

internal sealed record IncursionTaleData(LocalIncursionStatus Status) : PilotNotification("OnTaleData")
{
    public override object?[] Arguments =>
        [
            Status.System.SystemId,
            new BlueMap<long, IncursionTale>(new Dictionary<long, IncursionTale> { [Status.Focus.TaleId] = new(Status) }),
        ];
}

internal sealed record IncursionTaleRemoved(long TaleId) : PilotNotification("OnTaleRemove")
{
    // Build 3662168 identifies this HUD by the class. This focus has no retail tale template ID.
    public override object?[] Arguments => [TaleId, IncursionReport.TemplateClassId, null];
}

internal sealed record IncursionTale(LocalIncursionStatus Status) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "util.KeyVal",
            new Dictionary<string, object?>
            {
                ["taleID"] = Status.Focus.TaleId,
                ["templateClassID"] = IncursionReport.TemplateClassId,
                ["templateNameID"] = IncursionReport.TemplateNameId,
                ["locationID"] = Status.System.SystemId,
                ["severity"] = (int)Status.System.SceneType,
                ["incursedSystems"] = new BlueList<int>(Status.Focus.Systems!.Select(system => system.SystemId).ToArray()),
                ["hasFinalEncounter"] = Status.Focus.HasFinalEncounter,
                ["influenceData"] = new IncursionInfluenceData(Status.Focus),
                ["effects"] = IncursionEffectIcons.ForScene(Status.System.SceneType),
                ["hasChat"] = false,
                ["musicState"] = null,
            }
        );
}

internal static class IncursionEffectIcons
{
    private static readonly IncursionEffectIcon[] s_staging =
    [
        new("effectIcon_cyno", "effectCyno.png", "SystemEffectCynoHint", false),
        new("effectIcon_tax", "effectTax.png", "SystemEffectTaxHint", false),
    ];

    private static readonly BlueList<IncursionEffectIcon> s_stagingIcons = new(s_staging);

    internal static BlueList<IncursionEffectIcon> ForConstellation { get; } =
        new([
            .. s_staging,
            new("effectIcon_tank", "effectResistanceDecrease.png", "SystemEffectTankingHint", true),
            new("effectIcon_damage", "effectDamageDecrease.png", "SystemEffectDamageHint", true),
        ]);

    internal static BlueList<IncursionEffectIcon> ForScene(IncursionScene scene) =>
        scene == IncursionScene.Staging ? s_stagingIcons : ForConstellation;
}

// The pinned Agency and Incursion HUD index these dictionaries by name, texturePath, hint and isScalable.
internal sealed record IncursionEffectIcon(string Name, string Texture, string Hint, bool IsScalable) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["name"] = Name,
            ["texturePath"] = "res:/UI/Texture/classes/InfluenceBar/" + Texture,
            ["hint"] = "UI/Incursion/HUD/" + Hint,
            ["isScalable"] = IsScalable,
        };
}
