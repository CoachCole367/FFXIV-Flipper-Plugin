using FFXIVFlipper;
using System.Net;
using System.Text.Json;

var checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
void Throws(Action action, string name) { try { action(); } catch (ArgumentException) { checks++; return; } catch (InvalidDataException) { checks++; return; } throw new Exception(name); }
const uint world = 53, item = 10373;
var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
string Listings(uint w = world, uint id = item) => JsonSerializer.Serialize(new { worldID = w, itemID = id, lastUploadTime = now * 1000, listings = new[] { new { pricePerUnit = 100, quantity = 99, hq = false }, new { pricePerUnit = 200, quantity = 4, hq = true } } });
string History(int count = 4) => JsonSerializer.Serialize(new { worldID = world, itemID = item, entries = Enumerable.Range(0, count).Select(i => new { pricePerUnit = 100, quantity = 10, hq = false, timestamp = now - i * 3600 }) });
var parsed = MarketParser.Listings(Listings(), world, item);
var history = MarketParser.History(History(), world, item, now);
var snapshot = parsed with { History = history };
Check(StackEconomics.CashGain(100, 10, 200, 1) == -810, "Full stack charged for partial sale");
Check(StackEconomics.CashGain(100, 10, 200, 10) == 900, "Seller tax");
Check(StackEconomics.CashGain(100, 10, 200, 0) == -1000, "Unsold units are not cash");
Check(StackEconomics.CashGain(100000000, 100, 200000000, 100, .275m) == 4500000000m, "Large costs do not overflow");
Throws(() => StackEconomics.CashGain(100, 10, 200, 11), "Cannot sell beyond stack");
Check(parsed.Listings.Length == 2 && parsed.Listings[1].Hq, "Listing parsing preserves quality");
Throws(() => MarketParser.Listings(Listings(64), world, item), "Wrong world rejected");
Throws(() => MarketParser.Listings(Listings(world, 1), world, item), "Wrong item rejected");
Throws(() => MarketParser.Listings(Listings().Replace("\"hq\":false", "\"hq\":null"), world, item), "Unknown listing quality rejected");
Throws(() => MarketParser.Listings(Listings().Replace("\"pricePerUnit\":100", "\"pricePerUnit\":\"100\""), world, item), "Malformed numeric fields rejected");
Check(history.Complete && history.Sales.Length == 4, "History parsed");
Check(!MarketParser.History(History(MarketParser.HistoryLimit), world, item, now).Complete, "Capped history incomplete");
Check(!MarketParser.History(History().Replace("\"hq\":false", "\"hq\":null"), world, item, now).Complete, "Malformed history incomplete");
Check(!MarketParser.History(History().Replace($"\"timestamp\":{now}", $"\"timestamp\":{now + 3600}"), world, item, now).Complete, "Future sales incomplete");
var guide = MarketAnalysis.Analyze(snapshot, false, now);
Check(guide.Price == 100 && guide.QueueUnits == 99, "Matching-quality price and queue");
Check(guide.UnitsPerDay == 40m / 7, "Seven-day unit pace");
Check(MarketAnalysis.Analyze(snapshot, true, now).Price == null, "NQ sales never support HQ guidance");
Check(MarketAnalysis.Analyze(snapshot with { LastUploadTime = 0 }, false, now).Price == null, "Unknown age has no guidance");
Check(MarketAnalysis.Analyze(snapshot with { LastUploadTime = (now - 21601) * 1000 }, false, now).Price == null, "Stale snapshot has no guidance");
Check(MarketAnalysis.Analyze(snapshot with { LastUploadTime = (now + 3600) * 1000 }, false, now).Price == null, "Future upload rejected");
Check(MarketAnalysis.Analyze(snapshot with { History = history with { Complete = false } }, false, now).UnitsPerDay == null, "Missing coverage is not zero demand");
Check(MarketAnalysis.Analyze(snapshot with { History = history with { Sales = [] } }, false, now).Price == null, "No sales has no guidance");
Check(MarketAnalysis.Analyze(snapshot with { History = history with { WindowEnd = now - 1801 } }, false, now).Price == null, "Expired history must reload");
Check(MarketAnalysis.Analyze(snapshot with { Listings = [new(1, 99, false)] }, false, now).Price == null, "No extrapolation to unsupported cheap listing");
Check(MarketAnalysis.Analyze(snapshot with { History = history with { Sales = history.Sales.Select(s => s with { Timestamp = now - 4 * 86400 }).ToArray() } }, false, now).Price == null, "Old matching sales have no guidance");
Check(MarketAnalysis.Analyze(snapshot with { Listings = [] }, false, now).Price == null, "No listings has no guidance");

using (var client = new MarketClient(new StubHandler(request => new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("history/") ? History() : Listings()) })))
{
    var result = await client.Fetch(world, item, true, default);
    Check(result.History.Complete && result.Listings.Length == 2, "Client combines listings and world history");
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { await client.Fetch(world, item, true, canceled.Token); throw new Exception("Canceled cache request returned data"); } catch (OperationCanceledException) { checks++; }
}
var requests = 0;
using (var client = new MarketClient(new StubHandler(_ => { requests++; return new(HttpStatusCode.OK) { Content = new StringContent(Listings()) }; })))
{
    await client.Fetch(world, item, false, default); await client.Fetch(world, item, false, default);
    Check(requests == 1, "Recent listing requests use cache");
}
using (var client = new MarketClient(new StubHandler(r => r.RequestUri!.AbsolutePath.Contains("history/") ? new(HttpStatusCode.ServiceUnavailable) : new(HttpStatusCode.OK) { Content = new StringContent(Listings()) })))
{
    var result = await client.Fetch(world, item, true, default);
    Check(result.Listings.Length == 2 && !result.History.Complete && result.History.Status.Contains("unavailable"), "History failure preserves listing evidence without forecast");
}
requests = 0;
using (var client = new MarketClient(new StubHandler(_ => { requests++; var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new(TimeSpan.FromSeconds(30)); return response; })))
{
    for (var i = 0; i < 2; i++) try { await client.Fetch(world, item, false, default); throw new Exception("Rate limit ignored"); } catch (HttpRequestException) { }
    Check(requests == 1, "Rate limit cooldown blocks repeated requests");
}
Console.WriteLine($"{checks} regressions passed.");
if (args.Contains("--live"))
{
    using var client = new MarketClient();
    var result = await client.Fetch(world, item, true, default);
    Console.WriteLine($"Live world {world}, item {item}: {result.Listings.Length} listings; {result.History.Sales.Length} sales. {result.History.Status}");
    if (!result.History.Complete) throw new Exception("Live history unavailable or capped; see status above.");
}
sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
}
