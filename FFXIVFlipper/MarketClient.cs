using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
namespace FFXIVFlipper;
public sealed class MarketClient : IDisposable
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim slots = new(2);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Time, string Json)> cache = new();
    private long cooldownUntil;
    public MarketClient(HttpMessageHandler? handler = null)
    {
        http = handler == null ? new() : new(handler);
        http.Timeout = TimeSpan.FromSeconds(25);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FFXIVFlipper/0.5.2");
    }
    public async Task<MarketSnapshot> Fetch(uint world, uint item, bool withHistory, CancellationToken token, int listingLimit = 100)
    {
        var snapshot = MarketParser.Listings(await Get($"{world}/{item}?listings={listingLimit}&entries=0", token), world, item);
        snapshot = snapshot with { SampleLimited = snapshot.Listings.Length >= listingLimit };
        if (!withHistory) return snapshot;
        var until = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // History is deliberately uncached so the explicit end time always matches its coverage window.
        try
        {
            var json = await Get($"history/{world}/{item}?entriesToReturn={MarketParser.HistoryLimit}&entriesWithin={MarketParser.WindowSeconds}&entriesUntil={until}", token, false);
            return snapshot with { History = MarketParser.History(json, world, item, until) };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return snapshot with { History = new([], false, "Sales history timed out. Reload to retry.", until) };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or System.Text.Json.JsonException)
        {
            return snapshot with { History = new([], false, $"Sales history unavailable: {ex.Message}", until) };
        }
    }
    private async Task<string> Get(string path, CancellationToken token, bool useCache = true)
    {
        token.ThrowIfCancellationRequested();
        if (useCache && cache.TryGetValue(path, out var saved) && DateTimeOffset.UtcNow - saved.Time < TimeSpan.FromSeconds(30)) return saved.Json;
        await slots.WaitAsync(token);
        try
        {
            var remaining = Interlocked.Read(ref cooldownUntil) - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (remaining > 0) throw new HttpRequestException($"Universalis rate limit: retry in {remaining} seconds.");
            using var response = await http.GetAsync($"https://universalis.app/api/v2/{path}", token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(30);
                var seconds = Math.Max(1, (long)Math.Ceiling(wait.TotalSeconds));
                Interlocked.Exchange(ref cooldownUntil, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + seconds);
                throw new HttpRequestException($"Universalis rate limit: retry in {seconds} seconds.");
            }
            if (response.StatusCode == HttpStatusCode.NotFound) throw new HttpRequestException("World or item has no available market data.");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(token);
            // Validate JSON before retaining a successful response; identity and schema are checked by the parser.
            using var parsed = System.Text.Json.JsonDocument.Parse(json);
            if (useCache)
            {
                if (cache.Count >= 64) cache.Clear();
                cache[path] = (DateTimeOffset.UtcNow, json);
            }
            return json;
        }
        finally { slots.Release(); }
    }
    public async Task<uint[]> Candidates(string dc, int count, CancellationToken token)
    {
        var json = await Get($"extra/stats/most-recently-updated?dcName={Uri.EscapeDataString(dc)}&entries={Math.Clamp(count, 1, 200)}", token, false);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var rows) || rows.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new InvalidDataException("Candidate response has no items.");
        return rows.EnumerateArray().Where(x => x.TryGetProperty("itemID", out var id) && id.TryGetUInt32(out var value) && value > 0)
            .Select(x => x.GetProperty("itemID").GetUInt32()).Distinct().Take(count).ToArray();
    }
    public async Task<Dictionary<string, MarketSnapshot>> Supply(string dc, uint item, IReadOnlyDictionary<uint, string> worlds, CancellationToken token)
    {
        var json = await Get($"{Uri.EscapeDataString(dc)}/{item}?listings=1000&entries=0", token);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("itemID", out var id) || id.GetUInt32() != item || !root.TryGetProperty("dcName", out var name) || name.GetString() != dc)
            throw new InvalidDataException("Supply response did not match the requested item and data center.");
        if (!root.TryGetProperty("listings", out var rows) || rows.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new InvalidDataException("Cross-world listings unavailable.");
        var result = new Dictionary<string, MarketSnapshot>();
        foreach (var group in rows.EnumerateArray().Where(x => x.TryGetProperty("worldID", out var w) && w.TryGetUInt32(out var value) && worlds.ContainsKey(value)).GroupBy(x => x.GetProperty("worldID").GetUInt32()))
        {
            long uploaded = 0;
            if (root.TryGetProperty("worldUploadTimes", out var times) && times.TryGetProperty(group.Key.ToString(), out var time)) time.TryGetInt64(out uploaded);
            var worldJson = System.Text.Json.JsonSerializer.Serialize(new { worldID = group.Key, itemID = item, lastUploadTime = uploaded, listings = group.ToArray() });
            result[worlds[group.Key]] = MarketParser.Listings(worldJson, group.Key, item) with { SampleLimited = true };
        }
        return result;
    }
    public void Dispose() => http.Dispose();
}








