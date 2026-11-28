using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Sovereignty;

internal sealed partial class PilotEntosisStore
{
    private sealed record NodeProgress(
        SavedSovereigntyNode Node,
        TerritoryProgress Progress,
        SavedEntosisCycle[] Cycles,
        EntosisContribution[] Links
    );

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
                    Contributions(ValidatedCycles(world, catalog, Target(world, node.SystemId, node.Id)!)),
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
                .Select(row =>
                {
                    var cycles = ValidatedCycles(world, catalog, Target(world, row.SystemId, row.Id)!);
                    var links = Contributions(cycles);
                    var progress = SovereigntyWarfareRules.Advance(
                        new(row.Value, row.UpdatedAt),
                        links,
                        until,
                        profile.Duration,
                        profile.Regeneration,
                        conflict.DefenderAllianceId,
                        true,
                        attackMultiplier: (decimal)conflict.CampaignAdm
                    );
                    return new NodeProgress(row, progress, cycles, links);
                })
                .ToArray();
            long first = previews
                .Where(row => row.Progress.CompletedAt is not null)
                .Select(row => row.Progress.CompletedAt!.Value)
                .DefaultIfEmpty(until)
                .Min();
            foreach (var (node, preview, cycles, links) in previews)
            {
                var progress =
                    first == until
                        ? preview
                        : SovereigntyWarfareRules.Advance(
                            new(node.Value, node.UpdatedAt),
                            links,
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
                if (progress.CompletedAt == first && progress.Value < 0)
                {
                    RecordCampaignContribution(world, conflict, cycles, first);
                }
                Retire(world, cycles, first);
                if (progress.CompletedAt != first)
                {
                    continue;
                }
                conflict.DefenderPoints += progress.Value > 0 ? SovereigntyWarfareRules.NodePoints : -SovereigntyWarfareRules.NodePoints;
                StopTarget(world, node.Id);
                world.Remove(node);
                if (conflict.DefenderPoints is <= 0 or >= SovereigntyWarfareRules.MaximumDefenderPoints)
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
        RemoveCampaignNodes(world, conflict.SystemId);
        conflict.DefenderPoints = Math.Clamp(conflict.DefenderPoints, 0, SovereigntyWarfareRules.MaximumDefenderPoints);
        if (conflict.DefenderPoints == SovereigntyWarfareRules.MaximumDefenderPoints)
        {
            DiscardPendingCampaignCredits(world, conflict.HubId);
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
        foreach (var credit in world.Filter<SavedSovereigntyCampaignCredit>(row => row.HubId == conflict.HubId))
        {
            credit.VictoryAt = at;
        }
        DestroyClaimedHub(world, catalog, conflict.SystemId, conflict.HubId, at);
        SovereigntyDevelopmentPersistence.ReconcileCapital(world, conflict.DefenderAllianceId, at);
        // A replacement hub still needs ordinary neutral deployment and capture.
        conflict.Phase = SovereigntyPhase.Secure;
    }
}
