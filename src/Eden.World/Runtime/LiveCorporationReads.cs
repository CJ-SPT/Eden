using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<long, long> _corporationChangeHeads = new("resident.corporationChangeHead", "WorldCorporationChange");

    internal bool HasCorporationChanges(long pilotId, long cursor) =>
        cursor
        < Read(
            _corporationChangeHeads,
            pilotId,
            () =>
            {
                using WorldDbContext world = new(database.Path);
                return world.Set<SavedCorporationChange>().Where(c => c.PilotId == pilotId).Max(c => (long?)c.Id) ?? 0;
            }
        );

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
