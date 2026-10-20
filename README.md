# FFXIV Flipper Plugin

Standalone Dalamud plugin. This repository contains no website and has no runtime dependency on the browser project.

## Current status

Version 0.2.0 adds seven-day sell-world sales and guarded price references to `/fflipper`. Includes local item search, HQ/NQ comparisons, saved world settings, cancellation, basic request throttling/cache, and whole-stack cash-gain scenarios. This is an early port, not the full browser application.

The user verified the original window, item search and live comparison in game. The 0.2.0 rebuild reloaded successfully according to Dalamud logs; the new guidance UI still needs visual in-game verification. Release build passes with no warnings or errors. All 31 automated regressions pass. A live Universalis check for world 53 / item 10373 returned 94 listings and 313 sales.

## Build and test

Requires .NET 10 SDK and a compatible Dalamud API 15 installation. The SDK resolves the usual XIVLauncher installation; set `DALAMUD_HOME` for a custom installation.

```
dotnet build FFXIVFlipper/FFXIVFlipper.csproj -c Release
dotnet run --project tests/Economics.Tests.csproj
```

Optional live integration check (uses Universalis):

```
dotnet run --project tests/Economics.Tests.csproj -- --live
```

Add the built `FFXIVFlipper.dll` under Dalamud Settings > Experimental > Dev Plugin Locations, then enable the development plugin and use `/fflipper`. Rebuilds can trigger automatic reload; if necessary disable and enable this development plugin in `/xlplugins`. This is a development installation, not an official plugin repository release.

## Using the analysis

Select a marketable item, individual buy and sell worlds, and HQ/NQ. Load the comparison. World names resolve locally to numeric IDs; DC-wide history is never substituted for seller-world evidence. The plugin shows up to 100 uploaded listings per world and up to 2,000 uploaded sales in an explicit seven-day window on the sell world.

A reference price requires listing data no older than six hours, uncapped valid history, at least three matching-quality sales, a matching sale in the last three days, and at least three sales within 10% of the reference. The reference is the lower of the volume-weighted sale median and cheapest matching listing. Historical units/day near that price use the full seven-day denominator. These describe uploaded observations, not guaranteed actual market coverage or a forecast of your sales. Missing, capped, malformed, stale or unsupported evidence produces no guidance.

Competing units are a lower bound from sampled listings. There is no queue-clearance forecast or probability model yet. Use the reference in the manual scenario if desired; units sold remain your input. All purchased stack units count toward upfront cost and cash gain, including unsold leftovers. Seller tax is assumed at 5%; buyer tax and travel costs are excluded. Break-even assumes the entire stack sells.

Listing responses are cached for 30 seconds with at most 64 entries. At most two requests run at once; HTTP 429 establishes a shared cooldown. History requests are uncached with an explicit end time. Canceled results are invalidated, and history failure leaves listing comparisons available while withholding guidance.

## Port checklist

- [x] Separate plugin project and native window
- [x] Single-item listing comparison and manual stack scenario
- [x] Seven-day sales, quality isolation, basic price reference and evidence gates
- [x] Basic concurrency cap, listing cache and rate-limit cooldown
- [ ] Full demand model and sell-price guidance, with parity fixtures from the browser app
- [ ] Crafting profit and whole-stack ingredient purchases
- [ ] Cross-world candidate scanner, budgets and basket planning
- [ ] Vendor shop catalog and flip finder
- [ ] Watchlists, trade journal, history and compatible data migration
- [ ] Shared request deduplication, retry policy and complete listing coverage
- [ ] New guidance UI, in-game performance and lifecycle validation
- [ ] Release packaging and installation feed

Development references: https://dalamud.dev/plugin-development/ and https://docs.universalis.app/ . Official Dalamud repository submissions require human review and compliance with its AI usage policy; this project must not be represented as already approved.
