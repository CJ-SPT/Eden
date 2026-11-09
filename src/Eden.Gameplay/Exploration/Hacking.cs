using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Exploration;

public enum ExplorationKind
{
    Data,
    Relic,
}

public enum HackingNode
{
    Empty = -1,
    Segment,
    Virus,
    Core,
    Defense,
    Utility,
    UtilityTile,
    Cache,
}

public enum HackingSubsystem
{
    None = -1,
    SelfRepair = 0,
    KernelRot = 1,
    SecondaryVector = 2,
    Shield = 3,
    HighCore = 10,
    LowCore = 11,
    MediumCore = 12,
    Antivirus = 100,
    Firewall = 101,
    Suppressor = 102,
    Restoration = 103,
}

public enum HackingOutcome
{
    Running,
    Won,
    Lost,
}

public enum HackingActionKind
{
    Click,
    UtilityOnTile,
    UtilityOnVirus,
}

public sealed record HackingCoordinate(int X, int Y);

public sealed record HackingAction(HackingActionKind Kind, HackingCoordinate? Coordinate = null, int Slot = -1);

public sealed record HackingTile(
    int Id,
    HackingCoordinate Coordinate,
    HackingNode Type,
    HackingSubsystem Subtype,
    int Coherence = 0,
    int Strength = 0,
    bool Hidden = true,
    bool Blocked = false,
    int VectorTurns = 0,
    bool CacheDefense = false
);

public sealed record HackingUtility(
    int Id,
    HackingSubsystem Subtype = HackingSubsystem.None,
    int Remaining = 0,
    bool Active = false,
    HackingCoordinate? Target = null
);

public sealed record HackingBoard(
    IReadOnlyList<HackingTile> Tiles,
    IReadOnlyList<HackingUtility> Inventory,
    int Coherence,
    int BaseStrength,
    int Strength,
    HackingOutcome Outcome = HackingOutcome.Running,
    int Turn = 0
);

public sealed record AnalyzerModule(
    long Id,
    int TypeId,
    double DurationMs,
    double CapacitorNeed,
    double Range,
    int Coherence,
    int Strength,
    int Slots
);

public sealed record HackingContact(
    long PilotId,
    long ShipId,
    int SystemId,
    long ContainerId,
    ExplorationKind Kind,
    AnalyzerModule Module,
    long StartedAt,
    long ReadyAt,
    HackingBoard Board,
    double Capacitor
);

public sealed record HackingTurn(HackingContact Contact, HackingBoard Previous, HackingBoard Current, HackingAction? Action = null);

public sealed record ExplorationContainerInventory(InventoryItem Container, IReadOnlyList<InventoryItem> Items);

public interface IPirateExploration
{
    IReadOnlyList<CombatAnomaly> Signatures(int systemId, long now);
}

public interface IPilotHacking
{
    HackingContact Begin(long pilotId, long moduleId, long targetId, IReadOnlySet<long> locks, long now);
    HackingTurn Act(long pilotId, HackingAction action, long now);
    void Cancel(long pilotId, long now);
    ExplorationContainerInventory Read(long pilotId, long containerId, long now);
    IReadOnlyList<InventoryChange> Take(long pilotId, long shipId, InventoryMoveRequest request, long now);
}

public static class HackingRules
{
    public const int AnalyzerGroup = 538;
    public const int ContainerGroup = 1207;
    public const int HackingSkill = 21718;
    public const int ArchaeologySkill = 13278;
    public const int MaximumFailures = 2;
    public const int MaximumSlots = 3;
    public const int Width = 8;
    public const int Height = 8;
    public const int UtilityDuration = 3;
    public const int RepairPerTurn = 5;
    public const int VectorDamage = 20;
    public const int ShieldHits = 2;
    public const int Suppression = 15;
    public const int MinimumStrength = 10;
    public const int RestorationAmount = 20;
    public const int MaximumDistanceHint = 5;
    public const int DataStrengthAttribute = 208;
    public const int RelicStrengthAttribute = 210;
    public const int DataArchetype = 45;
    public const int RelicArchetype = 44;
    public const long FirstSiteId = 7_800_000_000_000;
    public const long FirstContainerId = 7_900_000_000_000;
    public const long ContainerEndId = 8_000_000_000_000;
    public const int ContainersPerSite = 100;
    public const double LootRange = 2_500;
    public const long SiteLifetime = TimeSpan.TicksPerDay;
    public const long ReplacementDelay = 600 * TimeSpan.TicksPerSecond;

