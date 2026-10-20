export function pickLatestTimestampSec(candidates) {
  var max = null
  for (var i = 0; i < candidates.length; i++) {
    var c = candidates[i]
    if (Number.isFinite(c)) {
      max = max === null ? c : Math.max(max, c)
    }
  }
  return max
}
