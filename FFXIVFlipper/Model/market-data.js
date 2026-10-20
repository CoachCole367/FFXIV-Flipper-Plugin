export const MODEL_VERSION = "2.0"
export const SCHEMA_VERSION = 2

const qualityCache = new WeakMap()
export function filterQuality(rows, quality) {
  if (!Array.isArray(rows)) return []
  let cache = qualityCache.get(rows)
  if (!cache) { cache = new Map(); qualityCache.set(rows, cache) }
  if (!cache.has(quality)) cache.set(quality, rows.filter(row => row.hq === (quality === "HQ")))
  return cache.get(quality)
}

export function historyCoverage(entries, options = {}) {
  const rows = Array.isArray(entries) ? entries : []
  const nowSec = Number.isFinite(options.nowSec) ? options.nowSec : Date.now() / 1000
  const days = options.lookbackDays > 0 ? options.lookbackDays : 7
  const cutoff = nowSec - days * 86400
  const timestamps = rows.map(row => row.timestampSec).filter(Number.isFinite)
  const oldestSec = timestamps.length ? Math.min(...timestamps) : null
  const available = options.available !== false && Array.isArray(entries)
  const capped = options.requestedCount > 0 && Math.max(rows.length, options.rawCount || 0) >= options.requestedCount
  const complete = available && !(options.invalidCount > 0) && (!capped || oldestSec <= cutoff)
  return { available, complete, sampleCount: rows.length, oldestSec,
    observedDays: complete ? days : Math.max(1 / 24, Math.min(days, (nowSec - (oldestSec || nowSec)) / 86400)),
    requestedDays: days, label: !available ? "Unavailable" : complete ? "Full requested window" : "Limited history sample" }
}

export function freshness(timestampSec, nowSec = Date.now() / 1000, maxAgeHours = 24) {
  const known = Number.isFinite(timestampSec) && timestampSec > 0
  const ageHours = known ? Math.max(0, (nowSec - timestampSec) / 3600) : null
  return { timestampSec: known ? timestampSec : null, ageHours,
    stale: !known || ageHours > maxAgeHours,
    label: !known ? "Age unknown" : ageHours > maxAgeHours ? "Stale" : "Recent" }
}

export function inventoryEconomics({ purchasedQty, expectedUnitsSold, totalCost, netSalePrice, markdownRatio = 0.2 }) {
  const bought = Math.max(0, purchasedQty || 0)
  const sold = Math.max(0, Math.min(bought, expectedUnitsSold || 0))
  const cost = Math.max(0, totalCost || 0)
  const remaining = bought - sold
  const expectedRevenue = sold * Math.max(0, netSalePrice || 0)
  const unsoldInventoryCost = bought ? remaining * cost / bought : 0
  return { expectedRevenue, realizedMargin: expectedRevenue - (cost - unsoldInventoryCost),
    cashRecovery: expectedRevenue - cost, unsoldUnits: remaining, unsoldInventoryCost,
    markdownValue: remaining * Math.max(0, netSalePrice || 0) * (1 - markdownRatio),
    downsideProfit: bought * Math.max(0, netSalePrice || 0) * (1 - markdownRatio) - cost }
}

// Overlapping forecasts are one observation of the same market window.
export function independentEvidence(history, options = {}) {
  const occupied = new Map()
  return (Array.isArray(history) ? history : []).map(run => ({ ...run, rows: (run.rows || []).filter(row => {
    if (!row.evaluation || !["hit_qty", "hit_partial", "miss"].includes(row.evaluation.status)) return false
    if (options.requireVersion && (row.schemaVersion !== SCHEMA_VERSION || row.modelVersion !== MODEL_VERSION || !["HQ", "NQ"].includes(row.quality))) return false
    if (row.schemaVersion >= 2 && (!row.quality || row.modelVersion !== MODEL_VERSION)) return false
    const key = [run.homeWorld, row.id, row.quality || "legacy", row.modelVersion || "legacy"].join(":")
    const start = run.timestampMs
    const end = start + run.holdingDays * 86400000
    const windows = occupied.get(key) || []
    if (windows.some(window => start < window.end && end > window.start)) return false
    windows.push({ start, end })
    occupied.set(key, windows)
    return true
  }) }))
}
