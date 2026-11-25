using System.Globalization;
using Eden.Gameplay.Mail;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

internal static class ContactCostWire
{
    internal static long Approval(object? value, long maximum = MailContactRules.MaximumApprovalIsk) =>
        value switch
        {
            null => 0,
            long cost when cost >= 0 && cost <= maximum => cost,
            double cost when cost >= 0 && cost <= maximum && cost == Math.Truncate(cost) => (long)cost,
            _ => throw new InvalidDataException("Invalid contact charge approval."),
        };

    internal static PilotCallResult Requote(long costIsk) =>
        new PilotCallResult.NamedFailure("ContactCostNotApproved", new CostValues(costIsk));

    internal static PilotCallResult RequoteMany(IReadOnlyList<MailContactCharge> charges) =>
        new PilotCallResult.NamedFailure("ContactCostNotApprovedForMany", new BatchCostValues(charges));

    private sealed record BatchCostValues(IReadOnlyList<MailContactCharge> Charges) : BlueModel
    {
        public override object ToBlue() =>
            new Dictionary<string, object?>
            {
                ["costNotApprovedFor"] = Charges
                    .Select(charge =>
                        (object?)
                            new Dictionary<string, object?>
                            {
                                ["charID"] = charge.RecipientId,
                                ["totalCost"] = charge.CostIsk,
                                ["approvedCost"] = 0L,
                            }
                    )
                    .ToList(),
            };
    }

    private sealed record CostValues(long CostIsk) : BlueModel
    {
        public override object ToBlue() =>
            new Dictionary<string, object?>
            {
                ["totalCost"] = CostIsk,
                ["totalCostISK"] = $"{CostIsk.ToString("N2", CultureInfo.InvariantCulture)} ISK",
            };
    }
}
