using System.Reflection;
using Jint;
namespace FFXIVFlipper;

// Each scan owns its interpreter and runs on a background task. Only embedded calculation code is loaded.
public sealed class PlannerEngine : IDisposable
{
    private readonly Engine engine;
    public PlannerEngine(CancellationToken token)
    {
        engine = new Engine(options => options.CancellationToken(token).TimeoutInterval(TimeSpan.FromSeconds(20))
            .MaxStatements(15000000).LimitMemory(256_000_000));
        engine.Execute("globalThis.structuredClone = x => JSON.parse(JSON.stringify(x));");
        var assembly = typeof(PlannerEngine).Assembly;
        foreach (var name in new[] { "api.js", "scanner-history.js", "scanner-strategy.js", "market-data.js", "scanner-planner.js" })
        {
            var resource = assembly.GetManifestResourceNames().Single(x => x.EndsWith(".Model." + name, StringComparison.Ordinal));
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            engine.Modules.Add(name, reader.ReadToEnd().Replace("from \"./", "from \"", StringComparison.Ordinal));
        }
        engine.Modules.Add("runner", """
            import { buildScannerOpportunitiesForSettings } from 'scanner-planner.js';
            import { buildScannerBasket, buildDemandModel, buildSellPriceGuide } from 'scanner-strategy.js';
            export function run(json) {
              const input = JSON.parse(json);
              const rows = buildScannerOpportunitiesForSettings(input);
              const basket = buildScannerBasket(rows, { budgetGil: input.budgetGil });
              return JSON.stringify({ rows, basket });
            }
            export function basket(json) { const a = JSON.parse(json); return JSON.stringify(buildScannerBasket(a.rows, {budgetGil:a.budgetGil})); }
            export function demand(json) { const a = JSON.parse(json); return JSON.stringify(buildDemandModel(a.entries, a.options)); }
            export function guide(json) { return JSON.stringify(buildSellPriceGuide(JSON.parse(json))); }
            """);
        var module = engine.Modules.Import("runner");
        engine.SetValue("runPlanner", module.Get("run"));
        engine.SetValue("runDemand", module.Get("demand"));
        engine.SetValue("runGuide", module.Get("guide"));
        engine.SetValue("runBasket", module.Get("basket"));
    }
    public string Run(string json) => engine.Invoke("runPlanner", json).AsString();
    public string Basket(string json) => engine.Invoke("runBasket", json).AsString();
    public string Demand(string json) => engine.Invoke("runDemand", json).AsString();
    public string Guide(string json) => engine.Invoke("runGuide", json).AsString();
    public void Dispose() => engine.Dispose();
}



