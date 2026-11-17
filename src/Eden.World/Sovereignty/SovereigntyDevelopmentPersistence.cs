using Eden.Data.World.Alliances;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.World.Runtime.Economy;

namespace Eden.World.Sovereignty;

internal static class SovereigntyDevelopmentPersistence
{
    internal static void Record(WorldAction world, int system, SovereigntyActivityKind kind, double amount, long now)
    {
        if (world is not EconomyTransaction || amount <= 0 || world.Set<SavedSovereigntyClaim>().Find(system) is null)
        {
            return;
        }
        if (!double.IsFinite(amount) || now < 0)
        {
            throw new InvalidDataException("Invalid sovereignty activity input.");
        }
        world.Add(
            new SavedSovereigntyActivityEvent
            {
                Id = world.Next("WorldSovereigntyActivityEvent"),
                SystemId = system,
                At = now,
                Kind = kind,
                Amount = amount,
            }
        );
    }

    internal static SovereigntyActivity Activity(WorldAction world, int system, long now)
    {
        var saved = world.Set<SavedSovereigntyActivity>().Find(system);
        return saved is null
            ? new(0, 0, now)
            : SovereigntyDevelopmentRules.Decay(
                new(
                    saved.MilitaryPoints,
                    saved.IndustrialPoints,
                    saved.UpdatedAt,
                    saved.LastMilitaryAt,
                    saved.LastIndustrialAt,
                    saved.Revision
                ),
                now
            );
    }

    internal static void SettleActivity(WorldAction world, int system, long now)
    {
        if (
            world.Set<SavedSovereigntyActivity>().Find(system) is not { } row
            || now <= row.UpdatedAt
            || row.MilitaryPoints == 0 && row.IndustrialPoints == 0
        )
        {
            return;
        }
        var value = Activity(world, system, now);
        row.MilitaryPoints = value.Military;
        row.IndustrialPoints = value.Industrial;
        row.UpdatedAt = value.UpdatedAt;
    }

    internal static void Apply(WorldAction world, SavedSovereigntyActivityEvent contribution, long at)
    {
        var row =
            world.Set<SavedSovereigntyActivity>().Find(contribution.SystemId)
            ?? world.Add(new SavedSovereigntyActivity { SystemId = contribution.SystemId, UpdatedAt = at }).Entity;
        var result = SovereigntyDevelopmentRules.Credit(
            Activity(world, contribution.SystemId, at),
            contribution.Kind,
            contribution.Amount,
            at
        );
        row.MilitaryPoints = result.Military;
        row.IndustrialPoints = result.Industrial;
        row.UpdatedAt = result.UpdatedAt;
        row.LastMilitaryAt = result.LastMilitaryAt;
        row.LastIndustrialAt = result.LastIndustrialAt;
        row.Revision = contribution.Id;
        world.Remove(contribution);
    }

    internal static void ReconcileCapital(WorldAction world, int alliance, long now)
    {
        var claims = world.Filter<SavedSovereigntyClaim>(row => row.AllianceId == alliance).OrderBy(row => row.SystemId).ToArray();
        var row = world.Set<SavedSovereigntyCapital>().Find(alliance);
        if (row is null && claims.Length == 0)
        {
            return;
        }
        row ??= world.Add(new SavedSovereigntyCapital { AllianceId = alliance, CurrentSince = now }).Entity;
        bool active = world.Set<SavedAlliance>().Find(alliance) is { ClosedAt: null };
        int? automatic = active && claims.Length == 1 ? claims[0].SystemId : null;
        if (row.CurrentSystemId is int current && (!active || !claims.Any(claim => claim.SystemId == current)))
        {
            ChangeCapital(world, row, null, now);
        }
        if (row.NewSystemId is int pending && (!active || !claims.Any(claim => claim.SystemId == pending)))
        {
            row.NewSystemId = null;
            row.ValidAfter = null;
        }
        if (automatic is int sole)
        {
            if (row.CurrentSystemId != sole)
            {
                ChangeCapital(world, row, sole, now);
            }
            row.NewSystemId = null;
            row.ValidAfter = null;
        }
        else if (row.NewSystemId is int next && row.ValidAfter is long at && now >= at)
        {
            ChangeCapital(world, row, next, at);
            row.NewSystemId = null;
            row.ValidAfter = null;
        }
    }

    private static void ChangeCapital(WorldAction world, SavedSovereigntyCapital row, int? system, long at)
    {
        row.PreviousSystemId = row.CurrentSystemId;
        row.CurrentSystemId = system;
        row.CurrentSince = at;
        // Other constellations may settle later. Retain effective times instead of rewriting their earlier ADM.
        world.Add(
            new SavedSovereigntyCapitalChange
            {
                Id = world.Next("WorldSovereigntyCapitalChange"),
                AllianceId = row.AllianceId,
                At = at,
                SystemId = system,
            }
        );
    }

    internal static int? CapitalAt(WorldAction world, int alliance, long now)
    {
        var row = world.Set<SavedSovereigntyCapital>().Find(alliance);
        if (row is null)
        {
            return null;
        }
        if (
            row.NewSystemId is int pending
            && row.ValidAfter is long at
            && now >= at
            && world.Set<SavedSovereigntyClaim>().Find(pending)?.AllianceId == alliance
        )
        {
            return pending;
        }
        var changes = world.Filter<SavedSovereigntyCapitalChange>(change => change.AllianceId == alliance).ToArray();
        var history = changes
            .Where(change => change.At <= now)
            .OrderByDescending(change => change.At)
            .ThenByDescending(change => change.Id)
            .FirstOrDefault();
        return history is not null ? history.SystemId
            : changes.Length > 0 ? null
            : now < row.CurrentSince ? row.PreviousSystemId
            : row.CurrentSystemId;
    }

    internal static SovereigntyCapital Capital(WorldAction world, int alliance) =>
        world.Set<SavedSovereigntyCapital>().Find(alliance) is { } row
            ? new(row.CurrentSystemId, row.NewSystemId, row.ValidAfter)
            : new(null, null, null);

    internal static SovereigntyDevelopment Read(WorldAction world, int system, long now)
    {
        now = Math.Max(now, world.Set<SavedSovereigntyConflict>().Find(system)?.DevelopmentAt ?? 0);
        var activity = Activity(world, system, now);
        now = Math.Max(now, activity.UpdatedAt);
        var claim = world.Set<SavedSovereigntyClaim>().Find(system);
        bool capital = claim is not null && CapitalAt(world, claim.AllianceId, now) == system;
        var indices = new SovereigntyIndices(
            SovereigntyDevelopmentRules.Level(activity.Military),
            SovereigntyDevelopmentRules.Level(activity.Industrial),
            claim is null ? 0 : SovereigntyRules.StrategicIndex(claim.ClaimedAt, now)
        );
        return new(
            system,
            activity.Military,
            activity.Industrial,
            indices,
            claim is null ? 1 : SovereigntyRules.DefenseMultiplier(indices, capital),
            capital,
            now < activity.LastMilitaryAt + SovereigntyDevelopmentRules.TrendDuration,
            now < activity.LastIndustrialAt + SovereigntyDevelopmentRules.TrendDuration,
            claim is null ? 0 : Math.Max(0, now - claim.ClaimedAt) / TimeSpan.TicksPerDay,
            activity.Revision
        );
    }
}
