using Eden.Gameplay.Industry;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Maps;

[Injectable]
internal sealed class IndustryMapHandler(IIndustryMap industry) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "map" || method is not ("GetIndustryJobsOverLast24Hours" or "GetIndustryCostModifier"))
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is not { Count: > 0 })
        {
            if (args is [long id] && id is > 0 and <= int.MaxValue && Enum.IsDefined((IndustryActivity)id))
            {
                var activity = (IndustryActivity)id;
                return method == "GetIndustryJobsOverLast24Hours"
                    ? new IndustryMapJobsReply(industry.JobsStarted(activity))
                    : new IndustryMapCostsReply(industry.CostModifiers(activity));
            }
            // The pinned 2D map supplies None for the all-jobs filter. Accept the legacy zero selector as well.
            if (method == "GetIndustryJobsOverLast24Hours" && args is [null or 0L])
            {
                return new IndustryMapJobsReply(industry.JobsStarted(null));
            }
        }
        return new PilotCallResult.Failure("Choose a supported activity for these industry statistics.");
    }
}

internal sealed record IndustryMapJobsReply(IReadOnlyList<IndustryMapJobCount> Systems) : BlueModel
{
    public override object ToBlue() =>
        new CompatibilityRowset(["solarSystemID", "noOfJobs"], Systems.Select(system => new IndustryMapJobRow(system)).ToArray()).ToBlue();
}

internal sealed record IndustryMapJobRow(IndustryMapJobCount System) : BlueModel
{
    public override object ToBlue() => new object?[] { System.SystemId, System.Count };
}

internal sealed record IndustryMapCostsReply(IReadOnlyList<IndustryMapCostModifier> Systems) : BlueModel
{
    public override object ToBlue() => Systems.ToDictionary(system => (long)system.SystemId, system => (object?)(double)system.Index);
}
