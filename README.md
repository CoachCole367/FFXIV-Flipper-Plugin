# FFXIV Flipper Plugin

Standalone C# Dalamud plugin. The original webpage is preserved in its own project. No HTML, CSS, browser, local website server or Node runtime is needed to use this plugin.

## Use it in game

Open `/fflipper` and choose **Find profitable flips**:

1. Choose your home / sell world (automatically filled from your character when no world is saved).
2. Choose the buy data center. Available DCs are in your home world's region; travel restrictions may still apply.
3. Enter available gil, number of items (default 50, maximum 200) and minimum expected gain per trade (default 10,000 gil).
4. Click **Scan for profitable flips**. Automatic discovery uses recently updated Universalis items; no item search is required.
5. Compare opportunities in the table, sorted by total expected gain. Click an item name to select its row and copy the raw localized name to the system clipboard (without the HQ/NQ suffix). Paste it into market-board search with Ctrl+V. The **Sell / unit** column shows the suggested home-world listing price before seller tax; the details panel contains exact full stacks and market evidence. The Basket column marks trades selected within the combined budget.

The list shows trades meeting your total expected-gain floor. The default 10,000-gil floor is applied before basket selection, keeping tiny trades out of both results and basket. This is separate from the advanced margin-per-planned-unit setting. You can lower the total floor explicitly. The underlying browser demand model is unchanged. Only the suggested basket is selected to fit the combined budget. An empty result means no plan passed the model and filters in this bounded sample. Skipped items and API failures are shown separately. Cancel and changed settings discard old results.

The plugin has two tabs: **Find profitable flips** and **Purchase journal**. Item names in both tables copy to the clipboard; paste into market-board search with Ctrl+V. The standalone Single item tool and selected-item scan option have been removed. Scanner sell-price estimates remain in the **Sell / unit** column and selected-trade details. Direct opening/searching in the game market-board UI is not implemented.

## Lightweight purchase journal

Open **Purchase journal** in `/fflipper`. Automatic purchase tracking is enabled by default and can be switched off. Only future successful market-board purchases made while the plugin is active can be captured. NPC purchases, trades, past purchases and offline activity are not imported.

The plugin matches Dalamud's outgoing purchase details to the server purchase event, checking item, quantity, character, world and a 30-second time window. It preserves HQ/NQ, actual listed unit price and purchase tax. Requests alone, mismatched/expired events and duplicate listings do not become purchases. Ambiguous overlapping requests are skipped rather than guessed. No inventory polling, market requests or forecast snapshots are needed for the journal. The event pair still needs verification with a real in-game purchase on this client.

Sales are **manual actual-sale updates** against a purchase: select the lot, enter quantity sold and total gil received after seller tax, review, then confirm. Partial sales accumulate on that lot rather than creating an unbounded event history. Realized profit subtracts only the matched portion of acquisition cost; net cash recovery subtracts the full purchase. These are distinct, and retained unsold inventory is never counted as profit. There is no automatic retainer-sale detection or model learning from these records yet.

Size and resource bounds:

- One minified `purchase-journal.json` in the plugin configuration folder: newest **1,000 purchases globally across characters**, with a **90-day age limit** checked on load/write, and a **512 KB hard file limit**. Older lots, including their summarized sale information, are removed. Totals cover retained records, not lifetime earnings or a permanent inventory ledger.
- Only IDs and numeric trade details are stored; item/world names resolve from local game data. Views and exports show the current character only. Local character IDs never go to Universalis or GitHub.
- Event-triggered background saves coalesce changes. Writes use one fixed temporary filename and replace the prior journal; no numbered backups or growing append log. Saves flush when the plugin unloads.
- **Export CSV** replaces `purchase-journal.csv` in the same folder. Each journal/export is bounded to 512 KB; the fixed temporary files are bounded too, so at most four managed files exist (2 MB combined upper bound including interrupted-write remnants). Any copies you make elsewhere are outside this retention policy.
- Journal UI shows 50 rows per page and keeps at most 1,000 cached item names. In the synthetic capacity test, 1,000 purchase records occupied **177,964 bytes**, with ingestion and final save completing in about 64 ms. This is a synthetic local result, not an in-game performance measurement.

Storage errors are displayed. A malformed, oversized or unsupported existing journal is preserved and is not silently overwritten. Recording stays read-only until that file is repaired or deliberately removed outside the plugin. The repository ignores journal/export filenames; no personal trade data is part of the source repository.

## Model and limits

Version 0.5.2 embeds the website's unchanged `scanner-strategy.js`, `scanner-planner.js` and `market-data.js` calculation modules using Jint 4.17.0. The two small helper exports are extracted from `api.js` and `scanner-history.js`. Only import names are normalized for the in-memory module loader. The structuredClone compatibility function handles the planner's JSON-compatible purchase-plan data. There is no script download, CLR exposure, website dependency or external JavaScript execution service.

The same demand distributions, recency/dispersion models, price-ladder competition, confidence taper, exit-price planning, full-stack purchasing, expected cash recovery, ranking and basket selection run off the game thread. New scans start without learned personal calibration, as does a fresh browser installation. Saved calibration/history learning, Smart Auto profile selection, crafting and vendor scanning are not ported yet.

Data is a bounded supply sample of up to 1,000 cheapest listings across the selected buy DC, plus a separate home-world listing request and up to 2,000 home-world sales in an explicit seven-day window. All DC supply is labeled sampled. Both HQ/NQ are evaluated separately. Capped, failed or malformed home history is skipped entirely. Stale or unknown-age sources are rejected by the browser planner's 24-hour freshness policy; purchased listing review times are checked too. Uploaded history is not guaranteed complete coverage of real trades.

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

- Clean release build; 31 market regressions plus three embedded-source hash checks and 39 journal checks (117 checks overall).
- 25 frozen browser parity fixtures compare numerical model output, plans, budgets, exact listing identifiers and quality; includes positive NQ/HQ and rejection cases.
- Nineteen scanner integration checks exercise automatic discovery, seller-world history, full-stack cost, budget, sampled supply, cancellation, missing history, wrong-DC rejection, timeout and rate-limit handling, total-gain thresholds, ordering, missing-value rejection and basket exclusion.
- Live scans exercised current API data. The final ten-item run (nine discovered items plus a known heavier calculation case) completed with one qualifying plan and no errors or skipped items. These are snapshot observations, not future profit guarantees.
- The user verified 0.3.0 scanning and dropdowns in game. The purchase event wiring, journal interactions, clipboard shortcut and new sell-price column need in-game verification. The scanner layout replaces the long form with compact controls, an opaque window, a comparison table, budget summary and one selected-trade detail panel; the two-tab interface still requires visual/interaction verification in game.

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

- Verify the redesigned 0.4.0 UI in a running game; profile in-game responsiveness.
- Add saved watchlists and compatible learning/calibration; optional retainer-sale automation remains separate from the bounded purchase journal.
- Expand candidate discovery and request batching/deduplication/retry handling.
- Port crafting/vendor tools if desired.
- Publish a tested release and installation feed.

Jint and Acornima license notices are in `licenses/`. Sources: https://dalamud.dev/plugin-development/ and https://docs.universalis.app/ . Official Dalamud repository submissions require human review and compliance with its AI usage policy; this project is not represented as already approved.






