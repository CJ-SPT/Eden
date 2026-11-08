using Eden.Gameplay.Industry;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Industry;

internal sealed record IndustryFacilityReply(IndustryFacility Facility, IReadOnlyList<int> Products, IReadOnlyList<int> ScienceCategories)
    : BlueModel
{
    public override object ToBlue() => IndustryWire.Facility(Facility, Products, ScienceCategories);
}

internal sealed record BlueprintReply(OwnedBlueprint Blueprint) : BlueModel
{
    public override object ToBlue() => IndustryWire.Blueprint(Blueprint);
}

internal sealed record IndustryJobReply(IndustryJobDetails Job) : BlueModel
{
    public override object ToBlue() => IndustryWire.Job(Job);
}

internal sealed record IndustryLocationReply(IndustryFacility Facility, long PilotId) : BlueModel
{
    public override object ToBlue() => IndustryWire.Location(Facility, PilotId);
}

internal sealed record BlueprintLimits(int MaximumResults) : BlueModel
{
    public override object ToBlue() => new Dictionary<string, object?> { ["maxBlueprintResults"] = MaximumResults };
}

internal sealed record BlueprintOwnerReply(IReadOnlyList<OwnedBlueprint> Blueprints, int Limit) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            new BlueList<BlueprintReply>(Blueprints.Take(Limit).Select(blueprint => new BlueprintReply(blueprint)).ToArray()),
            new BlueMap<int, long>(
                Blueprints
                    .GroupBy(blueprint => blueprint.Facility.Id)
                    .ToDictionary(group => group.Key, group => group.Sum(blueprint => (long)blueprint.Item.Quantity))
            ),
        };
}

internal sealed record IndustryMonitorReply(long MonitorId, IReadOnlyDictionary<int, long> Materials) : BlueModel
{
    public override object ToBlue() => new object?[] { MonitorId, new BlueMap<int, long>(Materials) };
}

internal sealed record IndustryMaterialsChanged(long MonitorId, IReadOnlyDictionary<int, long> Materials)
    : PilotNotification("OnIndustryMaterials")
{
    public override object?[] Arguments => [MonitorId, Materials.ToDictionary(p => (long)p.Key, p => (object?)p.Value)];
}

internal sealed record IndustryJobChanged(IndustryJobDetails Job) : PilotNotification("OnIndustryJob")
{
    public override object?[] Arguments => [Job.Id, Job.PilotId, Job.BlueprintId, Job.PilotId, (int)Job.Status, Job.SuccessfulRuns];
}
