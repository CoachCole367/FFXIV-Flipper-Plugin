using System.Diagnostics;
using System.Text.Json;
namespace FFXIVFlipper;
public static class LiveDiscovery
{
    public static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FFXIVFlipper-Validation/0.6.0");
        var catalogIds = JsonSerializer.Deserialize<uint[]>(await http.GetStringAsync("https://universalis.app/api/v2/marketable", deadline.Token))!
            .Distinct().OrderDescending().ToArray();
        var catalog = catalogIds.ToDictionary(x=>x,x=>$"Item {x}");
        var worlds = new Dictionary<uint,string> { [35]="Famfrit",[53]="Exodus",[55]="Lamia",[64]="Leviathan",[77]="Ultros",[78]="Behemoth",[93]="Excalibur",[95]="Hyperion" };
        using var market = new MarketClient();
        var cursor = 0;
        foreach(var source in new[]{CandidateSource.Balanced,CandidateSource.ActiveMarket})
        {
            var timer=Stopwatch.StartNew();
            var sweep=CandidateDiscovery.Sweep(catalogIds,cursor); cursor+=sweep.Length;
            var request=new DiscoveryRequest(source,64,"Primal",200,[10373,52256],sweep);
            var ids=await CandidateDiscovery.Find(market,request,deadline.Token);
            if(ids.Length==0||ids.Length>200||ids.Distinct().Count()!=ids.Length) throw new Exception("Invalid live candidate shortlist.");
            var settings=new ScanSettings(64,"Leviathan","Primal",worlds,2000000,200,7,.7,20,1000);
            var result=await new CrossWorldScanner(market).Run(settings,catalog,ids.Select(x=>new ScanItem(x,catalog[x])).ToArray(),
                p=>{if(p.Completed%25==0) Console.WriteLine($"{source} {p.Completed}/{p.Total}");},deadline.Token);
            if(result.Basket.TryGetProperty("totalCost",out var cost)&&cost.GetDouble()>settings.Budget) throw new Exception("Live basket exceeded budget.");
            Console.WriteLine($"{source}: screened {sweep.Length}, evaluated {result.Evaluated}, {result.Rows.Length} trades, {result.Warnings.Length} warnings, {timer.Elapsed.TotalSeconds:F2}s");
            foreach(var warning in result.Warnings.Take(5)) Console.WriteLine(warning);
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await CandidateDiscovery.Find(market,new(CandidateSource.Balanced,64,"Primal",200,[],catalogIds[..1000]),canceled.Token); throw new Exception("Canceled discovery returned."); }
        catch(OperationCanceledException) { Console.WriteLine("Canceled discovery stops before network work."); }
    }
}
