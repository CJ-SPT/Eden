using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Market;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.World.Characters;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

internal sealed partial class CorporationWalletStore
{
    internal static int SelectedKey(WorldAction world, long actorId, SavedCorporation? corporation)
    {
        var saved = world.Set<SavedCorporationWalletSelection>().Find(actorId);
        return
            saved is not null
            && saved.CorporationId == corporation?.Id
            && CorporationWalletRules.CanTake(CorporationRoleStore.Effective(world, actorId, corporation).Roles, saved.Key)
            ? saved.Key
            : CorporationWalletRules.MasterKey;
    }

    internal static long TaxBounty(WorldAction world, Pilot pilot, long grossCents, long now)
    {
        var corporation = world.Set<SavedCorporation>().Find(pilot.CorporationId);
        if (corporation is null)
        {
            return 0;
        }
        long tax = CorporationWalletRules.Tax(grossCents, corporation.Snapshot(0).Details.TaxRate);
        if (tax == 0)
        {
            return 0;
        }
        corporation.BalanceCents = checked(corporation.BalanceCents + tax);
        AddEntry(
            world,
            corporation.Id,
            CorporationWalletRules.MasterKey,
            now,
            pilot.Id,
            tax,
            corporation.BalanceCents,
            CorporationWalletRules.BountyTaxEntry,
            pilot.Id,
            ""
        );
        return tax;
    }

    internal static void AddEntry(
        WorldAction world,
        long owner,
        int key,
        long now,
        long counterparty,
        long amount,
        long balance,
        WalletEntryType type,
        long reference,
        string reason
    ) =>
        world.Add(
            new SavedWalletLedger
            {
                OwnerId = owner,
                Key = key,
                Time = now,
                CounterpartyId = counterparty,
                AmountCents = amount,
                BalanceCents = balance,
                EntryType = type,
                ReferenceId = reference,
                Reason = reason,
            }
        );
}
