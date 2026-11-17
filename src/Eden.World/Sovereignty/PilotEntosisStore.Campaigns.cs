using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Sovereignty;

internal sealed partial class PilotEntosisStore
{
    private sealed record NodeProgress(SavedSovereigntyNode Node, TerritoryProgress Progress);

    private static void Spawn(WorldAction world, CatalogDbContext catalog, SavedSovereigntyConflict conflict, long at)
    {
        var systems = EntosisCatalog.ConstellationSystems(catalog, conflict.ConstellationId);
        var occupants = systems.SelectMany(id => world.Filter<SavedSovereigntyNode>(row => row.SystemId == id)).ToArray();
        if (
            occupants.Length >= SovereigntyWarfareRules.MaximumConstellationNodes
            || occupants.Count(row => row.SourceSystemId == conflict.SystemId) >= SovereigntyWarfareRules.MaximumCampaignNodes
        )
        {
            return;
        }
        int[] available = systems
            .Where(id => occupants.Count(row => row.SystemId == id) < SovereigntyWarfareRules.MaximumSystemNodes)
            .ToArray();
        if (available.Length == 0)
        {
            return;
        }
        conflict.Sequence++;
        int system = SovereigntyWarfareRules.SelectNodeSystem(conflict.HubId, conflict.Sequence, available);
        long id = Math.Max(Eden.World.Inventory.InventoryPersistence.NextId(world), StructureRules.FirstStructureId + 1);
        var position = SovereigntyWarfareRules.NodePosition(
            id,
            occupants
                .Where(row => row.SystemId == system)
                .Select(row => new Eden.Gameplay.Flight.SpacePosition(row.X, row.Y, row.Z))
                .ToArray()
        );
        var claim = world.Set<SavedSovereigntyClaim>().Find(conflict.SystemId)!;
        world.Add(
            new SavedSovereigntyNode
            {
                Id = id,
                SystemId = system,
                SourceSystemId = conflict.SystemId,
                HubId = conflict.HubId,
                CorporationId = claim.CorporationId,
                AllianceId = conflict.DefenderAllianceId,
                X = position.X,
                Y = position.Y,
                Z = position.Z,
                UpdatedAt = at,
            }
        );
    }

    private long NextCampaignBoundary(WorldAction world, CatalogDbContext catalog, int constellation, long now)
    {
        var conflicts = world.Filter<SavedSovereigntyConflict>(row => row.ConstellationId == constellation).ToArray();
        long next = conflicts
            .Where(row => row.Phase == SovereigntyPhase.Reinforced && row.CampaignAt <= now)
            .Select(row => row.CampaignAt!.Value)
            .Concat(conflicts.Where(row => row.Phase == SovereigntyPhase.Campaign).Select(row => row.NextSpawnAt))
            .Append(now)
            .Min();
        if (!conflicts.Any(row => row.Phase == SovereigntyPhase.Campaign))
        {
            return next;
        }
        var profile = EntosisCatalog.Defense(catalog, true);
        foreach (var conflict in conflicts.Where(row => row.Phase == SovereigntyPhase.Campaign))
        {
            foreach (var node in world.Filter<SavedSovereigntyNode>(row => row.SourceSystemId == conflict.SystemId))
            {
                var progress = SovereigntyWarfareRules.Advance(
                    new(node.Value, node.UpdatedAt),
                    Contributions(world, catalog, Target(world, node.SystemId, node.Id)!, now),
                    next,
                    profile.Duration,
                    profile.Regeneration,
                    conflict.DefenderAllianceId,
                    true,
                    attackMultiplier: (decimal)conflict.CampaignAdm
                );
                if (progress.CompletedAt is long at)
                {
                    next = Math.Min(next, at);
                }
            }
        }
        return next;
    }

    private void AdvanceConstellationCampaigns(WorldAction world, CatalogDbContext catalog, int constellation, long now)
    {
        if (
            !world
                .Filter<SavedSovereigntyConflict>(row => row.ConstellationId == constellation)
                .Any(row => row.Phase == SovereigntyPhase.Campaign || row.Phase == SovereigntyPhase.Reinforced && row.CampaignAt <= now)
        )
        {
            return;
        }
        while (true)
        {
            var conflicts = world
                .Filter<SavedSovereigntyConflict>(row => row.ConstellationId == constellation)
                .OrderBy(row => row.SystemId)
                .ToArray();
            long next = NextCampaignBoundary(world, catalog, constellation, now);
            // All campaigns reach the same event time before shared spawn capacity can be reused.
            foreach (var conflict in conflicts.Where(row => row.Phase == SovereigntyPhase.Campaign))
            {
                AdvanceCampaign(world, catalog, conflict, next);
            }
            foreach (var conflict in conflicts.Where(row => row.Phase == SovereigntyPhase.Reinforced && row.CampaignAt == next))
            {
                conflict.Phase = SovereigntyPhase.Campaign;
                conflict.NextSpawnAt = checked(next + SovereigntyWarfareRules.ExtraNodeInterval);
                for (int i = 0; i < SovereigntyWarfareRules.InitialNodes; i++)
                {
                    Spawn(world, catalog, conflict, next);
                }
            }
            if (next == now)
            {
                break;
            }
        }
        // A late callback can settle an old contest after its protected day has already elapsed.
        foreach (
            var conflict in world
                .Filter<SavedSovereigntyConflict>(row => row.ConstellationId == constellation)
                .Where(row => row.Phase == SovereigntyPhase.Secure && row.Value == 1)
        )
        {
            var window = Window(world, conflict, Math.Max(now, conflict.SecureUntil));
            conflict.WindowStart = window.StartsAt;
            conflict.WindowEnd = window.EndsAt;
        }
    }

