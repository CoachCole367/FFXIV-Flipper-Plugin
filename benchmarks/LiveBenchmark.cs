using System.Diagnostics;
using System.Text.Json;
using System.Net;
public static class LiveBenchmark {
 public static async Task Run() {
  using var client=new HttpClient { BaseAddress=new Uri("https://universalis.app/api/v2/"),Timeout=TimeSpan.FromSeconds(60)};
  client.DefaultRequestHeaders.UserAgent.ParseAdd("FFXIVFlipper-Benchmark/0.5.2");
  using var candidates=JsonDocument.Parse(await client.GetStringAsync("extra/stats/most-recently-updated?dcName=Primal&entries=10"));
  var ids=candidates.RootElement.GetProperty("items").EnumerateArray().Select(x=>x.GetProperty("itemID").GetUInt32()).Distinct().ToArray();
  var until=DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var results=new List<object>();
  using var slots=new SemaphoreSlim(2);
  for(int round=0;round<3;round++) foreach(bool batch in (round%2==0?new[]{false,true}:new[]{true,false})) {
   int calls=0; long bytes=0; var watch=Stopwatch.StartNew();
   async Task Get(string path,uint[] requested) {
    await slots.WaitAsync();
    try {
     using var response=await client.GetAsync(path);
     if(response.StatusCode==HttpStatusCode.TooManyRequests) throw new Exception("Rate limited; benchmark stopped.");
     response.EnsureSuccessStatusCode(); var body=await response.Content.ReadAsByteArrayAsync(); Interlocked.Increment(ref calls); Interlocked.Add(ref bytes,body.Length);
     using var doc=JsonDocument.Parse(body);
     if(requested.Length>1) {
      var items=doc.RootElement.GetProperty("items");
      foreach(var id in requested) if(!items.TryGetProperty(id.ToString(),out var item)||item.GetProperty("itemID").GetUInt32()!=id) throw new Exception("Missing/mismatched batch item "+id);
     } else if(doc.RootElement.GetProperty("itemID").GetUInt32()!=requested[0]) throw new Exception("Mismatched item");
    } finally { slots.Release(); }
   }
   foreach(var chunk in ids.Chunk(batch?10:1)) {
    var key=string.Join(",",chunk);
    await Task.WhenAll(
     Get($"Primal/{key}?listings=1000&entries=0",chunk),
     Get($"64/{key}?listings=1000&entries=0",chunk));
    await Get($"history/64/{key}?entriesToReturn=2000&entriesWithin=604800&entriesUntil={until}",chunk);
   }
   watch.Stop(); var result=new {round,batch,items=ids.Length,requests=calls,ms=watch.Elapsed.TotalMilliseconds,bytes}; results.Add(result); Console.WriteLine(JsonSerializer.Serialize(result));
  }
  File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"live-results.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
 }
}
