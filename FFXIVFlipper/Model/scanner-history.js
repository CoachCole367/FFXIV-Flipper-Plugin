export function getScannerLiquidityBucketKey(liquidityPerDay) {
  var rate = Number.isFinite(liquidityPerDay) ? liquidityPerDay : 0
  if (rate < 0.75) {
    return "thin"
  }
  if (rate < 2) {
    return "steady"
  }
  if (rate < 5) {
    return "liquid"
  }
  return "hot"
}
