using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
namespace FFXIVFlipper;
public sealed class MainWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly MarketClient market = new();
    private CancellationTokenSource? cancellation;
    private Task<(MarketSnapshot Buy, MarketSnapshot Sell)>? pending;
    private MarketSnapshot? buy;
    private MarketSnapshot? sell;
    private string search = "";
    private bool showSearchResults;
    private (uint Id, string Name)[]? worldOptions;
    private (uint Id, string Name)[] searchResults = [];
    private string buyWorld;
    private string sellWorld;
    private bool hq;
    private uint item;
    private string itemName = "Select an item";
    private string status = "";
    private int sellPrice;
    private int soldUnits = 1;
    private Task<JsonElement[]>? guideTask;
    private JsonElement[]? modelGuides;
    private string guideStatus = "";
    private CancellationTokenSource? scanCancellation;
    private Task<ScanResult>? scanTask;
    private volatile ScanProgress scanProgress = new(0, 0, "Ready");
    private ScanResult? scanResult;
    private string scanStatus = "";
    private string scanDc;
    private int budget;
    private int scanItems;
    private int holdingDays;
    private int maxQuantity;
    private int confidence;
    private int minProfit;
    private bool scanSelectedOnly;
    private int scanRunId;
    private Dictionary<uint, string>? itemCatalog;
    public MainWindow(Configuration config) : base("FFXIV Flipper")
    {
        this.config = config;
        buyWorld = config.BuyWorld;
        sellWorld = config.SellWorld;
        hq = config.Hq;
        scanDc = config.ScanDc; budget = config.Budget; scanItems = config.ScanItems; holdingDays = config.HoldingDays;
        maxQuantity = config.MaxQuantity; confidence = config.ConfidencePercent; minProfit = config.MinProfit;
        Size = new Vector2(740, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
    }
    private void Invalidate()
    {
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
        if (pending != null) _ = pending.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        pending = null; buy = sell = null; modelGuides = null; guideStatus = "";
        if (guideTask != null) _ = guideTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        guideTask = null; status = "";
    }
    public override void Draw()
    {
        if (string.IsNullOrEmpty(sellWorld) && Plugin.Player.IsLoaded && Plugin.Player.HomeWorld.IsValid)
            sellWorld = Plugin.Player.HomeWorld.Value.Name.ToString();
        if (ImGui.BeginTabBar("Flipper tools"))
        {
            if (ImGui.BeginTabItem("Find profitable flips")) { DrawScanner(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Single item")) { DrawComparison(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }
    private void DrawComparison()
    {
        if (pending is { IsCompleted: true })
        {
            try { var result = pending.GetAwaiter().GetResult(); buy = result.Buy; sell = result.Sell; status = "Market snapshots loaded."; BeginGuide(); }
            catch (OperationCanceledException) { status = "Request canceled or timed out. Try again."; }
            catch (Exception ex) { status = $"Market request failed: {ex.Message}"; Plugin.Log.Warning(ex, "Universalis request failed"); }
            pending = null;
        }
        ImGui.TextWrapped("Single-item analysis. Confirm uploaded listings in game before buying.");
        if (ImGui.InputText("Item search", ref search, 100))
        {
            Invalidate();
            // Search once per edit, rather than scanning the item sheet on every game frame.
            showSearchResults = search.Length >= 2;
            searchResults = search.Length < 2 ? [] : Plugin.Data.GetExcelSheet<Item>()
                .Where(x => x.ItemSearchCategory.RowId != 0 && x.Name.ToString().Contains(search, StringComparison.OrdinalIgnoreCase))
                .Take(25).Select(x => (x.RowId, x.Name.ToString())).ToArray();
        }
        if (showSearchResults)
        {
            if (ImGui.BeginChild("Item results", new Vector2(0, 100), true))
                foreach (var row in searchResults)
                    if (ImGui.Selectable($"{row.Name}##{row.Id}", item == row.Id)) { Invalidate(); if (scanSelectedOnly) InvalidateScan(); item = row.Id; itemName = row.Name; search = row.Name; showSearchResults = false; sellPrice = 0; }
            ImGui.EndChild();
        }
        ImGui.TextUnformatted($"Item: {itemName} ({item})");
        if (DrawWorld("Buy world", ref buyWorld)) Invalidate();
        if (DrawWorld("Sell world", ref sellWorld)) { Invalidate(); InvalidateScan(); }
        if (ImGui.Checkbox("HQ", ref hq)) Invalidate();
        ImGui.BeginDisabled(item == 0 || string.IsNullOrWhiteSpace(buyWorld) || string.IsNullOrWhiteSpace(sellWorld) || pending != null);
        var load = ImGui.Button("Load market comparison");
        ImGui.EndDisabled();
        if (load) StartRequest();
        if (pending != null) { ImGui.SameLine(); if (ImGui.Button("Cancel")) { Invalidate(); status = "Canceled."; } }
        ImGui.TextWrapped(status);
        if (buy == null || sell == null) return;
        DrawSnapshot($"Buy - {buyWorld}", buy); DrawSnapshot($"Sell - {sellWorld}", sell);
        ImGui.Separator();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (guideTask is { IsCompleted: true })
        {
            try { modelGuides = guideTask.GetAwaiter().GetResult(); guideStatus = modelGuides.Length == 0 ? "No supported sell-price plans from this history." : "Website sell-price model loaded."; }
            catch (Exception ex) { guideStatus = $"Price analysis unavailable: {ex.Message}"; Plugin.Log.Warning(ex, "Price guide failed"); }
            guideTask = null;
        }
        ImGui.TextUnformatted("Sell-world sales and price guidance");
        ImGui.TextWrapped(sell.History.Status);
        ImGui.TextWrapped(guideStatus);
        if (modelGuides != null)
        {
            foreach (var plan in modelGuides)
            {
                ImGui.TextWrapped($"{Text(plan,"exitLabel")}: {Number(plan,"targetSell"):N0} gil/unit; sale within {holdingDays} days: {Number(plan,"sellWithinWindowProbability"):P0}; first sale estimate: {Format(plan,"estimatedDaysToFirstSale","N1")} days; {Text(plan,"confidenceLabel")} evidence.");
                if (ImGui.SmallButton($"Use {Text(plan,"exitLabel")} price")) sellPrice = (int)Number(plan,"targetSell");
            }
            ImGui.TextWrapped("Probabilities and times are model estimates from sampled competition and sales, not guaranteed outcomes.");
        }
        foreach (var sale in sell.History.Sales.Where(x => x.Hq == hq).Take(5))
            ImGui.TextUnformatted($"Sold {sale.Quantity} at {sale.PricePerUnit:N0} gil ({Math.Max(0, now - sale.Timestamp) / 3600} hours ago)");
        ImGui.Separator();
        ImGui.TextWrapped("Manual scenario. Assumes 5% seller tax; purchase tax and travel costs excluded. Units sold are your input, not a forecast.");
        ImGui.InputInt("Resale gil per unit", ref sellPrice);
        ImGui.InputInt("Units sold", ref soldUnits);
        var listing = buy.Listings.FirstOrDefault(x => x.Hq == hq);
        if (listing == null) { ImGui.TextUnformatted("No matching buy listing in this sample."); return; }
        if (!MarketAnalysis.IsFresh(buy.LastUploadTime, now)) ImGui.TextWrapped("Buy data is stale or has unknown age. Refresh and verify the stack in game.");
        ImGui.TextUnformatted($"Cheapest sampled stack: {listing.Quantity} x {listing.PricePerUnit:N0} gil; upfront {listing.Quantity * (decimal)listing.PricePerUnit:N0} gil");
        var breakEven = Math.Ceiling(listing.PricePerUnit / .95m);
        ImGui.TextUnformatted($"Break-even per unit if the entire stack sells: {breakEven:N0} gil");
        if (sellPrice >= 0 && soldUnits >= 0 && soldUnits <= listing.Quantity)
        {
            var gain = StackEconomics.CashGain(listing.PricePerUnit, listing.Quantity, sellPrice, soldUnits);
            ImGui.TextUnformatted($"Conditional cash gain: {gain:N0} gil; leftover units: {listing.Quantity - soldUnits}");
            if (gain < 0) ImGui.TextWrapped("This scenario does not recover the full stack purchase cost.");
        }
        else ImGui.TextWrapped("Enter a nonnegative price and units sold between zero and the full stack size.");
    }
    private void BeginGuide()
    {
        if (sell == null) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!sell.History.Complete || !MarketAnalysis.IsFresh(sell.LastUploadTime, now)) { guideStatus = "No guidance: home evidence is incomplete, stale or has unknown age."; return; }
        var quality = hq;
        var input = JsonSerializer.Serialize(new {
            currentListings = sell.Listings.Where(x => x.Hq == quality).Select(x => new { pricePerUnit = x.PricePerUnit, quantity = x.Quantity }),
            salesEntries = sell.History.Sales.Where(x => x.Hq == quality).Select(x => new { pricePerUnit = x.PricePerUnit, quantity = x.Quantity, timestampSec = x.Timestamp }),
            lookbackDays = 7, sellWindowDays = holdingDays, nowSec = now, targetCapRatio = .14, priceBandRatio = .1
        });
        var token = cancellation?.Token ?? default;
        guideStatus = "Calculating demand and sell-price tiers...";
        guideTask = Task.Run(() => {
            using var engine = new PlannerEngine(token);
            using var output = JsonDocument.Parse(engine.Guide(input));
            return output.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
        }, token);
    }
    private bool DrawWorld(string label, ref string selected)
    {
        worldOptions ??= Plugin.Data.GetExcelSheet<World>().Where(x => x.IsPublic && x.DataCenter.RowId != 0)
            .Select(x => (x.RowId, x.Name.ToString())).OrderBy(x => x.Item2).ToArray();
        var changed = false;
        if (ImGui.BeginCombo(label, string.IsNullOrEmpty(selected) ? "Choose a world" : selected))
        {
            foreach (var world in worldOptions)
                if (ImGui.Selectable(world.Name, world.Name == selected)) { selected = world.Name; changed = true; }
            ImGui.EndCombo();
        }
        return changed;
    }
    private void StartRequest()
    {
        Invalidate();
        var worlds = Plugin.Data.GetExcelSheet<World>();
        var source = worlds.FirstOrDefault(x => string.Equals(x.Name.ToString(), buyWorld.Trim(), StringComparison.OrdinalIgnoreCase));
        var destination = worlds.FirstOrDefault(x => string.Equals(x.Name.ToString(), sellWorld.Trim(), StringComparison.OrdinalIgnoreCase));
        if (source.RowId == 0 || destination.RowId == 0) { status = "Enter individual world names, such as Exodus or Leviathan. Data centers are not valid sell worlds."; return; }
        buyWorld = config.BuyWorld = source.Name.ToString(); sellWorld = config.SellWorld = destination.Name.ToString(); config.Hq = hq;
        Plugin.PluginInterface.SavePluginConfig(config);
        cancellation = new(); pending = FetchPair(source.RowId, destination.RowId, item, cancellation.Token); status = "Loading listings and seven-day sell-world sales...";
    }
    private async Task<(MarketSnapshot, MarketSnapshot)> FetchPair(uint source, uint destination, uint id, CancellationToken token)
    {
        if (source == destination) { var snapshot = await market.Fetch(source, id, true, token); return (snapshot, snapshot); }
        var sourceTask = market.Fetch(source, id, false, token);
        var destinationTask = market.Fetch(destination, id, true, token);
        await Task.WhenAll(sourceTask, destinationTask);
        return (await sourceTask, await destinationTask);
    }
    private void DrawSnapshot(string label, MarketSnapshot snapshot)
    {
        var match = snapshot.Listings.Where(x => x.Hq == hq).ToArray();
        var age = snapshot.LastUploadTime > 0 ? $"{Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - snapshot.LastUploadTime) / 60000)} minutes old" : "age unknown";
        ImGui.TextUnformatted($"{label}: {match.Length} matching listings sampled; {age}");
        foreach (var row in match.Take(3)) ImGui.TextUnformatted($"  {row.Quantity} units at {row.PricePerUnit:N0} gil each");
    }
    private void InvalidateScan()
    {
        Interlocked.Increment(ref scanRunId);
        scanCancellation?.Cancel(); scanCancellation?.Dispose(); scanCancellation = null;
        if (scanTask != null) _ = scanTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        scanTask = null; scanResult = null; scanStatus = "";
    }
    private void DrawScanner()
    {
        if (scanTask is { IsCompleted: true })
        {
            try { scanResult = scanTask.GetAwaiter().GetResult(); scanStatus = $"Checked {scanResult.Evaluated} items; {scanResult.Rows.Length} opportunities passed the filters."; }
            catch (OperationCanceledException) { scanStatus = "Scan canceled."; }
            catch (Exception ex) { scanStatus = $"Scan failed: {ex.Message}"; Plugin.Log.Warning(ex, "Cross-world scan failed"); }
            scanTask = null;
        }
        ImGui.TextWrapped("Find items to buy on another world and sell on your home world. Both NQ and HQ are evaluated separately.");
        if (DrawWorld("Home / sell world", ref sellWorld)) { Invalidate(); InvalidateScan(); scanDc = ""; }
        var home = Plugin.Data.GetExcelSheet<World>().FirstOrDefault(x => x.Name.ToString() == sellWorld);
        if (home.RowId == 0) { ImGui.TextWrapped("Choose your home world to start."); return; }
        var homeDc = home.DataCenter.Value;
        var dcs = Plugin.Data.GetExcelSheet<World>().Where(x => x.IsPublic && x.DataCenter.IsValid && x.DataCenter.Value.Region.RowId == homeDc.Region.RowId)
            .Select(x => x.DataCenter.Value.Name.ToString()).Distinct().Order().ToArray();
        if (string.IsNullOrEmpty(scanDc) || !dcs.Contains(scanDc)) scanDc = homeDc.Name.ToString();
        if (ImGui.BeginCombo("Buy data center", scanDc))
        {
            foreach (var dc in dcs) if (ImGui.Selectable(dc, dc == scanDc)) { InvalidateScan(); scanDc = dc; }
            ImGui.EndCombo();
        }
        ImGui.TextWrapped("Buying scope is the selected data center. Use DC travel if it differs from your home DC; access restrictions still apply.");
        if (ImGui.InputInt("Available gil", ref budget)) InvalidateScan();
        if (ImGui.InputInt("Items to scan (1-200)", ref scanItems)) InvalidateScan();
        if (ImGui.Checkbox("Scan selected item only", ref scanSelectedOnly)) InvalidateScan();
        if (scanSelectedOnly) ImGui.TextUnformatted($"Selected: {itemName}. Choose it in the Single item tab.");
        if (ImGui.CollapsingHeader("Scan settings"))
        {
            if (ImGui.InputInt("Sell within days (1-7)", ref holdingDays)) { InvalidateScan(); Invalidate(); }
            if (ImGui.InputInt("Minimum sale confidence % (1-99)", ref confidence)) InvalidateScan();
            if (ImGui.InputInt("Maximum planned resale units (1-100)", ref maxQuantity)) InvalidateScan();
            if (ImGui.InputInt("Minimum margin per planned unit", ref minProfit)) InvalidateScan();
            ImGui.TextWrapped("The full listed stacks are purchased even when the model recommends selling fewer units. Minimum ROI: 4%. Seller tax: 5%. Buyer tax and travel costs are excluded.");
        }
        var valid = budget > 0 && scanItems is >= 1 and <= 200 && holdingDays is >= 1 and <= 7 && confidence is >= 1 and <= 99 && maxQuantity is >= 1 and <= 100 && minProfit >= 0 && (!scanSelectedOnly || item > 0);
        if (!valid) ImGui.TextWrapped("Enter positive gil and scan settings within the shown ranges; select an item if using selected-item mode.");
        ImGui.BeginDisabled(!valid || scanTask != null);
        var start = ImGui.Button("Scan for profitable flips");
        ImGui.EndDisabled();
        if (start)
        {
            InvalidateScan();
            config.SellWorld = sellWorld; config.ScanDc = scanDc; config.Budget = budget; config.ScanItems = scanItems;
            config.HoldingDays = holdingDays; config.MaxQuantity = maxQuantity; config.ConfidencePercent = confidence; config.MinProfit = minProfit;
            Plugin.PluginInterface.SavePluginConfig(config);
            var worlds = Plugin.Data.GetExcelSheet<World>().Where(x => x.IsPublic && x.DataCenter.IsValid && x.DataCenter.Value.Name.ToString() == scanDc)
                .ToDictionary(x => x.RowId, x => x.Name.ToString());
            // Capture game-sheet data on the game thread; the worker only receives plain values.
            var catalog = itemCatalog ??= Plugin.Data.GetExcelSheet<Item>().Where(x => x.ItemSearchCategory.RowId != 0).ToDictionary(x => x.RowId, x => x.Name.ToString());
            var settings = new ScanSettings(home.RowId, sellWorld, scanDc, worlds, budget, scanItems, holdingDays, confidence / 100d, maxQuantity, minProfit);
            var manual = scanSelectedOnly ? new[] { new ScanItem(item, itemName) } : [];
            scanCancellation = new(); var token = scanCancellation.Token; var runId = scanRunId;
            scanProgress = new(0, 0, "Finding recently updated items...");
            scanTask = Task.Run(() => new CrossWorldScanner(market).Run(settings, catalog, manual, p => { if (Volatile.Read(ref scanRunId) == runId) scanProgress = p; }, token), token);
        }
        if (scanTask != null)
        {
            ImGui.SameLine(); if (ImGui.Button("Cancel scan")) { InvalidateScan(); scanStatus = "Scan canceled."; }
            var progress = scanProgress;
            ImGui.TextWrapped($"{progress.Completed}/{progress.Total} - {progress.Message}");
        }
        ImGui.TextWrapped(scanStatus);
        if (scanResult == null) return;
        var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - scanResult.FinishedAt;
        ImGui.TextUnformatted($"Result snapshot: {Math.Max(0, age) / 60} minutes ago");
        if (age > 1800) ImGui.TextWrapped("These results are over 30 minutes old. Run a fresh scan before buying.");
        ImGui.TextWrapped("Estimates use the website's demand, competition and whole-stack planner. Listings are sampled and may disappear; profit is not guaranteed.");
        var basket = scanResult.Basket;
        if (basket.TryGetProperty("unavailable", out _)) ImGui.TextWrapped("Combined basket unavailable. Review individual plans and keep the total spend within your budget.");
        else if (basket.ValueKind == JsonValueKind.Object)
        {
            ImGui.TextWrapped($"Suggested basket: upfront {Number(basket,"totalCost"):N0} gil; expected cash gain {Number(basket,"totalExpectedProfit"):N0} gil. Picks below show all qualifying opportunities; they are not all affordable together.");
        }
        if (basket.TryGetProperty("rows", out var basketRows))
            foreach (var pick in basketRows.EnumerateArray()) ImGui.BulletText($"Basket pick: {Text(pick,"itemName")} from {Text(pick,"buyWorld")}; spend {Number(pick,"planCost"):N0} gil");
        if (scanResult.Rows.Length == 0) ImGui.TextWrapped("No profitable plans passed these filters. Try another buy DC, a larger item sample or a longer sell window. Skipped data is listed below.");
        for (var index = 0; index < Math.Min(50, scanResult.Rows.Length); index++)
        {
            var row = scanResult.Rows[index];
            if (!ImGui.CollapsingHeader($"{Text(row,"itemName")} | Buy {Text(row,"buyWorld")} | Expected +{Number(row,"planExpectedProfit"):N0} gil##flip{index}")) continue;
            ImGui.TextWrapped($"Buy on {Text(row,"buyWorld")}; sell on {sellWorld} at {Number(row,"targetSell"):N0} gil per unit.");
            ImGui.TextWrapped($"Purchase {Number(row,"purchasedQty"):N0} units; upfront {Number(row,"planCost"):N0} gil. Plan to sell {Number(row,"recommendedQty"):N0}; expected units sold {Number(row,"expectedUnitsSoldAtRecommended"):N1}.");
            ImGui.TextWrapped($"Sale confidence {Number(row,"sellProbAtRecommended"):P0}; estimated clear time {Number(row,"daysToClearRecommended"):N1} days; expected cash gain {Number(row,"planExpectedProfit"):N0} gil.");
            ImGui.TextWrapped($"Unsold units {Number(row,"unsoldUnits"):N1}; cash outcome if all purchased units sell at a 20% markdown: {Number(row,"downsideProfit"):N0} gil.");
            if (row.TryGetProperty("usedListings", out var listings)) foreach (var listing in listings.EnumerateArray())
                ImGui.BulletText($"Buy full stack: {Number(listing,"quantity"):N0} x {Number(listing,"pricePerUnit"):N0} gil; listing {Text(listing,"listingId")}");
            if (row.TryGetProperty("buyFreshness",out var buyFresh)) ImGui.TextUnformatted($"Buy data: {Text(buyFresh,"label")}; {Number(buyFresh,"ageHours"):N1} hours old");
            if (row.TryGetProperty("homeFreshness",out var homeFresh)) ImGui.TextUnformatted($"Home data: {Text(homeFresh,"label")}; {Number(homeFresh,"ageHours"):N1} hours old");
            if (row.TryGetProperty("listingSampleLimited",out var limited) && limited.ValueKind == JsonValueKind.True) ImGui.TextWrapped("Listing sample is limited; competition may be understated.");
        }
        if (scanResult.Warnings.Length > 0 && ImGui.CollapsingHeader($"Skipped / unavailable data ({scanResult.Warnings.Length})"))
            foreach (var warning in scanResult.Warnings.Take(100)) ImGui.TextWrapped(warning);
    }
    private static string Format(JsonElement row, string field, string format) => row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble().ToString(format) : "N/A";
    private static double Number(JsonElement row, string field) => row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
    private static string Text(JsonElement row, string field) => row.TryGetProperty(field, out var value) ? value.ToString() : "";
    public void Dispose() { InvalidateScan(); Invalidate(); market.Dispose(); }
}









