using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Redemption;
using Eden.World.Runtime.Combat;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<long, PersonalContacts> _contacts = new(
        "resident.contacts",
        "WorldPilot",
        "WorldContact",
        "WorldAgentContact",
        "WorldBlockedContact"
    );
    private readonly ResidentSnapshot<long, IReadOnlyList<RedeemToken>> _redeemTokens = new(
        "resident.redeemTokens",
        "WorldAccount",
        "WorldRedeemToken"
    );
    private readonly ResidentSnapshot<long, IReadOnlyList<CorporationWalletDivision>> _corporationWallets = new(
        "resident.corporationWallets",
        "WorldPilot",
        "WorldCorporation",
        "WorldCorporationMemberAccess",
        "WorldCorporationTitle",
        "WorldCorporationWallet"
    );

    internal PersonalContacts Contacts(long pilot, Func<PersonalContacts> load) =>
        Read(
            _contacts,
            pilot,
            () =>
            {
                var contacts = load();
                return new PersonalContacts(Array.AsReadOnly(contacts.Addresses.ToArray()), Array.AsReadOnly(contacts.Blocked.ToArray()));
            }
        );

    internal IReadOnlyList<RedeemToken> RedeemTokens(long account, long now, Func<IReadOnlyList<RedeemToken>> load) =>
        Read(
            _redeemTokens,
            account,
            () => Array.AsReadOnly(load().ToArray()),
            now,
            tokens => tokens.Where(t => t.ExpiresAt.HasValue).Select(t => t.ExpiresAt!.Value).DefaultIfEmpty(long.MaxValue).Min()
        );

    internal IReadOnlyList<CorporationWalletDivision> CorporationWallet(long pilot, Func<IReadOnlyList<CorporationWalletDivision>> load) =>
        Read(_corporationWallets, pilot, () => Array.AsReadOnly(load().ToArray()));

    private void ObservePolling(IReadOnlyCollection<CombatRowChange> changes)
    {
        _contacts.Observe(changes);
        _redeemTokens.Observe(changes);
        _corporationWallets.Observe(changes);
    }

    private void ClearPolling()
    {
        _contacts.Clear();
        _redeemTokens.Clear();
        _corporationWallets.Clear();
    }
}
