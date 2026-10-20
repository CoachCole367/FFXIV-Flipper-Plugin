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
    private string buyWorld;
    private string sellWorld;
    private bool hq;
    private uint item;
    private string itemName = "Select an item";
    private string status = "";
    private int sellPrice;
    private int soldUnits = 1;
    public MainWindow(Configuration config) : base("FFXIV Flipper")
    {
        this.config = config;
        buyWorld = config.BuyWorld;
        sellWorld = config.SellWorld;
        hq = config.Hq;
        Size = new Vector2(670, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
    }
    private void Invalidate()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        // Observe errors on abandoned requests; only Draw consumes current results.
        if (pending != null) _ = pending.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        pending = null;
        buy = sell = null;
        status = "";
    }
    public override void Draw()
    {
        if (pending is { IsCompleted: true })
        {
            try { var result = pending.GetAwaiter().GetResult(); buy = result.Buy; sell = result.Sell; status = "Market snapshots loaded."; }
            catch (OperationCanceledException) { status = "Request canceled."; }
            catch (Exception ex) { status = "Market request failed. Retry when available."; Plugin.Log.Warning(ex, "Universalis request failed"); }
            pending = null;
        }
        ImGui.TextWrapped("Single-item market comparison. Prices are uploaded snapshots; confirm listings in game.");
        if (ImGui.InputText("Item search", ref search, 100)) Invalidate();
        if (search.Length >= 2 && ImGui.BeginChild("Item results", new Vector2(0, 120), true))
        {
            foreach (var row in Plugin.Data.GetExcelSheet<Item>().Where(x => x.ItemSearchCategory.RowId != 0 && x.Name.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)).Take(25))
                if (ImGui.Selectable($"{row.Name}##{row.RowId}", item == row.RowId)) { Invalidate(); item = row.RowId; itemName = row.Name.ToString(); }
            ImGui.EndChild();
        }
        else if (search.Length >= 2) ImGui.EndChild();
        ImGui.TextUnformatted($"Item: {itemName} ({item})");
        if (ImGui.InputText("Buy world", ref buyWorld, 60)) Invalidate();
        if (ImGui.InputText("Sell world", ref sellWorld, 60)) Invalidate();
        if (ImGui.Checkbox("HQ", ref hq)) Invalidate();
        ImGui.BeginDisabled(item == 0 || string.IsNullOrWhiteSpace(buyWorld) || string.IsNullOrWhiteSpace(sellWorld) || pending != null);
        if (ImGui.Button("Load market comparison"))
        {
            Invalidate();
            config.BuyWorld = buyWorld.Trim(); config.SellWorld = sellWorld.Trim(); config.Hq = hq;
            Plugin.PluginInterface.SavePluginConfig(config);
            cancellation = new();
            pending = FetchPair(config.BuyWorld, config.SellWorld, item, cancellation.Token);
            status = "Loading...";
        }
        ImGui.EndDisabled();
        if (pending != null) { ImGui.SameLine(); if (ImGui.Button("Cancel")) { Invalidate(); status = "Canceled."; } }
        ImGui.TextWrapped(status);
        if (buy == null || sell == null) return;
        DrawSnapshot("Buy", buy); DrawSnapshot("Sell", sell);
        ImGui.Separator();
        ImGui.TextWrapped("Manual scenario: choose a resale price and units sold. This is conditional arithmetic, not a demand forecast. Seller tax assumption: 5%; travel and purchase tax excluded.");
        ImGui.InputInt("Resale gil per unit", ref sellPrice);
        ImGui.InputInt("Units sold", ref soldUnits);
        var listing = buy.Listings.FirstOrDefault(x => x.Hq == hq);
        if (listing == null) { ImGui.TextUnformatted("No matching buy listing in this sample."); return; }
        ImGui.TextUnformatted($"Cheapest sampled stack: {listing.Quantity} x {listing.PricePerUnit:N0} gil; upfront {listing.Quantity * (decimal)listing.PricePerUnit:N0} gil");
        if (sellPrice >= 0 && soldUnits >= 0 && soldUnits <= listing.Quantity)
            ImGui.TextUnformatted($"Conditional cash gain: {StackEconomics.CashGain(listing.PricePerUnit, listing.Quantity, sellPrice, soldUnits):N0} gil; leftover units: {listing.Quantity - soldUnits}");
        else ImGui.TextWrapped("Enter a nonnegative price and units sold between zero and the full stack size.");
    }
    private async Task<(MarketSnapshot, MarketSnapshot)> FetchPair(string source, string destination, uint id, CancellationToken token)
    {
        var sourceTask = market.Fetch(source, id, token);
        var destinationTask = source == destination ? sourceTask : market.Fetch(destination, id, token);
        await Task.WhenAll(sourceTask, destinationTask);
        return (await sourceTask, await destinationTask);
    }
    private void DrawSnapshot(string label, MarketSnapshot snapshot)
    {
        var match = snapshot.Listings.Where(x => x.Hq == hq).ToArray();
        var age = snapshot.LastUploadTime > 0 ? $"{Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - snapshot.LastUploadTime) / 60000)} minutes old" : "age unknown";
        ImGui.TextUnformatted($"{label}: {match.Length} matching listings sampled; {age}");
        foreach (var row in match.Take(5)) ImGui.TextUnformatted($"  {row.Quantity} units at {row.PricePerUnit:N0} gil each");
    }
    public void Dispose() { Invalidate(); market.Dispose(); }
}
