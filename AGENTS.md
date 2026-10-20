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
