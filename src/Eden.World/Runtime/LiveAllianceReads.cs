namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<int, Eden.Gameplay.Alliances.AllianceSocialBook> _allianceSocial = new(
        "resident.allianceSocial",
        "WorldAllianceSocial"
    );

    internal Eden.Gameplay.Alliances.AllianceSocialBook AllianceSocial(
        int allianceId,
        Func<Eden.Gameplay.Alliances.AllianceSocialBook> load
    ) => Read(_allianceSocial, allianceId, load);

    private readonly ResidentSnapshot<int, long> _allianceBillingDeadline = new(
        "resident.allianceBillingDeadline",
        "WorldAlliance",
        "WorldAllianceMember",
        "WorldAllianceBill",
        "WorldAllianceLifecycle"
    );

    internal long AllianceBillingDeadline(Func<long> load) => Read(_allianceBillingDeadline, 0, load);
}
