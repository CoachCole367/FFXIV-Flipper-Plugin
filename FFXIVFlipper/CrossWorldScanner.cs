using System.Text.Json;
namespace FFXIVFlipper;
public sealed record ScanItem(uint Id, string Name);
public sealed record ScanSettings(uint HomeId, string HomeWorld, string BuyDc, Dictionary<uint,string> BuyWorlds, int Budget, int ItemLimit, int HoldingDays, double Confidence, int MaxQuantity, int MinProfit);
public sealed record ScanProgress(int Completed, int Total, string Message);
public sealed record ScanResult(JsonElement[] Rows, JsonElement Basket, int Evaluated, string[] Warnings, long FinishedAt);
public sealed class CrossWorldScanner(MarketClient market)
{
    public async Task<ScanResult> Run(ScanSettings settings, IReadOnlyDictionary<uint,string> catalog, ScanItem[] manual, Action<ScanProgress> progress, CancellationToken token)
    {
        var ids = manual.Length > 0 ? manual.Select(x => x.Id).Distinct().Take(settings.ItemLimit).ToArray() : await market.Candidates(settings.BuyDc, settings.ItemLimit, token);
        var items = ids.Where(catalog.ContainsKey).Select(id => new ScanItem(id, catalog[id])).ToArray();
        if (items.Length == 0) throw new InvalidDataException("No marketable candidates available. Retry, or choose a specific item.");
        var allRows = new List<JsonElement>(); var warnings = new List<string>();
        var evaluated = 0;

        // Network and interpreter work stay off the game thread. Requests share the client's concurrency cap.
        foreach (var candidate in items)
        {
            token.ThrowIfCancellationRequested();
            progress(new(evaluated, items.Length, $"Checking {candidate.Name}"));
            try
            {
                var supplyTask = market.Supply(settings.BuyDc, candidate.Id, settings.BuyWorlds, token);
                var homeTask = market.Fetch(settings.HomeId, candidate.Id, true, token, 1000);
                await Task.WhenAll(supplyTask, homeTask);
                var supply = await supplyTask; var home = await homeTask;
                // A missing or capped home history is not a zero-demand forecast.
                if (!home.History.Complete) { warnings.Add($"{candidate.Name}: {home.History.Status}"); continue; }
                supply[settings.HomeWorld] = home;
                var worldMap = supply.ToDictionary(x => x.Key, x => WorldData(x.Key, x.Value));
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var history = new { available = true, rawCount = home.History.Sales.Length, invalidCount = 0, entries = home.History.Sales.Select(s => new { pricePerUnit = s.PricePerUnit, quantity = s.Quantity, hq = s.Hq, timestampSec = s.Timestamp }) };
                var input = JsonSerializer.Serialize(new {
                    items = new[] { new { id = candidate.Id } }, homeWorld = settings.HomeWorld, nowSec = now,
                    listingsByItemWorld = new Dictionary<uint,object> { [candidate.Id] = worldMap },
                    historyByItemHome = new Dictionary<uint,object> { [candidate.Id] = history },
                    historyByItemDc = new Dictionary<uint,object>(), itemNameById = new Dictionary<uint,string> { [candidate.Id] = candidate.Name },
                    lookbackDays = 7, holdingDays = settings.HoldingDays, taxRate = .05, travelFee = 0,
                    minProfit = settings.MinProfit, minRoi = .04, minTargetProb = .18, qtyConfidence = settings.Confidence,
                    maxBuyQty = settings.MaxQuantity, targetCapRatio = .14, priceBandRatio = .1, referenceMarket = "home",
                    historyEntriesRequested = MarketParser.HistoryLimit, budgetGil = settings.Budget, allowStaleData = false
                });
                using var engine = new PlannerEngine(token);
                using var output = JsonDocument.Parse(engine.Run(input));
                allRows.AddRange(output.RootElement.GetProperty("rows").EnumerateArray().Select(x => x.Clone()));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { warnings.Add($"{candidate.Name}: market request timed out; item skipped."); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            catch (Exception ex) when (ex is Jint.Runtime.MemoryLimitExceededException or Jint.Runtime.StatementsCountOverflowException or TimeoutException)
            {
                warnings.Add($"{candidate.Name}: calculation reached its resource limit; no recommendation emitted.");
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException)
            {
                warnings.Add($"{candidate.Name}: {ex.Message}");
                if (ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) break;
            }
            finally { evaluated++; }
        }
        token.ThrowIfCancellationRequested();
        var rows = allRows.OrderByDescending(x => x.GetProperty("riskAdjusted").GetDouble()).ToArray();
        using var basketEngine = new PlannerEngine(token);
        JsonElement basket;
        try
        {
            using var document = JsonDocument.Parse(basketEngine.Basket(JsonSerializer.Serialize(new { rows, budgetGil = settings.Budget })));
            basket = document.RootElement.Clone();
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is Jint.Runtime.MemoryLimitExceededException or Jint.Runtime.StatementsCountOverflowException or TimeoutException)
        {
            warnings.Add("Basket calculation reached its resource limit. Individual opportunities remain available; no combined basket is recommended.");
            basket = JsonSerializer.SerializeToElement(new { unavailable = true });
        }
        progress(new(evaluated, items.Length, "Scan complete"));
        return new(rows, basket, evaluated, warnings.ToArray(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
    private static object WorldData(string world, MarketSnapshot snapshot) => new {
        lastUpdatedSec = snapshot.LastUploadTime > 0 ? (double?)snapshot.LastUploadTime / 1000 : null,
        sampleLimited = snapshot.SampleLimited,
        listings = snapshot.Listings.Select(l => new { pricePerUnit = l.PricePerUnit, quantity = l.Quantity, hq = l.Hq,
            lastReviewTimeSec = l.LastReviewTimeSec > 0 ? (long?)l.LastReviewTimeSec : null,
            listingId = l.ListingId, worldName = world })
    };
}