    public static bool IsContainer(long id) => id >= FirstContainerId && id < ContainerEndId;

    public static bool Neighbors(HackingCoordinate a, HackingCoordinate b)
    {
        int dx = b.X - a.X;
        int dy = b.Y - a.Y;
        // Pinned hackingUIConst's odd-row offset hex grid.
        return dy == 0 ? Math.Abs(dx) == 1 : Math.Abs(dy) == 1 && (dx == 0 || dx == (a.Y % 2 == 0 ? -1 : 1));
    }

    public static HackingBoard Create(int seed, int difficulty, int coherence, int strength, int slots)
    {
        if (difficulty is < 0 or > 5 || coherence <= 0 || strength <= 0 || slots is < 1 or > MaximumSlots)
        {
            throw new InvalidDataException("Invalid hacking profile.");
        }
        Random random = new(seed);
        List<HackingTile> tiles = [];
        int core = random.Next(Height) * Width + Width - 1;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int id = y * Width + x;
                HackingNode type = HackingNode.Segment;
                HackingSubsystem subtype = HackingSubsystem.None;
                int hp = 0;
                int attack = 0;
                int roll = random.Next(100);
                if (id == core)
                {
                    type = HackingNode.Core;
                    subtype =
                        difficulty < 3 ? HackingSubsystem.LowCore
                        : difficulty == 3 ? HackingSubsystem.MediumCore
                        : HackingSubsystem.HighCore;
                    hp = 50 + difficulty * 10;
                    attack = 10;
                }
                else if (x != 0 && roll < 12 + difficulty * 2)
                {
                    type = HackingNode.Defense;
                    subtype = (HackingSubsystem)(
                        100
                        + random.Next(
                            difficulty >= 4 ? 4
                            : difficulty == 3 ? 2
                            : 2
                        )
                    );
                    if (difficulty == 3 && random.Next(3) == 0)
                    {
                        subtype = HackingSubsystem.Restoration;
                    }
                    hp = subtype == HackingSubsystem.Antivirus ? 30 + difficulty * 5 : 40 + difficulty * 10;
                    attack = subtype == HackingSubsystem.Antivirus ? 30 : 20;
                }
                else if (x != 0 && roll < 28 + difficulty * 2)
                {
                    type = HackingNode.UtilityTile;
                    subtype = (HackingSubsystem)random.Next(4);
                }
                else if (x != 0 && roll < 33 + difficulty * 2)
                {
                    type = HackingNode.Cache;
                }
                tiles.Add(
                    new(
                        id + 2,
                        new(x, y),
                        type,
                        subtype,
                        hp,
                        attack,
                        id != 0,
                        CacheDefense: type == HackingNode.Cache && random.Next(2) == 0
                    )
                );
            }
        }
        return Refresh(
            new(tiles.ToArray(), Enumerable.Range(0, slots).Select(i => new HackingUtility(i)).ToArray(), coherence, strength, strength)
        );
    }

    public static HackingBoard Step(HackingBoard previous, HackingAction action)
    {
        if (!Enum.IsDefined(action.Kind))
        {
            throw new InvalidDataException("Invalid hacking action.");
        }
        if (previous.Outcome != HackingOutcome.Running)
        {
            throw new InvalidDataException("This hacking attempt has ended.");
        }
        var tiles = previous.Tiles.ToArray();
        var inventory = previous.Inventory.ToArray();
        int coherence = previous.Coherence;
        int shields = -1;
        int index = Array.FindIndex(tiles, t => t.Coordinate == action.Coordinate);
        if (action.Kind == HackingActionKind.Click)
        {
            if (index < 0 || tiles[index].Hidden && tiles[index].Blocked || !Accessible(tiles, tiles[index]))
            {
                throw new InvalidDataException("That hacking node is not reachable.");
            }
            var tile = tiles[index];
            if (tile.Hidden)
            {
                // Server-generated cache content is deterministic and never disclosed until opened.
                tiles[index] = tile with
                {
                    Hidden = false,
                };
            }
            else if (tile.Type is HackingNode.Core or HackingNode.Defense)
            {
                int hp = Math.Max(0, tile.Coherence - previous.Strength);
                tiles[index] = tile with { Coherence = hp };
                if (hp > 0)
                {
                    shields = Array.FindIndex(inventory, u => u.Subtype == HackingSubsystem.Shield && u.Active && u.Remaining > 0);
                    if (shields >= 0)
                    {
                        var shield = inventory[shields];
                        inventory[shields] = shield.Remaining == 1 ? new(shield.Id) : shield with { Remaining = shield.Remaining - 1 };
                    }
                    else
                    {
                        coherence = Math.Max(0, coherence - tile.Strength);
                    }
                }
                if (hp == 0 && tile.Type == HackingNode.Defense)
                {
                    tiles[index] = tile with { Type = HackingNode.Segment, Subtype = HackingSubsystem.None, Coherence = 0, Strength = 0 };
                }
            }
            else if (tile.Type == HackingNode.UtilityTile)
            {
                int free = Array.FindIndex(inventory, u => u.Subtype == HackingSubsystem.None);
                if (free < 0)
                {
                    throw new InvalidDataException("The utility inventory is full.");
                }
                inventory[free] = new(tile.Id, tile.Subtype);
                tiles[index] = tile with { Type = HackingNode.Segment, Subtype = HackingSubsystem.None };
            }
            else if (tile.Type == HackingNode.Cache)
            {
                bool defense = tile.CacheDefense;
                tiles[index] = tile with
                {
                    Type = defense ? HackingNode.Defense : HackingNode.UtilityTile,
                    Subtype = defense ? HackingSubsystem.Antivirus : HackingSubsystem.KernelRot,
                    Coherence = defense ? 40 : 0,
                    Strength = defense ? 20 : 0,
                };
            }
            else
            {
                throw new InvalidDataException("Choose an unexplored node, defense or utility.");
            }
        }
        else
        {
            if (
                action.Slot < 0
                || action.Slot >= inventory.Length
                || inventory[action.Slot].Subtype == HackingSubsystem.None
                || inventory[action.Slot].Active
            )
            {
                throw new InvalidDataException("Select an available utility.");
            }
            var utility = inventory[action.Slot];
            bool self = utility.Subtype is HackingSubsystem.SelfRepair or HackingSubsystem.Shield;
            if ((action.Kind == HackingActionKind.UtilityOnVirus) != self)
            {
                throw new InvalidDataException("This utility cannot be applied to that target.");
            }
            if (self)
            {
                inventory[action.Slot] = utility with
                {
                    Active = true,
                    Remaining = utility.Subtype == HackingSubsystem.Shield ? ShieldHits : UtilityDuration,
                };
            }
            else
            {
                if (index < 0 || tiles[index].Hidden || tiles[index].Type is not (HackingNode.Core or HackingNode.Defense))
                {
                    throw new InvalidDataException("Select a revealed, reachable defense or core.");
                }
                var target = tiles[index];
                tiles[index] =
                    utility.Subtype == HackingSubsystem.KernelRot
                        ? target with
                        {
                            Coherence = Math.Max(1, target.Coherence / 2),
                        }
                        : target with
                        {
                            VectorTurns = UtilityDuration,
                        };
                inventory[action.Slot] =
                    utility.Subtype == HackingSubsystem.SecondaryVector
                        ? utility with
                        {
                            Active = true,
                            Remaining = UtilityDuration,
                            Target = target.Coordinate,
                        }
                        : new(utility.Id);
            }
        }
        // Lethal retaliation ends the game before utility ticks; repair cannot revive a defeated virus.
        if (coherence <= 0)
        {
            return Refresh(
                previous with
                {
                    Tiles = tiles,
                    Inventory = inventory,
                    Coherence = 0,
                    Outcome = HackingOutcome.Lost,
                    Turn = previous.Turn + 1,
                }
            );
        }
        for (int i = 0; i < tiles.Length; i++)
        {
            var tile = tiles[i];
            if (tile.VectorTurns > 0 && tile.Coherence > 0)
            {
                tile = tile with { Coherence = Math.Max(0, tile.Coherence - VectorDamage), VectorTurns = tile.VectorTurns - 1 };
                if (tile.Coherence == 0 && tile.Type == HackingNode.Defense)
                {
                    tile = tile with { Type = HackingNode.Segment, Subtype = HackingSubsystem.None, Strength = 0 };
                }
                tiles[i] = tile;
            }
        }
        for (int i = 0; i < inventory.Length; i++)
        {
            var utility = inventory[i];
            if (utility.Subtype == HackingSubsystem.SecondaryVector && utility.Active)
            {
                inventory[i] = utility.Remaining == 1 ? new(utility.Id) : utility with { Remaining = utility.Remaining - 1 };
            }
            if (utility.Subtype == HackingSubsystem.SelfRepair && utility.Active)
            {
                coherence += RepairPerTurn;
                inventory[i] = utility.Remaining == 1 ? new(utility.Id) : utility with { Remaining = utility.Remaining - 1 };
            }
        }
        bool won = tiles.Any(t => t.Type == HackingNode.Core && t.Coherence == 0);
        if (!won && coherence > 0)
        {
            foreach (var restorer in tiles.Where(t => !t.Hidden && t.Subtype == HackingSubsystem.Restoration && t.Coherence > 0))
            {
                int target = Array.FindIndex(
                    tiles,
                    t => !t.Hidden && t.Type == HackingNode.Defense && t.Id != restorer.Id && t.Coherence > 0
                );
                if (target >= 0)
                {
                    tiles[target] = tiles[target] with { Coherence = tiles[target].Coherence + RestorationAmount };
                }
            }
        }
        var outcome =
            won ? HackingOutcome.Won
            : coherence <= 0 ? HackingOutcome.Lost
            : HackingOutcome.Running;
        return Refresh(
            previous with
            {
                Tiles = tiles,
                Inventory = inventory,
                Coherence = coherence,
                Outcome = outcome,
                Turn = previous.Turn + 1,
            }
        );
    }

    public static bool Accessible(IReadOnlyList<HackingTile> tiles, HackingTile tile) =>
        !tile.Hidden || tiles.Any(t => !t.Hidden && t.Type == HackingNode.Segment && Neighbors(t.Coordinate, tile.Coordinate));

    public static int DistanceHint(HackingBoard board, HackingCoordinate from)
    {
        HashSet<HackingCoordinate> visited = [from];
        HashSet<HackingCoordinate> frontier = [from];
        for (int distance = 1; distance <= MaximumDistanceHint; distance++)
        {
            var next = board.Tiles.Where(t => !visited.Contains(t.Coordinate) && frontier.Any(p => Neighbors(p, t.Coordinate))).ToArray();
            if (next.Any(t => t.Type is HackingNode.Core or HackingNode.UtilityTile))
            {
                return distance;
            }
            frontier = next.Select(t => t.Coordinate).ToHashSet();
            visited.UnionWith(frontier);
        }
        return MaximumDistanceHint;
    }

    private static HackingBoard Refresh(HackingBoard board)
    {
        var tiles = board
            .Tiles.Select(t =>
                t with
                {
                    Blocked = board.Tiles.Any(d =>
                        !d.Hidden
                        && d.Type == HackingNode.Defense
                        && d.Coherence > 0
                        && d.Id != t.Id
                        && Neighbors(d.Coordinate, t.Coordinate)
                    ),
                }
            )
            .ToArray();
        int suppressors = tiles.Count(t => !t.Hidden && t.Subtype == HackingSubsystem.Suppressor && t.Coherence > 0);
        return board with
        {
            Tiles = tiles,
            Strength = suppressors == 0 ? board.BaseStrength : Math.Max(MinimumStrength, board.BaseStrength - suppressors * Suppression),
        };
    }
}
