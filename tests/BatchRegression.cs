using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace FFXIVFlipper;
public static class BatchRegression
{
    public static async Task<int> Run()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "browser-parity.json")));
        var positive = fixtures.RootElement.GetProperty("cases").EnumerateArray().First(x => x.GetProperty("kind").GetString() == "scanner" && x.GetProperty("expected").GetProperty("rows").GetArrayLength() > 0);
        var input = positive.GetProperty("input"); var id = input.GetProperty("items")[0].GetProperty("id").GetUInt32();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var shift = now - input.GetProperty("nowSec").GetInt64();
        var map = input.GetProperty("listingsByItemWorld").GetProperty(id.ToString());
        var worlds = new Dictionary<uint,string> { [64]="Leviathan", [55]="Ultros", [53]="Exodus" };
        object[] Listings(string name) => map.GetProperty(name).GetProperty("listings").EnumerateArray().Select(x => (object)new {
            pricePerUnit=x.GetProperty("pricePerUnit").GetInt32(), quantity=x.GetProperty("quantity").GetInt32(), hq=x.GetProperty("hq").GetBoolean(),
            listingID=x.GetProperty("listingId").GetString(), lastReviewTime=now,worldID=worlds.Single(w=>w.Value==name).Key, worldName=name
        }).ToArray();
        var home=JsonSerializer.Serialize(new { worldID=64,itemID=id,lastUploadTime=now*1000,listings=Listings("Leviathan") });
        var supply=JsonSerializer.Serialize(new {dcName="Primal",itemID=id,worldUploadTimes=worlds.ToDictionary(x=>x.Key.ToString(),x=>now*1000),listings=worlds.Values.SelectMany(Listings)});
        var sales=input.GetProperty("historyByItemHome").GetProperty(id.ToString()).GetProperty("entries").EnumerateArray().Select(x=>new {pricePerUnit=x.GetProperty("pricePerUnit").GetInt32(),quantity=x.GetProperty("quantity").GetInt32(),hq=x.GetProperty("hq").GetBoolean(),timestamp=x.GetProperty("timestampSec").GetInt64()+shift}).ToArray();
        var history=JsonSerializer.Serialize(new {worldID=64,itemID=id,entries=sales});

        var checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        var ids = Enumerable.Range(0, 21).Select(i => id + (uint)i).ToArray();
        var catalog = ids.ToDictionary(x => x, x => "Synthetic " + x);
        var settings = new ScanSettings(64, "Leviathan", "Primal", worlds, 1000000, 21, 2, .58, 20, 10, 0);
        var calls = 0;
        string Body(HttpRequestMessage request, uint? omitted = null, uint? wrongWorld = null, bool capped = false)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("extra/")) return JsonSerializer.Serialize(new { items = ids.Select(x => new { itemID = x }) });
            var requested = path.Split('/').Last().Split(',').Select(uint.Parse).ToArray();
            JsonObject Item(uint itemId)
            {
                var node = JsonNode.Parse(path.Contains("history/") ? history : path.Contains("Primal/") ? supply : home)!.AsObject();
                node["itemID"] = itemId;
                if (itemId == wrongWorld && !path.Contains("Primal/")) node["worldID"] = 999;
                if (capped && itemId == ids[1] && path.Contains("history/"))
                    node["entries"] = new JsonArray(Enumerable.Range(0, 2000).Select(_ => node["entries"]![0]!.DeepClone()).ToArray());
                return node;
            }
            if (requested.Length == 1) return Item(requested[0]).ToJsonString();
            var entries = new JsonObject();
            foreach (var itemId in requested)
                if (itemId != omitted || !path.Contains("history/")) entries[itemId.ToString()] = Item(itemId);
            var batch = new JsonObject { ["items"] = entries, ["unresolvedItems"] = new JsonArray() };
            if (path.Contains("Primal/")) batch["dcName"] = "Primal"; else batch["worldID"] = 64;
            return batch.ToJsonString();
        }
        using (var client = new MarketClient(new Handler((r, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body(r)) });
        })))
        {
            var batch = await client.ScanBatch("Primal", 64, ids[..10], worlds, default);
            Check(calls == 3 && batch.Count == 10 && batch.Values.All(x => x.Error == null), "Ten-item scan must use three requests.");
            var singleHome = await client.Fetch(64, ids[0], true, default, 1000);
            var singleSupply = await client.Supply("Primal", ids[0], worlds, default);
            Check(JsonSerializer.Serialize(singleHome.Listings) == JsonSerializer.Serialize(batch[ids[0]].Home!.Listings), "Batch home listings differ from single fetch.");
            Check(JsonSerializer.Serialize(singleHome.History.Sales) == JsonSerializer.Serialize(batch[ids[0]].Home!.History.Sales), "Batch sales differ from single fetch.");
            Check(JsonSerializer.Serialize(singleSupply) == JsonSerializer.Serialize(batch[ids[0]].Supply), "Batch source worlds differ from single fetch.");
            calls = 0;
            var scan = await new CrossWorldScanner(client).Run(settings, catalog, [], _ => {}, default);
            Check(calls == 10 && scan.Evaluated == 21 && scan.Warnings.Length == 0, "21-item scan must use discovery plus nine requests, including singleton tail.");
            Check(scan.Rows.Select(x => x.GetProperty("id").GetUInt32()).Distinct().Count() == 21, "Batch scan lost or duplicated item recommendations.");
            Check(scan.Basket.GetProperty("totalCost").GetDouble() <= settings.Budget, "Batched basket exceeded capital.");
        }
        using (var client = new MarketClient(new Handler((r, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body(r, ids[1])) }))))
        {
            var batch = await client.ScanBatch("Primal", 64, ids[..10], worlds, default);
            Check(batch[ids[1]].Error != null && batch.Values.Count(x => x.Error == null) == 9, "Missing batch item must not discard valid neighbors.");
        }
        using (var client = new MarketClient(new Handler((r, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body(r, wrongWorld: ids[1])) }))))
        {
            var batch = await client.ScanBatch("Primal", 64, ids[..10], worlds, default);
            Check(batch[ids[1]].Error != null && batch.Values.Count(x => x.Error == null) == 9, "Wrong item world must be isolated.");
        }
        using (var client = new MarketClient(new Handler((r, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body(r, capped: true)) }))))
        {
            var scan = await new CrossWorldScanner(client).Run(settings with { ItemLimit = 10 }, catalog, ids[..10].Select(x => new ScanItem(x, catalog[x])).ToArray(), _ => {}, default);
            Check(scan.Warnings.Any() && scan.Rows.All(x => x.GetProperty("id").GetUInt32() != ids[1]), "Capped batch history must not become a forecast.");
        }
        var active = 0; var maximum = 0;
        using (var client = new MarketClient(new Handler(async (r, token) =>
        {
            var current = Interlocked.Increment(ref active); maximum = Math.Max(maximum, current);
            try { await Task.Delay(10, token); return new(HttpStatusCode.OK) { Content = new StringContent(Body(r)) }; }
            finally { Interlocked.Decrement(ref active); }
        })))
        {
            await client.ScanBatch("Primal", 64, ids[..10], worlds, default);
            Check(maximum == 2 && active == 0, "Batch requests exceeded two active HTTP calls.");
        }
        using (var canceled = new CancellationTokenSource())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            active = 0;
            using var client = new MarketClient(new Handler(async (_, token) =>
            {
                if (Interlocked.Increment(ref active) == 2) entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); throw new Exception("Unreachable"); }
                finally { Interlocked.Decrement(ref active); }
            }));
            var task = client.ScanBatch("Primal", 64, ids[..10], worlds, canceled.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); canceled.Cancel();
            try { await task; throw new Exception("Canceled batch returned"); } catch (OperationCanceledException) { }
            Check(active == 0, "Canceled batch left active or queued requests.");
        }
        using (var canceled = new CancellationTokenSource())
        {
            calls = 0; active = 0;
            using var client = new MarketClient(new Handler(async (r, token) =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call <= 3) return new(HttpStatusCode.OK) { Content = new StringContent(Body(r)) };
                Interlocked.Increment(ref active);
                try { await Task.Delay(Timeout.Infinite, token); throw new Exception("Unreachable"); }
                finally { Interlocked.Decrement(ref active); }
            }));
            var manual = ids.Select(x => new ScanItem(x, catalog[x])).ToArray();
            var sawPrefetch = false;
            try
            {
                await new CrossWorldScanner(client).Run(settings, catalog, manual, _ =>
                { sawPrefetch = active == 2; canceled.Cancel(); }, canceled.Token);
                throw new Exception("Canceled pipeline returned");
            }
            catch (OperationCanceledException) { }
            Check(sawPrefetch && active == 0 && calls == 5, "Pipeline must prefetch just one batch and drain it on cancellation.");
        }
        calls = 0;
        using (var client = new MarketClient(new Handler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        })))
        {
            var scan = await new CrossWorldScanner(client).Run(settings, catalog, ids.Select(x => new ScanItem(x, catalog[x])).ToArray(), _ => {}, default);
            Check(calls == 1 && scan.Rows.Length == 0 && scan.Warnings.Any(x => x.Contains("rate limit")), "Rate-limited pipeline must stop without requesting the next batch.");
        }
        return checks;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
}