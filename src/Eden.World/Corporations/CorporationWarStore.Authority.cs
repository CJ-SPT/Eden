using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Data.World.Structures;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Structures;

namespace Eden.World.Corporations;

internal sealed partial class CorporationWarStore
{
    internal static bool HasActiveWar(WorldAction world, int firstCorporationId, int secondCorporationId, long now)
    {
        if (world is not EconomyTransaction resident || !resident.MayHaveCorporationWars() || firstCorporationId == secondCorporationId)
        {
            return false;
        }
        int first = Organization(world, firstCorporationId);
        int second = Organization(world, secondCorporationId);
        return first != second && resident.ActiveCorporationWar(first, second, now);
    }

    internal static bool ParticipatesInActiveWar(WorldAction world, int corporationId, long now) =>
        world is EconomyTransaction resident
        && resident.MayHaveCorporationWars()
        && resident.ParticipatesInCorporationWar(Organization(world, corporationId), now);

    internal static bool IsActiveWarHeadquarters(WorldAction world, long structureId, long now) =>
        world is not SqlWorldAction
        && world
            .Filter<SavedCorporationWar>(war => war.HeadquartersId == structureId && war.EndingAt == null)
            .AsEnumerable()
            .Any(war => war.BillDueAt > now);

    internal static bool PreventsJoiningAlliance(WorldAction world, int corporationId, long now) =>
        world is not SqlWorldAction
        && ForOrganization(world, corporationId)
            .Any(war => (war.Mutual || war.AttackerId == corporationId) && (Finish(war) is null || Finish(war) > now));

    private static bool Active(SavedCorporationWar war, long now) => war.StartsAt <= now && (Finish(war) is null || Finish(war) > now);

    private static long? Finish(SavedCorporationWar war) => war.EndsAt ?? war.BillDueAt + CorporationWarRules.Cooldown;

    private static CorporationWar Snapshot(SavedCorporationWar war, long now)
    {
        return new(
            war.Id,
            war.AttackerId,
            war.DefenderId,
            war.HeadquartersId,
            war.DeclaredAt,
            war.StartsAt,
            war.EndingAt,
            war.EndsAt,
            war.RetractedAt,
            war.RetractedBy,
            war.BillIssuedAt <= now && now < war.BillDueAt ? war.BillId : null,
            war.Mutual,
            war.ReasonEnded,
            war.InheritedFromWarId
        );
    }

    private static SavedCorporationWar NewWar(long id, int owner, int target, long now) =>
        new()
        {
            Id = id,
            AttackerId = owner,
            DefenderId = target,
            DeclaredAt = now,
            StartsAt = checked(now + CorporationWarRules.Warmup),
        };

    private static IEnumerable<SavedCorporationWar> ForOrganization(WorldAction world, int owner)
    {
        if (owner <= 0)
        {
            return [];
        }
        return world.Filter<SavedCorporationWar>(war => war.AttackerId == owner || war.DefenderId == owner).AsEnumerable();
    }

    private static IEnumerable<SavedCorporationWar> ForPair(WorldAction world, int first, int second) =>
        ForOrganization(world, first).Where(war => war.AttackerId == second || war.DefenderId == second);

    internal static int Organization(WorldAction world, int corporation) =>
        world.Set<SavedAllianceMember>().Find(corporation) is { } membership
        && world.Set<SavedAlliance>().Find(membership.AllianceId) is { ClosedAt: null } alliance
            ? alliance.Id
            : corporation;

    private static bool IsOrganization(WorldAction world, int owner) =>
        world.Set<SavedAlliance>().Find(owner) is { ClosedAt: null }
        || world.Set<SavedCorporation>().Find(owner) is { ClosedAt: null } && Organization(world, owner) == owner;

    private static SavedCorporation? Payer(WorldAction world, int owner)
    {
        int id = world.Set<SavedAlliance>().Find(owner) is { ClosedAt: null } alliance
            ? alliance.ExecutorCorporationId.GetValueOrDefault()
            : owner;
        return world.Set<SavedCorporation>().Find(id) is { ClosedAt: null } corporation ? corporation : null;
    }

    private static (int Owner, SavedCorporation Payer) Authorization(WorldAction world, long pilotId)
    {
        var corporation = CorporationRoleStore.Current(world, pilotId);
        int owner = Organization(world, corporation.Id);
        if (Payer(world, owner)?.Id != corporation.Id)
        {
            throw new InvalidDataException("Only the alliance executor corporation may manage its wars.");
        }
        CorporationRoleStore.RequireDirector(world, pilotId, corporation);
        return (owner, corporation);
    }

    private void CheckOpponent(WorldAction world, int owner, int target, long now)
    {
        if (target == owner || !IsOrganization(world, target))
        {
            throw new InvalidDataException("Choose another player corporation or alliance.");
        }
        foreach (var existing in ForPair(world, owner, target).ToArray())
        {
            Settle(world, existing, now);
            if (existing.EndingAt is null)
            {
                throw new InvalidDataException("These organizations already have a war.");
            }
        }
    }

    private static IEnumerable<SavedStructure> Structures(WorldAction world, int owner)
    {
        if (world.Set<SavedAlliance>().Find(owner) is { ClosedAt: null })
        {
            return world
                .Filter<SavedAllianceMember>(row => row.AllianceId == owner)
                .AsEnumerable()
                .Where(row => world.Set<SavedCorporation>().Find(row.CorporationId) is { ClosedAt: null })
                .SelectMany(row => CorporationStructures(world, row.CorporationId));
        }
        return world.Set<SavedCorporation>().Find(owner) is { ClosedAt: null } && Organization(world, owner) == owner
            ? CorporationStructures(world, owner)
            : [];
    }

    private static IEnumerable<SavedStructure> CorporationStructures(WorldAction world, int corporationId) =>
        world.Filter<SavedStructure>(s => s.CorporationId == corporationId).AsEnumerable();

    private static long? EligibilityLoss(WorldAction world, int owner, long now, long? declaredAt = null)
    {
        if (world.Set<SavedAlliance>().Find(owner)?.ClosedAt is long allianceClosed)
        {
            return allianceClosed;
        }
        if (world.Set<SavedCorporation>().Find(owner)?.ClosedAt is long corporationClosed)
        {
            return corporationClosed;
        }

        var history = Structures(world, owner).Select(s => (Start: s.DeployedAt, End: Departure(s, now))).ToArray();
        if (declaredAt is long since)
        {
            // Keep the original loss when a replacement was deployed before a delayed maintenance tick.
            foreach (long ended in history.Where(span => span.End >= since).Select(span => span.End!.Value).Distinct().Order())
            {
                if (!history.Any(span => span.Start <= ended && (span.End is null || span.End > ended)))
                {
                    return ended;
                }
            }
        }
        return history.Any(span => span.End is null) ? null : history.Select(span => span.End).Max() ?? now;
    }

    private static long? Departure(SavedStructure structure, long now) =>
        structure.State is StructureState.Destroyed or StructureState.Unanchored ? structure.StateStartedAt
        : structure.UnanchorAt <= now ? structure.UnanchorAt
        : null;

    private static bool ValidHeadquarters(CatalogDbContext catalog, SavedStructure structure, long now) =>
        Departure(structure, now) is null
        && structure.State
            is not (
                StructureState.Unknown
                or StructureState.DeployVulnerable
                or StructureState.Anchoring
                or StructureState.AnchorVulnerable
            )
        && catalog.Definitions.SystemSecurity.TryGetValue(structure.SystemId, out double security)
        && security >= StructureRules.HighSecurityBoundary
        && StructureCatalog.Read(catalog, structure.TypeId) is { Dockable: true };
}
