# Scanning and purchase journal

[Back to the README](../README.md)

## Choosing items to scan

The scanner evaluates HQ and NQ separately. Each scan checks at most 200 candidates in detail.

| Source | Items it checks |
| --- | --- |
| Balanced discovery | Interleaves an active-market sweep and recent buy-DC updates. |
| Active market sweep | Screens the next 1,000 marketable catalog IDs, then checks the most active candidates on your home world. |
| Recently updated | Checks the latest buy-DC items returned by Universalis. |

Active sweeps start with newer catalog IDs and wrap around. Candidates need an observed home-world sale within three days; ranking uses home-world daily traded gil. The saved position advances when a scan starts, including canceled or failed scans. Changing category resets it. The category applies only to the active-market portion of Balanced discovery.

Aggregate activity only selects candidates. Every recommendation still requires the original full demand model, raw home-world history, freshness checks and whole-stack purchase planning. No activity or missing evidence is not a forecast of zero demand.

The compact scanner keeps sell world, buy data center, available gil and minimum gain visible. Search mode, category and remaining filters are under the collapsed **Scan settings** section. Watchlist management, save buttons and watchlist-only scanning are temporarily paused. Saved lists remain in configuration but are excluded from scans; an old watchlist-only selection falls back to Balanced discovery.

## Reading results

Expected gain is the default table order. Click any column heading to sort or reverse it. Unknown selling times stay last. Sorting preserves the selected trade and basket; journal sorting covers all retained records before pagination.

The default minimum expected gain is 10,000 gil per trade. It is applied before basket selection and is separate from the minimum margin per planned unit. Only the suggested basket fits the combined budget; all displayed trades may not be affordable together.

All units in purchased stacks count toward upfront cost, even when fewer are expected to sell. Profit assumes 5% seller tax and excludes buyer tax and travel costs. Click an item name to copy its localized market-board search name without the HQ/NQ suffix. Direct market-board opening, buying, travel and repricing are not automated.

The supply sample contains up to 1,000 cheapest buy-DC listings per item, separate home-world listings, and up to 2,000 home-world sales in a seven-day window. DC supply is marked sampled. Capped, failed or malformed home history is excluded. The preserved planner rejects stale/unknown-age sources and checks listing review times. Uploaded history may not cover all real trades.

The summary separates items attempted, successfully analyzed and skipped. Successful analysis can still produce no supported trade; skipped items never count as analyzed. Temporary HTTP 502/503/504 errors or request timeouts receive one retry after a 1–1.5 second pause. Each attempt has a 25-second timeout; a failed request can therefore take about 51.5 seconds in total. Cancellation interrupts the pause, and rate limits, oversized responses and invalid data are not retried. A rate-limit stop preserves completed opportunities with a warning. Canceling or changing settings discards old results. An empty result means no supported plan passed the filters in this sample.

## Purchase journal

Purchase tracking is on by default for new configurations. The **Automatically log market-board purchases** checkbox preserves your saved choice. Only confirmed market-board purchases made while tracking is enabled can be recorded. NPC purchases, trades, past purchases and offline activity are not imported. Requests are matched to confirmations by item, quality, quantity, character, world and a 30-second window. Ambiguous or duplicate events are skipped.

Sales are manual: select a purchase, enter units sold and total gil received after seller tax, review, and confirm. Partial sales accumulate on that purchase. Realized profit subtracts the acquisition cost of sold units; net cash recovery subtracts the entire purchase cost. Unsold stock is not counted as realized profit. Automatic retainer-sale detection and model learning are not implemented.

### Storage and recovery

- Keeps the newest 1,000 purchases across characters, for up to 90 days, in a file capped at 512 KB. Expired or excess lots and their sale summaries are removed on load/write.
- Stores IDs and numeric trade details locally. Current-character views and exports do not expose other characters' records; character IDs are never sent to Universalis or GitHub.
- Saves are event-triggered and coalesced, with one fixed temporary file; no polling, numbered backups or growing logs. Pending saves flush on unload.
- Export CSV replaces the same purchase-journal.csv file. Journal, export and their fixed temporary files total at most four managed files, each capped at 512 KB. Copies you make elsewhere are outside retention limits.
- An export already running finishes for the character who requested it, even if you log out or switch characters. Export again on the new character to replace that CSV. Pending sale confirmations and entry fields clear on character changes; a reviewed sale must belong to the current character.
- Totals cover retained records, not lifetime earnings or a permanent inventory ledger.

Storage errors are displayed. A malformed, oversized or unsupported journal is preserved and recording becomes read-only; the plugin does not overwrite it.

Development and installed builds use the same FFXIVFlipper configuration identity and journal folder. Back up the journal, disable/remove the dev location, and install the repository version without deleting configuration. Disable/re-enable persistence is verified; the exact dev-to-release migration has not been manually verified.
