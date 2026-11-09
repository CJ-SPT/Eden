using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Information;
using Eden.Protocol.Exploration;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Tests.Exploration;

public sealed class HackingRuleTests
{
    private static HackingTile Tile(
        int x,
        HackingNode type = HackingNode.Segment,
        HackingSubsystem subtype = HackingSubsystem.None,
        int health = 0,
        int strength = 0,
        bool hidden = false
    ) => new(x + 2, new(x, 0), type, subtype, health, strength, hidden);

    private static HackingBoard Board(params HackingTile[] tiles) =>
        new(
            tiles,
            [new(100, HackingSubsystem.SelfRepair), new(101, HackingSubsystem.KernelRot), new(102, HackingSubsystem.Shield)],
            40,
            20,
            20
        );

    [Fact]
    public void CoreRetaliatesOnlyIfItSurvivesAndEarlierSnapshotsRemainUnchanged()
    {
        var original = Board(Tile(0), Tile(1, HackingNode.Core, HackingSubsystem.LowCore, 40, 10));
        var first = HackingRules.Step(original, new(HackingActionKind.Click, new(1, 0)));
        Assert.Equal(30, first.Coherence);
        Assert.Equal(20, first.Tiles[1].Coherence);
        Assert.Equal(40, original.Coherence);
        Assert.Equal(40, original.Tiles[1].Coherence);
        var final = HackingRules.Step(first, new(HackingActionKind.Click, new(1, 0)));
        Assert.Equal(HackingOutcome.Won, final.Outcome);
        Assert.Equal(30, final.Coherence);
        Assert.Throws<InvalidDataException>(() => HackingRules.Step(final, new(HackingActionKind.Click, new(1, 0))));
    }

    [Fact]
    public void HiddenNodesRequireAdjacentExplorationAndCannotBeAttackedBeforeReveal()
    {
        var original = Board(Tile(0), Tile(1, hidden: true), Tile(2, HackingNode.Core, HackingSubsystem.LowCore, 50, 10, true));
        Assert.Throws<InvalidDataException>(() => HackingRules.Step(original, new(HackingActionKind.Click, new(2, 0))));
        var next = HackingRules.Step(original, new(HackingActionKind.Click, new(1, 0)));
        var reveal = HackingRules.Step(next, new(HackingActionKind.Click, new(2, 0)));
        Assert.Equal(40, reveal.Coherence);
        Assert.Equal(50, reveal.Tiles[2].Coherence);
        Assert.False(reveal.Tiles[2].Hidden);
    }

    [Fact]
    public void ShieldAbsorbsTwoRetaliationsAndRepairRunsForThreeTurns()
    {
        var board = Board(Tile(0), Tile(1, HackingNode.Defense, HackingSubsystem.Firewall, 200, 20));
        board = HackingRules.Step(board, new(HackingActionKind.UtilityOnVirus, Slot: 2));
        board = HackingRules.Step(board, new(HackingActionKind.UtilityOnVirus, Slot: 0));
        Assert.Equal(45, board.Coherence);
        board = HackingRules.Step(board, new(HackingActionKind.Click, new(1, 0)));
        Assert.Equal(50, board.Coherence);
        board = HackingRules.Step(board, new(HackingActionKind.Click, new(1, 0)));
        Assert.Equal(55, board.Coherence);
        Assert.Equal(HackingSubsystem.None, board.Inventory[0].Subtype);
        Assert.Equal(HackingSubsystem.None, board.Inventory[2].Subtype);
        board = HackingRules.Step(board, new(HackingActionKind.Click, new(1, 0)));
        Assert.Equal(35, board.Coherence);
    }

    [Fact]
    public void KernelRotHalvesDefenseAndRejectsSelfTargetOrForgedSlots()
    {
        var board = Board(Tile(0), Tile(1, HackingNode.Defense, HackingSubsystem.Firewall, 80, 10));
        Assert.Throws<InvalidDataException>(() => HackingRules.Step(board, new(HackingActionKind.UtilityOnVirus, Slot: 1)));
        Assert.Throws<InvalidDataException>(() => HackingRules.Step(board, new(HackingActionKind.UtilityOnTile, new(1, 0), -1)));
        var next = HackingRules.Step(board, new(HackingActionKind.UtilityOnTile, new(1, 0), 1));
        Assert.Equal(40, next.Tiles[1].Coherence);
        Assert.Equal(HackingSubsystem.None, next.Inventory[1].Subtype);
        Assert.Equal(80, board.Tiles[1].Coherence);
    }

