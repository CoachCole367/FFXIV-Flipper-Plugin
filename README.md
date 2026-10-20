# FFXIV Flipper Plugin

Standalone Dalamud plugin. This repository contains no website and has no runtime dependency on the browser project.

## Current status

Early development, not a complete port and not verified in a running game. Includes `/fflipper`, local item search, HQ/NQ market comparisons from Universalis, saved world settings, cancellation, and a manual whole-stack cash-gain scenario. No automated purchases or repricing.

## Build and test

Requires .NET 10 SDK and a compatible Dalamud API 15 installation. The SDK resolves the usual XIVLauncher installation; set `DALAMUD_HOME` for a custom installation.

```
dotnet build FFXIVFlipper/FFXIVFlipper.csproj -c Release
dotnet run --project tests/Economics.Tests.csproj
```

Add the built `FFXIVFlipper.dll` under Dalamud Settings > Experimental > Dev Plugin Locations, then enable the development plugin and use `/fflipper`. This is a development installation, not an official plugin repository release.

## Port checklist

- [x] Separate plugin project and native window
- [x] Single-item listing comparison and manual stack scenario
- [ ] Full demand model and sell-price guidance, with parity fixtures from the browser app
- [ ] Crafting profit and whole-stack ingredient purchases
- [ ] Cross-world candidate scanner, budgets and basket planning
- [ ] Vendor shop catalog and flip finder
- [ ] Watchlists, trade journal, history and compatible data migration
- [ ] Shared rate limits, retry cooldowns, freshness and coverage gates
- [ ] In-game performance and lifecycle validation
- [ ] Release packaging and installation feed

Market samples can be incomplete or stale. Missing evidence must never become a forecast. Current scenarios assume a 5% seller tax and exclude buyer tax and travel costs. Estimates are not guaranteed executed profits.

Development references: https://dalamud.dev/plugin-development/ and https://docs.universalis.app/ . Official Dalamud repository submissions require human review and compliance with its AI usage policy; this project must not be represented as already approved.
