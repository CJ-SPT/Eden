using Eden.Data.World.Characters;
using Eden.Data.World.Mail;
using Eden.Data.World.Market;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Mail;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Mail;

[Injectable]
internal sealed class MailContactPolicyStore(WorldDatabase database) : IMailContactPolicy
{
    private ResidentEconomy Economy =>
        LiveWorld.Find(database.Path)?.Economy ?? throw new InvalidDataException("Mail contact settings require the resident world.");

    public long? ContactCost(long pilotId) => Economy.Inspect(world => Cost(world, pilotId));

    public void SetContactCost(long pilotId, long? costIsk)
    {
        if (costIsk is < 0 or > MailContactRules.MaximumCostIsk)
        {
            throw new InvalidDataException("Choose a mail contact charge between 0 and 1,000,000 ISK.");
        }
        using var world = Economy.Begin(new(pilots: [pilotId]));
        if (!world.PilotExists(pilotId))
        {
            throw new InvalidDataException("Unknown mail pilot.");
        }
        var saved = world.Set<SavedMailContactPolicy>().Find(pilotId);
        if (costIsk == 0)
        {
            if (saved is not null)
            {
                world.Remove(saved);
            }
        }
        else
        {
            saved ??= world.Add(new SavedMailContactPolicy { PilotId = pilotId }).Entity;
            saved.CostIsk = costIsk;
        }
        world.Commit();
    }

    private static long? Cost(WorldAction world, long pilotId) =>
        world.Set<SavedMailContactPolicy>().Find(pilotId) is { } policy ? policy.CostIsk : 0;

    internal static void Charge(EconomyTransaction world, Pilot sender, MailDraft draft, long now)
    {
        List<(long Recipient, long Isk)> charges = [];
        foreach (long recipientId in draft.Recipients)
        {
            long? fee = Cost(world, recipientId);
            if (fee == 0)
            {
                continue;
            }
            var recipient = world.Pilots.Find(recipientId)!;
            if (recipient.CorporationId == sender.CorporationId || world.Set<SavedContact>().Find(recipientId, sender.Id) is not null)
            {
                continue;
            }
            long required = fee ?? throw new InvalidDataException("A recipient only accepts mail from known pilots.");
            charges.Add((recipientId, required));
        }
        long total = charges.Sum(charge => charge.Isk);
        if (total != draft.ApprovedContactCostIsk)
        {
            if (total == 0)
            {
                throw new InvalidDataException("The mail contact charge changed. Send the message without an approved charge.");
            }
            throw new MailContactCostApprovalRequired(total);
        }
        if (total == 0)
        {
            return;
        }
        var wallet = world.Set<PilotWallet>().Find(sender.Id);
        if (wallet is null || wallet.Cents < total * MarketDefinitions.CentsPerIsk)
        {
            throw new InvalidDataException("Your wallet cannot cover the mail contact charge.");
        }
        foreach (var (recipient, isk) in charges)
        {
            long cents = isk * MarketDefinitions.CentsPerIsk;
            wallet.Cents -= cents;
            CorporationWalletStore.AddEntry(
                world,
                sender.Id,
                (int)WalletAccountKey.Cash,
                now,
                CombatDefinitions.ConcordCorporation,
                -cents,
                wallet.Cents,
                WalletEntryType.Cspa,
                recipient,
                ""
            );
        }
    }
}
