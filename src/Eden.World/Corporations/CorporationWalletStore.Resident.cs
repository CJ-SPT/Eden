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
}
