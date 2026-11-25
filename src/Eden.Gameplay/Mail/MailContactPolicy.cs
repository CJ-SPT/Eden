namespace Eden.Gameplay.Mail;

public interface IMailContactPolicy
{
    long? ContactCost(long pilotId);
    void SetContactCost(long pilotId, long? costIsk);
}

public sealed record MailContactCharge(long RecipientId, long CostIsk);

public sealed class MailContactCostApprovalRequired(long costIsk, IReadOnlyList<MailContactCharge> charges) : Exception
{
    public long CostIsk { get; } = costIsk;
    public IReadOnlyList<MailContactCharge> Charges { get; } = charges;
}

public static class MailContactRules
{
    // Build 3662168 Mail & Communications Settings charge edit control.
    public const long MaximumCostIsk = 1_000_000;
    public const long MaximumApprovalIsk = MaximumCostIsk * MailRules.MaximumRecipients;
}
