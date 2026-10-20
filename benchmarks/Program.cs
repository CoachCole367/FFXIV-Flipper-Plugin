using System.Diagnostics;
using System.Text.Json;
using FFXIVFlipper;

if(args.Contains("--live")) { await LiveBenchmark.Run(); return; }
// Fixed fixture replay. Transport is deliberately simulated, not a live API benchmark.
var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures.json"))).RootElement.GetProperty("cases").EnumerateArray().Where(x=>x.GetProperty("kind").GetString()=="scanner").ToArray();
const int count=50, repeats=3; int latencyMs=args.Contains("--model-only")?0:10;
var inputs=Enumerable.Range(0,count).Select(i=>fixtures[i%fixtures.Length].GetProperty("input").GetRawText()).ToArray();
var expected=new string[count];
for(int i=0;i<count;i++) { using var e=new BenchmarkEngine(default,false,false); expected[i]=JsonDocument.Parse(e.Run(inputs[i])).RootElement.GetProperty("rows").GetRawText(); }
var results=new List<object>();
foreach(var fixture in JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures.json"))).RootElement.GetProperty("cases").EnumerateArray()) {
 using var original=new BenchmarkEngine(default,false,false);
 using var optimized=new BenchmarkEngine(default,true,false);
 var input=fixture.GetProperty("input").GetRawText();
 string Run(BenchmarkEngine e)=>fixture.GetProperty("kind").GetString() switch { "scanner"=>e.Run(input), "demand"=>e.Demand(input), "guide"=>e.Guide(input), _=>throw new Exception("Unknown fixture") };
 if(Run(original)!=Run(optimized)) throw new Exception("Prepared-module fixture mismatch");
}
Console.WriteLine("All 25 prepared-module fixture outputs match baseline exactly.");
// Warm both paths before timed samples; cold preparation is reported separately.
var cold=Stopwatch.StartNew(); using(var warm=new BenchmarkEngine(default,true,true)) warm.Run(inputs[0]); cold.Stop();
for(int mask=0;mask<16;mask++) {
 if(args.Contains("--model-only") && (mask&5)!=0) continue;
 bool batch=(mask&1)!=0, prepared=(mask&2)!=0,pipeline=(mask&4)!=0,skip=(mask&8)!=0;
 var times=new List<double>(); var allocations=new List<long>(); int requests=0;
 for(int repeat=0;repeat<repeats;repeat++) {
 GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
 long before=GC.GetTotalAllocatedBytes(true); var watch=Stopwatch.StartNew();
 using var slots=new SemaphoreSlim(2);
 int calls=0;
 async Task Request() { await slots.WaitAsync(); try { Interlocked.Increment(ref calls); await Task.Delay(latencyMs); } finally { slots.Release(); } }
 async Task Fetch(int n) { // Same three endpoint families, with at most two requests active.
   await Task.WhenAll(Request(),Request()); await Request();
 }
 var rows=new List<JsonElement>();
 void Calculate(int start,int n) { for(int i=start;i<start+n;i++) { using var engine=new BenchmarkEngine(default,prepared,skip); using var doc=JsonDocument.Parse(engine.Run(inputs[i])); var actual=doc.RootElement.GetProperty("rows"); if(actual.GetRawText()!=expected[i]) throw new Exception($"Row parity failed: {mask}/{i}"); rows.AddRange(actual.EnumerateArray().Select(x=>x.Clone())); } }
 int size=batch?10:1;
 Task? next=null;
 for(int i=0;i<count;i+=size) {
   int n=Math.Min(size,count-i);
   await (next??Fetch(n));
   next=pipeline && i+n<count?Fetch(Math.Min(size,count-i-n)):null;
   Calculate(i,n);
 }
 using(var engine=new BenchmarkEngine(default,prepared,skip)) {
  var basket=engine.Basket(JsonSerializer.Serialize(new{rows,budgetGil=1000000}));
  // Every variant must return the identical final basket.
  if(mask==0 && repeat==0) File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"baseline-basket.json"),basket);
  if(basket!=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"baseline-basket.json"))) throw new Exception("Basket parity failed");
 }
 watch.Stop(); times.Add(watch.Elapsed.TotalMilliseconds); allocations.Add(GC.GetTotalAllocatedBytes(true)-before); requests=calls+1;
 }
 var record=new {mask,batch,prepared,pipeline,skipPerItemBasket=skip,items=count,requests,medianMs=times.Order().ElementAt(1),allocatedMb=allocations.Order().ElementAt(1)/1e6};
 results.Add(record); Console.WriteLine(JsonSerializer.Serialize(record));
}
File.WriteAllText(Path.Combine(AppContext.BaseDirectory,args.Contains("--model-only")?"model-results.json":"results.json"),JsonSerializer.Serialize(new{count,repeats,latencyMs,coldPreparedMs=cold.Elapsed.TotalMilliseconds,fixtureCount=fixtures.Length,results},new JsonSerializerOptions{WriteIndented=true}));
