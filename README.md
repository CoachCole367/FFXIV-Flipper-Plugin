# FFXIV Flipper Plugin

Standalone C# Dalamud plugin. The original webpage is preserved in its own project. No HTML, CSS, browser, local website server or Node runtime is needed to use this plugin.

## Use it in game

Open `/fflipper` and choose **Find profitable flips**:

1. Choose your home / sell world (automatically filled from your character when no world is saved).
2. Choose the buy data center. Available DCs are in your home world's region; travel restrictions may still apply.
3. Enter available gil and the number of items to scan (default 50, maximum 200).
4. Click **Scan for profitable flips**. Automatic discovery uses recently updated Universalis items; no item search is required.
5. Review the suggested budget basket and expand opportunities for the buy world, exact full stacks, upfront capital, target sell price, expected cash gain, sale confidence, clear time, freshness and markdown scenario.

The list shows all qualifying opportunities. Only the suggested basket is selected to fit the combined budget. An empty result means no plan passed the model and filters in this bounded sample. Skipped items and API failures are shown separately. Cancel and changed settings discard old results.

Optional selected-item mode scans the item chosen in **Single item**. Item suggestions close after selection; world controls are real dropdowns. The Single item tab also provides the website's full sell-price tiers and manual whole-stack scenarios.

## Model and limits

Version 0.3.0 embeds the website's unchanged `scanner-strategy.js`, `scanner-planner.js` and `market-data.js` calculation modules using Jint 4.17.0. The two small helper exports are extracted from `api.js` and `scanner-history.js`. Only import names are normalized for the in-memory module loader. The structuredClone compatibility function handles the planner's JSON-compatible purchase-plan data. There is no script download, CLR exposure, website dependency or external JavaScript execution service.

The same demand distributions, recency/dispersion models, price-ladder competition, confidence taper, exit-price planning, full-stack purchasing, expected cash recovery, ranking and basket selection run off the game thread. New scans start without learned personal calibration, as does a fresh browser installation. Saved calibration/history learning, Smart Auto profile selection, crafting and vendor scanning are not ported yet.

Data is a bounded supply sample of up to 1,000 cheapest listings across the selected buy DC, plus a separate home-world listing request and up to 2,000 home-world sales in an explicit seven-day window. All DC supply is labeled sampled. Both HQ/NQ are evaluated separately. Capped, failed or malformed home history is skipped entirely. Stale or unknown-age sources are rejected by the browser planner's 24-hour freshness policy; purchased listing review times are checked too. Single-item sell-price guidance uses a stricter six-hour listing-age gate. Uploaded history is not guaranteed complete coverage of real trades.

All purchased stack units count toward upfront capital, even when fewer are forecast to sell. Profit assumes 5% seller tax and excludes buyer tax and travel costs. Verify exact listings and final cost in game. A price gap is not guaranteed realized profit. The acquisition sample may miss smaller stacks or other profitable items. No automated buying, traveling, listing or repricing is performed.

Network requests share a two-request concurrency cap, a bounded 30-second listing cache and HTTP 429 cooldown. Calculations have cancellation, time, statement and allocation limits; an item reaching a limit is skipped with a warning. Every item uses a separate interpreter, so its temporary model state is released independently. Request timeouts skip the item; a rate-limit stop returns completed opportunities with an explicit warning. Do not interpret a skipped calculation as unprofitable evidence.

## Build and install

Requires .NET 10 SDK and a compatible Dalamud API 15 installation. Set `DALAMUD_HOME` for a custom installation.

```
dotnet build FFXIVFlipper/FFXIVFlipper.csproj -c Release
dotnet run --project tests/Economics.Tests.csproj
```

Add the built `FFXIVFlipper.dll` under Dalamud Settings > Experimental > Dev Plugin Locations, then enable it. Keep `Jint.dll`, `Acornima.dll`, the plugin manifest/dependency files and license notices beside the plugin in the build output. Add only the plugin DLL as a dev location, not every dependency DLL. Rebuilds normally reload automatically; otherwise disable and re-enable the development plugin in `/xlplugins`.

Packaged output is under `FFXIVFlipper/bin/Release/FFXIVFlipper/latest.zip`. This is a development build, not an official Dalamud repository release.

## Validation

- Clean release build; 31 market regressions plus three embedded-source hash checks.
- 25 frozen browser parity fixtures compare numerical model output, plans, budgets, exact listing identifiers and quality; includes positive NQ/HQ and rejection cases.
- Eleven scanner integration checks exercise automatic discovery, seller-world history, full-stack cost, budget, sampled supply, cancellation, missing history, wrong-DC rejection, timeout and rate-limit handling.
- Live scans exercised current API data. The final ten-item run (nine discovered items plus a known heavier calculation case) completed with one qualifying plan and no errors or skipped items. These are snapshot observations, not future profit guarantees.
- The user verified the earlier comparison UI in game. New scanner layout, dropdowns and in-game scanning still require visual/interaction verification.

Regenerate browser reference fixtures only when deliberately updating the copied model, with Node available for development:

```
node tools/generate-parity.mjs "D:/Code/FFXIV Flipper"
```

The generator reads the separate webpage project and writes only this plugin's test fixtures. It does not edit the webpage. Fixtures retain source SHA-256 hashes to detect model drift.

Optional live API checks:

```
dotnet run --project tests/Economics.Tests.csproj -- --live
dotnet run --project tests/Economics.Tests.csproj -- --scan-live
```

## Remaining work

- Verify the new UI and a scan in a running game; profile in-game responsiveness.
- Add saved watchlists/trade history and compatible learning/calibration.
- Expand candidate discovery and request batching/deduplication/retry handling.
- Port crafting/vendor tools if desired.
- Publish a tested release and installation feed.

Jint and Acornima license notices are in `licenses/`. Sources: https://dalamud.dev/plugin-development/ and https://docs.universalis.app/ . Official Dalamud repository submissions require human review and compliance with its AI usage policy; this project is not represented as already approved.

