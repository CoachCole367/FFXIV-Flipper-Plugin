using Dalamud.Game.Command;
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
    private readonly WindowSystem windows = new("FFXIVFlipper");
    private readonly MainWindow main;
    public Plugin()
    {
        var config = PluginInterface.GetPluginConfig() as Configuration ?? new();
        main = new(config);
        windows.AddWindow(main);
        Commands.AddHandler("/fflipper", new CommandInfo(OnCommand) { HelpMessage = "Open FFXIV Flipper market research." });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += Open;
        PluginInterface.UiBuilder.OpenConfigUi += Open;
    }
    private void OnCommand(string command, string args) => Open();
    private void Open() => main.IsOpen = true;
    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= Open;
        PluginInterface.UiBuilder.OpenConfigUi -= Open;
        Commands.RemoveHandler("/fflipper");
        windows.RemoveAllWindows();
        main.Dispose();
    }
}

