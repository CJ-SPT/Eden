using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;

namespace Eden.Gameplay.Mining;

public interface IAutoMiningAvailability
{
    bool AutoMiningEnabled { get; }
}

public interface IPilotAutoMining
{
    AutoMiningFilter ReadFilter(long pilotId);
    void SaveFilter(long pilotId, AutoMiningFilter filter);
    AutoMiningOptions ReadOptions(long pilotId);
    void SaveOptions(long pilotId, AutoMiningOptions options);
    IReadOnlyDictionary<DogmaAttributeId, double> ShipAttributes(long pilotId);
    IReadOnlyList<AutoMiningModule> Modules(long pilotId, long shipId);
    IReadOnlyDictionary<long, double> Survey(long pilotId, int systemId, IReadOnlyList<long> visibleTargets);
    JettisonResult Jettison(long pilotId, long shipId, MiningFamily family);
}

public enum AutoMiningTargetOrder
{
    Nearest,
    Largest,
    Smallest,
}

public sealed record AutoMiningOptions(MiningFamily Family, AutoMiningTargetOrder TargetOrder, bool Approach, bool Jettison)
{
    public static AutoMiningOptions Default { get; } = new(MiningFamily.Ore, AutoMiningTargetOrder.Nearest, false, false);

    public bool Matches(StaticSpaceObject rock, AutoMiningFilter filter) =>
        Family == MiningFamily.Ore ? filter.Matches(rock) : MiningDefinitions.MatchesFamily(Family, rock.CategoryId, rock.GroupId);
}

public sealed record AutoMiningModule(long Id, double Range, MiningFamily Family, bool DeepCore, InventoryTypeList? CrystalTargets)
{
    public bool Matches(StaticSpaceObject rock) =>
        (rock.GroupId != MiningDefinitions.MercoxitGroup || DeepCore)
        && (CrystalTargets?.Contains(rock.TypeId, rock.GroupId, rock.CategoryId) ?? true);
}

public sealed class AutoMiningFilter
{
    private const int MaximumLength = 512;
    private static readonly Dictionary<string, int> s_oreGroups = Enum.GetValues<MiningResourceGroup>()
        .ToDictionary(group => group.ToString(), group => (int)group, StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<int> _groups;

    static AutoMiningFilter()
    {
        s_oreGroups.Add("Mercoxit", MiningDefinitions.MercoxitGroup);
    }

    private AutoMiningFilter(HashSet<int> groups)
    {
        _groups = groups;
        Text = string.Join(", ", s_oreGroups.Where(group => groups.Contains(group.Value)).Select(group => group.Key));
    }

    public const string ModId = "eden.automining";
    public string Text { get; }

    public bool Matches(StaticSpaceObject rock) =>
        rock.CategoryId == AsteroidDefinitions.Category
        && (rock.GroupId == MiningDefinitions.MercoxitGroup || Enum.IsDefined((MiningResourceGroup)rock.GroupId))
        && (_groups.Count == 0 || _groups.Contains(rock.GroupId));

    public static AutoMiningFilter Parse(string text)
    {
        if (text.Length > MaximumLength)
        {
            throw new InvalidDataException($"Keep the ore filter to {MaximumLength} characters or fewer.");
        }
        HashSet<int> groups = [];
        foreach (string name in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string key = string.Concat(name.Where(character => !char.IsWhiteSpace(character)));
            if (!s_oreGroups.TryGetValue(key, out int group))
            {
                throw new InvalidDataException("Enter ore group names such as Veldspar or Scordite, separated by commas.");
            }
            groups.Add(group);
        }
        var result = new AutoMiningFilter(groups);
        if (result.Text.Length > MaximumLength)
        {
            throw new InvalidDataException($"Keep the ore filter to {MaximumLength} characters or fewer.");
        }
        return result;
    }
}

public readonly record struct AutoMiningCandidate(StaticSpaceObject Rock, double Distance)
{
    public static AutoMiningCandidate[] Nearest(
        IEnumerable<StaticSpaceObject> rocks,
        SpacePosition origin,
        double shipRadius,
        double range
    ) =>
        rocks
            .Select(rock => new AutoMiningCandidate(rock, Math.Max(0, origin.DistanceTo(rock.Position) - shipRadius - rock.Radius)))
            .Where(candidate => candidate.Distance <= range)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Rock.Id)
            .ToArray();
}
