using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
namespace FFXIVFlipper;
public sealed record ScanMarketItem(Dictionary<string, MarketSnapshot>? Supply, MarketSnapshot? Home, string? Error);
public sealed class MarketClient : IDisposable
{
    internal const int MaxResponseBytes = 16 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly SemaphoreSlim slots = new(2);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Time, string Json)> cache = new();
    private long cooldownUntil;
    public MarketClient(HttpMessageHandler? handler = null)
    {
        http = handler == null ? new() : new(handler);
        http.Timeout = TimeSpan.FromSeconds(25);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FFXIVFlipper/0.6.1");
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
            // ResponseHeadersRead avoids buffering an unbounded body. Keep the timeout
            // active through streaming too; HttpClient's timeout ends at the headers.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(http.Timeout);
            var requestToken = timeout.Token;
            using var response = await http.GetAsync($"https://universalis.app/api/v2/{path}", HttpCompletionOption.ResponseHeadersRead, requestToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(30);
                var seconds = Math.Max(1, (long)Math.Ceiling(wait.TotalSeconds));
                Interlocked.Exchange(ref cooldownUntil, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + seconds);
                throw new HttpRequestException($"Universalis rate limit: retry in {seconds} seconds.");
            }
            if (response.StatusCode == HttpStatusCode.NotFound) throw new HttpRequestException("World or item has no available market data.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new InvalidDataException("Market response exceeds the 16 MiB limit.");
            using var body = new MemoryStream();
            await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
            var buffer = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxResponseBytes - (int)body.Length + 1)), requestToken);
                if (read == 0) break;
                if (body.Length + read > MaxResponseBytes)
                    throw new InvalidDataException("Market response exceeds the 16 MiB limit.");
                body.Write(buffer, 0, read);
            }
            using var boundedContent = new ByteArrayContent(body.ToArray());
            boundedContent.Headers.ContentType = response.Content.Headers.ContentType;
            var json = await boundedContent.ReadAsStringAsync(requestToken);
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
        using var doc = JsonDocument.Parse(json);
        return ParseSupply(doc.RootElement, dc, item, worlds);
    }
    private static Dictionary<string, MarketSnapshot> ParseSupply(JsonElement root, string dc, uint item, IReadOnlyDictionary<uint, string> worlds)
    {
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
    // Responses are intentionally uncached: at most the current and next ten-item batches
    // are retained by the scanner, rather than accumulating large raw batch payloads.
    public async Task<Dictionary<uint, ScanMarketItem>> ScanBatch(string dc, uint homeWorld, uint[] items,
        IReadOnlyDictionary<uint, string> worlds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (items.Length is < 1 or > 10 || items.Any(x => x == 0) || items.Distinct().Count() != items.Length)
            throw new ArgumentException("A scan batch must contain one to ten distinct item IDs.", nameof(items));
        var ids = string.Join(",", items);
        var until = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        async Task<(string? Json, string? Error)> Read(string path)
        {
            try { return (await Get(path, token, false), null); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return (null, "Market request timed out; item skipped."); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException)
            { return (null, ex.Message); }
        }
        var supplyTask = Read($"{Uri.EscapeDataString(dc)}/{ids}?listings=1000&entries=0");
        var homeTask = Read($"{homeWorld}/{ids}?listings=1000&entries=0");
        var historyTask = Read($"history/{homeWorld}/{ids}?entriesToReturn={MarketParser.HistoryLimit}&entriesWithin={MarketParser.WindowSeconds}&entriesUntil={until}");
        await Task.WhenAll(supplyTask, homeTask, historyTask);
        token.ThrowIfCancellationRequested();
        var responses = new[] { await supplyTask, await homeTask, await historyTask };
        var result = new Dictionary<uint, ScanMarketItem>();
        var error = responses.FirstOrDefault(x => x.Error?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) == true).Error
            ?? responses.FirstOrDefault(x => x.Error != null).Error;
        if (error != null)
        {
            foreach (var item in items) result[item] = new(null, null, error);
            return result;
        }
        using var supplyDoc = JsonDocument.Parse(responses[0].Json!);
        using var homeDoc = JsonDocument.Parse(responses[1].Json!);
        using var historyDoc = JsonDocument.Parse(responses[2].Json!);
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var supply = BatchItem(supplyDoc.RootElement, item, items.Length > 1, dc, null);
                var home = BatchItem(homeDoc.RootElement, item, items.Length > 1, null, homeWorld);
                var history = BatchItem(historyDoc.RootElement, item, items.Length > 1, null, homeWorld);
                var snapshot = MarketParser.Listings(home.GetRawText(), homeWorld, item);
                snapshot = snapshot with
                {
                    SampleLimited = snapshot.Listings.Length >= 1000,
                    History = MarketParser.History(history.GetRawText(), homeWorld, item, until)
                };
                result[item] = new(ParseSupply(supply, dc, item, worlds), snapshot, null);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or FormatException or OverflowException)
            { result[item] = new(null, null, ex.Message); }
        }
        return result;
    }
    private static JsonElement BatchItem(JsonElement root, uint item, bool batch, string? dc, uint? world)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            (dc != null && (!root.TryGetProperty("dcName", out var dcName) || dcName.GetString() != dc)) ||
            (world != null && (!root.TryGetProperty("worldID", out var worldId) || !worldId.TryGetUInt32(out var value) || value != world)))
            throw new InvalidDataException("Batch response did not match the requested market.");
        if (batch)
        {
            if (root.TryGetProperty("unresolvedItems", out var unresolved) && unresolved.ValueKind == JsonValueKind.Array &&
                unresolved.EnumerateArray().Any(x => x.TryGetUInt32(out var value) && value == item))
                throw new InvalidDataException($"Item {item}: market data unresolved.");
              if (!root.TryGetProperty("items", out var entries) || entries.ValueKind != JsonValueKind.Object ||
                !entries.TryGetProperty(item.ToString(), out root))
                throw new InvalidDataException($"Item {item}: missing from market batch.");
        }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("itemID", out var id) || !id.TryGetUInt32(out var actual) || actual != item ||
            (dc != null && (!root.TryGetProperty("dcName", out var name) || name.GetString() != dc)) ||
            (world != null && (!root.TryGetProperty("worldID", out var w) || !w.TryGetUInt32(out var actualWorld) || actualWorld != world)))
            throw new InvalidDataException($"Item {item}: response identity mismatch.");
        return root;
    }
    public async Task<uint[]> ActiveCandidates(uint homeWorld, uint[] candidates, int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var scores = new List<(uint Id, double Score)>();
        // Bounded chunks; two requests maximum through Get, with no aggregate cache.
        var chunks = candidates.Where(x => x > 0).Distinct().Take(CandidateDiscovery.SweepSize).Chunk(100).ToArray();
        for (var offset = 0; offset < chunks.Length; offset += 2)
        {
            var pair = chunks.Skip(offset).Take(2).ToArray();
            var payloads = await Task.WhenAll(pair.Select(ids => Get($"aggregated/{homeWorld}/{string.Join(",", ids)}", token, false)));
            for (var i = 0; i < payloads.Length; i++)
            {
                using var doc = JsonDocument.Parse(payloads[i]);
                if (!doc.RootElement.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Market activity data unavailable.");
                var allowed = pair[i].ToHashSet();
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (var row in rows.EnumerateArray())
                {
                    if (!row.TryGetProperty("itemId", out var id) || !id.TryGetUInt32(out var value) || !allowed.Contains(value))
                        throw new InvalidDataException("Market activity response contains an unexpected item.");
                    var score = CandidateDiscovery.ActivityScore(row, now);
                    if (score > 0) scores.Add((value, score));
                }
            }
        }
        return scores.OrderByDescending(x => x.Score).ThenBy(x => x.Id).Select(x => x.Id).Distinct().Take(count).ToArray();
    }
    public void Dispose() => http.Dispose();
}








