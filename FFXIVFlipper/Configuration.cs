using Dalamud.Configuration;
namespace FFXIVFlipper;
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string BuyWorld { get; set; } = "";
    public string SellWorld { get; set; } = "";
    public bool Hq { get; set; }
}
