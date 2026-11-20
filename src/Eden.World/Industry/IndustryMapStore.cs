using Eden.Gameplay.Industry;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Industry;

[Injectable]
internal sealed class IndustryMapStore(WorldDatabase database) : IIndustryMap
{
    public IReadOnlyList<IndustryMapJobCount> JobsStarted(IndustryActivity? activity) =>
        LiveWorld.Find(database.Path)?.Economy.IndustryMapJobs(activity) ?? [];

    public IReadOnlyList<IndustryMapCostModifier> CostModifiers(IndustryActivity activity) =>
        LiveWorld.Find(database.Path)?.Economy.IndustryMapCosts() ?? [];
}
