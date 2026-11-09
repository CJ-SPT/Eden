using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Eden.Data.Catalog.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.World.Capsuleers;
using Eden.World.Catalog;

namespace Eden.Server.Capsuleers;

internal static class TrafficImportCommand
{
    private const int DefaultWindowDays = 7;
    private const int MaximumParallelValidators = 4;
    private const int ProgressInterval = 500;

    internal static async Task<bool> TryRun(string[] args, CancellationToken cancellationToken = default)
    {
        if (!args.Contains("--import-traffic"))
        {
            return false;
        }
        string? Value(string option)
        {
            int index = Array.IndexOf(args, option);
            if (index < 0)
            {
                return null;
            }
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{option} requires a value.");
            }
            return args[index + 1];
        }
        string output = Path.GetFullPath(Value("--import-traffic")!);
        string directory = Path.GetDirectoryName(output)!;
        string catalog = Path.GetFullPath(Value("--catalog") ?? Path.Combine(directory, "catalog.sqlite"));
        DateOnly yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        DateOnly through = Value("--through") is { } last ? Parse(last) : yesterday;
        DateOnly from = Value("--from") is { } first ? Parse(first) : through.AddDays(1 - DefaultWindowDays);
        if (from > through || through > yesterday)
        {
            throw new InvalidDataException("Traffic imports require an ordered range of complete UTC dates.");
        }
        if (string.Equals(output, catalog, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Traffic output must be separate from the pinned catalog.");
        }
        Directory.CreateDirectory(directory);
        using FileStream ownership = new(output + ".import.owner", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using HttpClient client = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Eden-Traffic/1.0 (+https://github.com/CJ-SPT/Eden)");
        if (Value("--through") is null)
        {
            through = await new TrafficDownloader(client, output + ".cache", Console.WriteLine).LatestCompleteDate(
                yesterday,
                cancellationToken
            );
            from = Value("--from") is { } requestedFrom ? Parse(requestedFrom) : through.AddDays(1 - DefaultWindowDays);
            if (from > through)
            {
                throw new InvalidDataException("The requested start follows the latest published complete archive.");
            }
            Console.WriteLine($"Using published complete UTC archives {from:yyyy-MM-dd} through {through:yyyy-MM-dd}.");
        }
        var metadata = await Import(client, catalog, output, from, through, Console.WriteLine, cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        return true;

        static DateOnly Parse(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    internal static async Task<TrafficSnapshotManifest> Import(
        HttpClient client,
        string catalogPath,
        string output,
        DateOnly from,
        DateOnly through,
        Action<string> log,
        CancellationToken cancellationToken = default
    )
    {
        var started = Stopwatch.StartNew();
        TrafficSnapshot.CheckReplacement(output);
        string cache = output + ".cache";
        Directory.CreateDirectory(cache);
        var download = new TrafficDownloader(client, cache, log);
        using CatalogDbContext catalog = new(catalogPath);
        CatalogSources.CheckBuild(catalog);
        string fingerprint = TrafficSnapshot.Fingerprint(catalog);
        var systems = catalog.Definitions.Systems;
        string cacheFingerprint =
            fingerprint + "|" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(TrafficFitValidator).Assembly.Location)));
        string fitCachePath = Path.Combine(cache, "fits.json");
        var previousFits = File.Exists(fitCachePath)
            ? JsonSerializer.Deserialize<TrafficFitCache>(await File.ReadAllTextAsync(fitCachePath, cancellationToken))
            : null;
        ConcurrentDictionary<string, string> fitDecisions =
            previousFits?.Fingerprint == cacheFingerprint ? new(previousFits.Decisions) : [];
        ConcurrentDictionary<string, object> fitGates = [];
        object cacheGate = new();
        List<TrafficArchive> archives = [];
        List<TrafficProfile> candidates = [];
        Dictionary<string, long> rejected = [];
        HashSet<long> seen = [];
        Dictionary<(long Character, int Region, TrafficSecurityBand Band, int Hull), TrafficProfile> newest = [];
        long duplicates = 0;
        long records = 0;
        for (var date = through; date >= from; date = date.AddDays(-1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = await download.Archive(date, cancellationToken);
            string checksum;
            await using (var input = File.OpenRead(path))
            {
                checksum = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
            }
            archives.Add(new(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), checksum, new FileInfo(path).Length));
            await using var source = File.OpenRead(path);
            var kills =
                await JsonSerializer.DeserializeAsync<Dictionary<string, TrafficKillmail>>(source, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Empty traffic archive.");
            List<TrafficProfile> pending = [];
            foreach (var (key, kill) in kills)
            {
                records++;
                if (!long.TryParse(key, out long id) || id <= 0 || kill.Id != id || kill.Time == default)
                {
                    Reject("Invalid kill identity.");
                    continue;
                }
                if (!seen.Add(id))
                {
                    duplicates++;
                    continue;
                }
                if (kill.Victim is not { CharacterId: > 0, HullTypeId: > 0 } victim)
                {
                    Reject("Missing player victim.");
                    continue;
                }
                if (!systems.TryGetValue(kill.SystemId, out var location))
                {
                    Reject("Unknown source system.");
                    continue;
                }
                try
                {
                    var security =
                        catalog.Read(CatalogData.Client.Solarsystemcontent, (long)kill.SystemId)?.Security
                        ?? throw new InvalidDataException("Missing source security.");
                    TrafficProfile profile = new(
                        victim.CharacterId,
                        "",
                        id,
                        kill.Time,
                        kill.SystemId,
                        location.RegionId,
                        TrafficRules.SecurityBand(security),
                        victim.HullTypeId,
                        TrafficKillItem.Equipment(victim.Items ?? [])
                    );
                    pending.Add(profile);
                }
                catch (Exception error) when (error is InvalidDataException or OverflowException)
                {
                    Reject(error.Message);
                }
            }
            int completed = 0;
            var groups = pending
                .GroupBy(profile => (profile.CharacterId, profile.RegionId, profile.SecurityBand, profile.HullTypeId))
                .Select(group => group.OrderByDescending(profile => profile.KilledAt).ThenByDescending(profile => profile.KillId).ToArray())
                .ToArray();
            Parallel.ForEach<TrafficProfile[], TrafficFitValidator>(
                groups,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(MaximumParallelValidators, Environment.ProcessorCount),
                    CancellationToken = cancellationToken,
                },
                () => new(catalogPath),
                (group, _, validator) =>
                {
                    foreach (var profile in group)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string reason;
                        var observation = (profile.CharacterId, profile.RegionId, profile.SecurityBand, profile.HullTypeId);
                        TrafficProfile? previous;
                        lock (candidates)
                        {
                            newest.TryGetValue(observation, out previous);
                        }
                        if (
                            previous is not null
                            && (
                                previous.KilledAt > profile.KilledAt
                                || previous.KilledAt == profile.KilledAt && previous.KillId >= profile.KillId
                            )
                        )
                        {
                            reason = "Superseded compatible pilot/hull observation.";
                        }
                        else
                        {
                            try
                            {
                                validator.Check(profile);
                                string signature = TrafficSnapshot.ValidationSignature(profile);
                                lock (fitGates.GetOrAdd(signature, _ => new object()))
                                {
                                    if (!fitDecisions.TryGetValue(signature, out reason!))
                                    {
                                        reason = validator.Validate(profile) ?? "";
                                        fitDecisions[signature] = reason;
                                    }
                                }
                            }
                            catch (InvalidDataException error)
                            {
                                reason = error.Message;
                            }
                        }
                        if (reason.Length > 0)
                        {
                            Reject(reason);
                        }
                        else
                        {
                            lock (candidates)
                            {
                                if (previous is not null)
                                {
                                    candidates.Remove(previous);
                                    Reject("Superseded compatible pilot/hull observation.");
                                }
                                candidates.Add(profile);
                                newest[observation] = profile;
                            }
                        }
                        int count = Interlocked.Increment(ref completed);
                        if (count % ProgressInterval == 0)
                        {
                            SaveFits();
                            log($"Validating {date:yyyy-MM-dd}: {count:N0}/{pending.Count:N0} victims; {started.Elapsed:c}");
                        }
                    }
                    return validator;
                },
                validator => validator.Dispose()
            );
            SaveFits();
            log($"{date:yyyy-MM-dd}: {records:N0} records, {candidates.Count:N0} compatible fits; {started.Elapsed:c}");
        }
        var names = await download.Names(candidates.Select(profile => profile.CharacterId), cancellationToken);
        List<TrafficProfile> accepted = [];
        foreach (var profile in candidates)
        {
            if (
                !names.TryGetValue(profile.CharacterId, out string? name)
                || CharacterRules.ValidateName(name, false) != CharacterNameResult.Available
            )
            {
                Reject("Unavailable or invalid character name.");
                continue;
            }
            accepted.Add(profile with { Name = name });
        }
        if (accepted.Count == 0)
        {
            throw new InvalidDataException("No complete supported traffic fits were accepted; the installed snapshot was preserved.");
        }
        var metadata = new TrafficSnapshotManifest(
            TrafficRules.SnapshotVersion,
            TrafficSnapshot.Identity(fingerprint, accepted),
            fingerprint,
            from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            archives.OrderBy(archive => archive.Date).ToArray(),
            records,
            duplicates,
            accepted.Count,
            accepted.Select(profile => profile.CharacterId).Distinct().Count(),
            accepted.Select(profile => profile.HullTypeId).Distinct().Count(),
            accepted.Select(TrafficSnapshot.Signature).Distinct().Count(),
            rejected
        );
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".sqlite";
        try
        {
            TrafficSnapshot.Write(temporary, metadata, accepted);
            TrafficSnapshot.Read(temporary, fingerprint);
            cancellationToken.ThrowIfCancellationRequested();
            TrafficSnapshot.CheckReplacement(output);
            File.Move(temporary, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        log(
            $"Imported {metadata.Pilots:N0} pilots, {metadata.Hulls:N0} hulls, {metadata.Fits:N0} fits "
                + $"from {archives.Sum(archive => archive.Bytes):N0} archive bytes in {started.Elapsed:c}."
        );
        return metadata;

        void Reject(string reason)
        {
            lock (rejected)
            {
                rejected[reason] = rejected.GetValueOrDefault(reason) + 1;
            }
        }

        void SaveFits()
        {
            lock (cacheGate)
            {
                string temporary = fitCachePath + ".part";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new TrafficFitCache(cacheFingerprint, fitDecisions.ToDictionary())));
                File.Move(temporary, fitCachePath, overwrite: true);
            }
        }
    }
}
