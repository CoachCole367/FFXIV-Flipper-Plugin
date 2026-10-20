# FFXIV Flipper Plugin
- Stack: C#, .NET 10, Dalamud API 15; Jint 4.17.0 runs embedded calculation-only ES modules. Universalis data and Lumina local catalogs.
- Build: `dotnet build FFXIVFlipper/FFXIVFlipper.csproj -c Release`.
- Tests: `dotnet run --project tests/Economics.Tests.csproj`. No separate lint tool.
- Optional live checks: append `-- --live` or `-- --scan-live`. Fixtures regenerate with `node tools/generate-parity.mjs "path/to/separate/website"`.
- Keep the website in its existing separate repository. Never copy HTML/CSS/browser UI here. Domain calculation modules are deliberately shared snapshots.
- Preserve the model exactly; retain source hashes and browser parity fixtures. Do not silently rewrite embedded algorithms. No downloaded scripts or CLR exposure.
- Keep UI/game-sheet reads on the game thread; network/model work runs in background tasks. Cancel and invalidate on changed inputs and disposal.
- Preserve quality/world/freshness/coverage distinctions. Missing evidence is not a forecast. Charge whole purchased stacks and full upfront capital.
- Definition of done: build, market/parity/scanner regressions, and in-game flows tested or explicitly unverified. Keep README accurate and dependency license notices packaged.

- Purchase journal: 1,000 lots / 90 days / 512 KB; no snapshots, polling or growing backups. Log only matched confirmed purchases; sales stay actual manual entries. Keep character IDs and journal/export data local.

- Scanner performance: batches of at most ten, max two HTTP requests, one prefetched batch, fresh interpreter per item. Cache only fixed prepared code; never retain batch payloads across scans. Multi-batch live check: dotnet run --project tests/Economics.Tests.csproj -c Release -- --scan-live-wide.

- Discovery aggregates only select candidates; full history/model still decides trades. Eight watchlists max, 200 IDs each; no persisted aggregate payloads. Full live validation: dotnet run --project tests/Economics.Tests.csproj -c Release -- --discovery-live. Release staging: tools/package-release.ps1; never claim feed URLs work until public downloads are verified.
