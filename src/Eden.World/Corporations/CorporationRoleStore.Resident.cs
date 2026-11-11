using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

internal sealed partial class CorporationRoleStore
{
    internal static CorporationMemberAccess Effective(WorldAction world, long pilotId, SavedCorporation? corporation)
    {
        if (corporation is null)
        {
            return new(new());
        }
        var access = Read(world, pilotId, corporation.Id);
        if (corporation.CeoId == pilotId)
        {
            return access with { Roles = new(CorporationRules.CeoRoles), BlockRoles = false };
        }
        if (access.BlockRoles)
        {
            return access with { Roles = new() };
        }
        var roles = access.Roles;
        foreach (var title in world.Set<SavedCorporationTitle>().Where(t => t.CorporationId == corporation.Id).AsEnumerable())
        {
            if ((access.TitleMask & title.TitleId) != 0)
            {
                roles = roles.Union(title.Snapshot().Roles);
            }
        }
        if (roles.Has(CorporationRole.Director))
        {
            roles = roles with { Roles = roles.Roles | CorporationRules.PersonnelManagerRole };
        }
        return access with { Roles = roles };
    }

    internal static CorporationMemberAccess Read(WorldAction world, long pilotId, int corporationId) =>
        world.Set<SavedCorporationMemberAccess>().Find(pilotId) is { } row && row.CorporationId == corporationId
            ? JsonSerializer.Deserialize<CorporationMemberAccess>(row.DataJson)!
            : new(new());
}