    private void AdvanceCampaign(WorldAction world, CatalogDbContext catalog, SavedSovereigntyConflict conflict, long now)
    {
        var profile = EntosisCatalog.Defense(catalog, true);
        while (conflict.Phase == SovereigntyPhase.Campaign)
        {
            var nodes = world.Filter<SavedSovereigntyNode>(row => row.SourceSystemId == conflict.SystemId).OrderBy(row => row.Id).ToArray();
            long until = Math.Min(now, conflict.NextSpawnAt);
            var previews = nodes
                .Select(row => new NodeProgress(
                    row,
                    SovereigntyWarfareRules.Advance(
                        new(row.Value, row.UpdatedAt),
                        Contributions(world, catalog, Target(world, row.SystemId, row.Id)!, now),
                        until,
                        profile.Duration,
                        profile.Regeneration,
                        conflict.DefenderAllianceId,
                        true,
                        attackMultiplier: (decimal)conflict.CampaignAdm
                    )
                ))
                .ToArray();
            long first = previews
                .Where(row => row.Progress.CompletedAt is not null)
                .Select(row => row.Progress.CompletedAt!.Value)
                .DefaultIfEmpty(until)
                .Min();
            foreach (var (node, preview) in previews)
            {
                var progress =
                    first == until
                        ? preview
                        : SovereigntyWarfareRules.Advance(
                            new(node.Value, node.UpdatedAt),
                            Contributions(world, catalog, Target(world, node.SystemId, node.Id)!, now),
                            first,
                            profile.Duration,
                            profile.Regeneration,
                            conflict.DefenderAllianceId,
                            true,
                            attackMultiplier: (decimal)conflict.CampaignAdm
                        );
                node.Value = progress.Value;
                node.UpdatedAt = progress.UpdatedAt;
                node.ScoringTeam = progress.ScoringTeam;
                node.Rate = progress.Rate;
                node.Blocked = progress.Blocked;
                Retire(world, world.Filter<SavedEntosisCycle>(row => row.HubId == node.Id).ToArray(), first);
                if (progress.CompletedAt != first)
                {
                    continue;
                }
                conflict.DefenderPoints += progress.Value > 0 ? SovereigntyWarfareRules.NodePoints : -SovereigntyWarfareRules.NodePoints;
                StopTarget(world, node.Id);
                world.Remove(node);
                if (conflict.DefenderPoints is <= 0 or >= 100)
                {
                    SettleCampaign(world, catalog, conflict, first);
                    break;
                }
                Spawn(world, catalog, conflict, first);
            }
            if (conflict.Phase != SovereigntyPhase.Campaign)
            {
                break;
            }
            if (first == conflict.NextSpawnAt)
            {
                Spawn(world, catalog, conflict, first);
                conflict.NextSpawnAt = checked(first + SovereigntyWarfareRules.ExtraNodeInterval);
            }
            if (first == now)
            {
                break;
            }
        }
    }

    private void SettleCampaign(WorldAction world, CatalogDbContext catalog, SavedSovereigntyConflict conflict, long at)
    {
        foreach (var node in world.Filter<SavedSovereigntyNode>(row => row.SourceSystemId == conflict.SystemId).ToArray())
        {
            StopTarget(world, node.Id);
            world.Remove(node);
        }
        conflict.DefenderPoints = Math.Clamp(conflict.DefenderPoints, 0, 100);
        if (conflict.DefenderPoints == 100)
        {
            conflict.Phase = SovereigntyPhase.Secure;
            conflict.CampaignAt = null;
            conflict.Value = 1;
            conflict.Rate = 0;
            conflict.UpdatedAt = at;
            conflict.ScoringTeam = null;
            conflict.Blocked = false;
            // A defended structure stays secure until the following day's window.
            var nextWindow = Window(world, conflict, at + TimeSpan.TicksPerDay);
            conflict.SecureUntil = nextWindow.StartsAt;
            conflict.WindowStart = nextWindow.StartsAt;
            conflict.WindowEnd = nextWindow.EndsAt;
            return;
        }
        StopTarget(world, conflict.HubId);
        if (world.Set<SavedStructure>().Find(conflict.HubId) is { } structure)
        {
            structure.State = StructureState.Destroyed;
            structure.StateStartedAt = at;
            structure.StateEndsAt = null;
        }
        if (world.Set<SavedSovereigntyClaim>().Find(conflict.SystemId) is { } claim)
        {
            world.Remove(claim);
        }
        if (world.Set<SavedSovereigntyHub>().Find(conflict.SystemId) is { } hub)
        {
            world.Remove(hub);
        }
        SovereigntyResourcePersistence.DestroyHub(world, catalog, conflict.HubId, at);
        world.Remove(conflict);
        SovereigntyDevelopmentPersistence.ReconcileCapital(world, conflict.DefenderAllianceId, at);
        // No winner receives a free replacement hull or automatic claim.
        conflict.Phase = SovereigntyPhase.Secure;
    }
}
