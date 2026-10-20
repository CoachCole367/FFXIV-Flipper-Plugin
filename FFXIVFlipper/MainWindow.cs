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
    private (uint Id, string Name)[]? worldOptions;
    private string sellWorld;
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
    private CandidateSource source;
    private uint[]? sweepCatalog;
    private (uint Id, string Name)[]? categories;
    private int opportunitySortColumn = 4, journalSortColumn;
    private bool opportunityDescending = true, journalDescending = true, journalSortDirty = true;
    private int[]? opportunityOrder;
    private string clipboardNotice = "";
    private long clipboardNoticeUntil;
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
    private ulong exportCharacter;
    private readonly Dictionary<uint, string> journalItemNames = [];
    public MainWindow(Configuration config, TradeJournal journal, System.Action clearPendingPurchases) : base("FFXIV Flipper")
    {
        this.config = config; this.journal = journal; this.clearPendingPurchases = clearPendingPurchases;
        config.Watchlists = CandidateDiscovery.Normalize(config.Watchlists);
        if (config.Watchlists.Count == 0) config.Watchlists.Add(new());
        config.ActiveWatchlist = Math.Clamp(config.ActiveWatchlist, 0, config.Watchlists.Count - 1);
        source = config.CandidateSource is CandidateSource.ActiveMarket or CandidateSource.RecentUpdates
            ? config.CandidateSource : CandidateSource.Balanced;
        sellWorld = config.SellWorld;
        scanDc = config.ScanDc; budget = config.Budget; scanItems = config.ScanItems; holdingDays = config.HoldingDays;
        maxQuantity = config.MaxQuantity; confidence = config.ConfidencePercent; minProfit = config.MinProfit; minExpectedProfit = config.MinExpectedProfit;
        Size = new Vector2(1040, 740);
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(800, 600), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        SizeCondition = ImGuiCond.FirstUseEver;
    }
    public override void PreDraw() => ImGui.SetNextWindowBgAlpha(0.98f);
    public override void Draw()
    {
        UpdateCharacter(Plugin.Player.IsLoaded ? Plugin.Player.ContentId : 0);
        if (string.IsNullOrEmpty(sellWorld) && Plugin.Player.IsLoaded && Plugin.Player.HomeWorld.IsValid)
            sellWorld = Plugin.Player.HomeWorld.Value.Name.ToString();
        if (ImGui.BeginTabBar("Flipper tools"))
        {
            if (ImGui.BeginTabItem("Find profitable flips")) { DrawScanner(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Purchase journal")) { DrawJournal(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
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
    private void InvalidateScan()
    {
        Interlocked.Increment(ref scanRunId);
        scanCancellation?.Cancel(); scanCancellation?.Dispose(); scanCancellation = null;
        if (scanTask != null) _ = scanTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        scanTask = null; scanResult = null; opportunityOrder = null; selectedOpportunity = -1; scanStatus = "";
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
        var home = Plugin.Data.GetExcelSheet<World>().FirstOrDefault(x => x.Name.ToString() == sellWorld);
        if (ImGui.BeginTable("Scan controls", 4, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn(); FieldLabel("Sell world");
            if (DrawWorld("##ScanHome", ref sellWorld)) { InvalidateScan(); scanDc = ""; }
            home = Plugin.Data.GetExcelSheet<World>().FirstOrDefault(x => x.Name.ToString() == sellWorld);
            ImGui.TableNextColumn(); FieldLabel("Buy data center");
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
            if (ImGui.InputInt("##Budget", ref budget, 0, 0)) InvalidateScan();
            ImGui.TableNextColumn(); FieldLabel("Minimum gain / trade");
            if (ImGui.InputInt("##MinExpectedGain", ref minExpectedProfit, 0, 0)) InvalidateScan();
            Hint("Total expected cash gain after the full stack cost and assumed seller tax. Applied before the budget basket is chosen.");
            ImGui.EndTable();
        }
        ImGui.Spacing();
        ImGui.BeginDisabled(!ValidScan(home.RowId) || scanTask != null);
        var start = ImGui.Button("Scan for flips", new Vector2(160, 0));
        ImGui.EndDisabled();
        if (start) StartScan(home.RowId);
        ImGui.SameLine();
        ImGui.TextDisabled($"{scanItems} items  |  {holdingDays} day window  |  {confidence}% minimum sell chance");
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Scan settings##CompactScanSettings"))
        {
            DrawDiscovery();
            if (ImGui.BeginTable("Extra scan controls", 3, ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn(); FieldLabel("Items to check (1-200)");
                if (ImGui.InputInt("##ScanCount", ref scanItems, 0, 0)) InvalidateScan();
                ImGui.TableNextColumn(); FieldLabel("Sell within days (1-7)");
                if (ImGui.InputInt("##HoldDays", ref holdingDays, 0, 0)) { InvalidateScan(); }
                ImGui.TableNextColumn(); FieldLabel("Minimum sell chance % (1-99)");
                if (ImGui.InputInt("##Confidence", ref confidence, 0, 0)) InvalidateScan();
                ImGui.TableNextColumn(); FieldLabel("Planned resale units (1-100)");
                if (ImGui.InputInt("##MaxQty", ref maxQuantity, 0, 0)) InvalidateScan();
                ImGui.TableNextColumn(); FieldLabel("Minimum margin / planned unit");
                if (ImGui.InputInt("##MinUnitMargin", ref minProfit, 0, 0)) InvalidateScan();
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
    private static bool ReadSort(ref int column, ref bool descending)
    {
        var specs = ImGui.TableGetSortSpecs();
        if (specs.IsNull || specs.SpecsCount == 0 || !specs.SpecsDirty) return false;
        column = specs.Specs.ColumnIndex;
        descending = specs.Specs.SortDirection == ImGuiSortDirection.Descending;
        specs.SpecsDirty = false;
        return true;
    }
    private uint[] GetSweepCatalog()
    {
        return sweepCatalog ??= Plugin.Data.GetExcelSheet<Item>()
            .Where(x => x.ItemSearchCategory.RowId != 0 && (config.SweepCategory == 0 || x.ItemSearchCategory.RowId == config.SweepCategory))
            .Select(x => x.RowId).OrderDescending().ToArray();
    }
    private void SaveDiscovery()
    {
        InvalidateScan(); config.CandidateSource = source;
        Plugin.PluginInterface.SavePluginConfig(config);
    }
    private void DrawDiscovery()
    {
        if (ImGui.BeginTable("Candidate controls", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn(); FieldLabel("Search mode");
            string Label(CandidateSource value) => value switch { CandidateSource.Balanced => "Balanced discovery", CandidateSource.ActiveMarket => "Active market sweep", _ => "Recently updated" };
            if (ImGui.BeginCombo("##CandidateSource", Label(source)))
            {
                foreach (var value in new[] { CandidateSource.Balanced, CandidateSource.ActiveMarket, CandidateSource.RecentUpdates })
                    if (ImGui.Selectable(Label(value), source == value)) { source = value; SaveDiscovery(); }
                ImGui.EndCombo();
            }
            Hint("Balanced combines active markets and recent updates. Watchlists are paused; saved lists are kept.");
            ImGui.TableNextColumn(); FieldLabel("Item category");
            categories ??= Plugin.Data.GetExcelSheet<ItemSearchCategory>().Where(x => x.RowId != 0 && !string.IsNullOrWhiteSpace(x.Name.ToString()))
                .Select(x => (x.RowId, x.Name.ToString())).OrderBy(x => x.Item2).Prepend((0u, "All marketable items")).ToArray();
            ImGui.BeginDisabled(source == CandidateSource.RecentUpdates);
            if (ImGui.BeginCombo("##SweepCategory", categories.FirstOrDefault(x => x.Id == config.SweepCategory).Name ?? "All marketable items"))
            {
                foreach (var category in categories)
                    if (ImGui.Selectable(category.Name, category.Id == config.SweepCategory))
                    { config.SweepCategory = category.Id; config.SweepCursor = 0; sweepCatalog = null; SaveDiscovery(); }
                ImGui.EndCombo();
            }
            ImGui.EndDisabled();
            Hint("Category applies to the active-market sweep; recent updates can include other categories.");
            ImGui.EndTable();
        }
    }
    private bool ValidScan(uint homeId) => homeId != 0 && budget > 0 && scanItems is >= 1 and <= 200
        && holdingDays is >= 1 and <= 7 && confidence is >= 1 and <= 99 && maxQuantity is >= 1 and <= 100
        && minProfit >= 0 && minExpectedProfit >= 0;
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
        scanCancellation = new(); var token = scanCancellation.Token; var runId = scanRunId;
        var sweep = GetSweepCatalog();
        var request = new DiscoveryRequest(source, homeId, scanDc, scanItems,
            [], // Watchlists are paused; saved lists remain in configuration.
            CandidateDiscovery.Sweep(sweep, config.SweepCursor));
        if (source is CandidateSource.ActiveMarket or CandidateSource.Balanced)
            config.SweepCursor = sweep.Length == 0 ? 0 : (Math.Max(0, config.SweepCursor) % sweep.Length + request.Sweep.Length) % sweep.Length;
        config.CandidateSource = source;
        Plugin.PluginInterface.SavePluginConfig(config);
        scanProgress = new(0, 0, "Finding candidates...");
        scanTask = Task.Run(async () =>
        {
            var ids = await CandidateDiscovery.Find(market, request, token);
            var candidates = ids.Where(catalog.ContainsKey).Select(id => new ScanItem(id, catalog[id])).ToArray();
            if (candidates.Length == 0) throw new InvalidDataException("No active candidates found in this catalog slice. Scan again for the next slice, or change category/search mode.");
            return await new CrossWorldScanner(market).Run(settings, catalog, candidates,
                p => { if (Volatile.Read(ref scanRunId) == runId) scanProgress = p; }, token);
        }, token);
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
        ImGui.TextDisabled($"Click a column heading to sort; click again to reverse. Basket marks picks that fit together. Updated {age}m ago.");
        ClipboardHint();
        if (age >= 30) ImGui.TextWrapped("Results are over 30 minutes old. Scan again before buying.");
        if (result.Rows.Length == 0)
        {
            ImGui.Spacing(); ImGui.TextUnformatted("No worthwhile trades passed these filters.");
            ImGui.TextWrapped($"{result.LowValueFiltered} smaller opportunities were excluded by the {minExpectedProfit:N0} gil minimum. Try another buy DC or a larger item sample.");
        }
        else
        {
            var tableHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y * .48f, 120, 340);
            if (ImGui.BeginTable("Opportunities", 8, ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, tableHeight)))
            {
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3.3f);
                ImGui.TableSetupColumn("Buy world", ImGuiTableColumnFlags.WidthStretch, 1.2f);
                ImGui.TableSetupColumn("Upfront", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("Sell / unit", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("Expected gain", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, 1.2f);
                ImGui.TableSetupColumn("Sell chance", ImGuiTableColumnFlags.WidthStretch, .9f);
                ImGui.TableSetupColumn("Days", ImGuiTableColumnFlags.WidthStretch, .6f);
                ImGui.TableSetupColumn("Basket", ImGuiTableColumnFlags.WidthStretch, .7f);
                ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
                if (ReadSort(ref opportunitySortColumn, ref opportunityDescending)) opportunityOrder = null;
                opportunityOrder ??= TableSorting.Opportunities(result.Rows, opportunitySortColumn, opportunityDescending, row => basketRows.Any(p => SameTrade(p, row)));
                foreach (var index in opportunityOrder)
                {
                    var row = result.Rows[index];
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    if (ImGui.Selectable($"{Text(row,"itemName")}##trade{index}", selectedOpportunity == index)) { selectedOpportunity = index; CopyMarketName((uint)Number(row,"id")); }
                    Hint(Text(row,"itemName") + " - click to copy the market-board search name.");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(Text(row,"buyWorld"));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{Number(row,"planCost"):N0}");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{Number(row,"targetSell"):N0}");
                    Hint($"Suggested sell price per unit on {sellWorld}, before seller tax. This is an estimate, not a guaranteed sale.");
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
        if (ImGui.SmallButton(Text(row,"itemName") + "##copyTradeName")) CopyMarketName((uint)Number(row,"id"));
        Hint("Copy item name for market-board search (without the HQ/NQ suffix).");
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
    internal void UpdateCharacter(ulong owner)
    {
        if (journalCharacter == owner) return;
        journalCharacter = owner; journalRevision = -1; journalRows = [];
        journalSelection = Guid.Empty; journalPage = 0; journalSortDirty = true;
        journalUnsoldUnits = 0; journalRealizedProfit = 0; journalCashRecovered = 0;
        saleToConfirm = null; saleQuantity = 1; saleNetReceived = 0;
        reviewedSaleQuantity = 0; reviewedSaleNet = 0; journalMessage = "";
        clearPendingPurchases();
    }
    private void DrawSaleConfirmation(ulong owner)
    {
        if (!ImGui.BeginPopupModal("Confirm recorded sale", ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (saleToConfirm == null || owner == 0 || saleToConfirm.CharacterId != owner)
        {
            saleToConfirm = null; ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return;
        }
        ImGui.TextUnformatted(JournalItem(saleToConfirm));
        ImGui.TextUnformatted($"Record {reviewedSaleQuantity} sold for {reviewedSaleNet:N0} gil net?");
        ImGui.TextDisabled("This updates the retained purchase record; it does not sell an item in game.");
        if (ImGui.Button("Record sale"))
        {
            journalMessage = journal.RecordSale(saleToConfirm.Id, owner, reviewedSaleQuantity, reviewedSaleNet) ? "Actual sale recorded locally." : "Sale could not be recorded. Check quantity and journal status.";
            saleToConfirm = null; ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { saleToConfirm = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }
    private void DrawJournal()
    {
        var owner = journalCharacter;
        // Process an existing modal before logged-out/empty-journal early returns.
        DrawSaleConfirmation(owner);
        ImGui.TextUnformatted("PURCHASE JOURNAL");
        ClipboardHint();
        var tracking = config.TrackPurchases;
        if (ImGui.Checkbox("Automatically log market-board purchases", ref tracking))
        {
            clearPendingPurchases(); config.TrackPurchases = tracking; Plugin.PluginInterface.SavePluginConfig(config);
        }
        ImGui.TextDisabled("Records future confirmed purchases. No inventory polling or market snapshots.");
        ImGui.TextDisabled($"Keeps the newest {TradeJournal.MaxEntries:N0} purchases for up to {TradeJournal.RetentionDays} days; older entries are removed on load/write.");
        ImGui.TextDisabled($"Journal: {journal.FileBytes / 1024d:N1} KB / {TradeJournal.MaxFileBytes / 1024} KB maximum. {journal.Status}");
        if (owner == 0) { ImGui.TextWrapped("Log in to see purchases for this character."); return; }
        var revision = journal.Revision;
        if (journalRevision != revision || journalCharacter != owner)
        {
            journalRows = journal.Snapshot(owner); journalSortDirty = true; journalRevision = revision; journalCharacter = owner;
            journalUnsoldUnits = journalRows.Sum(x => (long)x.Remaining); journalRealizedProfit = journalRows.Sum(x => x.RealizedProfit); journalCashRecovered = journalRows.Sum(x => (decimal)x.CashRecovered);
            journalPage = Math.Min(journalPage, Math.Max(0, (journalRows.Length - 1) / 50));
            if (!journalRows.Any(x => x.Id == journalSelection)) journalSelection = journalRows.FirstOrDefault()?.Id ?? Guid.Empty;
        }
        if (exportTask is { IsCompleted: true })
        {
            try { exportTask.GetAwaiter().GetResult(); if (exportCharacter == owner) journalMessage = "CSV saved. Each export replaces the same file."; }
            catch (Exception ex) { if (exportCharacter == owner) journalMessage = "Export failed: " + ex.Message; }
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
        if (ImGui.Button("Export CSV")) { exportCharacter = owner; exportTask = journal.ExportAsync(owner); }
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
        if (ImGui.BeginTable("Purchases", 7, ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, height)))
        {
            ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, 1.1f);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.8f);
            ImGui.TableSetupColumn("Buy world", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Bought / sold", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Paid + tax", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Net sales", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Realized gain", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
            if (ReadSort(ref journalSortColumn, ref journalDescending)) { journalSortDirty = true; journalPage = 0; }
            if (journalSortDirty)
            {
                journalRows = TableSorting.Purchases(journalRows, journalSortColumn, journalDescending, JournalItem,
                    id => Plugin.Data.GetExcelSheet<World>().TryGetRow(id, out var w) ? w.Name.ToString() : $"World {id}");
                journalSortDirty = false;
            }
            foreach (var lot in journalRows.Skip(journalPage * 50).Take(50))
            {
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                var stamp = DateTimeOffset.FromUnixTimeSeconds(lot.PurchasedAt).ToLocalTime();
                if (ImGui.Selectable($"{stamp:MM/dd HH:mm}##lot{lot.Id}", journalSelection == lot.Id))
                { journalSelection = lot.Id; saleQuantity = Math.Max(1, lot.Remaining); saleNetReceived = 0; journalMessage = ""; }
                ImGui.TableNextColumn();
                if (ImGui.Selectable(JournalItem(lot) + $"##copyLot{lot.Id}", journalSelection == lot.Id))
                { journalSelection = lot.Id; saleQuantity = Math.Max(1, lot.Remaining); saleNetReceived = 0; CopyMarketName(lot.ItemId); }
                Hint("Click the item name to copy it for market-board search.");
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
    private void CopyMarketName(uint itemId)
    {
        // Resolve the raw localized item name: planner labels append HQ/NQ, which must not enter search text.
        if (itemId == 0 || !Plugin.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var row) || string.IsNullOrWhiteSpace(row.Name.ToString()))
        {
            clipboardNotice = "Item name unavailable; clipboard unchanged.";
        }
        else
        {
            try { var name = row.Name.ToString(); ImGui.SetClipboardText(name); clipboardNotice = $"Copied: {name}"; }
            catch (Exception ex) { clipboardNotice = "Could not copy item name. Try again."; Plugin.Log.Warning(ex, "Item clipboard copy failed"); }
        }
        clipboardNoticeUntil = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 4000;
    }
    private void ClipboardHint() => ImGui.TextDisabled(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < clipboardNoticeUntil
        ? clipboardNotice : "Click an item name to copy it; paste into market-board search with Ctrl+V.");
    private static string Format(JsonElement row, string field, string format) => row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble().ToString(format) : "N/A";
    private static double Number(JsonElement row, string field) => row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
    private static string Text(JsonElement row, string field) => row.TryGetProperty(field, out var value) ? value.ToString() : "";
    public void Dispose() { InvalidateScan(); market.Dispose(); }
}

















