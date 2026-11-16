namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<int, long> _allianceBillingDeadline = new(
        "resident.allianceBillingDeadline",
        "WorldAlliance",
        "WorldAllianceMember",
        "WorldAllianceBill",
        "WorldAllianceLifecycle"
    );

    internal long AllianceBillingDeadline(Func<long> load) => Read(_allianceBillingDeadline, 0, load);
}