    [Fact]
    public void SecondaryVectorDealsThreeTicksAndPreventsReuseWhileItRuns()
    {
        var board = Board(Tile(0), Tile(1, HackingNode.Defense, HackingSubsystem.Firewall, 100, 0), Tile(2, hidden: true));
        board = board with { Inventory = [new(100, HackingSubsystem.SecondaryVector)] };
        board = HackingRules.Step(board, new(HackingActionKind.UtilityOnTile, new(1, 0), 0));
        Assert.Equal(80, board.Tiles[1].Coherence);
        Assert.Equal(2, board.Inventory[0].Remaining);
        Assert.Throws<InvalidDataException>(() => HackingRules.Step(board, new(HackingActionKind.UtilityOnTile, new(1, 0), 0)));
        board = HackingRules.Step(board, new(HackingActionKind.Click, new(1, 0)));
        Assert.Equal(40, board.Tiles[1].Coherence);
        board = HackingRules.Step(board, new(HackingActionKind.Click, new(1, 0)));
        Assert.Equal(HackingNode.Segment, board.Tiles[1].Type);
        Assert.Equal(HackingSubsystem.None, board.Inventory[0].Subtype);
    }

    [Fact]
    public void RevealedDefensesRemainAttackableWhenTheyBlockEachOthersNeighbors()
    {
        var board = Board(
            Tile(0),
            Tile(1, HackingNode.Defense, HackingSubsystem.Firewall, 20, 10),
            Tile(2, HackingNode.Defense, HackingSubsystem.Suppressor, 20, 0),
            Tile(3, hidden: true)
        );
        board = HackingRules.Step(board, new(HackingActionKind.UtilityOnVirus, Slot: 2));
        Assert.Equal(10, board.Strength);
        Assert.True(board.Tiles[1].Blocked);
        Assert.Throws<InvalidDataException>(() => HackingRules.Step(board, new(HackingActionKind.Click, new(3, 0))));
        board = HackingRules.Step(board, new(HackingActionKind.Click, new(2, 0)));
        board = HackingRules.Step(board, new(HackingActionKind.Click, new(2, 0)));
        Assert.Equal(20, board.Strength);
    }

    [Fact]
    public void RestorationHealsAnotherRevealedDefenseButNeverTheCore()
    {
        var board = Board(
            Tile(0),
            Tile(1, HackingNode.Defense, HackingSubsystem.Restoration, 80, 10),
            Tile(2, HackingNode.Core, HackingSubsystem.LowCore, 50, 10),
            Tile(3, HackingNode.Defense, HackingSubsystem.Firewall, 40, 0)
        );
        board = HackingRules.Step(board, new(HackingActionKind.UtilityOnVirus, Slot: 2));
        Assert.Equal(60, board.Tiles[3].Coherence);
        Assert.Equal(50, board.Tiles[2].Coherence);
    }

    [Fact]
    public void DistanceHintsUseHexPathsAndIncludeUsefulUtilities()
    {
        var board = Board(
            Tile(0),
            Tile(1),
            Tile(2, HackingNode.UtilityTile, HackingSubsystem.Shield),
            Tile(3),
            Tile(4, HackingNode.Core, HackingSubsystem.LowCore, 40)
        );
        Assert.Equal(2, HackingRules.DistanceHint(board, new(0, 0)));
        Assert.True(HackingRules.Neighbors(new(0, 1), new(1, 0)));
        Assert.True(HackingRules.Neighbors(new(1, 0), new(0, 1)));
    }

    [Fact]
    public void LethalRetaliationCannotBeUndoneByAnActiveRepair()
    {
        var board = Board(Tile(0), Tile(1, HackingNode.Core, HackingSubsystem.LowCore, 100, 100));
        board = HackingRules.Step(board, new(HackingActionKind.UtilityOnVirus, Slot: 0));
        var lost = HackingRules.Step(board, new(HackingActionKind.Click, new(1, 0)));
        Assert.Equal(HackingOutcome.Lost, lost.Outcome);
        Assert.Equal(0, lost.Coherence);
        Assert.Equal(80, lost.Tiles[1].Coherence);
    }

