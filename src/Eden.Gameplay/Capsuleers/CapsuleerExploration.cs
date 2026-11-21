using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Capsuleers;

public enum ExplorationPhase
{
    Launching,
    Scanning,
    Warping,
    Working,
    Returning,
}

public sealed record ExplorationProgress(int StationId, SpacePosition Center, long EndsAt)
{
    public ExplorationPhase Phase { get; init; }
    public long ReadyTicks { get; init; }
    public int RangeStep { get; init; } = CapsuleerExplorationRules.InitialRangeStep;
    public int Scans { get; init; }
    public bool ScanInFlight { get; init; }
    public long SignalId { get; init; }
    public long ContainerId { get; init; }
    public long[] VisitedContainers { get; init; } = [];
    public long[] LootIds { get; init; } = [];
}

public static class CapsuleerExplorationRules
{
    public const int InitialRangeStep = 5;
    public const int MaximumScans = 9;
    public const int MaximumHackTurns = 75;
    public const double SafeSecurity = 0.45;
    public const long SortieTicks = 10 * TimeSpan.TicksPerMinute;
    public const long RetryTicks = 5 * TimeSpan.TicksPerMinute;

    private static readonly SpacePosition[] s_offsets =
    [
        new(1, 0, 0),
        new(-1, 0, 0),
        new(0, 1, 0),
        new(0, -1, 0),
        new(0, 0, 1),
        new(0, 0, -1),
        new(1, 1, 1),
        new(-1, -1, -1),
    ];

    public static ProbePlacement[] Formation(IReadOnlyList<ScanProbe> probes, SpacePosition center, int step) =>
        probes
            .Select(
                (probe, index) =>
                {
                    double range = probe.Profile.BaseRangeAu * Math.Pow(probe.Profile.RangeFactor, step - 1);
                    return new ProbePlacement(
                        probe.Id,
                        center + s_offsets[index].Unit * (range * ProbeScanRules.AstronomicalUnit / 8),
                        step
                    );
                }
            )
            .ToArray();

    public static HackingAction? Hack(HackingBoard board)
    {
        var visible = board.Tiles.Where(tile => !tile.Hidden).ToArray();
        var core = visible.FirstOrDefault(tile => tile.Type == HackingNode.Core);
        var threats = visible
            .Where(tile => tile.Type == HackingNode.Defense)
            .OrderByDescending(tile => tile.Subtype is HackingSubsystem.Restoration or HackingSubsystem.Suppressor)
            .ThenBy(tile => Retaliation(tile, board.Strength))
            .ThenBy(tile => tile.Id)
            .ToArray();
        var slots = board.Inventory.Select((value, index) => (value, index)).ToArray();
        var active = slots.Where(slot => slot.value.Active).Select(slot => slot.value.Subtype).ToHashSet();
        var repair = Array.FindIndex(slots, slot => !slot.value.Active && slot.value.Subtype == HackingSubsystem.SelfRepair);
        if (repair >= 0 && !active.Contains(HackingSubsystem.SelfRepair) && board.Coherence <= 55)
        {
            return new(HackingActionKind.UtilityOnVirus, Slot: repair);
        }
        if (core is not null)
        {
            return Fight(core, board, active);
        }
        if (
            slots.Any(slot => slot.value.Subtype == HackingSubsystem.None)
            && visible.FirstOrDefault(tile => tile.Type == HackingNode.UtilityTile) is { } utility
        )
        {
            return new(HackingActionKind.Click, utility.Coordinate);
        }
        var obstructing = threats.FirstOrDefault(tile => tile.Subtype is HackingSubsystem.Restoration or HackingSubsystem.Suppressor);
        if (obstructing is not null)
        {
            return Fight(obstructing, board, active);
        }
        var frontier = board
            .Tiles.Where(tile => tile.Hidden && !tile.Blocked && HackingRules.Accessible(visible, tile))
            .OrderByDescending(tile => tile.Coordinate.X + tile.Coordinate.Y)
            .ThenBy(tile => tile.Id)
            .FirstOrDefault();
        if (frontier is not null)
        {
            return new(HackingActionKind.Click, frontier.Coordinate);
        }
        if (threats.FirstOrDefault() is { } threat)
        {
            return Fight(threat, board, active);
        }
        return null;
    }

    private static int Retaliation(HackingTile tile, int strength) => ((tile.Coherence - 1) / strength) * tile.Strength;

    private static HackingAction Fight(HackingTile tile, HackingBoard board, HashSet<HackingSubsystem> active)
    {
        if (tile.Coherence > board.Strength)
        {
            for (int i = 0; i < board.Inventory.Count; i++)
            {
                var utility = board.Inventory[i];
                if (utility.Active)
                {
                    continue;
                }
                if (
                    utility.Subtype == HackingSubsystem.KernelRot
                    || utility.Subtype == HackingSubsystem.SecondaryVector && tile.VectorTurns == 0
                )
                {
                    return new(HackingActionKind.UtilityOnTile, tile.Coordinate, i);
                }
                if (utility.Subtype == HackingSubsystem.Shield && !active.Contains(HackingSubsystem.Shield))
                {
                    return new(HackingActionKind.UtilityOnVirus, Slot: i);
                }
            }
        }
        return new(HackingActionKind.Click, tile.Coordinate);
    }
}
