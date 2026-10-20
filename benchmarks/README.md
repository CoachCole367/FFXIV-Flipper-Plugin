# Scanner performance benchmark — 2026-10-20

Recommendation: combine ten-item request batches, shared prepared modules with a fresh interpreter per item, and a bounded one-batch lookahead. Removing each item's unused basket is optional cleanup; it produced no meaningful additional speedup.

These are benchmark prototypes, not changes to the running plugin. The website and production plugin sources were not edited. No new dependencies were added.

## Measured results

Three repetitions; medians shown. Release build, .NET 10.0.101, Jint 4.17.0, user's Windows machine.

| Option | Controlled replay, 50 items | Total managed allocations |
| --- | ---: | ---: |
| Current behavior | 3.380 s | 849.1 MB |
| Batching only | 1.897 s | 849.0 MB |
| Prepared modules only | 2.893 s | 691.2 MB |
| Pipeline only | 2.242 s | 849.2 MB |
| Remove per-item basket only | 3.392 s | 843.0 MB |
| Batching + prepared modules | 1.497 s | 691.1 MB |
| Batching + pipeline | 1.808 s | 848.9 MB |
| Prepared modules + pipeline | 2.080 s | 691.2 MB |
| Batching + prepared modules + pipeline | **1.398 s** | 691.1 MB |
| All four | 1.435 s | **686.9 MB** |

All 16 combinations are in replay-results.json. The 36 ms difference between the final two configurations is too small to establish a dependable winner from three runs.

Calculation-only replay (no artificial waits): current 1.843 s; prepared modules 1.414 s; basket removal 1.825 s; both 1.413 s. Prepared modules reduced model time about 23% and allocated bytes about 19%. Basket removal alone saved about 1%, within normal timing variation. Allocations are cumulative temporary bytes, **not retained RAM or plugin disk size**.

Live Universalis transport comparison, ten newly discovered Primal candidates, Leviathan home world:

| Request strategy | Median | Observed range | Requests |
| --- | ---: | ---: | ---: |
| Individual items | 3.899 s | 3.087–4.232 s | 30 |
| One ten-item batch | **0.675 s** | 0.480–0.805 s | **3** |

Payloads were approximately 412–413 KB in both strategies. Ten-item response identities were validated for every requested item. Listings remained capped at 1,000 per item and history at 2,000 per item over seven days. Each run used one fixed history end time. Test order alternated individual/batch, batch/individual, individual/batch. At most two requests were active. No rate limits occurred. CDN caches, changing candidates and network conditions affect these numbers; an earlier different-world probe showed a smaller 3.4x transport gain. This is not a universal speed guarantee.

For 200 uncached items, the request-count arithmetic is 601 -> 61 including discovery, provided all three endpoint families can serve every batch without retries. A full live 200-item scan was not timed.

## Method and limits

- Controlled replay cycles through the 15 frozen scanner fixtures for 50 item calculations. Three samples per configuration. This includes positive NQ/HQ opportunities and rejection cases, but does not represent every large live market.
- HTTP is simulated in replay: fixed 10 ms per endpoint call, two concurrent requests, batches of ten, at most one prefetched batch. Batches intentionally have the same simulated latency as singles. Actual batch payload/network costs are measured separately in the live test. **The 2.4x combined replay gain is not a measured live full-scan speedup.**
- Each replay compares every item's complete row JSON and the final basket against the baseline. All 16 combinations passed. Separately, all 25 scanner/demand/guide fixtures matched the baseline exactly with prepared modules.
- The prepared-code cache contains only the six fixed embedded modules, including the runner. Every item still receives a fresh isolated interpreter. The prototype does not retain snapshots or market history.
- BenchmarkEngine.cs is an isolated snapshot of the 0.5.2 engine with benchmark switches. It must be refreshed if production model wiring changes.
- Live tests validate batch transport and item identities, not production batch parsing, incomplete-item fallback, cancellation or in-game responsiveness. Those need implementation and regression coverage before deployment.
- No long-duration retained-memory test or in-game benchmark was performed. These results do not establish a leak-free implementation.
- A preliminary attempt to build the live harness while the replay executable was running failed because Windows locked the executable. It was rerun successfully after replay completed; preliminary replay timings may include small build interference. The separate calculation-only pass had no overlapping build.

## Reproduce

From the plugin repository:

~~~
dotnet run --project benchmarks/Scanner.Benchmarks.csproj -c Release
dotnet run --project benchmarks/Scanner.Benchmarks.csproj -c Release -- --model-only
dotnet run --project benchmarks/Scanner.Benchmarks.csproj -c Release -- --live
~~~

Run sequentially. Live mode makes one discovery request and 99 market requests across three comparison rounds; it stops on rate limiting. No buyer/retainer names, journals or raw API bodies are saved. Numerical reports are written under bin/Release/net10.0; the three JSON files beside this README preserve this benchmark's results.

Implementation priority: batching first, prepared modules second, bounded pipelining third. Keep concurrency at two, process results in candidate order, preserve per-item failure/coverage checks, and release each batch after use. Re-test on captured heavy-market inputs before treating the combined gain as representative of in-game scans.
