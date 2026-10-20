using Dalamud.Configuration;
namespace FFXIVFlipper;
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool TrackPurchases { get; set; } = true;
    public string BuyWorld { get; set; } = "";
    public string SellWorld { get; set; } = "";
    public bool Hq { get; set; }
    public string ScanDc { get; set; } = "";
    public int Budget { get; set; } = 500000;
    public int ScanItems { get; set; } = 50;
    public int HoldingDays { get; set; } = 2;
    public int MaxQuantity { get; set; } = 20;
    public int ConfidencePercent { get; set; } = 70;
    public int MinProfit { get; set; } = 1000;
    public int MinExpectedProfit { get; set; } = 10000;
}



