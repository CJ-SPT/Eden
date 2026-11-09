using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Eden.Server.Capsuleers;

internal sealed class TrafficDownloader(HttpClient client, string cache, Action<string> log)
{
    private const int Attempts = 4;
    private const int NameBatchSize = 500;

    internal async Task<DateOnly> LatestCompleteDate(DateOnly latest, CancellationToken cancellationToken)
    {
        using var response = await Send(() => new(HttpMethod.Get, "https://r2z2.zkillboard.com/history/totals.json"), cancellationToken);
        var totals =
            await response.Content.ReadFromJsonAsync<Dictionary<string, long>>(cancellationToken)
            ?? throw new InvalidDataException("Missing traffic archive totals.");
        var dates = totals
            .Where(entry => entry.Value > 0)
            .Select(entry =>
                DateOnly.TryParseExact(
                    entry.Key,
                    "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var date
                )
                    ? (DateOnly?)date
                    : null
            )
            .Where(date => date.HasValue && date.Value <= latest)
            .Select(date => date!.Value)
            .ToArray();
        return dates.Length > 0 ? dates.Max() : throw new InvalidDataException("No published complete traffic archive is available.");
    }

    internal async Task<string> Archive(DateOnly date, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cache);
        string name = date.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        string path = Path.Combine(cache, name + ".json");
        if (File.Exists(path))
        {
            return path;
        }
        string temporary = path + ".part";
        try
        {
            using var response = await Send(
                () => new(HttpMethod.Get, $"https://r2z2.zkillboard.com/history/raw/{name}.json"),
                cancellationToken
            );
            await using (var output = File.Create(temporary))
            {
                await response.Content.CopyToAsync(output, cancellationToken);
            }
            // A truncated or error response is never promoted to a completed cache file.
            await using (var input = File.OpenRead(temporary))
            using (var document = await JsonDocument.ParseAsync(input, cancellationToken: cancellationToken))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Invalid daily traffic archive.");
                }
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    if (!long.TryParse(entry.Name, out long id) || id <= 0 || entry.Value.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidDataException("Daily traffic archive contains an invalid killmail entry.");
                    }
                }
            }
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal async Task<Dictionary<long, string>> Names(IEnumerable<long> characters, CancellationToken cancellationToken)
    {
        string path = Path.Combine(cache, "names.json");
        Dictionary<long, string> names = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<long, string>>(await File.ReadAllTextAsync(path, cancellationToken)) ?? []
            : [];
        var missing = characters.Distinct().Where(character => !names.ContainsKey(character)).Order().ToArray();
        int completed = 0;
        foreach (var batch in missing.Chunk(NameBatchSize))
        {
            await Resolve(batch);
            string temporary = path + ".part";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(names), cancellationToken);
            File.Move(temporary, path, overwrite: true);
            completed += batch.Length;
            log($"Resolved names: {completed:N0}/{missing.Length:N0}");
        }
        return names;

        async Task Resolve(long[] ids)
        {
            try
            {
                using var response = await Send(
                    () =>
                        new(HttpMethod.Post, "https://esi.evetech.net/latest/universe/names/?datasource=tranquility")
                        {
                            Content = JsonContent.Create(ids),
                        },
                    cancellationToken
                );
                var values = await response.Content.ReadFromJsonAsync<TrafficCharacter[]>(cancellationToken) ?? [];
                foreach (var value in values.Where(value => value.Category == "character" && ids.Contains(value.Id)))
                {
                    names[value.Id] = value.Name;
                }
            }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            {
                // ESI can reject a whole batch containing a deleted identity.
                // Isolate it without discarding otherwise resolvable pilots.
                if (ids.Length > 1)
                {
                    int half = ids.Length / 2;
                    await Resolve(ids[..half]);
                    await Resolve(ids[half..]);
                }
            }
        }
    }

    private async Task<HttpResponseMessage> Send(Func<HttpRequestMessage> request, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            using var message = request();
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception error)
                when (attempt + 1 < Attempts
                    && !cancellationToken.IsCancellationRequested
                    && error is HttpRequestException or TaskCanceledException
                )
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
                continue;
            }
            if (
                (
                    response.StatusCode == HttpStatusCode.TooManyRequests
                    || (int)response.StatusCode >= (int)HttpStatusCode.InternalServerError
                )
                && attempt + 1 < Attempts
            )
            {
                TimeSpan delay =
                    response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                    ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                response.Dispose();
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }
            try
            {
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }
}
