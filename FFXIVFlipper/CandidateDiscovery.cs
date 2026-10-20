using System.Text.Json;
namespace FFXIVFlipper;
public enum CandidateSource { Balanced, ActiveMarket, Watchlist, RecentUpdates }
public sealed class SavedWatchlist
{
    public string Name { get; set; } = "Favorites";
    public List<uint> Items { get; set; } = [];
}
public sealed record DiscoveryRequest(CandidateSource Source, uint HomeWorld, string BuyDc, int Count, uint[] Watchlist, uint[] Sweep);
public static class CandidateDiscovery
{
    public const int MaxLists = 8, MaxWatchlistItems = 200, SweepSize = 1000;
    public static uint[] Sweep(uint[] catalog, int cursor, int count = SweepSize)
    {
        if (catalog.Length == 0) return [];
        var start = (int)((uint)Math.Max(0, cursor) % catalog.Length);
        return Enumerable.Range(0, Math.Min(count, catalog.Length)).Select(i => catalog[(start + i) % catalog.Length]).ToArray();
    }
    // Round-robin sources prevents a full recent list from crowding out new candidates.
    public static uint[] Mix(int count, params uint[][] sources)
    {
        var result = new List<uint>(); var seen = new HashSet<uint>();
        var length = sources.Length == 0 ? 0 : sources.Max(x => x.Length);
        for (var i = 0; i < length && result.Count < count; i++)
            foreach (var source in sources)
                if (i < source.Length && source[i] != 0 && seen.Add(source[i]))
                { result.Add(source[i]); if (result.Count == count) break; }
        return result.ToArray();
    }
    public static async Task<uint[]> Find(MarketClient market, DiscoveryRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var count = Math.Clamp(request.Count, 1, 200);
        if (request.Source == CandidateSource.Watchlist) return request.Watchlist.Where(x => x > 0).Distinct().Take(count).ToArray();
        if (request.Source == CandidateSource.RecentUpdates) return await market.Candidates(request.BuyDc, count, token);
        var active = market.ActiveCandidates(request.HomeWorld, request.Sweep, count, token);
        if (request.Source == CandidateSource.ActiveMarket) return await active;
        var recent = market.Candidates(request.BuyDc, count, token);
        await Task.WhenAll(active, recent);
        return Mix(count, request.Watchlist, await active, await recent);
    }
    // Aggregates choose candidates only. They never replace raw history or support a trade recommendation.
    public static double ActivityScore(JsonElement result, long now)
    {
        double best = 0;
        foreach (var quality in new[] { "nq", "hq" })
        {
            if (!result.TryGetProperty(quality, out var data)) continue;
            double Read(string group, string field)
            {
                return data.TryGetProperty(group, out var g) && g.TryGetProperty("world", out var w) &&
                    w.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : 0;
            }
            var stamp = Read("recentPurchase", "timestamp") / 1000;
            if (stamp <= 0 || stamp > now + 60 || now - stamp > 3 * 86400) continue;
            var price = Read("averageSalePrice", "price"); var quantity = Read("dailySaleVelocity", "quantity");
            if (price > 0 && quantity > 0 && double.IsFinite(price * quantity)) best = Math.Max(best, price * quantity);
        }
        return best;
    }
    public static List<SavedWatchlist> Normalize(IEnumerable<SavedWatchlist>? lists) =>
        (lists ?? []).Where(x => x != null).Take(MaxLists).Select(x => new SavedWatchlist
        { Name = string.IsNullOrWhiteSpace(x.Name) ? "Favorites" : x.Name.Trim()[..Math.Min(40, x.Name.Trim().Length)],
          Items = (x.Items ?? []).Where(id => id > 0 && id < 1_000_000).Distinct().Take(MaxWatchlistItems).ToList() }).ToList();
}
