using Dalamud.Game.Command;
using Dalamud.Game.Network.Structures;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
namespace FFXIVFlipper;
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IPlayerState Player { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IMarketBoard MarketBoard { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    private readonly TradeJournal journal;
    private readonly PurchaseTracker tracker;
    private readonly Configuration config;
    private volatile bool disposed;
    private readonly WindowSystem windows = new("FFXIVFlipper");
    private readonly MainWindow main;
    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new();
        journal = new(PluginInterface.GetPluginConfigDirectory()); tracker = new(journal);
        main = new(config, journal, tracker.Clear);
        Framework.Update += OnFrameworkUpdate;
        MarketBoard.PurchaseRequested += OnPurchaseRequested;
        MarketBoard.ItemPurchased += OnItemPurchased;
        windows.AddWindow(main);
        Commands.AddHandler("/fflipper", new CommandInfo(OnCommand) { HelpMessage = "Open FFXIV Flipper market research." });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += Open;
        PluginInterface.UiBuilder.OpenConfigUi += Open;
    }
    private void OnPurchaseRequested(IMarketBoardPurchaseHandler purchase)
    {
        var item = purchase.CatalogId >= 1_000_000 ? purchase.CatalogId - 1_000_000 : purchase.CatalogId;
        var quantity = purchase.ItemQuantity; var price = purchase.PricePerUnit; var tax = purchase.TotalTax;
        var listing = purchase.ListingId; var hq = purchase.IsHq || purchase.CatalogId >= 1_000_000; var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _ = Framework.RunOnFrameworkThread(() => {
            if (disposed) return;
            if (!config.TrackPurchases || !Player.IsLoaded || !Player.CurrentWorld.IsValid || quantity > 999) { tracker.Clear(); return; }
            tracker.Request(new(Player.ContentId, Player.CurrentWorld.RowId, item, hq, (int)quantity, price, tax, listing, time));
        });
    }
    private void OnItemPurchased(IMarketBoardPurchase purchase)
    {
        var item = purchase.CatalogId; var quantity = purchase.ItemQuantity; var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _ = Framework.RunOnFrameworkThread(() => {
            if (disposed) return;
            if (!config.TrackPurchases || !Player.IsLoaded || !Player.CurrentWorld.IsValid) { tracker.Clear(); return; }
            tracker.Confirm(item, quantity, Player.ContentId, Player.CurrentWorld.RowId, time);
        });
    }
    private void OnCommand(string command, string args) => Open();
    private void OnFrameworkUpdate(IFramework framework) => main.UpdateCharacter(Player.IsLoaded ? Player.ContentId : 0);
    private void Open() => main.IsOpen = true;
    public void Dispose()
    {
        disposed = true;
        Framework.Update -= OnFrameworkUpdate;
        MarketBoard.PurchaseRequested -= OnPurchaseRequested;
        MarketBoard.ItemPurchased -= OnItemPurchased;
        tracker.Clear();
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= Open;
        PluginInterface.UiBuilder.OpenConfigUi -= Open;
        Commands.RemoveHandler("/fflipper");
        windows.RemoveAllWindows();
        main.Dispose(); journal.Dispose();
    }
}




