using System.Security.Cryptography;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.FactionWarfare;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.FactionWarfare;
using Eden.World.Runtime.Economy;

namespace Eden.World.Exploration;

internal sealed partial class PirateExplorationStore
{
    // Reserve a slot outside the ordinary and sovereignty exploration rotations.
    private const int MilitiaOperationSlot = -1;
    private static readonly TimeSpan OperationResponse = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan OperationCollapse = TimeSpan.FromMinutes(20);
    private readonly NpcProfileCatalog _operationProfiles = new();

    private void ReadOperationCenter(
        WorldAction world,
        CatalogDbContext catalog,
        int system,
        MilitiaFaction? occupier,
        long now,
        List<CombatAnomaly> signatures,
        ref long deadline
    )
    {
        var existing = world
            .Filter<SavedExplorationSite>(row => row.SystemId == system && row.SovereigntySlot == MilitiaOperationSlot)
            .Max(row => (long?)row.Id);
        var site = existing is long id ? world.Set<SavedExplorationSite>().Find(id) : null;
        var definition = occupier is { } faction ? MilitiaOperationCatalog.Read(catalog, faction) : null;
        if (site is { ReplaceAt: null } && (site.DungeonId != definition?.DungeonId || site.ExpiresAt <= now))
        {
            Retire(world, site, Math.Min(now, site.ExpiresAt));
        }
        if (definition is null)
        {
            return;
        }
        if (site?.ReplaceAt > now)
        {
            deadline = Math.Min(deadline, site.ReplaceAt.Value);
            return;
        }
        if (site is null || site.ReplaceAt is not null)
        {
            long siteId = Math.Max(
                HackingRules.FirstSiteId,
                (world.Filter<SavedExplorationSite>(_ => true).Max(row => (long?)row.Id) ?? 0) + 1
            );
            site = world
                .Add(
                    new SavedExplorationSite
                    {
                        Id = siteId,
                        SystemId = system,
                        SovereigntySlot = MilitiaOperationSlot,
                        Kind = (int)ExplorationKind.Data,
                        DungeonId = definition.DungeonId,
                        X = 120_000_000_000,
                        Y = 30_000_000_000,
                        Z = 270_000_000_000,
                        ExpiresAt = checked(now + HackingRules.SiteLifetime),
                    }
                )
                .Entity;
            for (int index = 0; index < definition.Containers.Count; index++)
            {
                world.Add(
                    new SavedHackingContainer
                    {
                        Id = HackingRules.FirstContainerId + (siteId - HackingRules.FirstSiteId) * HackingRules.ContainersPerSite + index,
                        SiteId = siteId,
                        Index = index,
                        LootSeed = RandomNumberGenerator.GetInt32(int.MaxValue),
                    }
                );
            }
            world.SaveChanges();
        }

        var containers = world.Filter<SavedHackingContainer>(row => row.SiteId == site.Id).OrderBy(row => row.Index).ToArray();
        if (site.ResponseAt is long response && !site.ResponseSpawned)
        {
            if (response <= now)
            {
                SpawnOperationResponse(world, catalog, site, (MilitiaFaction)definition.FactionId, containers, now);
            }
            else
            {
                deadline = Math.Min(deadline, response);
            }
        }

        foreach (var container in containers)
        {
            if (
                world.Filter<SavedHackingAttempt>(row => row.ContainerId == container.Id && row.RecoveryPending).SingleOrDefault() is
                { } attempt
            )
            {
                ReleaseModule(world, attempt);
                world.Remove(attempt);
            }
        }
        signatures.Add(
            new(
                site.Id,
                ScannerTargetIds.FromExplorationSequence(site.Id - HackingRules.FirstSiteId),
                system,
                definition.DungeonId,
                definition.NameId,
                definition.Difficulty,
                new(site.X, site.Y, site.Z),
                site.ResponseAt is not null,
                containers
                    .Select(row =>
                        Object(
                            catalog,
                            site,
                            row,
                            world.Filter<SavedHackingAttempt>(attempt => attempt.ContainerId == row.Id && !attempt.RecoveryPending).Any(),
                            definition
                        )
                    )
                    .ToArray(),
                definition.FactionId,
                true,
                ExplorationKind.Data
            )
        );
        deadline = Math.Min(deadline, site.ExpiresAt);
    }

    private void SpawnOperationResponse(
        WorldAction world,
        CatalogDbContext catalog,
        SavedExplorationSite site,
        MilitiaFaction occupier,
        SavedHackingContainer[] containers,
        long now
    )
    {
        site.ResponseSpawned = true;
        var pilot = containers
            .Where(row => row.OwnerId > 0)
            .Select(row => world.Pilots.Find(row.OwnerId))
            .FirstOrDefault(pilot => pilot?.ActiveShip.Position is { SystemId: var system } && system == site.SystemId);
        int type = MilitiaComplexCatalog.DefenderType(occupier, MilitiaComplexFamily.Large);
        var hull = _operationProfiles.Read(catalog, type);
        var npc = world
            .Add(
                new WorldNpc
                {
                    TypeId = type,
                    FactionId = (int)occupier,
                    CorporationId = MilitiaRules.Corporation(occupier),
                    SystemId = site.SystemId,
                    AnchorKind = NpcAnchorKind.GhostSite,
                    EncounterId = site.Id,
                    Role = NpcRole.MilitiaComplexDefender,
                    X = site.X,
                    Y = site.Y,
                    Z = site.Z + NpcSpawnRules.MemberSpacing,
                    HomeX = site.X,
                    HomeY = site.Y,
                    HomeZ = site.Z + NpcSpawnRules.MemberSpacing,
                    ShieldCharge = hull.Defense.Shield,
                    UpdatedAt = now,
                    MotionAt = now,
                }
            )
            .Entity;
        world.SaveChanges();
        if (pilot is not null)
        {
            world.Add(NpcAggression.Start(npc.Id, pilot.ActiveShipId, NpcGuardRules.ReactionMilliseconds, now));
        }
    }
}
