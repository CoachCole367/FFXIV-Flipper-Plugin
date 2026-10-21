using System.Net;
using System.Text.Json;
namespace FFXIVFlipper;
public static class ScannerRegression
{
    public static async Task<int> Run()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "browser-parity.json")));
        var positive = fixtures.RootElement.GetProperty("cases").EnumerateArray().First(x => x.GetProperty("kind").GetString() == "scanner" && x.GetProperty("expected").GetProperty("rows").GetArrayLength() > 0);
        var input = positive.GetProperty("input"); var id = input.GetProperty("items")[0].GetProperty("id").GetUInt32();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var shift = now - input.GetProperty("nowSec").GetInt64();
        var map = input.GetProperty("listingsByItemWorld").GetProperty(id.ToString());
        var worlds = new Dictionary<uint,string> { [64]="Leviathan", [55]="Ultros", [53]="Exodus" };
        object[] Listings(string name) => map.GetProperty(name).GetProperty("listings").EnumerateArray().Select(x => (object)new {
            pricePerUnit=x.GetProperty("pricePerUnit").GetInt32(), quantity=x.GetProperty("quantity").GetInt32(), hq=x.GetProperty("hq").GetBoolean(),
            listingID=x.GetProperty("listingId").GetString(), lastReviewTime=now,worldID=worlds.Single(w=>w.Value==name).Key, worldName=name
        }).ToArray();
        var home=JsonSerializer.Serialize(new { worldID=64,itemID=id,lastUploadTime=now*1000,listings=Listings("Leviathan") });
        var supply=JsonSerializer.Serialize(new {dcName="Primal",itemID=id,worldUploadTimes=worlds.ToDictionary(x=>x.Key.ToString(),x=>now*1000),listings=worlds.Values.SelectMany(Listings)});
        var sales=input.GetProperty("historyByItemHome").GetProperty(id.ToString()).GetProperty("entries").EnumerateArray().Select(x=>new {pricePerUnit=x.GetProperty("pricePerUnit").GetInt32(),quantity=x.GetProperty("quantity").GetInt32(),hq=x.GetProperty("hq").GetBoolean(),timestamp=x.GetProperty("timestampSec").GetInt64()+shift}).ToArray();
        var history=JsonSerializer.Serialize(new {worldID=64,itemID=id,entries=sales});
        var settings=new ScanSettings(64,"Leviathan","Primal",worlds,1000000,5,2,.58,20,10);
        var catalog=new Dictionary<uint,string> { [id]="Synthetic test item" };
        using var tradeExamples = JsonDocument.Parse("""
          [{"id":1,"planExpectedProfit":726,"sellProbAtRecommended":0.9},
           {"id":2,"planExpectedProfit":2647,"sellProbAtRecommended":0.9},
           {"id":3,"planExpectedProfit":10000,"sellProbAtRecommended":0.8},
           {"id":4,"planExpectedProfit":20000,"sellProbAtRecommended":0.7}]
          """);
        var worthwhile = CrossWorldScanner.SelectWorthwhile(tradeExamples.RootElement.EnumerateArray(), 10000);
        if(worthwhile.Length!=2) throw new Exception("Total-gain floor did not remove low-value trades or excluded the exact boundary.");
        if(worthwhile[0].GetProperty("id").GetInt32()!=4) throw new Exception("Trades not sorted by total expected gain.");
        using var invalidTrades=JsonDocument.Parse("""[{"planExpectedProfit":null},{"profit":10000},{"planExpectedProfit":0},{"planExpectedProfit":-100}]""");
        if(CrossWorldScanner.SelectWorthwhile(invalidTrades.RootElement.EnumerateArray(),0).Length!=0) throw new Exception("Missing or non-positive gain entered recommendations.");
        using var misleadingMargin=JsonDocument.Parse("""[{"profit":10000,"planExpectedProfit":500,"sellProbAtRecommended":0.9}]""");
        if(CrossWorldScanner.SelectWorthwhile(misleadingMargin.RootElement.EnumerateArray(),10000).Length!=0) throw new Exception("Unit margin bypassed total-profit floor.");
        if(CrossWorldScanner.SelectWorthwhile(tradeExamples.RootElement.EnumerateArray(),0).Length!=4) throw new Exception("Explicit lower minimum did not allow smaller trades.");
        using var tiedTrades=JsonDocument.Parse("""[{"id":1,"planExpectedProfit":10000,"sellProbAtRecommended":0.7},{"id":2,"planExpectedProfit":10000,"sellProbAtRecommended":0.9}]""");
        if(CrossWorldScanner.SelectWorthwhile(tiedTrades.RootElement.EnumerateArray(),10000)[0].GetProperty("id").GetInt32()!=2) throw new Exception("Confidence did not break equal-gain ties.");
        var calls = new List<string>();
        HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path=request.RequestUri!.AbsolutePath; calls.Add(path);
            return new(HttpStatusCode.OK) { Content=new StringContent(path.Contains("extra/") ? JsonSerializer.Serialize(new {items=new[]{new{itemID=id}}}) : path.Contains("history/") ? history : path.Contains("Primal/") ? supply : home) };
        }
        using(var client=new MarketClient(new Handler(Respond)))
        {
            var result=await new CrossWorldScanner(client).Run(settings,catalog,[],_=>{},default);
            if(result.Rows.Length==0 || result.Warnings.Length!=0) throw new Exception("Synthetic automatic scanner did not recommend a supported plan.");
            if(result.Analyzed != result.Evaluated) throw new Exception("Successful scan counts are inconsistent.");
            var row=result.Rows[0];
            var stackCost=row.GetProperty("usedListings").EnumerateArray().Sum(x=>x.GetProperty("quantity").GetDouble()*x.GetProperty("pricePerUnit").GetDouble());
            if(Math.Abs(row.GetProperty("planCost").GetDouble()-stackCost)>.001) throw new Exception("Scanner omitted full-stack capital.");
            if(!row.GetProperty("listingSampleLimited").GetBoolean()) throw new Exception("DC listing sample must be marked limited.");
            if(result.Basket.GetProperty("totalCost").GetDouble()>settings.Budget) throw new Exception("Basket exceeded budget.");
            var poor=await new CrossWorldScanner(client).Run(settings with {Budget=1},catalog,[],_=>{},default);
            if(poor.Rows.Length!=0) throw new Exception("Scanner ignored upfront budget.");
            var filtered=await new CrossWorldScanner(client).Run(settings with {MinExpectedProfit=int.MaxValue},catalog,[],_=>{},default);
            if(filtered.Rows.Length!=0 || filtered.LowValueFiltered==0) throw new Exception("Scanner did not apply total-gain filter to actual model output.");
            if(filtered.Basket.GetProperty("rows").GetArrayLength()!=0) throw new Exception("Low-value trade remained in the suggested basket.");
            using var canceled=new CancellationTokenSource(); canceled.Cancel();
            try { await new CrossWorldScanner(client).Run(settings,catalog,[],_=>{},canceled.Token); throw new Exception("Canceled scan returned results."); } catch(OperationCanceledException) { }
        }
        using(var client=new MarketClient(new Handler(request=>request.RequestUri!.AbsolutePath.Contains("history/") ? new(HttpStatusCode.ServiceUnavailable) : Respond(request))))
        {
            var unavailable=await new CrossWorldScanner(client).Run(settings,catalog,[new(id,"Synthetic test item")],_=>{},default);
            if(unavailable.Rows.Length!=0||unavailable.Warnings.Length==0) throw new Exception("Missing history became a scanner forecast.");
            if(unavailable.Analyzed != 0 || unavailable.Evaluated != 1) throw new Exception("Unavailable evidence counted as successful analysis.");
        }
        using(var client=new MarketClient(new Handler(_=>new(HttpStatusCode.OK){Content=new StringContent(supply.Replace("Primal","Aether"))})))
        {
            try { await client.Supply("Primal",id,worlds,default); throw new Exception("Wrong DC supply accepted."); } catch(InvalidDataException) { }
        }
        if(!calls.Any(x=>x.Contains("extra/")) || !calls.Any(x=>x.Contains("history/64/"))) throw new Exception("Automatic candidates or seller-world history were not requested.");
        using(var client=new MarketClient(new Handler(_=>throw new TaskCanceledException("Simulated timeout"))))
        {
            var timedOut=await new CrossWorldScanner(client).Run(settings,catalog,[new(id,"Synthetic test item")],_=>{},default);
            if(timedOut.Rows.Length!=0 || !timedOut.Warnings.Any(x=>x.Contains("timed out"))) throw new Exception("Request timeout did not become a skipped item.");
        }
        using(var client=new MarketClient(new Handler(_=>new(HttpStatusCode.TooManyRequests))))
        {
            var limited=await new CrossWorldScanner(client).Run(settings,catalog,[new(id,"Synthetic test item")],_=>{},default);
            if(limited.Rows.Length!=0 || !limited.Warnings.Any(x=>x.Contains("rate limit"))) throw new Exception("Rate limit did not preserve an explicit partial scan result.");
        }
        return 21;
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(respond(request));
    }
}



