using System.Text.Json;
namespace FFXIVFlipper;
public sealed record Listing(int PricePerUnit, int Quantity, bool Hq, long LastReviewTimeSec = 0, string ListingId = "");
public sealed record Sale(int PricePerUnit, int Quantity, bool Hq, long Timestamp);
public sealed record SalesHistory(Sale[] Sales, bool Complete, string Status, long WindowEnd);
public sealed record MarketSnapshot(long LastUploadTime, Listing[] Listings, SalesHistory History, bool SampleLimited = false);

public static class MarketParser
{
    public const int HistoryLimit = 2000;
    public const int WindowSeconds = 7 * 86400;
    public static MarketSnapshot Listings(string text, uint world, uint item)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        CheckIdentity(root, world, item);
        if (!root.TryGetProperty("listings", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Market response has no listing data.");
        var listings = new List<Listing>();
        foreach (var row in rows.EnumerateArray())
        {
            if (!TryTrade(row, out var price, out var qty, out var hq)) throw new InvalidDataException("Listing sample contains malformed or unknown-quality rows.");
            listings.Add(new(price, qty, hq, Number(row, "lastReviewTime"), row.TryGetProperty("listingID", out var listingId) ? listingId.ToString() : ""));
        }
        var uploaded = Number(root, "lastUploadTime");
        return new(uploaded, listings.OrderBy(x => x.PricePerUnit).ToArray(), new([], false, "Sales history unavailable.", 0));
    }
    public static SalesHistory History(string text, uint world, uint item, long until)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        CheckIdentity(root, world, item);
        if (!root.TryGetProperty("entries", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Market response has no sales history.");
        var sales = new List<Sale>();
        var invalid = false;
        foreach (var row in rows.EnumerateArray())
        {
            if (!TryTrade(row, out var price, out var qty, out var hq)) { invalid = true; continue; }
            var time = Number(row, "timestamp");
            if (time <= 0 || time > until) { invalid = true; continue; }
            if (time >= until - WindowSeconds) sales.Add(new(price, qty, hq, time));
        }
        var capped = rows.GetArrayLength() >= HistoryLimit;
        var complete = !invalid && !capped;
        return new(sales.OrderByDescending(x => x.Timestamp).ToArray(), complete,
            invalid ? "Limited history: malformed sales excluded." : capped ? "Limited history: response reached the 2,000-sale cap." : "Seven-day uploaded history sample.", until);
    }
    private static void CheckIdentity(JsonElement root, uint world, uint item)
    {
        if (Number(root, "worldID") != world || Number(root, "itemID") != item)
            throw new InvalidDataException("Market response did not match the requested world and item.");
    }
    private static long Number(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var value) ? value : 0;
    private static bool TryTrade(JsonElement row, out int price, out int quantity, out bool hq)
    {
        price = quantity = 0; hq = false;
        if (row.ValueKind != JsonValueKind.Object) return false;
        if (!row.TryGetProperty("hq", out var quality) || quality.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        hq = quality.GetBoolean();
        return row.TryGetProperty("pricePerUnit", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out price) && price > 0 &&
            row.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number && q.TryGetInt32(out quantity) && quantity > 0;
    }
}

public sealed record PriceGuidance(int? Price, decimal? UnitsPerDay, long QueueUnits, int SaleCount, string Reason);
public static class MarketAnalysis
{
    public static bool IsFresh(long uploadMs, long nowSeconds) => uploadMs > 0 && uploadMs <= nowSeconds * 1000 + 60000 && nowSeconds * 1000 - uploadMs <= 6 * 3600000L;
    public static PriceGuidance Analyze(MarketSnapshot snapshot, bool hq, long now)
    {
        var history = snapshot.History;
        var sales = history.Sales.Where(x => x.Hq == hq && x.Timestamp >= now - MarketParser.WindowSeconds && x.Timestamp <= now).ToArray();
        PriceGuidance Missing(string reason) => new(null, null, 0, sales.Length, reason);
        if (!IsFresh(snapshot.LastUploadTime, now)) return Missing("No guidance: sell-world listing data is stale or has unknown age (limit: 6 hours).");
        if (!history.Complete) return Missing($"No guidance: {history.Status}");
        if (history.WindowEnd <= 0 || now - history.WindowEnd > 1800 || history.WindowEnd > now + 60) return Missing("No guidance: reload sales history.");
        if (sales.Length < 3) return Missing("No guidance: fewer than three matching-quality sales in seven days.");
        if (now - sales.Max(x => x.Timestamp) > 3 * 86400) return Missing("No guidance: no matching-quality sale in the last three days.");
        var ordered = sales.OrderBy(x => x.PricePerUnit).ToArray();
        var halfway = (ordered.Sum(x => (long)x.Quantity) + 1) / 2;
        long cumulative = 0;
        var median = ordered[0].PricePerUnit;
        foreach (var sale in ordered) { cumulative += sale.Quantity; if (cumulative >= halfway) { median = sale.PricePerUnit; break; } }
        var listings = snapshot.Listings.Where(x => x.Hq == hq).ToArray();
        if (listings.Length == 0) return Missing("No guidance: no matching-quality sell listing in the retrieved sample.");
        var price = Math.Min(median, listings.Min(x => x.PricePerUnit));
        var near = sales.Where(x => x.PricePerUnit >= price * .9m && x.PricePerUnit <= price * 1.1m).ToArray();
        if (near.Length < 3) return Missing("No guidance: fewer than three matching sales within 10% of the candidate price.");
        var units = near.Sum(x => (decimal)x.Quantity) / 7m;
        var queue = listings.Where(x => x.PricePerUnit <= price).Sum(x => (long)x.Quantity);
        return new(price, units, queue, sales.Length,
            "Reference: lower of matching-quality volume-weighted median and cheapest sampled listing. Historical pace is not your expected sales.");
    }
}



