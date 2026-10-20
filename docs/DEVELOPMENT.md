# Development

[Back to the README](../README.md)

## Build and test

Requires .NET 10 SDK and compatible Dalamud API 15 references. Set DALAMUD_HOME for a custom reference installation.

~~~powershell
dotnet build FFXIVFlipper/FFXIVFlipper.csproj -c Release
dotnet run --project tests/Economics.Tests.csproj -c Release
~~~

No separate lint tool is configured. Add the built FFXIVFlipper.dll under Dalamud Settings → Experimental → Dev Plugin Locations. Keep its manifest/dependency files, Jint.dll, Acornima.dll and licenses beside it. Add only the plugin DLL as a dev location. Rebuilds normally reload automatically.

The original webpage remains in its separate project. No website server, browser or Node runtime is required to run the plugin.

## Model and resource limits

Jint 4.17.0 runs embedded calculation-only module snapshots from the browser app. Source hashes and 25 browser parity fixtures preserve the model; .gitattributes prevents Windows checkout line-ending conversion. Import names are normalized for the in-memory loader. There is no downloaded script execution or CLR exposure.

The preserved model includes demand distributions, recency/dispersion, price-ladder competition, confidence taper, exit-price planning, full-stack costs, ranking and baskets. Personal calibration/history learning, Smart Auto profiles, crafting and vendor scanning are not ported.

Network/model work stays off the game thread. At most two HTTP requests run concurrently, with ten-item detail batches and one prefetched batch. Only fixed prepared modules are shared; every item gets an isolated interpreter with cancellation, time, statement and allocation limits. Batch and aggregate payloads are not retained across scans. Single-item listing requests have a bounded 30-second cache. HTTP 429 establishes a cooldown.

See the [benchmark report](../benchmarks/README.md) for measurements and limitations. Its benchmark engine is a reference snapshot of version 0.5.2.

Regenerate fixtures only when deliberately updating the calculation snapshots:

~~~powershell
node tools/generate-parity.mjs "path/to/separate/website"
~~~

The generator reads the webpage source and writes plugin fixtures; it does not edit the webpage.

## Validation

Version 0.6.0 passes 165 checks: 31 market, 3 source-hash, 25 browser parity, 19 scanner, 14 batching/pipeline, 34 discovery/sorting and 39 journal checks. Coverage includes cancellation, identity/quality, incomplete history, budgets, sorting, bounded persistence and journal reload/corruption/write failures. The tagged commit's GitHub build passed.

Two live runs each screened 1,000 IDs and evaluated 200 candidates. Balanced found 35 trades in 31.44s with 10 warnings; a different active-market slice found 8 in 27.67s with 21 warnings. Upstream/network failures explicitly excluded affected items. Different candidate sets and snapshot results are not a source-quality comparison or a profit guarantee.

The user verified a 200-item Balanced scan, both Upfront sort directions, restart/cancel, responsiveness and journal persistence after disable/re-enable. In-game watchlist persistence, real purchase/sale events and a long-duration memory soak remain manual checks.

Optional live checks:

~~~powershell
dotnet run --project tests/Economics.Tests.csproj -c Release -- --live
dotnet run --project tests/Economics.Tests.csproj -c Release -- --scan-live
dotnet run --project tests/Economics.Tests.csproj -c Release -- --scan-live-wide
dotnet run --project tests/Economics.Tests.csproj -c Release -- --discovery-live
~~~

## Release

~~~powershell
./tools/package-release.ps1
~~~

This tests, builds and stages a versioned ZIP, SHA-256 checksum and repo.json under dist/. Use -PublishRepository owner/name for different hosting. The ZIP allowlist contains only plugin/dependency DLLs, metadata and license notices. Config, journals, exports, raw market data, fixtures and website files are excluded.

The GitHub workflow builds a release-candidate artifact; it does not auto-publish. Tag the matching commit, publish the ZIP/checksum as GitHub release assets, then commit the generated repo.json at the repository root. Verify unauthenticated feed/download access and the downloaded ZIP's checksum before announcing it. The script itself does not publish or change visibility.

The custom installation/update feed is linked in the README. Disable the dev entry before installing a release to avoid two loaded copies. Do not delete plugin configuration during migration.

Jint/Acornima notices are in [licenses](../licenses). See [Universalis documentation](https://docs.universalis.app/) and [Dalamud custom repositories](https://dalamud.dev/plugin-publishing/custom-repositories/). This custom release is not an official Dalamud repository submission.

## Publishing privacy

Use a GitHub-provided noreply address for Git author and committer emails before committing. Existing public commit metadata requires a coordinated history rewrite to remove. GitHub ownership and required installation/update URLs remain public.

Local environment files and private key files are ignored. Never place credentials in tracked files. Release packages exclude journals, exports, configuration, debug symbols and raw market data; compiler source references use a neutral path. Keep third-party copyright notices intact.
