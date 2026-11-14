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
    IReadOnlyList<AutoMiningOre> OreTypes();
    AutoMiningFilter ReadFilter(long pilotId);
    void SaveFilter(long pilotId, AutoMiningFilter filter);
    AutoMiningOptions ReadOptions(long pilotId);
    void SaveOptions(long pilotId, AutoMiningOptions options);
    void SaveSettings(long pilotId, int systemId, AutoMiningFilter filter, AutoMiningOptions options, AutoMiningHaulSettings hauling);
    IReadOnlyDictionary<DogmaAttributeId, double> ShipAttributes(long pilotId);
    IReadOnlyList<AutoMiningModule> Modules(long pilotId, long shipId);
    IReadOnlyDictionary<long, double> Survey(long pilotId, int systemId, IReadOnlyList<long> visibleTargets);
    JettisonResult Jettison(long pilotId, long shipId, MiningFamily family);
}

public sealed record AutoMiningOre(int TypeId, int GroupId, string Name);

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
    public const int MaximumTypes = 512;
    private const int MaximumLegacyLength = 512;
    private const int MaximumSavedLength = MaximumTypes * 13;
    private static readonly Dictionary<string, int> s_legacyGroups = Enum.GetValues<MiningResourceGroup>()
        .ToDictionary(group => group.ToString(), group => (int)group, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _groups;
    private readonly HashSet<int> _types;

    static AutoMiningFilter()
    {
        s_legacyGroups.Add("Mercoxit", MiningDefinitions.MercoxitGroup);
    }

    private AutoMiningFilter(HashSet<int> groups, HashSet<int> types)
    {
        _groups = groups;
        _types = types;
        Text = string.Join(
            ", ",
            s_legacyGroups
                .Where(group => groups.Contains(group.Value))
                .Select(group => group.Key)
                .Concat(types.Order().Select(typeId => FormattableString.Invariant($"#{typeId}")))
        );
    }

    public const string ModId = "eden.automining";
    public string Text { get; }

    public bool Matches(StaticSpaceObject rock) =>
        MiningDefinitions.MatchesFamily(MiningFamily.Ore, rock.CategoryId, rock.GroupId)
        && (Text.Length == 0 || Includes(rock.TypeId, rock.GroupId));

    public bool Includes(int typeId, int groupId) => _types.Contains(typeId) || _groups.Contains(groupId);

    public static IEnumerable<string> LegacyGroupNames(IReadOnlyList<AutoMiningOre> available)
    {
        var groups = available.Select(ore => ore.GroupId).ToHashSet();
        return s_legacyGroups
            .Where(group => groups.Contains(group.Value))
            .Select(group => group.Key)
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    public static AutoMiningFilter FromTypes(IEnumerable<int> requested, IReadOnlyList<AutoMiningOre> available)
    {
        var types = requested.ToHashSet();
        if (types.Count > MaximumTypes || !types.IsSubsetOf(available.Select(ore => ore.TypeId)))
        {
            throw new InvalidDataException("Choose ore types from the available list.");
        }
        return new([], types);
    }

    public static AutoMiningFilter Parse(string text, bool saved = false)
    {
        int limit = saved ? MaximumSavedLength : MaximumLegacyLength;
        if (text.Length > limit)
        {
            throw new InvalidDataException($"Keep the ore filter to {limit} characters or fewer.");
        }
        HashSet<int> groups = [];
        HashSet<int> types = [];

        foreach (string name in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (saved && name[0] == '#' && int.TryParse(name.AsSpan(1), out int typeId) && typeId > 0)
            {
                types.Add(typeId);
                continue;
            }
            string key = string.Concat(name.Where(character => !char.IsWhiteSpace(character)));
            if (!s_legacyGroups.TryGetValue(key, out int group))
            {
                throw new InvalidDataException("Choose ore types from the available list.");
            }
            groups.Add(group);
        }
        return new(groups, types);
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
