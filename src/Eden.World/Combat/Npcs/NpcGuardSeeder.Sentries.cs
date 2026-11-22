using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Combat.Npcs;

internal sealed partial class NpcGuardSeeder
{
    // Sentry slots follow the mobile guard and customs slots on each existing perimeter.
    private const int FirstSentrySlot = 3;
    private const int SentryCount = 2;
    private const double SentryStandOff =
        50_000 - NpcGuardRules.AnchorClearance - NpcGuardRules.PatrolRadius - NpcGuardRules.FormationMargin;

    internal static IEnumerable<(long Id, SpacePosition Position, double Range)> LocalSentries(
        WorldAction world,
        CatalogDbContext catalog,
        int system,
        SpacePosition position,
        long now
    )
    {
        foreach (var site in catalog.Definition(s_guardAnchors, system).Sites)
        {
            if (site.Position.DistanceTo(position) > NpcEncounterDefinitions.VisibilityRange)
            {
                continue;
            }
            SeedSentries(world, catalog, system, site, now);
            foreach (var candidate in NpcPopulationMember.AtAnchor(world, site.Id))
            {
                if (
                    candidate is not { Active: true, Role: NpcRole.Sentry }
                    || world.Set<WorldNpc>().Find(candidate.Id) is not { } sentry
                    || Profile(catalog, sentry.TypeId, site.FactionId) is not { } profile
                )
                {
                    continue;
                }
                var location = NpcMotion.Position(sentry);
                double range = NpcSentryRules.GridRange(profile.Attributes);
                if (position.DistanceTo(location) <= range)
                {
                    yield return (candidate.Id, location, range);
                }
            }
        }
    }

    private static void SeedSentries(WorldAction world, CatalogDbContext catalog, int system, GuardAnchor site, long now)
    {
        var occupied = NpcPopulationMember
            .AtAnchor(world, site.Id)
            .Where(member => member is { Active: true, Role: NpcRole.Sentry })
            .Select(member => member.Slot)
            .ToArray();
        if (
            occupied.Length == SentryCount
            || NpcSentryRules.Type((NpcEmpireFaction)site.FactionId) is not int sentryType
            || catalog.FittingType(sentryType)?.GroupId != NpcSentryRules.Group
            || Profile(catalog, sentryType, site.FactionId) is not { } profile
        )
        {
            return;
        }
        var home = NpcGuardRules.Home(site.Position, site.Radius + SentryStandOff, site.Lanes, CatalogObstacles(catalog, system));
        if (home is null)
        {
            return;
        }

        for (int index = 0; index < SentryCount; index++)
        {
            int slot = FirstSentrySlot + index;
            if (occupied.Contains(slot))
            {
                continue;
            }
            double z = home.Z + index * NpcSpawnRules.MemberSpacing;
            world.Add(
                new WorldNpc
                {
                    EncounterId = site.Id,
                    SpawnSlot = slot,
                    AnchorKind = site.Kind,
                    SystemId = system,
                    TypeId = sentryType,
                    Role = NpcRole.Sentry,
                    FactionId = site.FactionId,
                    CorporationId = profile.CorporationId,
                    HomeX = home.X,
                    HomeY = home.Y,
                    HomeZ = z,
                    X = home.X,
                    Y = home.Y,
                    Z = z,
                    ShieldCharge = profile.Attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity),
                    UpdatedAt = now,
                }
            );
        }
    }
}
