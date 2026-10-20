# 0.6.1

- Stream market responses with a 16 MiB size limit and a timeout covering body reads; reject oversized evidence without caching it.
- Clear sale reviews, entry fields and journal selection on logout/character changes, including while the plugin window is closed.
- Keep exports tied to their requesting character and suppress stale completion messages after a switch.
- Correct purchase-tracking documentation: enabled by default for new configurations.
- Add HTTP boundary/cancellation and cross-character journal/export regression checks; document publishing permissions and pending in-game verification.

Validation: 188 automated checks pass. Two-character in-game sale-confirmation and CSV-export checks remain unverified because a second character was unavailable.

# 0.6.0

- Click opportunity and purchase-journal column headings to sort in either direction.
- Save up to eight named watchlists of 200 item IDs.
- Discover candidates beyond the latest updates with rotating, category-filtered active-market sweeps.
- Balanced discovery combines watchlists, sales activity and recent updates.
- Keep the unchanged full demand model, whole-stack costs, history coverage checks and bounded journal.
- Retain ten-item batching, at most two requests and one prefetched batch.
- Add versioned packaging, checksum, custom update-feed generation and build validation.

Validation: 165 checks, including sorting in both directions, persistence bounds, candidate rotation, aggregate identity/freshness, model parity, cancellation, request limits, and journal reload/corruption recovery. Two 200-candidate live scans completed with explicit unavailable-item warnings. User confirmed the 200-item Balanced scan, Upfront sorting, cancellation, responsiveness and journal persistence after disable/re-enable.

Limitations: Universalis data remains sampled; upstream failures skip affected items. Watchlist UI persistence after an in-game reload and real purchase/sale event wiring need manual verification. Aggregate activity ranks candidates; it does not guarantee profitability.
