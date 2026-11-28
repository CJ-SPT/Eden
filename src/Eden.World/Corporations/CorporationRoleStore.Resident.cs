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
    internal static bool AssignBotStationManager(WorldAction world, SavedCorporation corporation, long member, long now)
    {
        var effective = Effective(world, member, corporation);
        if (effective.Roles.Has(CorporationRole.StationManager))
        {
            return true;
        }
        var stored = Read(world, member, corporation.Id);
        var director = Effective(world, corporation.CeoId, corporation);
        if (
            !CorporationMembershipStore.BotLed(world, corporation)
            || world.Pilots.Find(member)?.CorporationId != corporation.Id
            || world.Set<Eden.Data.World.Capsuleers.SavedCapsuleerAgent>().Find(member) is null
            || stored.BlockRoles
            || stored.AuthorityHoldVoteId.HasValue
            || !director.Roles.Has(CorporationRole.Director)
        )
        {
            return false;
        }
        var updated = stored with { Roles = stored.Roles with { Roles = stored.Roles.Roles | (long)CorporationRole.StationManager } };
        Save(world, member, corporation.Id, updated);
        var after = Effective(world, member, corporation);
        CorporationRoleRules.Authorize(true, director.Roles, effective.Roles, after.Roles);
        Notify(world, corporation.Id, new(corporation.Id, member, effective, after, corporation.CeoId, now), null);
        return true;
    }

    internal static CorporationMemberAccess Effective(WorldAction world, long pilotId, SavedCorporation? corporation)
    {
        if (corporation is null)
        {
            return new(new());
        }
        var access = Read(world, pilotId, corporation.Id);
        if (access.AuthorityHoldVoteId.HasValue)
        {
            return access with { Roles = new() };
        }
        if (corporation.CeoId == pilotId)
        {
            return access with
            {
                Roles = new(CorporationRules.CeoRoles),
                BlockRoles = false,
                DirectRoles = new(CorporationRules.DirectorRole),
            };
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
        CorporationRoleSet direct = roles.Has(CorporationRole.Director) ? new(CorporationRules.DirectorRole) : access.Roles;
        return access with { Roles = roles, DirectRoles = direct == roles ? null : direct };
    }

    internal static CorporationMemberAccess Read(WorldAction world, long pilotId, int corporationId) =>
        world.Set<SavedCorporationMemberAccess>().Find(pilotId) is { } row && row.CorporationId == corporationId
            ? JsonSerializer.Deserialize<CorporationMemberAccess>(row.DataJson)!
            : new(new());
}
