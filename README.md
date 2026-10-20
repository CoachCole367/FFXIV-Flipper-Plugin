# FFXIV Flipper

Find cross-world flips, check suggested sell prices, and track purchases in game.

## Installation

Requires **XIVLauncher with Dalamud enabled**.

1. In game, run `/xlsettings`.
2. Open **Experimental → Custom Plugin Repositories**.
3. Add this URL, enable its checkbox, and save:

   ```text
   https://raw.githubusercontent.com/CoachCole367/FFXIV-Flipper-Plugin/main/repo.json
   ```

4. Run `/xlplugins`, search for **FFXIV Flipper**, and install it.
5. Run `/fflipper` to open the plugin.

Install future updates through the Dalamud plugin installer. If you previously used a development build, disable its dev plugin entry before installing; keep your plugin configuration to retain the journal.

## Typical daily use

1. Open `/fflipper`. Choose your sell world, buy data center, available gil, and minimum expected gain.
2. Click **Scan for flips**. Open **Scan settings** to change search mode, categories, item count, or filters. Watchlists are temporarily paused; saved lists are kept.
3. Review expected gain, upfront cost, and selling time. Click column headings to sort and select a trade to see the exact stacks to buy. Only the suggested basket fits the combined budget.
4. Click an item name to copy it for a market-board search. Verify the listings in game, then travel and buy manually. **Sell / unit** is the suggested listing price on your sell world.
5. Purchase tracking is **on by default**; check **Automatically log market-board purchases** in **Purchase journal** before buying if you previously disabled it. After selling, select the purchase and enter the units sold and total gil received after seller tax. Sales must be recorded manually.

Prices are Universalis snapshots and can change. Estimates assume 5% seller tax and exclude buyer tax and travel costs. Purchases, travel, and repricing are manual. Journal records stay local and retain up to 1,000 purchases for 90 days.
