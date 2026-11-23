using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eden.Data.Catalog.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

internal sealed class TrafficSnapshot(
    TrafficSnapshotManifest? manifest,
    TrafficProfile[] profiles,
    IReadOnlyList<TrafficProfile>? recorded = null
)
{
    internal static TrafficSnapshot Empty { get; } = new(null, []);
    internal TrafficSnapshotManifest? Manifest => manifest;
    internal IReadOnlyList<TrafficProfile> Profiles => profiles;
    private readonly Dictionary<(int Region, TrafficSecurityBand Band), TrafficProfile[]> _regions = profiles
        .GroupBy(profile => (profile.RegionId, profile.SecurityBand))
        .ToDictionary(group => group.Key, group => Ordered(group));
    private readonly Dictionary<TrafficSecurityBand, TrafficProfile[]> _bands = profiles
        .GroupBy(profile => profile.SecurityBand)
        .ToDictionary(group => group.Key, group => Ordered(group));
    private readonly TrafficProfile[] _global = Ordered(profiles);
    private readonly Dictionary<long, TrafficProfile> _kills = (recorded ?? profiles).ToDictionary(profile => profile.KillId);

    internal TrafficProfile? Find(long killId) => _kills.GetValueOrDefault(killId);

    private const int MaximumRuntimePools = 128;
    private readonly Lock _runtimePoolGate = new();
    private readonly Dictionary<(long Catalog, ReservationsKey Policy), TrafficCandidatePools> _runtimePools = [];

    private readonly record struct ReservationsKey(
        int Region,
        TrafficSecurityBand Band,
        bool HighSecurity,
        bool LightAllowed,
        double CargoVolume,
        double DroneVolume,
        bool Correction
    );

    internal TrafficCandidatePools RuntimeCandidates(
        long catalogGeneration,
        int region,
        TrafficSecurityBand band,
        bool highSecurity,
        bool lightAllowed,
        Func<TrafficProfile, bool> compatible,
        Func<TrafficProfile, int> hullGroup,
        double cargoVolume = 0,
        double droneVolume = 0,
        bool correction = false
    )
    {
        var key = (catalogGeneration, new ReservationsKey(region, band, highSecurity, lightAllowed, cargoVolume, droneVolume, correction));
        lock (_runtimePoolGate)
        {
            if (_runtimePools.TryGetValue(key, out var existing))
            {
                return existing;
            }
        }
        var prepared = PrepareCandidates(region, band, compatible, hullGroup);
        // Materialize while the caller's catalog is alive. Completed Lazy factories release
        // their captures, leaving only immutable snapshot profiles and stable selection order.
        int tierCount = 0;
        foreach (var tier in prepared.Tiers)
        {
            tierCount++;
            if (tier.Value.Groups.Length > 0 && correction)
            {
                // Correction takes the first compatible profile and has no identity reservation to skip.
                break;
            }
        }
        if (tierCount < prepared.Tiers.Length)
        {
            prepared = new(prepared.Tiers[..tierCount]);
        }
        lock (_runtimePoolGate)
        {
            if (_runtimePools.TryGetValue(key, out var existing))
            {
                return existing;
            }
            if (_runtimePools.Count >= MaximumRuntimePools)
            {
                _runtimePools.Remove(_runtimePools.Keys.First());
            }
            _runtimePools.Add(key, prepared);
            return prepared;
        }
    }

    private static TrafficProfile[] Ordered(IEnumerable<TrafficProfile> values) =>
        values.OrderByDescending(profile => profile.KilledAt).ThenByDescending(profile => profile.KillId).ToArray();

    // Each tier already orders newest kills first, and filtering preserves that order.
    private static TrafficProfile[] Latest(IEnumerable<TrafficProfile> ordered) =>
        ordered.DistinctBy(profile => profile.CharacterId).OrderBy(profile => profile.CharacterId).ToArray();

    internal IEnumerable<TrafficProfile> Candidates(
        long seed,
        int system,
        int slot,
        int region,
        TrafficSecurityBand band,
        Func<TrafficProfile, bool>? compatible = null,
        Func<TrafficProfile, int>? hullGroup = null
    ) => PrepareCandidates(region, band, compatible, hullGroup).Candidates(seed, system, slot);

    internal TrafficCandidatePools PrepareCandidates(
        int region,
        TrafficSecurityBand band,
        Func<TrafficProfile, bool>? compatible,
        Func<TrafficProfile, int>? hullGroup
    )
    {
        var regional = Pool(_regions.GetValueOrDefault((region, band), []));
        var security = Pool(_bands.GetValueOrDefault(band, []));
        return new([Tier(regional, null), Tier(security, regional), Tier(Pool(_global), security)]);

        Lazy<TrafficProfile[]> Pool(TrafficProfile[] values) => new(() => Latest(compatible is null ? values : values.Where(compatible)));

        Lazy<TrafficCandidateTier> Tier(Lazy<TrafficProfile[]> current, Lazy<TrafficProfile[]>? previous) =>
            new(() =>
            {
                var pool = current.Value;
                // Each broader tier contains every compatible character in the preceding tier.
                // Leave holes for repeats so the seeded group and member rotations keep their original lengths.
                if (previous is not null && previous.Value.Length == pool.Length)
                {
                    return new(hullGroup is not null, []);
                }
                var earlier = previous?.Value.Select(profile => profile.CharacterId).ToHashSet();
                TrafficCandidateGroup Group(int key, IEnumerable<TrafficProfile> members) =>
                    new(key, members.Select(profile => earlier?.Contains(profile.CharacterId) == true ? null : profile).ToArray());

                return hullGroup is null
                    ? new(false, [Group(0, pool)])
                    : new(true, pool.GroupBy(hullGroup).OrderBy(group => group.Key).Select(group => Group(group.Key, group)).ToArray());
            });
    }

    internal sealed record TrafficCandidateGroup(int Key, TrafficProfile?[] Members);

    internal sealed record TrafficCandidateTier(bool Grouped, TrafficCandidateGroup[] Groups);

    internal sealed record TrafficCandidatePools(Lazy<TrafficCandidateTier>[] Tiers)
    {
        internal IEnumerable<TrafficProfile> Candidates(long seed, int system, int slot)
        {
            foreach (var prepared in Tiers)
            {
                var tier = prepared.Value;
                var groups = tier.Groups;
                int groupStart = TrafficRules.Start(seed, system, slot, groups.Length);
                for (int index = 0; index < groups.Length; index++)
                {
                    var group = groups[(groupStart + index) % groups.Length];
                    var members = group.Members;
                    int start = TrafficRules.Start(tier.Grouped ? seed ^ group.Key : seed, system, slot, members.Length);
                    for (int member = 0; member < members.Length; member++)
                    {
                        if (members[(start + member) % members.Length] is { } profile)
                        {
                            yield return profile;
                        }
                    }
                }
            }
        }
    }

    internal static string Fingerprint(CatalogDbContext catalog) => catalog.Definitions.Identity.SourceFingerprint;

    internal static string Signature(TrafficProfile profile) =>
        profile.HullTypeId
        + ":"
        + string.Join(
            ";",
            profile
                .Equipment.OrderBy(item => item.Flag)
                .ThenBy(item => item.TypeId)
                .Select(item => $"{item.Flag}:{item.TypeId}:{item.Quantity}")
        );

    internal static string ValidationSignature(TrafficProfile profile) =>
        profile.HullTypeId
        + ":"
        + string.Join(
            ";",
            profile.Equipment.OrderBy(item => item.Flag).ThenBy(item => item.TypeId).Select(item => $"{item.Flag}:{item.TypeId}")
        );

    internal static string Identity(string fingerprint, IEnumerable<TrafficProfile> values) =>
        Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    fingerprint
                        + "\n"
                        + string.Join("\n", values.OrderBy(profile => profile.KillId).Select(profile => JsonSerializer.Serialize(profile)))
                )
            )
        );

    internal static void Write(string path, TrafficSnapshotManifest metadata, IReadOnlyList<TrafficProfile> values)
    {
        using var connection = TrafficConnections.Snapshot(path, writable: true);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE Manifest(Payload TEXT NOT NULL);
            CREATE TABLE Profile(KillId INTEGER PRIMARY KEY, CharacterId INTEGER NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX Profile_Character ON Profile(CharacterId);
            """;
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO Manifest VALUES ($payload)";
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(metadata));
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO Profile VALUES ($kill, $character, $payload)";
        command.Parameters.AddWithValue("$kill", 0L);
        command.Parameters.AddWithValue("$character", 0L);
        foreach (var profile in values)
        {
            command.Parameters["$kill"].Value = profile.KillId;
            command.Parameters["$character"].Value = profile.CharacterId;
            command.Parameters["$payload"].Value = JsonSerializer.Serialize(profile);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal static void CheckReplacement(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        using var connection = TrafficConnections.Snapshot(path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var reader = command.ExecuteReader();
        List<string> tables = [];
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }
        if (!tables.SequenceEqual(["Manifest", "Profile"]))
        {
            throw new InvalidDataException("Traffic import may replace only an existing traffic snapshot, never another database.");
        }
    }

    internal static TrafficSnapshot Read(string path, string fingerprint, CatalogDbContext? catalog = null)
    {
        using var connection = TrafficConnections.Snapshot(path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        if (!Equals(command.ExecuteScalar(), "ok"))
        {
            throw new InvalidDataException("Traffic snapshot failed its SQLite integrity check.");
        }
        var metadata = ReadManifest(command, fingerprint);
        command.CommandText = "SELECT Payload FROM Profile ORDER BY KillId";
        using var reader = command.ExecuteReader();
        List<TrafficProfile> values = [];
        while (reader.Read())
        {
            values.Add(
                JsonSerializer.Deserialize<TrafficProfile>(reader.GetString(0))
                    ?? throw new InvalidDataException("Missing traffic profile.")
            );
        }
        if (values.Count != metadata.Accepted || Identity(fingerprint, values) != metadata.Identity)
        {
            throw new InvalidDataException("Traffic snapshot content checksum does not match.");
        }
        if (catalog is null)
        {
            return new(metadata, values.ToArray());
        }
        var mutationTypes = values
            .SelectMany(profile => profile.Equipment)
            .Select(item => item.TypeId)
            .Distinct()
            .Where(type => MutationCatalog.BaseType(catalog, type) != type)
            .ToHashSet();
        if (mutationTypes.Count == 0)
        {
            return new(metadata, values.ToArray());
        }
        var compatible = values.Where(profile => !profile.Equipment.Any(item => mutationTypes.Contains(item.TypeId))).ToArray();
        Console.Error.WriteLine($"Skipped {values.Count - compatible.Length:N0} traffic fits with missing Abyssal mutations.");
        // Keep recorded fits available by kill ID for saved identities, ammunition and correction.
        return new(metadata, compatible, values);
    }

    internal static TrafficSnapshotManifest ReadManifest(string path, string fingerprint)
    {
        using var connection = TrafficConnections.Snapshot(path);
        connection.Open();
        using var command = connection.CreateCommand();
        return ReadManifest(command, fingerprint);
    }

    private static TrafficSnapshotManifest ReadManifest(Microsoft.Data.Sqlite.SqliteCommand command, string fingerprint)
    {
        command.CommandText = "SELECT Payload FROM Manifest";
        if (
            command.ExecuteScalar() is not string payload
            || JsonSerializer.Deserialize<TrafficSnapshotManifest>(payload) is not { } metadata
        )
        {
            throw new InvalidDataException("Missing traffic snapshot manifest.");
        }
        if (metadata.Version != TrafficRules.SnapshotVersion || metadata.CatalogFingerprint != fingerprint)
        {
            throw new InvalidDataException("Traffic snapshot version or catalog fingerprint does not match; reimport it.");
        }
        return metadata;
    }
}
