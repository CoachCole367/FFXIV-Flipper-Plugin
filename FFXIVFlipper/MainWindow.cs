using System.Numerics;
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
    private (uint Id, string Name)[] searchResults = [];
    private string buyWorld;
    private string sellWorld;
    private bool hq;
    private uint item;
    private string itemName = "Select an item";
    private string status = "";
    private int sellPrice;
    private int soldUnits = 1;
    private PriceGuidance? currentGuidance;
    private long guidanceAt;
    public MainWindow(Configuration config) : base("FFXIV Flipper")
    {
        this.config = config;
        buyWorld = config.BuyWorld;
        sellWorld = config.SellWorld;
        hq = config.Hq;
        Size = new Vector2(740, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
    }
    private void Invalidate()
    {
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
        if (pending != null) _ = pending.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        pending = null; buy = sell = null; currentGuidance = null; status = "";
    }
    public override void Draw()
    {
        if (pending is { IsCompleted: true })
        {
            try { var result = pending.GetAwaiter().GetResult(); buy = result.Buy; sell = result.Sell; status = "Market snapshots loaded."; }
            catch (OperationCanceledException) { status = "Request canceled or timed out. Try again."; }
            catch (Exception ex) { status = $"Market request failed: {ex.Message}"; Plugin.Log.Warning(ex, "Universalis request failed"); }
            pending = null;
        }
        ImGui.TextWrapped("Single-item analysis. Confirm uploaded listings in game before buying.");
        if (ImGui.InputText("Item search", ref search, 100))
        {
            Invalidate();
            // Search once per edit, rather than scanning the item sheet on every game frame.
            searchResults = search.Length < 2 ? [] : Plugin.Data.GetExcelSheet<Item>()
                .Where(x => x.ItemSearchCategory.RowId != 0 && x.Name.ToString().Contains(search, StringComparison.OrdinalIgnoreCase))
                .Take(25).Select(x => (x.RowId, x.Name.ToString())).ToArray();
        }
        if (search.Length >= 2)
        {
            if (ImGui.BeginChild("Item results", new Vector2(0, 100), true))
                foreach (var row in searchResults)
                    if (ImGui.Selectable($"{row.Name}##{row.Id}", item == row.Id)) { Invalidate(); item = row.Id; itemName = row.Name; sellPrice = 0; }
            ImGui.EndChild();
        }
        ImGui.TextUnformatted($"Item: {itemName} ({item})");
        if (ImGui.InputText("Buy world", ref buyWorld, 60)) Invalidate();
        if (ImGui.InputText("Sell world", ref sellWorld, 60)) Invalidate();
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
        if (currentGuidance == null || now - guidanceAt >= 30)
        {
            currentGuidance = MarketAnalysis.Analyze(sell, hq, now); guidanceAt = now;
        }
        var guidance = currentGuidance;
        ImGui.TextUnformatted("Sell-world sales and price guidance");
        ImGui.TextWrapped(sell.History.Status);
        ImGui.TextUnformatted($"{guidance.SaleCount} matching-quality sale records in the requested seven-day window");
        ImGui.TextWrapped(guidance.Reason);
        if (guidance.Price is int recommended)
        {
            ImGui.TextUnformatted($"Reference sell price: {recommended:N0} gil per unit");
            ImGui.TextUnformatted($"Historical units/day near this price: {guidance.UnitsPerDay:N1}");
            ImGui.TextWrapped($"At least {guidance.QueueUnits:N0} competing units at or below this price in the listing sample. This is not a complete queue or a clearance forecast.");
            if (ImGui.Button("Use reference in scenario")) sellPrice = recommended;
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
    public void Dispose() { Invalidate(); market.Dispose(); }
}