    [Fact]
    public void ExplorationAndCombatIdentifiersCannotOverlapAtTheReservedBoundary()
    {
        Assert.Equal(ScannerTargetIds.FromSequence(0), ScannerTargetIds.FromCombatSequence(0));
        Assert.NotEqual(ScannerTargetIds.FromCombatSequence(0), ScannerTargetIds.FromExplorationSequence(0));
        Assert.Throws<InvalidDataException>(() => ScannerTargetIds.FromCombatSequence(ScannerTargetIds.ExplorationSequenceOffset));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScannerTargetIds.FromExplorationSequence(-1));
    }

    [Theory]
    [InlineData(NpcPirateFaction.Guristas, SalvageFamily.Caldari)]
    [InlineData(NpcPirateFaction.Angels, SalvageFamily.Minmatar)]
    [InlineData(NpcPirateFaction.Serpentis, SalvageFamily.Gallente)]
    [InlineData(NpcPirateFaction.BloodRaiders, SalvageFamily.BloodRaider)]
    [InlineData(NpcPirateFaction.Sansha, SalvageFamily.Sansha)]
    public void RelicRewardsUseFactionSalvageAndKeepAdvancedMaterialsInNullSecurity(NpcPirateFaction faction, SalvageFamily family)
    {
        for (int band = 0; band < 3; band++)
        {
            for (int seed = 0; seed < 20; seed++)
            {
                var loot = ExplorationLootRules.Roll(seed, ExplorationKind.Relic, faction, band, [], []);
                var pool = SalvageLootRules.Pool(family, advanced: band == 2 && seed % 3 == 0).Select(t => (int)t).ToArray();
                Assert.Equal(2, loot.Count);
                Assert.All(
                    loot,
                    stack =>
                    {
                        Assert.Contains(stack.TypeId, pool);
                        Assert.InRange(stack.Quantity, 1, 3);
                    }
                );
                Assert.Equal(loot, ExplorationLootRules.Roll(seed, ExplorationKind.Relic, faction, band, [], []));
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void GeneratedBoardsAreDeterministicConnectedAndHaveOneCore(int difficulty)
    {
        var first = HackingRules.Create(123, difficulty, 90, 25, 3);
        var second = HackingRules.Create(123, difficulty, 90, 25, 3);
        Assert.Equal(first.Tiles, second.Tiles);
        Assert.Single(first.Tiles, t => t.Type == HackingNode.Core);
        Assert.Equal(HackingRules.Width * HackingRules.Height, first.Tiles.Count);
        var visited = new HashSet<HackingCoordinate> { new(0, 0) };
        while (true)
        {
            var adjacent = first
                .Tiles.Where(t => !visited.Contains(t.Coordinate) && visited.Any(p => HackingRules.Neighbors(p, t.Coordinate)))
                .ToArray();
            if (adjacent.Length == 0)
            {
                break;
            }
            visited.UnionWith(adjacent.Select(t => t.Coordinate));
        }
        Assert.Equal(first.Tiles.Count, visited.Count);
    }

    [Fact]
    public void NativeStartMasksUnexploredObjectsAndRoundTripsTheEventList()
    {
        var board = HackingRules.Create(10, 1, 90, 25, 3);
        HackingContact contact = new(1, 2, 3, 4, ExplorationKind.Relic, new(5, 22177, 10000, 20, 5000, 90, 25, 3), 100, 200, board, 100);
        var decoded = Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(new HackingEvents(contact, board))));
        var tiles = decoded.OfType<Dictionary<string, object?>>().Where(e => (long)e["eventID"]! == 101).ToArray();
        Assert.Equal(board.Tiles.Count, tiles.Length);
        foreach (var entry in tiles)
        {
            var tile = Assert.IsType<Dictionary<string, object?>>(entry["eventData"]);
            if (Equals(tile["hidden"], true))
            {
                Assert.Equal(-1L, tile["type"]);
                Assert.Equal(-1L, tile["subtype"]);
                Assert.Equal(0L, tile["coherence"]);
            }
        }
        Assert.Equal(2L, Assert.IsType<Dictionary<string, object?>>(decoded[^1])["eventID"]);
    }
}
