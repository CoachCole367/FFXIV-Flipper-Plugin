# 0.6.0

- Click opportunity and purchase-journal column headings to sort in either direction.
- Save up to eight named watchlists of 200 item IDs.
- Discover candidates beyond the latest updates with rotating, category-filtered active-market sweeps.
- Balanced discovery combines watchlists, sales activity and recent updates.
- Keep the unchanged full demand model, whole-stack costs, history coverage checks and bounded journal.
- Retain ten-item batching, at most two requests and one prefetched batch.
- Add versioned packaging, checksum, custom update-feed generation and build validation.

Validation: 165 checks, including sorting in both directions, persistence bounds, candidate rotation, aggregate identity/freshness, model parity, cancellation, request limits, and journal reload/corruption recovery. Two 200-candidate live scans completed with explicit unavailable-item warnings. User confirmed the 200-item Balanced scan, Upfront sorting, cancellation and responsiveness in game.

Limitations: Universalis data remains sampled; upstream failures skip affected items. Watchlist UI persistence after an in-game reload and real purchase/sale event wiring need manual verification. Aggregate activity ranks candidates; it does not guarantee profitability.
