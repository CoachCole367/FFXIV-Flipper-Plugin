using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
namespace FFXIVFlipper;
public sealed class MainWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly TradeJournal journal;
    private readonly System.Action clearPendingPurchases;
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
    private int minExpectedProfit;
    private int selectedOpportunity = -1;
    private bool scanSelectedOnly;
    private int scanRunId;
    private Dictionary<uint, string>? itemCatalog;
    private long journalRevision = -1;
    private ulong journalCharacter;
    private PurchaseLot[] journalRows = [];
    private long journalUnsoldUnits;
    private decimal journalRealizedProfit;
    private decimal journalCashRecovered;
    private Guid journalSelection;
    private int journalPage;
    private int saleQuantity = 1;
    private int saleNetReceived;
    private PurchaseLot? saleToConfirm;
    private int reviewedSaleQuantity;
    private int reviewedSaleNet;
    private string journalMessage = "";
    private Task? exportTask;
    private readonly Dictionary<uint, string> journalItemNames = [];
    public MainWindow(Configuration config, TradeJournal journal, System.Action clearPendingPurchases) : base("FFXIV Flipper")
    {
        this.config = config; this.journal = journal; this.clearPendingPurchases = clearPendingPurchases;
        buyWorld = config.BuyWorld;
        sellWorld = config.SellWorld;
        hq = config.Hq;
        scanDc = config.ScanDc; budget = config.Budget; scanItems = config.ScanItems; holdingDays = config.HoldingDays;
        maxQuantity = config.MaxQuantity; confidence = config.ConfidencePercent; minProfit = config.MinProfit; minExpectedProfit = config.MinExpectedProfit;
        Size = new Vector2(1040, 740);
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(800, 600), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
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
    public override void PreDraw() => ImGui.SetNextWindowBgAlpha(0.98f);
    public override void Draw()
    {
        if (string.IsNullOrEmpty(sellWorld) && Plugin.Player.IsLoaded && Plugin.Player.HomeWorld.IsValid)
            sellWorld = Plugin.Player.HomeWorld.Value.Name.ToString();
        if (ImGui.BeginTabBar("Flipper tools"))
        {
            if (ImGui.BeginTabItem("Find profitable flips")) { DrawScanner(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Single item")) { DrawComparison(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Purchase journal")) { DrawJournal(); ImGui.EndTabItem(); }
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
        scanTask = null; scanResult = null; selectedOpportunity = -1; scanStatus = "";
    }
    private void DrawScanner()
    {
        if (scanTask is { IsCompleted: true })
        {
            try
            {
                scanResult = scanTask.GetAwaiter().GetResult();
                selectedOpportunity = scanResult.Rows.Length > 0 ? 0 : -1;
                scanStatus = $"{scanResult.Evaluated} items checked  |  {scanResult.Rows.Length} worthwhile trades  |  {scanResult.LowValueFiltered} smaller trades filtered";
            }
            catch (OperationCanceledException) { scanStatus = "Scan canceled."; }
            catch (Exception ex) { scanStatus = $"Scan failed: {ex.Message}"; Plugin.Log.Warning(ex, "Cross-world scan failed"); }
            scanTask = null;
        }
        ImGui.TextUnformatted("MARKET SCANNER");
        ImGui.TextDisabled("Find worthwhile purchases elsewhere to sell on your home world.");
        ImGui.Spacing();
        var home = Plugin.Data.GetExcelSheet<World>().FirstOrDefault(x => x.Name.ToString() == sellWorld);
        if (ImGui.BeginTable("Scan controls", 3, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn(); FieldLabel("Sell on");
            if (DrawWorld("##ScanHome", ref sellWorld)) { Invalidate(); InvalidateScan(); scanDc = ""; }
            home = Plugin.Data.GetExcelSheet<World>().FirstOrDefault(x => x.Name.ToString() == sellWorld);
            ImGui.TableNextColumn(); FieldLabel("Buy in data center");
            var dcs = home.RowId == 0 ? [] : Plugin.Data.GetExcelSheet<World>()
                .Where(x => x.IsPublic && x.DataCenter.IsValid && x.DataCenter.Value.Region.RowId == home.DataCenter.Value.Region.RowId)
                .Select(x => x.DataCenter.Value.Name.ToString()).Distinct().Order().ToArray();
            if (home.RowId != 0 && (string.IsNullOrEmpty(scanDc) || !dcs.Contains(scanDc))) scanDc = home.DataCenter.Value.Name.ToString();
            if (ImGui.BeginCombo("##ScanDc", string.IsNullOrEmpty(scanDc) ? "Choose a data center" : scanDc))
            {
                foreach (var dc in dcs) if (ImGui.Selectable(dc, dc == scanDc)) { InvalidateScan(); scanDc = dc; }
                ImGui.EndCombo();
            }
            ImGui.TableNextColumn(); FieldLabel("Available gil");
            if (ImGui.InputInt("##Budget", ref budget, 10000, 100000)) InvalidateScan();
            ImGui.TableNextColumn(); FieldLabel("Items to check");
            if (ImGui.InputInt("##ScanCount", ref scanItems, 25, 50)) InvalidateScan();
            Hint("1-200 recently updated items on the buy data center.");
            ImGui.TableNextColumn(); FieldLabel("Minimum expected gain / trade");
            if (ImGui.InputInt("##MinExpectedGain", ref minExpectedProfit, 5000, 10000)) InvalidateScan();
            Hint("Total expected cash gain after the full stack cost and assumed seller tax. Applied before the budget basket is chosen.");
            ImGui.TableNextColumn(); FieldLabel(" ");
            var valid = ValidScan(home.RowId);
            ImGui.BeginDisabled(!valid || scanTask != null);
            var start = ImGui.Button("Scan for flips", new Vector2(ImGui.GetContentRegionAvail().X, 0));
            ImGui.EndDisabled();
            if (start) StartScan(home.RowId);
            ImGui.EndTable();
        }
        if (ImGui.CollapsingHeader("Filters & scan options"))
        {
            if (ImGui.BeginTable("Extra scan controls", 3, ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn(); FieldLabel("Sell within days (1-7)");
                if (ImGui.InputInt("##HoldDays", ref holdingDays)) { InvalidateScan(); Invalidate(); }
                ImGui.TableNextColumn(); FieldLabel("Minimum sell chance % (1-99)");
                if (ImGui.InputInt("##Confidence", ref confidence, 5, 10)) InvalidateScan();
                ImGui.TableNextColumn(); FieldLabel("Planned resale units (1-100)");
                if (ImGui.InputInt("##MaxQty", ref maxQuantity)) InvalidateScan();
                ImGui.TableNextColumn(); FieldLabel("Minimum margin / planned unit");
                if (ImGui.InputInt("##MinUnitMargin", ref minProfit, 500, 1000)) InvalidateScan();
                ImGui.TableNextColumn();
                if (ImGui.Checkbox("Selected item only", ref scanSelectedOnly)) InvalidateScan();
                if (scanSelectedOnly) ImGui.TextWrapped(item > 0 ? itemName : "Choose an item in Single item.");
                ImGui.EndTable();
            }
            ImGui.TextDisabled("HQ and NQ checked separately. Minimum ROI 4%. DC travel access may vary.");
        }
        if (!ValidScan(home.RowId)) ImGui.TextWrapped("Choose a home world; use 1-200 items, positive gil and valid filter values.");
        if (scanTask != null)
        {
            var progress = scanProgress;
            ImGui.ProgressBar(progress.Total > 0 ? progress.Completed / (float)progress.Total : 0, new Vector2(-100, 0), $"{progress.Completed}/{progress.Total}");
            ImGui.SameLine(); if (ImGui.Button("Cancel", new Vector2(90, 0))) { InvalidateScan(); scanStatus = "Scan canceled."; }
            ImGui.TextDisabled(progress.Message);
        }
        if (!string.IsNullOrEmpty(scanStatus)) ImGui.TextWrapped(scanStatus);
        ImGui.Separator();
        if (scanResult == null)
        {
            ImGui.Spacing(); ImGui.TextUnformatted("Ready to find your next trade.");
            ImGui.TextWrapped($"Plans below {Math.Max(0, minExpectedProfit):N0} gil expected gain will be excluded. Start a scan to compare opportunities.");
            return;
        }
        DrawScanResults();
    }
    private bool ValidScan(uint homeId) => homeId != 0 && budget > 0 && scanItems is >= 1 and <= 200
        && holdingDays is >= 1 and <= 7 && confidence is >= 1 and <= 99 && maxQuantity is >= 1 and <= 100
        && minProfit >= 0 && minExpectedProfit >= 0 && (!scanSelectedOnly || item > 0);
    private void StartScan(uint homeId)
    {
        InvalidateScan();
        config.SellWorld = sellWorld; config.ScanDc = scanDc; config.Budget = budget; config.ScanItems = scanItems;
        config.HoldingDays = holdingDays; config.MaxQuantity = maxQuantity; config.ConfidencePercent = confidence;
        config.MinProfit = minProfit; config.MinExpectedProfit = minExpectedProfit;
        Plugin.PluginInterface.SavePluginConfig(config);
        var worlds = Plugin.Data.GetExcelSheet<World>().Where(x => x.IsPublic && x.DataCenter.IsValid && x.DataCenter.Value.Name.ToString() == scanDc)
            .ToDictionary(x => x.RowId, x => x.Name.ToString());
        var catalog = itemCatalog ??= Plugin.Data.GetExcelSheet<Item>().Where(x => x.ItemSearchCategory.RowId != 0).ToDictionary(x => x.RowId, x => x.Name.ToString());
        var settings = new ScanSettings(homeId, sellWorld, scanDc, worlds, budget, scanItems, holdingDays, confidence / 100d, maxQuantity, minProfit, minExpectedProfit);
        var manual = scanSelectedOnly ? new[] { new ScanItem(item, itemName) } : [];
        scanCancellation = new(); var token = scanCancellation.Token; var runId = scanRunId;
        scanProgress = new(0, 0, "Finding recently updated items...");
        scanTask = Task.Run(() => new CrossWorldScanner(market).Run(settings, catalog, manual,
            p => { if (Volatile.Read(ref scanRunId) == runId) scanProgress = p; }, token), token);
    }
    private void DrawScanResults()
    {
        var result = scanResult!;
        var basket = result.Basket;
        var basketAvailable = !basket.TryGetProperty("unavailable", out _);
        var basketRows = basket.TryGetProperty("rows", out var picks) ? picks.EnumerateArray().ToArray() : [];
        if (basketAvailable && ImGui.BeginTable("Basket summary", 3, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn(); ImGui.TextDisabled("BASKET EXPECTED GAIN"); ProfitText(Number(basket,"totalExpectedProfit"));
            ImGui.TableNextColumn(); ImGui.TextDisabled("UPFRONT GIL"); ImGui.TextUnformatted($"{Number(basket,"totalCost"):N0} / {budget:N0}");
            ImGui.TableNextColumn(); ImGui.TextDisabled("BUYING ROUTE"); ImGui.TextUnformatted($"{basketRows.Length} trades across {Number(basket,"distinctWorldsVisited"):N0} worlds");
            ImGui.EndTable();
        }
        else if (!basketAvailable) ImGui.TextWrapped("Basket unavailable. Keep the combined cost of any purchases within your budget.");
        var age = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - result.FinishedAt) / 60;
        ImGui.TextDisabled($"Expected gain sorted high to low. Basket marks picks that fit together. Updated {age}m ago.");
        if (age >= 30) ImGui.TextWrapped("Results are over 30 minutes old. Scan again before buying.");
        if (result.Rows.Length == 0)
        {
            ImGui.Spacing(); ImGui.TextUnformatted("No worthwhile trades passed these filters.");
            ImGui.TextWrapped($"{result.LowValueFiltered} smaller opportunities were excluded by the {minExpectedProfit:N0} gil minimum. Try another buy DC or a larger item sample.");
        }
        else
        {
            var tableHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y * .48f, 120, 340);
            if (ImGui.BeginTable("Opportunities", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, tableHeight)))
            {
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3.3f);
                ImGui.TableSetupColumn("Buy world", ImGuiTableColumnFlags.WidthStretch, 1.2f);
                ImGui.TableSetupColumn("Upfront", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("Expected gain", ImGuiTableColumnFlags.WidthStretch, 1.2f);
                ImGui.TableSetupColumn("Sell chance", ImGuiTableColumnFlags.WidthStretch, .9f);
                ImGui.TableSetupColumn("Days", ImGuiTableColumnFlags.WidthStretch, .6f);
                ImGui.TableSetupColumn("Basket", ImGuiTableColumnFlags.WidthStretch, .7f);
                ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
                for (var index = 0; index < result.Rows.Length; index++)
                {
                    var row = result.Rows[index];
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    if (ImGui.Selectable($"{Text(row,"itemName")}##trade{index}", selectedOpportunity == index, ImGuiSelectableFlags.SpanAllColumns)) selectedOpportunity = index;
                    Hint(Text(row,"itemName"));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(Text(row,"buyWorld"));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{Number(row,"planCost"):N0}");
                    ImGui.TableNextColumn(); ProfitText(Number(row,"planExpectedProfit"));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{Number(row,"sellProbAtRecommended"):P0}");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(Format(row,"daysToClearRecommended","N1"));
                    ImGui.TableNextColumn(); ImGui.TextDisabled(basketRows.Any(p => SameTrade(p,row)) ? "Pick" : "");
                }
                ImGui.EndTable();
            }
            ImGui.Spacing();
            if (selectedOpportunity >= 0 && selectedOpportunity < result.Rows.Length)
            {
                if (ImGui.BeginChild("Selected trade", new Vector2(0, Math.Max(130, ImGui.GetContentRegionAvail().Y - 38)), true)) DrawTradeDetails(result.Rows[selectedOpportunity]);
                ImGui.EndChild();
            }
        }
        if (result.Warnings.Length > 0 && ImGui.CollapsingHeader($"Unavailable / skipped items ({result.Warnings.Length})"))
            foreach (var warning in result.Warnings.Take(100)) ImGui.TextWrapped(warning);
        ImGui.TextDisabled("Estimates, not guaranteed profit. 5% seller tax; buyer tax and travel costs excluded.");
    }
    private void DrawTradeDetails(JsonElement row)
    {
        ImGui.TextUnformatted(Text(row,"itemName"));
        ImGui.TextUnformatted($"Buy on {Text(row,"buyWorld")}  ->  Sell on {sellWorld} at {Number(row,"targetSell"):N0} gil / unit");
        ImGui.Separator();
        ImGui.TextWrapped($"Purchase {Number(row,"purchasedQty"):N0} units for {Number(row,"planCost"):N0} gil. Plan to sell {Number(row,"recommendedQty"):N0}; expected sales {Number(row,"expectedUnitsSoldAtRecommended"):N1} units.");
        if (row.TryGetProperty("usedListings", out var listings) && ImGui.BeginTable("Exact stacks", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableSetupColumn("Buy full stack"); ImGui.TableSetupColumn("Gil / unit"); ImGui.TableSetupColumn("Stack cost"); ImGui.TableHeadersRow();
            foreach (var listing in listings.EnumerateArray())
            {
                ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted($"{Number(listing,"quantity"):N0} units");
                Hint($"Listing ID: {Text(listing,"listingId")}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{Number(listing,"pricePerUnit"):N0}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{Number(listing,"quantity") * Number(listing,"pricePerUnit"):N0}");
            }
            ImGui.EndTable();
        }
        if (ImGui.CollapsingHeader("Risk & market evidence"))
        {
            ImGui.TextWrapped($"Expected unsold units: {Number(row,"unsoldUnits"):N1}. Cash gain if the entire purchased stack sells at a 20% markdown: {Number(row,"downsideProfit"):N0} gil.");
            if (row.TryGetProperty("buyFreshness", out var source)) ImGui.TextUnformatted($"Buy data: {Text(source,"label")}, {Format(source,"ageHours","N1")}h old");
            if (row.TryGetProperty("homeFreshness", out var home)) ImGui.TextUnformatted($"Sell data: {Text(home,"label")}, {Format(home,"ageHours","N1")}h old");
            ImGui.TextWrapped("Listings are sampled; unseen competition can lower sales. Verify the full stacks and final cost in game.");
        }
    }
    private static bool SameTrade(JsonElement a, JsonElement b) => Number(a,"id") == Number(b,"id") && Text(a,"quality") == Text(b,"quality") && Text(a,"buyWorld") == Text(b,"buyWorld");
    private static void FieldLabel(string text) { ImGui.TextUnformatted(text); ImGui.SetNextItemWidth(-1); }
    private static void Hint(string text) { if (ImGui.IsItemHovered()) { ImGui.BeginTooltip(); ImGui.TextUnformatted(text); ImGui.EndTooltip(); } }
    private static void ProfitText(double amount) => ImGui.TextColored(amount > 0 ? new Vector4(.48f,.85f,.58f,1) : new Vector4(.8f,.8f,.8f,1), $"{(amount > 0 ? "+" : "")}{amount:N0}");
    private void DrawJournal()
    {
        ImGui.TextUnformatted("PURCHASE JOURNAL");
        var tracking = config.TrackPurchases;
        if (ImGui.Checkbox("Automatically log market-board purchases", ref tracking))
        {
            clearPendingPurchases(); config.TrackPurchases = tracking; Plugin.PluginInterface.SavePluginConfig(config);
        }
        ImGui.TextDisabled("Records future confirmed purchases. No inventory polling or market snapshots.");
        ImGui.TextDisabled($"Keeps the newest {TradeJournal.MaxEntries:N0} purchases for up to {TradeJournal.RetentionDays} days; older entries are removed on load/write.");
        ImGui.TextDisabled($"Journal: {journal.FileBytes / 1024d:N1} KB / {TradeJournal.MaxFileBytes / 1024} KB maximum. {journal.Status}");
        if (!Plugin.Player.IsLoaded || Plugin.Player.ContentId == 0) { ImGui.TextWrapped("Log in to see purchases for this character."); return; }
        var owner = Plugin.Player.ContentId;
        var revision = journal.Revision;
        if (journalRevision != revision || journalCharacter != owner)
        {
            journalRows = journal.Snapshot(owner); journalRevision = revision; journalCharacter = owner;
            journalUnsoldUnits = journalRows.Sum(x => (long)x.Remaining); journalRealizedProfit = journalRows.Sum(x => x.RealizedProfit); journalCashRecovered = journalRows.Sum(x => (decimal)x.CashRecovered);
            journalPage = Math.Min(journalPage, Math.Max(0, (journalRows.Length - 1) / 50));
            if (!journalRows.Any(x => x.Id == journalSelection)) journalSelection = journalRows.FirstOrDefault()?.Id ?? Guid.Empty;
        }
        if (exportTask is { IsCompleted: true })
        {
            try { exportTask.GetAwaiter().GetResult(); journalMessage = "CSV saved. Each export replaces the same file."; }
            catch (Exception ex) { journalMessage = "Export failed: " + ex.Message; }
            exportTask = null;
        }
        ImGui.Separator();
        if (ImGui.BeginTable("Retained trade totals", 3, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn(); ImGui.TextDisabled("RETAINED PURCHASES"); ImGui.TextUnformatted($"{journalRows.Length:N0} lots / {journalUnsoldUnits:N0} units unsold");
            ImGui.TableNextColumn(); ImGui.TextDisabled("REALIZED PROFIT ON RECORDED SALES"); ProfitText((double)journalRealizedProfit);
            ImGui.TableNextColumn(); ImGui.TextDisabled("NET CASH AFTER PURCHASES"); ProfitText((double)journalCashRecovered);
            ImGui.EndTable();
        }
        ImGui.TextDisabled("Totals cover retained records for this character only. Unsold stock is not counted as profit.");
        ImGui.BeginDisabled(exportTask != null || journalRows.Length == 0);
        if (ImGui.Button("Export CSV")) exportTask = journal.ExportAsync(owner);
        ImGui.EndDisabled(); Hint(journal.ExportPath);
        ImGui.SameLine(); if (ImGui.Button("Retry save")) journal.RetrySave();
        if (!string.IsNullOrEmpty(journalMessage)) ImGui.TextWrapped(journalMessage);
        if (journalRows.Length == 0)
        {
            ImGui.Spacing(); ImGui.TextWrapped("No retained purchases yet. Make a market-board purchase while tracking is enabled; it will appear here after confirmation. NPC purchases and past trades are not imported.");
            return;
        }
        var pages = Math.Max(1, (journalRows.Length + 49) / 50);
        ImGui.BeginDisabled(journalPage == 0); if (ImGui.SmallButton("Previous")) journalPage--; ImGui.EndDisabled();
        ImGui.SameLine(); ImGui.TextUnformatted($"Page {journalPage + 1} / {pages} (50 purchases per page)");
        ImGui.SameLine(); ImGui.BeginDisabled(journalPage >= pages - 1); if (ImGui.SmallButton("Next")) journalPage++; ImGui.EndDisabled();
        var height = Math.Clamp(ImGui.GetContentRegionAvail().Y * .55f, 110, 320);
        if (ImGui.BeginTable("Purchases", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, height)))
        {
            ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.8f);
            ImGui.TableSetupColumn("Buy world", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Bought / sold", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Paid + tax", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Net sales", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Realized gain", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
            foreach (var lot in journalRows.Skip(journalPage * 50).Take(50))
            {
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                var stamp = DateTimeOffset.FromUnixTimeSeconds(lot.PurchasedAt).ToLocalTime();
                if (ImGui.Selectable($"{stamp:MM/dd HH:mm}##lot{lot.Id}", journalSelection == lot.Id, ImGuiSelectableFlags.SpanAllColumns))
                { journalSelection = lot.Id; saleQuantity = Math.Max(1, lot.Remaining); saleNetReceived = 0; journalMessage = ""; }
                ImGui.TableNextColumn(); ImGui.TextUnformatted(JournalItem(lot)); Hint(JournalItem(lot));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Plugin.Data.GetExcelSheet<World>().TryGetRow(lot.WorldId, out var world) ? world.Name.ToString() : $"World {lot.WorldId}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{lot.Quantity} / {lot.SoldQuantity}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{lot.TotalCost:N0}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{lot.NetRevenue:N0}");
                ImGui.TableNextColumn(); ProfitText((double)lot.RealizedProfit);
            }
            ImGui.EndTable();
        }
        var selected = journalRows.FirstOrDefault(x => x.Id == journalSelection);
        if (selected == null) return;
        ImGui.Spacing(); ImGui.TextUnformatted(JournalItem(selected));
        ImGui.TextWrapped($"Bought {selected.Quantity} at {selected.UnitPrice:N0} gil each + {selected.PurchaseTax:N0} gil purchase tax. {selected.Remaining} units remain unsold in this record.");
        if (selected.Remaining == 0) { ImGui.TextDisabled("All units in this purchase have a recorded sale."); return; }
        ImGui.TextDisabled("Record an actual sale manually. Enter gil received AFTER seller tax, not a forecast price.");
        var reviewSale = false;
        if (ImGui.BeginTable("Record sale", 3, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn(); FieldLabel("Units sold"); ImGui.InputInt("##SaleQty", ref saleQuantity);
            ImGui.TableNextColumn(); FieldLabel("Total gil received after tax"); ImGui.InputInt("##SaleNet", ref saleNetReceived, 1000, 10000);
            ImGui.TableNextColumn(); FieldLabel(" ");
            ImGui.BeginDisabled(saleQuantity < 1 || saleQuantity > selected.Remaining || saleNetReceived < 0);
            if (ImGui.Button("Review sale", new Vector2(ImGui.GetContentRegionAvail().X, 0))) reviewSale = true;
            ImGui.EndDisabled(); ImGui.EndTable();
        }
        if (reviewSale) { saleToConfirm = selected; reviewedSaleQuantity = saleQuantity; reviewedSaleNet = saleNetReceived; ImGui.OpenPopup("Confirm recorded sale"); }
        if (saleToConfirm != null && ImGui.BeginPopupModal("Confirm recorded sale", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted(JournalItem(saleToConfirm));
            ImGui.TextUnformatted($"Record {reviewedSaleQuantity} sold for {reviewedSaleNet:N0} gil net?");
            ImGui.TextDisabled("This updates the retained purchase record; it does not sell an item in game.");
            if (ImGui.Button("Record sale"))
            {
                journalMessage = journal.RecordSale(saleToConfirm.Id, owner, reviewedSaleQuantity, reviewedSaleNet) ? "Actual sale recorded locally." : "Sale could not be recorded. Check quantity and journal status.";
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine(); if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }
    private string JournalItem(PurchaseLot lot)
    {
        if (!journalItemNames.TryGetValue(lot.ItemId, out var name))
        {
            name = Plugin.Data.GetExcelSheet<Item>().TryGetRow(lot.ItemId, out var row) ? row.Name.ToString() : $"Item {lot.ItemId}";
            if (journalItemNames.Count >= TradeJournal.MaxEntries) journalItemNames.Clear();
            journalItemNames[lot.ItemId] = name;
        }
        return name + (lot.Hq ? " (HQ)" : " (NQ)");
    }
    private static string Format(JsonElement row, string field, string format) => row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble().ToString(format) : "N/A";
    private static double Number(JsonElement row, string field) => row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
    private static string Text(JsonElement row, string field) => row.TryGetProperty(field, out var value) ? value.ToString() : "";
    public void Dispose() { InvalidateScan(); Invalidate(); market.Dispose(); }
}















