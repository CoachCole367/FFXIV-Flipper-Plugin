using System.Net.Http;
using System.Text.Json;
namespace FFXIVFlipper;
public sealed record Listing(int PricePerUnit, int Quantity, bool Hq);
public sealed record MarketSnapshot(long LastUploadTime, Listing[] Listings);
public sealed class MarketClient : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(25) };
    public MarketClient() => http.DefaultRequestHeaders.UserAgent.ParseAdd("FFXIVFlipper/0.1.0");
    public async Task<MarketSnapshot> Fetch(string world, uint item, CancellationToken token)
    {
        using var response = await http.GetAsync($"https://universalis.app/api/v2/{Uri.EscapeDataString(world)}/{item}?listings=100&entries=0", token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = json.RootElement;
        if (!root.TryGetProperty("listings", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Market response has no listing data.");
        var listings = new List<Listing>();
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("hq", out var hq) || hq.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
            if (!row.TryGetProperty("pricePerUnit", out var p) || !p.TryGetInt32(out var price) || price <= 0) continue;
            if (!row.TryGetProperty("quantity", out var q) || !q.TryGetInt32(out var quantity) || quantity <= 0) continue;
            listings.Add(new(price, quantity, hq.GetBoolean()));
        }
        var uploaded = root.TryGetProperty("lastUploadTime", out var time) && time.TryGetInt64(out var value) ? value : 0;
        return new(uploaded, listings.OrderBy(x => x.PricePerUnit).ToArray());
    }
    public void Dispose() => http.Dispose();
}
