using System.Numerics;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Exploration;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable]
internal sealed class CorporationMemberQueryStore(WorldDatabase database) : ICorporationMemberQueries
{
    public IReadOnlyList<CorporationMemberTracking> Tracking(long actorId, bool full) =>
        WorldActionScope.Inspect(
            database,
            world =>
            {
                var actor = world.Pilots.Find(actorId) ?? throw new InvalidDataException("Unknown pilot.");
                var corporation = world.Set<SavedCorporation>().Find(actor.CorporationId);
                if (corporation is null)
                {
                    return Array.Empty<CorporationMemberTracking>();
                }
                if (full)
                {
                    CorporationRoleStore.RequireDirector(world, actorId, corporation);
                }
                return world
                    .Pilots.Where(pilot => pilot.CorporationId == corporation.Id)
                    .OrderBy(pilot => pilot.Id)
                    .AsEnumerable()
                    .Select(pilot =>
                    {
                        var access = CorporationRoleStore.Effective(world, pilot.Id, corporation);
                        long joined =
                            world
                                .Set<SavedCorporationEmployment>()
                                .Where(row => row.PilotId == pilot.Id)
                                .OrderByDescending(row => row.Id)
                                .Select(row => (long?)row.StartedAt)
                                .FirstOrDefault()
                            ?? pilot.CreatedAt;
                        var activity = world.Set<SavedPilotActivity>().Find(pilot.Id);
                        var ship = full ? world.Ships.Find(pilot.ActiveShipId) : null;
                        long? location =
                            ship is null || ship.Position?.SystemId is int system && AbyssalRules.IsSystem(system)
                                ? null
                                : ship.Position?.SystemId ?? ship.DockedLocationId;
                        return new CorporationMemberTracking(
                            new(pilot.Id, pilot.Name, corporation.Id, joined, access.Roles.Roles, access),
                            new(activity?.LoggedOnAt, activity?.LoggedOffAt),
                            location,
                            ship?.TypeId
                        );
                    })
                    .ToArray();
            }
        );

    public IReadOnlyList<long> Query(long actorId, CorporationMemberQuery query)
    {
        CorporationMemberQueryRules.Validate(query);
        return WorldActionScope.Inspect(
            database,
            world =>
            {
                var corporation = CorporationRoleStore.Current(world, actorId);
                var titles =
                    query.SearchTitles || query.IncludeImplied
                        ? world
                            .Set<SavedCorporationTitle>()
                            .Where(title => title.CorporationId == corporation.Id)
                            .Select(title => title.Snapshot())
                            .ToArray()
                        : [];
                List<long> matched = [];
                foreach (var pilot in world.Pilots.Where(pilot => pilot.CorporationId == corporation.Id))
                {
                    var access = CorporationRoleStore.Read(world, pilot.Id, corporation.Id);
                    var roles = pilot.Id == corporation.CeoId ? new(CorporationRules.CeoRoles) : access.Roles;
                    List<string> names = [pilot.Name];
                    if (query.SearchTitles && access.Title.Length != 0)
                    {
                        names.Add(access.Title);
                    }
                    foreach (var title in titles.Where(title => (access.TitleMask & title.Id) != 0))
                    {
                        if (query.IncludeImplied)
                        {
                            roles = roles.Union(title.Roles);
                        }
                        if (query.SearchTitles)
                        {
                            names.Add(title.Name);
                        }
                    }
                    if (access.AuthorityHoldVoteId.HasValue || access.BlockRoles && pilot.Id != corporation.CeoId)
                    {
                        roles = new();
                    }
                    if (query.IncludeImplied && roles.Has(CorporationRole.Director))
                    {
                        roles = new(
                            CorporationRoleRules.KnownRoles,
                            CorporationRoleRules.KnownRoles,
                            CorporationRoleRules.LocationalRoles,
                            CorporationRoleRules.LocationalRoles,
                            CorporationRoleRules.LocationalRoles,
                            CorporationRoleRules.LocationalRoles,
                            CorporationRoleRules.LocationalRoles,
                            CorporationRoleRules.LocationalRoles
                        );
                    }
                    long joined =
                        world
                            .Set<SavedCorporationEmployment>()
                            .Where(row => row.PilotId == pilot.Id)
                            .OrderByDescending(row => row.Id)
                            .Select(row => (long?)row.StartedAt)
                            .FirstOrDefault()
                        ?? pilot.CreatedAt;
                    var member = new CorporationMember(
                        pilot.Id,
                        pilot.Name,
                        corporation.Id,
                        joined,
                        roles.Roles,
                        access with
                        {
                            Roles = roles,
                        }
                    );
                    if (CorporationMemberQueryRules.Matches(member, query.Criteria, names))
                    {
                        matched.Add(pilot.Id);
                    }
                }
                return matched.Order().ToArray();
            }
        );
    }

    public IReadOnlyList<long> AboveAverageShareholders(long actorId) =>
        WorldActionScope.Inspect(
            database,
            world =>
            {
                var corporation = CorporationRoleStore.Current(world, actorId);
                var holdings = world
                    .Set<SavedCorporationShareHolding>()
                    .Where(row => row.CorporationId == corporation.Id && row.Shares > 0)
                    .ToArray();
                BigInteger total = holdings.Aggregate(BigInteger.Zero, (sum, holding) => sum + holding.Shares);
                var members = world.Pilots.Where(pilot => pilot.CorporationId == corporation.Id).Select(pilot => pilot.Id).ToHashSet();
                return holdings
                    .Where(holding => members.Contains(holding.ShareholderId) && (BigInteger)holding.Shares * members.Count > total)
                    .Select(holding => holding.ShareholderId)
                    .Order()
                    .ToArray();
            }
        );
}
