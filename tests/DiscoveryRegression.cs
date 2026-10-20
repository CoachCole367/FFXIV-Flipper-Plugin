using System.Net;
using System.Text.Json;
namespace FFXIVFlipper;
public static class DiscoveryRegression
{
    public static async Task<int> Run()
    {
        var checks = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        Check(CandidateDiscovery.Sweep([1,2,3,4], 2, 3).SequenceEqual(new uint[]{3,4,1}), "Catalog sweep must wrap.");
        Check(CandidateDiscovery.Sweep([1,2], int.MaxValue).Length == 2, "Small sweep duplicated items.");
        Check(CandidateDiscovery.Sweep([], 0).Length == 0, "Empty catalog failed.");
        Check(CandidateDiscovery.Mix(5, [1,2,3], [1,4,5], [6,2]).SequenceEqual(new uint[]{1,6,2,4,3}), "Balanced discovery did not interleave unique sources.");
        Check(CandidateDiscovery.Mix(200, [0,1,1], []).SequenceEqual(new uint[]{1}), "Mix retained invalid or duplicate IDs.");
        var lists = CandidateDiscovery.Normalize(Enumerable.Range(0, 12).Select(_ => new SavedWatchlist
        { Name = new string('a',100), Items = Enumerable.Range(0, 300).Select(x => (uint)x).ToList() }));
        Check(lists.Count == 8 && lists.All(x => x.Name.Length == 40 && x.Items.Count == 200), "Watchlist storage was not bounded.");
        var roundtrip = CandidateDiscovery.Normalize(JsonSerializer.Deserialize<List<SavedWatchlist>>(JsonSerializer.Serialize(lists)));
        Check(roundtrip[0].Items.SequenceEqual(lists[0].Items), "Saved watchlist IDs did not survive reload.");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        JsonElement Aggregate(uint id, double quantity, long timestamp) => JsonSerializer.SerializeToElement(new
        { itemId=id, nq=new { recentPurchase=new {world=new{timestamp=timestamp*1000}}, averageSalePrice=new{world=new{price=100}}, dailySaleVelocity=new{world=new{quantity}} } });
        Check(CandidateDiscovery.ActivityScore(Aggregate(1,4,now), now) == 400, "Activity ranking did not use home-world traded value.");
        Check(CandidateDiscovery.ActivityScore(Aggregate(1,4,now-4*86400), now) == 0, "Stale aggregate sale entered shortlist.");
        Check(CandidateDiscovery.ActivityScore(Aggregate(1,4,now+120), now) == 0, "Future aggregate sale entered shortlist.");
        Check(CandidateDiscovery.ActivityScore(JsonSerializer.SerializeToElement(new {nq=new {}}), now) == 0, "Missing activity became demand.");
        var requests = 0;
        using (var market = new MarketClient(new Handler(r =>
        {
            requests++;
            return new(HttpStatusCode.OK) {Content=new StringContent(JsonSerializer.Serialize(new { results=new[]{Aggregate(3,2,now), Aggregate(1,5,now), Aggregate(2,1,now-4*86400)} }))};
        })))
        {
            var active = await market.ActiveCandidates(64, [1,2,3], 2, default);
            Check(active.SequenceEqual(new uint[]{1,3}) && requests == 1, "Aggregate ranking or candidate identity failed.");
            var watchlist = await CandidateDiscovery.Find(market, new(CandidateSource.Watchlist,64,"Primal",200,[3,1,3],[]), default);
            Check(watchlist.SequenceEqual(new uint[]{3,1}) && requests == 1, "Watchlist mode must bypass discovery requests.");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { await market.ActiveCandidates(64,[1],1,cancel.Token); throw new Exception("Canceled discovery returned."); }
            catch(OperationCanceledException) { checks++; }
        }
        using (var market = new MarketClient(new Handler(_ => new(HttpStatusCode.OK) {Content=new StringContent(JsonSerializer.Serialize(new{results=new[]{Aggregate(999,10,now)}}))})))
        {
            try { await market.ActiveCandidates(64,[1],1,default); throw new Exception("Unexpected aggregate item accepted."); }
            catch(InvalidDataException) {checks++;}
        }
        using var rowsDoc = JsonDocument.Parse("""
        [{"itemName":"Zulu","buyWorld":"B","planCost":10,"targetSell":100,"planExpectedProfit":2,"sellProbAtRecommended":0.9,"daysToClearRecommended":null},
         {"itemName":"Alpha","buyWorld":"C","planCost":2,"targetSell":30,"planExpectedProfit":10,"sellProbAtRecommended":0.7,"daysToClearRecommended":2},
         {"itemName":"Beta","buyWorld":"A","planCost":100,"targetSell":200,"planExpectedProfit":20,"sellProbAtRecommended":0.8,"daysToClearRecommended":1}]
        """);
        var rows=rowsDoc.RootElement.EnumerateArray().Select(x=>x.Clone()).ToArray();
        var ascending=new[]{new[]{1,2,0},new[]{2,0,1},new[]{1,0,2},new[]{1,0,2},new[]{0,1,2},new[]{1,2,0},new[]{2,1,0},new[]{0,2,1}};
        for(var column=0;column<8;column++)
        {
            var c=column;
            Check(TableSorting.Opportunities(rows,c,false,r=>r.GetProperty("itemName").GetString()=="Alpha").SequenceEqual(ascending[c]),"Ascending opportunity sort failed: "+c);
            var expected=c==6?new[]{1,2,0}:c==7?new[]{1,0,2}:ascending[c].Reverse().ToArray();
            Check(TableSorting.Opportunities(rows,c,true,r=>r.GetProperty("itemName").GetString()=="Alpha").SequenceEqual(expected),"Descending opportunity sort failed: "+c);
        }
        Check(rows[0].GetProperty("itemName").GetString()=="Zulu","Sorting mutated row identity.");
        var lots=new[]{new PurchaseLot(Guid.NewGuid(),1,1,1,false,1,10,100,0),new PurchaseLot(Guid.NewGuid(),2,1,2,true,2,2,500,0,1,600)};
        Check(TableSorting.Purchases(lots,6,true,x=>x.ItemId.ToString(),x=>x.ToString())[0].ItemId==2,"Journal profit sort failed.");
        Check(TableSorting.Purchases(lots,0,true,x=>x.ItemId.ToString(),x=>x.ToString())[0].PurchasedAt==2,"Journal date sort failed.");
        return checks;
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) => Task.FromResult(respond(request));
    }
}
