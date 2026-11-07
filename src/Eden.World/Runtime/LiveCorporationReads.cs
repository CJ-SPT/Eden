using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<long, IReadOnlyList<LoyaltyBalance>> _corporationLoyalty = new(
        "resident.corporationLoyalty",
        "WorldPilot",
        "WorldCorporation",
        "WorldCorporationMemberAccess",
        "WorldCorporationTitle",
        "WorldCorporationLoyaltyWallet"
    );

    internal IReadOnlyList<LoyaltyBalance> CorporationLoyalty(long pilotId, Func<IReadOnlyList<LoyaltyBalance>> load) =>
        Read(_corporationLoyalty, pilotId, () => Array.AsReadOnly(load().ToArray()));

    private readonly ResidentSnapshot<int, PlayerCorporation?> _corporations = new(
        "resident.corporation",
        "WorldCorporation",
        "WorldPilot"
    );

    internal PlayerCorporation? Corporation(int corporationId, Func<PlayerCorporation?> load) => Read(_corporations, corporationId, load);

    private readonly ResidentSnapshot<long, CorporationIdentity> _corporationIdentities = new(
        "resident.corporationIdentity",
        "WorldPilot",
        "WorldShip",
        "WorldPositionMembership",
        "WorldCorporation",
        "WorldCorporationEmployment",
        "WorldCorporationMemberAccess",
        "WorldCorporationTitle",
        "WorldCorporationWalletSelection"
    );

    internal CorporationIdentity CorporationIdentity(long pilotId, Func<CorporationIdentity> load) =>
        Read(_corporationIdentities, pilotId, load);
}
