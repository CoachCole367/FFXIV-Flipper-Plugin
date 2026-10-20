using Dalamud.Configuration;
namespace FFXIVFlipper;
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public CandidateSource CandidateSource { get; set; } = CandidateSource.Balanced;
    public List<SavedWatchlist> Watchlists { get; set; } = [new()];
    public int ActiveWatchlist { get; set; }
    public uint SweepCategory { get; set; }
    public int SweepCursor { get; set; }
    public int Version { get; set; } = 1;
    public bool TrackPurchases { get; set; } = true;
    public string SellWorld { get; set; } = "";
    public string ScanDc { get; set; } = "";
    public int Budget { get; set; } = 500000;
    public int ScanItems { get; set; } = 50;
    public int HoldingDays { get; set; } = 2;
    public int MaxQuantity { get; set; } = 20;
    public int ConfidencePercent { get; set; } = 70;
    public int MinProfit { get; set; } = 1000;
    public int MinExpectedProfit { get; set; } = 10000;
}




