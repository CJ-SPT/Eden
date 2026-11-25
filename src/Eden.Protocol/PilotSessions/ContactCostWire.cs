using System.Globalization;
using Eden.Gameplay.Mail;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

internal static class ContactCostWire
{
    internal static long Approval(object? value) =>
        value switch
        {
            null => 0,
            long cost when cost is >= 0 and <= MailContactRules.MaximumApprovalIsk => cost,
            double cost when cost is >= 0 and <= MailContactRules.MaximumApprovalIsk && cost == Math.Truncate(cost) => (long)cost,
            _ => throw new InvalidDataException("Invalid contact charge approval."),
        };

    internal static PilotCallResult Requote(long costIsk) =>
        new PilotCallResult.NamedFailure("ContactCostNotApproved", new CostValues(costIsk));

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
