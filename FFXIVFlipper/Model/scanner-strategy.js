const percentileSortCache = new WeakMap()
export function pickPercentile(values, percentile) {
  var safeValues = []
  if (Array.isArray(values)) {
    if (!percentileSortCache.has(values)) percentileSortCache.set(values, values.filter(value => Number.isFinite(value) && value > 0).sort((a,b) => a-b))
    safeValues = percentileSortCache.get(values)
  }

  if (!safeValues.length) {
    return null
  }

  if (safeValues.length === 1) {
    return safeValues[0]
  }

  var p = clamp(Number.isFinite(percentile) ? percentile : 0.5, 0, 1)
  var position = (safeValues.length - 1) * p
  var lower = Math.floor(position)
  var upper = Math.ceil(position)
  if (lower === upper) {
    return safeValues[lower]
  }

  var weight = position - lower
  return safeValues[lower] + ((safeValues[upper] - safeValues[lower]) * weight)
}

export function pickScannerReferencePoint(referenceMarket, statsBySource) {
  var safeStats = statsBySource && typeof statsBySource === "object" ? statsBySource : {}
  var order = referenceMarket === "home"
    ? ["home", "dc", "region"]
    : (referenceMarket === "dc" ? ["dc", "home", "region"] : ["dc", "home", "region"])

  for (var i = 0; i < order.length; i++) {
    var source = order[i]
    var stats = safeStats[source] || {}
    var min = Number.isFinite(stats.min) ? stats.min : null
    var median = Number.isFinite(stats.median) ? stats.median : null
    var anchor = Number.isFinite(stats.anchor) ? stats.anchor : null
    if (Number.isFinite(min) || Number.isFinite(median) || Number.isFinite(anchor)) {
      return {
        source: source,
        min: min,
        median: median,
        anchor: anchor,
        anchorFast: Number.isFinite(stats.anchorFast) ? stats.anchorFast : null,
        anchorBalanced: Number.isFinite(stats.anchorBalanced) ? stats.anchorBalanced : anchor,
        anchorPatient: Number.isFinite(stats.anchorPatient) ? stats.anchorPatient : null
      }
    }
  }

  return {
    source: null,
    min: null,
    median: null,
    anchor: null,
    anchorFast: null,
    anchorBalanced: null,
    anchorPatient: null
  }
}

export function buildHybridTargetPrice(args) {
  var settings = args && typeof args === "object" ? args : {}
  var referenceMarket = settings.referenceMarket || "home"
  var targetCapRatio = Number.isFinite(settings.targetCapRatio) ? Math.max(0, settings.targetCapRatio) : 0.1
  var preferredStackQty = Number.isFinite(settings.preferredStackQty) && settings.preferredStackQty > 0
    ? settings.preferredStackQty
    : 1
  var statsBySource = settings.statsBySource && typeof settings.statsBySource === "object"
    ? settings.statsBySource
    : {}
  var homeListings = Array.isArray(settings.homeListings) ? settings.homeListings : []

  var referencePoint = pickScannerReferencePoint(referenceMarket, statsBySource)
  var saleAnchor = Number.isFinite(referencePoint.anchor)
    ? referencePoint.anchor
    : (Number.isFinite(referencePoint.median) ? referencePoint.median : referencePoint.min)

  var targetSell = Number.isFinite(saleAnchor) ? saleAnchor : null
  var targetBasis = Number.isFinite(referencePoint.anchor)
    ? "sale_anchor"
    : (Number.isFinite(referencePoint.median) ? "reference_median" : "reference_min")

  var depthCapPrice = getDepthCapPrice(homeListings, preferredStackQty)
  var targetWasCapped = false
  var targetCapReason = null

  if (!Number.isFinite(targetSell) && Number.isFinite(depthCapPrice)) {
    targetSell = depthCapPrice
    targetBasis = "depth_cap"
  }

  if (Number.isFinite(depthCapPrice) && Number.isFinite(targetSell) && targetSell > depthCapPrice) {
    targetSell = depthCapPrice
    targetWasCapped = true
    targetCapReason = "depth"
  }

  if (Number.isFinite(referencePoint.median) && Number.isFinite(targetSell)) {
    var medianCapPrice = referencePoint.median * (1 + targetCapRatio)
    if (targetSell > medianCapPrice) {
      targetSell = medianCapPrice
      targetWasCapped = true
      if (!targetCapReason) {
        targetCapReason = "median"
      }
    }
  }

  targetSell = roundGilPrice(targetSell)

  return {
    referenceSource: referencePoint.source,
    referenceMin: Number.isFinite(referencePoint.min) ? referencePoint.min : null,
    referenceMedian: Number.isFinite(referencePoint.median) ? referencePoint.median : null,
    saleAnchor: Number.isFinite(referencePoint.anchor) ? referencePoint.anchor : null,
    depthCapPrice: Number.isFinite(depthCapPrice) ? depthCapPrice : null,
    targetSell: Number.isFinite(targetSell) && targetSell > 0 ? targetSell : null,
    targetBasis: targetBasis,
    targetWasCapped: targetWasCapped,
    targetCapReason: targetCapReason
  }
}

export function buildHybridExitPlans(args) {
  var settings = args && typeof args === "object" ? args : {}
  var referenceMarket = settings.referenceMarket || "home"
  var targetCapRatio = Number.isFinite(settings.targetCapRatio) ? Math.max(0, settings.targetCapRatio) : 0.1
  var preferredStackQty = Number.isFinite(settings.preferredStackQty) && settings.preferredStackQty > 0
    ? settings.preferredStackQty
    : 1
  var holdingDays = Number.isFinite(settings.holdingDays) && settings.holdingDays > 0
    ? settings.holdingDays
    : 2
  var statsBySource = settings.statsBySource && typeof settings.statsBySource === "object"
    ? settings.statsBySource
    : {}
  var homeListings = Array.isArray(settings.homeListings) ? settings.homeListings : []
  var marketFragilityScore = clamp(Number.isFinite(settings.marketFragilityScore) ? settings.marketFragilityScore : 0, 0, 1)
  var referencePoint = pickScannerReferencePoint(referenceMarket, statsBySource)
  var plans = []
  var seenTargets = {}
  var exitStyles = ["fast", "balanced", "patient"]

  exitStyles.forEach(function (exitStyle) {
    var plan = buildHybridExitPlanVariant({
      exitStyle: exitStyle,
      holdingDays: holdingDays,
      targetCapRatio: targetCapRatio,
      preferredStackQty: preferredStackQty,
      homeListings: homeListings,
      referencePoint: referencePoint,
      marketFragilityScore: marketFragilityScore
    })
    if (!plan || !Number.isFinite(plan.targetSell) || plan.targetSell <= 0) {
      return
    }

    var targetKey = plan.targetSell.toFixed(4)
    if (Object.prototype.hasOwnProperty.call(seenTargets, targetKey)) {
      return
    }
    seenTargets[targetKey] = true
    plans.push(plan)
  })

  return plans
}

export function buildDemandModel(entries, options) {
  var settings = options && typeof options === "object" ? options : {}
  var safeEntries = normalizeSalesEntries(entries)
  var lookbackDays = Number.isFinite(settings.lookbackDays) && settings.lookbackDays > 0
    ? settings.lookbackDays
    : 7
  var horizonDays = Number.isFinite(settings.horizonDays) && settings.horizonDays > 0
    ? settings.horizonDays
    : 1
  var timestampedEntries = safeEntries.filter(function (entry) {
    return Number.isFinite(entry.timestampSec)
  })
  var nowSec = Number.isFinite(settings.nowSec)
    ? settings.nowSec
    : Date.now() / 1000
  var saleCount = safeEntries.length
  var totalUnits = safeEntries.reduce(function (sum, entry) {
    return sum + entry.quantity
  }, 0)
  var halfLifeDays = Number.isFinite(settings.halfLifeDays) && settings.halfLifeDays > 0
    ? settings.halfLifeDays
    : clamp(lookbackDays * 0.5, 1.5, 4.5)
  var saleBucketStats = buildDailyDemandBuckets(timestampedEntries, lookbackDays, nowSec, function () {
    return 1
  })
  var unitBucketStats = buildDailyDemandBuckets(timestampedEntries, lookbackDays, nowSec, function (entry) {
    return entry.quantity
  })
  var saleWeightedStats = computeWeightedDailyDemandStats(saleBucketStats.buckets, halfLifeDays)
  var unitWeightedStats = computeWeightedDailyDemandStats(unitBucketStats.buckets, halfLifeDays)
  var activeDays = Math.max(saleBucketStats.activeDays, unitBucketStats.activeDays)
  var timingCoverage = safeEntries.length ? (timestampedEntries.length / safeEntries.length) : 0
  var sparseTiming = !timestampedEntries.length || timingCoverage < 0.6
  var saleSeries = buildDemandSeriesModel({
    totalValue: saleCount,
    sampleCount: saleCount,
    activeDays: saleBucketStats.activeDays,
    lookbackDays: lookbackDays,
    horizonDays: horizonDays,
    sparseTiming: sparseTiming,
    weightedStats: saleWeightedStats
  })
  var unitSeries = buildDemandSeriesModel({
    totalValue: totalUnits,
    sampleCount: safeEntries.length,
    activeDays: unitBucketStats.activeDays,
    lookbackDays: lookbackDays,
    horizonDays: horizonDays,
    sparseTiming: sparseTiming,
    weightedStats: unitWeightedStats
  })
  var averageUnitsPerSale = saleCount > 0 ? (totalUnits / saleCount) : 0
  var suggestedStackQty = Math.max(1, Math.round(
    pickPercentile(safeEntries.map(function (entry) {
      return entry.quantity
    }), 0.5) || averageUnitsPerSale || 1
  ))

  return {
    modelType: unitSeries.modelType,
    sparseFallback: saleSeries.sparseFallback || unitSeries.sparseFallback,
    entryCount: safeEntries.length,
    saleCount: saleCount,
    timestampCoverage: timingCoverage,
    activeDays: activeDays,
    totalUnits: totalUnits,
    lookbackDays: lookbackDays,
    horizonDays: horizonDays,
    halfLifeDays: halfLifeDays,
    recencyBlendWeight: Math.max(saleSeries.recencyBlendWeight, unitSeries.recencyBlendWeight),
    simpleRatePerDay: unitSeries.simpleRatePerDay,
    weightedRatePerDay: unitSeries.weightedRatePerDay,
    modeledRatePerDay: unitSeries.modeledRatePerDay,
    varianceRatePerDay: unitSeries.varianceRatePerDay,
    meanUnits: unitSeries.meanValue,
    varianceUnits: unitSeries.varianceValue,
    dispersionRatio: unitSeries.dispersionRatio,
    negativeBinomialSize: unitSeries.negativeBinomialSize,
    averageUnitsPerSale: averageUnitsPerSale,
    suggestedStackQty: suggestedStackQty,
    saleModelType: saleSeries.modelType,
    saleSimpleRatePerDay: saleSeries.simpleRatePerDay,
    saleWeightedRatePerDay: saleSeries.weightedRatePerDay,
    saleRatePerDay: saleSeries.modeledRatePerDay,
    saleVarianceRatePerDay: saleSeries.varianceRatePerDay,
    meanSales: saleSeries.meanValue,
    varianceSales: saleSeries.varianceValue,
    saleDispersionRatio: saleSeries.dispersionRatio,
    saleNegativeBinomialSize: saleSeries.negativeBinomialSize,
    unitModelType: unitSeries.modelType,
    unitSimpleRatePerDay: unitSeries.simpleRatePerDay,
    unitWeightedRatePerDay: unitSeries.weightedRatePerDay,
    unitRatePerDay: unitSeries.modeledRatePerDay,
    unitVarianceRatePerDay: unitSeries.varianceRatePerDay,
    unitDispersionRatio: unitSeries.dispersionRatio,
    unitNegativeBinomialSize: unitSeries.negativeBinomialSize
  }
}

export function getDemandModelSaleProbabilityAtLeast(model, threshold, scale) {
  return getDemandModelProbabilityForMetric(model, threshold, scale, "sales")
}

export function getDemandModelUnitProbabilityAtLeast(model, threshold, scale) {
  return getDemandModelProbabilityForMetric(model, threshold, scale, "units")
}

export function getDemandModelProbabilityAtLeast(model, threshold, scale) {
  return getDemandModelUnitProbabilityAtLeast(model, threshold, scale)
}

export function getDemandModelExpectedUnitsCapped(model, maxUnits, scale) {
  return getDemandModelExpectedUnitsAfterCompetition(model, 0, maxUnits, scale)
}

export function getDemandModelExpectedUnitsAfterCompetition(model, competitionUnits, maxUnits, scale) {
  var scaled = getScaledDemandModel(model, scale, "units")
  var blockedUnits = Math.max(0, Math.ceil(Number.isFinite(competitionUnits) ? competitionUnits : 0))
  var capUnits = Number.isFinite(maxUnits) && maxUnits > 0
    ? Math.max(0, Math.ceil(maxUnits))
    : null

  if (!(scaled.meanUnits > 0)) {
    return 0
  }

  if (capUnits === null) {
    return Math.max(0, scaled.meanUnits - blockedUnits)
  }

  var expected = 0
  for (var i = 1; i <= capUnits; i++) {
    expected += getDemandModelUnitProbabilityAtLeast(scaled, blockedUnits + i, 1)
  }
  return clamp(expected, 0, capUnits)
}

export function getDemandModelExpectedUnitsAfterQueue(model, queueUnits, maxUnits, scale) {
  return getDemandModelExpectedUnitsAfterCompetition(model, queueUnits, maxUnits, scale)
}

export function estimateExecution(demandModel, competition, holdingDays, quantity, calibrationScale) {
  const pressure = buildPriceLadderPressureModel({ demandModel, competition, holdingDays, preferredStackQty: Math.min(quantity, demandModel.suggestedStackQty || 1) })
  const calibration = Number.isFinite(calibrationScale) ? Math.max(0, calibrationScale) : 1
  const queueUnits = Math.max(0, Math.ceil(competition.effectiveCompetitionUnits || 0))
  const queueEvents = Math.max(0, Math.floor(competition.effectiveCompetitionSaleEvents || 0))
  const unitScale = pressure.unitRateScale * pressure.targetConfidenceScale * calibration
  const saleScale = pressure.firstSaleScale * calibration
  const unitRate = (demandModel.unitRatePerDay || 0) * unitScale
  const saleRate = (demandModel.saleRatePerDay || 0) * saleScale
  return {
    pressure, queueUnits, queueEvents,
    firstSaleProbability: getDemandModelSaleProbabilityAtLeast(demandModel,queueEvents+1,saleScale),
    clearProbability: getDemandModelUnitProbabilityAtLeast(demandModel,queueUnits+quantity,unitScale),
    expectedUnits: getDemandModelExpectedUnitsAfterCompetition(demandModel,queueUnits,quantity,unitScale),
    firstSaleDays: saleRate > 0 ? (queueEvents+1)/saleRate : Infinity,
    clearDays: Math.max(saleRate > 0 ? (queueEvents+1)/saleRate : Infinity,unitRate > 0 ? (queueUnits+quantity)/unitRate : Infinity)
  }
}

export function buildMarketFragilityOverlay(args) {
  var settings = args && typeof args === "object" ? args : {}
  var homeListings = normalizeListings(settings.homeListings)
  var recentExecutedMedian = Number.isFinite(settings.recentExecutedMedian) && settings.recentExecutedMedian > 0
    ? settings.recentExecutedMedian
    : null
  var unitRatePerDay = Number.isFinite(settings.unitRatePerDay) && settings.unitRatePerDay > 0
    ? settings.unitRatePerDay
    : 0
  var holdingDays = Number.isFinite(settings.holdingDays) && settings.holdingDays > 0
    ? settings.holdingDays
    : 2
  var saleCount = Number.isFinite(settings.saleCount) && settings.saleCount > 0
    ? settings.saleCount
    : 0
  var frontBandRatio = Number.isFinite(settings.frontBandRatio) && settings.frontBandRatio > 0
    ? settings.frontBandRatio
    : 0.005

  if (!homeListings.length) {
    return createMarketFragilityOverlayResult()
  }

  var currentMin = getMinListingPrice(homeListings)
  var totalPostedSupply = homeListings.reduce(function (sum, listing) {
    return sum + listing.quantity
  }, 0)
  var historyConfidence = clamp(saleCount / 8, 0.35, 1)
  var floorBreakRatio = Number.isFinite(recentExecutedMedian) && recentExecutedMedian > 0 && Number.isFinite(currentMin)
    ? Math.max(0, (recentExecutedMedian - currentMin) / recentExecutedMedian)
    : 0
  var floorBreakScore = clamp((floorBreakRatio - 0.04) / 0.18, 0, 1) * historyConfidence
  var frontBandHigh = Number.isFinite(currentMin) ? (currentMin * (1 + frontBandRatio)) : null
  var frontBandListings = Number.isFinite(frontBandHigh)
    ? homeListings.filter(function (listing) {
      return listing.pricePerUnit <= frontBandHigh
    })
    : []
  var frontBandUnits = frontBandListings.reduce(function (sum, listing) {
    return sum + listing.quantity
  }, 0)
  var frontSpreadPct = null

  if (frontBandListings.length > 1) {
    var frontMax = frontBandListings[frontBandListings.length - 1].pricePerUnit
    if (Number.isFinite(currentMin) && currentMin > 0 && Number.isFinite(frontMax)) {
      frontSpreadPct = (frontMax - currentMin) / currentMin
    }
  }

  var frontListingDensity = clamp((frontBandListings.length - 2) / 5, 0, 1)
  var frontTightness = frontBandListings.length >= 3
    ? clamp((frontBandRatio - (Number.isFinite(frontSpreadPct) ? frontSpreadPct : frontBandRatio)) / Math.max(frontBandRatio, 0.0001), 0, 1)
    : 0
  var nearPriceCrowdingScore = clamp((frontListingDensity * 0.7) + (frontTightness * 0.3), 0, 1)
  var postedSupplyDays = unitRatePerDay > 0 ? (totalPostedSupply / unitRatePerDay) : Number.POSITIVE_INFINITY
  var supplyOverhangScore = Number.isFinite(postedSupplyDays)
    ? clamp((postedSupplyDays - Math.max(holdingDays * 1.2, 1.5)) / Math.max(holdingDays * 3.2, 3), 0, 1) * historyConfidence
    : 0
  var stressScore = clamp(
    (floorBreakScore * 0.45) +
    (nearPriceCrowdingScore * 0.3) +
    (supplyOverhangScore * 0.25),
    0,
    1
  )

  return createMarketFragilityOverlayResult({
    stressScore: stressScore,
    floorBreakRatio: floorBreakRatio,
    floorBreakScore: floorBreakScore,
    nearPriceCrowdingScore: nearPriceCrowdingScore,
    frontBandListingCount: frontBandListings.length,
    frontBandUnits: frontBandUnits,
    frontSpreadPct: frontSpreadPct,
    supplyOverhangScore: supplyOverhangScore,
    postedSupplyDays: postedSupplyDays,
    totalPostedSupply: totalPostedSupply,
    historyConfidence: historyConfidence,
    targetDiscountRatioFast: clamp(stressScore * 0.0125, 0, 0.0125),
    targetDiscountRatioBalanced: clamp(stressScore * 0.025, 0, 0.025),
    targetDiscountRatioPatient: clamp(stressScore * 0.04, 0, 0.04),
    holdWindowScale: clamp(1 - (stressScore * 0.16), 0.84, 1),
    qtyScale: clamp(1 - (stressScore * 0.3), 0.7, 1),
    signalPenalty: clamp(stressScore * 0.16, 0, 0.16)
  })
}

export function applyMarketFragilityQtyCap(baseQty, qtyScale) {
  var safeQty = Number.isInteger(baseQty) && baseQty > 0 ? baseQty : 0
  if (safeQty < 1) {
    return 0
  }
  var safeScale = clamp(Number.isFinite(qtyScale) ? qtyScale : 1, 0.7, 1)
  return Math.max(1, Math.floor((safeQty - 1) * safeScale) + 1)
}

export function buildDiscretePriceCandidates(args) {
  var settings = args && typeof args === "object" ? args : {}
  var exitStyle = settings.exitStyle === "fast" || settings.exitStyle === "patient"
    ? settings.exitStyle
    : "balanced"
  var homeListings = normalizeListings(settings.homeListings)
  var referencePoint = settings.referencePoint && typeof settings.referencePoint === "object"
    ? settings.referencePoint
    : {}
  var baseTarget = roundGilPrice(settings.baseTarget)
  var currentMin = getMinListingPrice(homeListings)
  var lowerBound = Math.max(1, roundGilPrice(Number.isFinite(settings.lowerBound) ? settings.lowerBound : 1))
  var upperBound = Math.max(lowerBound, roundGilPrice(
    Number.isFinite(settings.upperBound)
      ? settings.upperBound
      : (Number.isFinite(baseTarget) ? baseTarget : (Number.isFinite(currentMin) ? currentMin : lowerBound))
  ))
  var prices = {}
  var offsets = exitStyle === "fast"
    ? [-2, -1, 0, 1]
    : (exitStyle === "patient" ? [-3, -1, 0, 1, 2, 3] : [-2, -1, 0, 1, 2])
  var percentOffsets = exitStyle === "fast"
    ? [-0.005, 0, 0.005]
    : (exitStyle === "patient" ? [-0.01, 0, 0.01, 0.02] : [-0.0075, 0, 0.0075, 0.015])
  var anchors = [
    baseTarget,
    currentMin,
    Number(referencePoint.min),
    Number(referencePoint.median),
    Number(referencePoint.anchorFast),
    Number(referencePoint.anchorBalanced),
    Number(referencePoint.anchorPatient),
    Number(settings.depthCapPrice),
    Number(settings.depthSoftCapPrice)
  ]
  var listingAnchors = homeListings.slice(0, Math.min(homeListings.length, 6)).map(function (listing) {
    return listing.pricePerUnit
  })

  anchors.concat(listingAnchors).forEach(function (anchor) {
    addCandidate(anchor)
    offsets.forEach(function (offset) {
      addCandidate(anchor + offset)
    })
    percentOffsets.forEach(function (ratio) {
      addCandidate(anchor * (1 + ratio))
    })
  })

  if (Number.isFinite(currentMin)) {
    addCandidate(currentMin - 1)
    if (exitStyle === "fast") {
      addCandidate(currentMin - 2)
    }
  }

  var ordered = Object.keys(prices).map(Number).filter(price => price >= lowerBound && price <= upperBound).sort((a,b) => a-b)
  if (ordered.length <= 24) return ordered
  var selected = new Set([baseTarget, roundGilPrice(currentMin), roundGilPrice(settings.depthCapPrice), roundGilPrice(referencePoint.anchorPatient)].filter(price => prices[price]))
  for (var sample = 0; sample < 20; sample++) selected.add(ordered[Math.round(sample * (ordered.length-1) / 19)])
  return Array.from(selected).sort((a,b)=>a-b).slice(0,24)

  function addCandidate(value) {
    var rounded = roundGilPrice(value)
    if (!Number.isFinite(rounded) || rounded < lowerBound || rounded > upperBound) {
      return
    }
    prices[rounded] = true
  }
}

export function buildSellPriceGuide(args) {
  var settings = args && typeof args === "object" ? args : {}
  var currentListings = Array.isArray(settings.currentListings) ? settings.currentListings : []
  var referenceListings = Array.isArray(settings.referenceListings) ? settings.referenceListings : currentListings
  var salesEntries = normalizeSalesEntries(settings.salesEntries)
  var lookbackDays = Number.isFinite(settings.lookbackDays) && settings.lookbackDays > 0
    ? settings.lookbackDays
    : 7
  var sellWindowDays = Number.isFinite(settings.sellWindowDays) && settings.sellWindowDays > 0
    ? settings.sellWindowDays
    : 2
  var targetCapRatio = Number.isFinite(settings.targetCapRatio) ? Math.max(0, settings.targetCapRatio) : 0.12
  var priceBandRatio = Number.isFinite(settings.priceBandRatio) && settings.priceBandRatio > 0
    ? settings.priceBandRatio
    : 0.1
  var timestampedEntries = salesEntries.filter(function (entry) {
    return Number.isFinite(entry.timestampSec)
  })
  var nowSec = Number.isFinite(settings.nowSec)
    ? settings.nowSec
    : Date.now() / 1000
  var salePrices = normalizeSalesEntries(settings.referenceSalesEntries || salesEntries).map(function (entry) {
    return entry.pricePerUnit
  }).filter(function (value) {
    return Number.isFinite(value) && value > 0
  })
  var stackMedian = pickPercentile(salesEntries.map(function (entry) {
    return entry.quantity
  }), 0.5)
  var currentMinListing = getMinListingPrice(currentListings)
  var candidatePrices = buildSellPriceSurfaceCandidates({
    currentListings: currentListings,
    referenceListings: referenceListings,
    salePrices: salePrices,
    stackMedian: stackMedian,
    sellWindowDays: sellWindowDays,
    targetCapRatio: targetCapRatio
  })
  var candidatePlans = candidatePrices.map(function (targetSell) {
    return buildSellPriceGuidePlanMetrics(
      {
        exitStyle: "candidate",
        exitLabel: "Candidate",
        targetSell: targetSell,
        targetWasCapped: false,
        targetCapReason: null,
        targetBasis: "price_surface"
      },
      currentListings,
      salesEntries,
      lookbackDays,
      sellWindowDays,
      priceBandRatio,
      stackMedian,
      nowSec
    )
  }).filter(function (plan) {
    return !!plan &&
      plan.bandSalesCount > 0 &&
      (plan.bandSaleRate > 0 || plan.bandUnitRate > 0)
  })
  var frontier = buildSellPriceParetoFrontier(candidatePlans)
  var guidePlans = pickSellPriceGuideTiers(frontier, candidatePlans, sellWindowDays)
  var fastPlan = guidePlans.filter(function (plan) {
    return plan.exitStyle === "fast"
  })[0] || null
  var fireSaleCandidatePlans = buildFireSaleSurfaceCandidatePrices(currentMinListing, salePrices, sellWindowDays).map(function (targetSell) {
    return buildSellPriceGuidePlanMetrics(
      {
        exitStyle: "candidate",
        exitLabel: "Candidate",
        targetSell: targetSell,
        targetWasCapped: false,
        targetCapReason: null,
        targetBasis: "fire_sale_surface"
      },
      currentListings,
      salesEntries,
      lookbackDays,
      sellWindowDays,
      priceBandRatio,
      stackMedian,
      nowSec
    )
  }).filter(function (plan) {
    return !!plan &&
      plan.bandSalesCount > 0 &&
      (plan.bandSaleRate > 0 || plan.bandUnitRate > 0)
  })
  var fireSalePlan = pickFireSaleGuidePlan(fireSaleCandidatePlans, fastPlan, sellWindowDays)

  if (fireSalePlan) {
    guidePlans.unshift(fireSalePlan)
  }

  return guidePlans.map(function (plan) {
    plan.candidateCount = candidatePrices.length
    plan.frontierCount = frontier.length
    return plan
  })
}

function buildSellPriceGuidePlanMetrics(plan, currentListings, salesEntries, lookbackDays, sellWindowDays, priceBandRatio, stackMedian, nowSec) {
  if (!plan || !Number.isFinite(plan.targetSell) || plan.targetSell <= 0) {
    return null
  }

  var targetSell = roundGilPrice(plan.targetSell)
  var bandLow = Number.isFinite(targetSell) ? targetSell * (1 - priceBandRatio) : null
  var bandHigh = Number.isFinite(targetSell) ? targetSell * (1 + priceBandRatio) : null
  var supportBandLow = Number.isFinite(targetSell)
    ? targetSell * (1 - Math.min(priceBandRatio * 0.35, 0.025))
    : bandLow
  var bandSales = salesEntries.filter(function (entry) {
    return Number.isFinite(entry.pricePerUnit) &&
      Number.isFinite(supportBandLow) &&
      Number.isFinite(bandHigh) &&
      entry.pricePerUnit >= supportBandLow &&
      entry.pricePerUnit <= bandHigh
  })
  var bandUnitsSold = bandSales.reduce(function (sum, entry) {
    return sum + entry.quantity
  }, 0)
  var demandModel = buildDemandModel(bandSales, {
    lookbackDays: lookbackDays,
    horizonDays: sellWindowDays,
    nowSec: nowSec
  })
  var saleRatePerDay = Number.isFinite(demandModel.saleRatePerDay) ? demandModel.saleRatePerDay : 0
  var unitRatePerDay = Number.isFinite(demandModel.unitRatePerDay) ? demandModel.unitRatePerDay : 0
  var suggestedStackQty = Math.max(1, Math.round(
    pickPercentile(bandSales.map(function (entry) {
      return entry.quantity
    }), 0.5) ||
    stackMedian ||
    1
  ))
  var competition = analyzeTargetCompetition(currentListings, targetSell, {
    preferredStackQty: suggestedStackQty,
    nearTargetRatio: 0.01,
    averageUnitsPerSale: demandModel.averageUnitsPerSale
  })
  var ladderPressure = buildPriceLadderPressureModel({
    competition: competition,
    demandModel: demandModel,
    holdingDays: sellWindowDays,
    preferredStackQty: suggestedStackQty
  })
  var rawCompetitionUnits = Number.isFinite(competition.rawCompetitionUnits)
    ? competition.rawCompetitionUnits
    : 0
  var effectiveCompetitionUnits = Number.isFinite(competition.effectiveCompetitionUnits)
    ? competition.effectiveCompetitionUnits
    : 0
  var rawCompetitionSaleEvents = Number.isFinite(competition.rawCompetitionSaleEvents)
    ? competition.rawCompetitionSaleEvents
    : 0
  var effectiveCompetitionSaleEvents = Number.isFinite(competition.effectiveCompetitionSaleEvents)
    ? competition.effectiveCompetitionSaleEvents
    : 0
  var saleEventsToFirstFill = Math.max(1, Math.floor(effectiveCompetitionSaleEvents) + 1)
  var firstSaleScale = Number.isFinite(ladderPressure.firstSaleScale) && ladderPressure.firstSaleScale > 0
    ? ladderPressure.firstSaleScale
    : 1
  var estimatedDaysToFirstSale = saleRatePerDay > 0
    ? (saleEventsToFirstFill / Math.max(saleRatePerDay * firstSaleScale, 0.0001))
    : Number.POSITIVE_INFINITY
  var rawDaysToFirstSale = saleRatePerDay > 0
    ? (saleEventsToFirstFill / saleRatePerDay)
    : Number.POSITIVE_INFINITY
  var throughputDaysToClearStack = unitRatePerDay > 0 && ladderPressure.unitRateScale > 0
    ? ((effectiveCompetitionUnits + suggestedStackQty) / Math.max(unitRatePerDay * ladderPressure.unitRateScale * ladderPressure.targetConfidenceScale, 0.0001))
    : Number.POSITIVE_INFINITY
  var estimatedDaysToClearStack = Math.max(
    Number.isFinite(ladderPressure.estimatedDaysToClearSuggestedStack)
      ? ladderPressure.estimatedDaysToClearSuggestedStack
      : 0,
    estimatedDaysToFirstSale,
    throughputDaysToClearStack
  )
  var execution = estimateExecution(demandModel, competition, sellWindowDays, suggestedStackQty, 1)
  estimatedDaysToFirstSale = execution.firstSaleDays
  estimatedDaysToClearStack = execution.clearDays
  var clearSuggestedStackProbability = execution.clearProbability
  var expectedUnitsAfterCompetition = execution.expectedUnits
  var sellWithinWindowProbability = execution.firstSaleProbability
  var expectedGrossGil = expectedUnitsAfterCompetition * targetSell
  var expectedGilPerDay = expectedGrossGil / Math.max(sellWindowDays, 0.25)
  var riskAdjustedGilPerDay = expectedGilPerDay *
    (0.55 + (0.45 * sellWithinWindowProbability)) *
    ladderPressure.targetConfidenceScale /
    (1 + (ladderPressure.competitionPressureScore * 0.14) + (ladderPressure.exitFragilityScore * 0.1))
  var currentMinListing = getMinListingPrice(currentListings)
  var recentMedian = pickPercentile(salesEntries.map(function (entry) {
    return entry.pricePerUnit
  }), 0.5)
  var guideConfidence = buildSellPriceGuideConfidence({
    bandSalesCount: bandSales.length,
    saleRatePerDay: saleRatePerDay,
    activeDays: demandModel.activeDays,
    lookbackDays: lookbackDays,
    sparseFallback: demandModel.sparseFallback
  })

  return {
    exitStyle: plan.exitStyle,
    exitLabel: plan.exitLabel,
    targetSell: targetSell,
    targetWasCapped: !!plan.targetWasCapped,
    targetCapReason: plan.targetCapReason,
    targetBasis: plan.targetBasis,
    currentMinListing: Number.isFinite(currentMinListing) ? currentMinListing : null,
    recentMedian: Number.isFinite(recentMedian) ? recentMedian : null,
    livePremiumToRecentMedian: Number.isFinite(currentMinListing) && Number.isFinite(recentMedian) && recentMedian > 0
      ? ((currentMinListing - recentMedian) / recentMedian)
      : null,
    suggestedStackQty: suggestedStackQty,
    confidenceLabel: guideConfidence.label,
    confidenceReasons: guideConfidence.reasons,
    confidenceScore: guideConfidence.score,
    bandSalesCount: bandSales.length,
    bandSaleRate: saleRatePerDay,
    bandUnitRate: unitRatePerDay,
    bandLambda: unitRatePerDay,
    demandModelType: demandModel.unitModelType || demandModel.modelType,
    demandDispersionRatio: demandModel.unitDispersionRatio || demandModel.dispersionRatio,
    competitionSaleRateScale: ladderPressure.saleRateScale,
    competitionUnitRateScale: ladderPressure.unitRateScale,
    competitionProbabilityScale: ladderPressure.probabilityScale,
    competitionTargetConfidenceScale: ladderPressure.targetConfidenceScale,
    competitionHoldWindowScale: ladderPressure.holdWindowScale,
    competitionQtyScale: ladderPressure.qtyScale,
    competitionDelayDays: ladderPressure.competitionDelayDays,
    competitionSupplyOverhangNearTargetScore: ladderPressure.supplyOverhangNearTargetScore,
    exitFragilityScore: ladderPressure.exitFragilityScore,
    cheaperUnitPressureScore: ladderPressure.cheaperUnitPressureScore,
    samePriceCongestionScore: ladderPressure.samePriceCongestionScore,
    nearPriceCrowdingScore: ladderPressure.nearPriceCrowdingScore,
    cheaperUnitsAhead: competition.cheaperUnitsAhead,
    samePriceUnitsAhead: competition.samePriceUnitsAhead,
    samePriceListingCount: competition.samePriceListingCount,
    priceBandCrowdingUnits: competition.priceBandCrowdingUnits,
    undercutRisk: competition.undercutRisk,
    rawCompetitionUnits: rawCompetitionUnits,
    effectiveCompetitionUnits: effectiveCompetitionUnits,
    rawCompetitionSaleEvents: rawCompetitionSaleEvents,
    effectiveCompetitionSaleEvents: effectiveCompetitionSaleEvents,
    queueEventsAhead: effectiveCompetitionSaleEvents,
    queueAheadUnits: rawCompetitionUnits,
    effectiveQueueAheadUnits: effectiveCompetitionUnits,
    unitsNeededToSellOne: saleEventsToFirstFill,
    windowDemandMu: demandModel.meanUnits,
    estimatedDaysToFirstSale: estimatedDaysToFirstSale,
    estimatedDaysToSellOne: estimatedDaysToFirstSale,
    rawDaysToFirstSale: rawDaysToFirstSale,
    rawDaysToSellOne: rawDaysToFirstSale,
    estimatedDaysToClearStack: estimatedDaysToClearStack,
    clearSuggestedStackProbability: clearSuggestedStackProbability,
    expectedUnitsAfterCompetition: expectedUnitsAfterCompetition,
    expectedGrossGil: expectedGrossGil,
    expectedGilPerDay: expectedGilPerDay,
    riskAdjustedGilPerDay: riskAdjustedGilPerDay,
    sellWithinWindowProbability: sellWithinWindowProbability,
    priceLadderPressureScore: ladderPressure.competitionPressureScore,
    competitionPressureScore: ladderPressure.competitionPressureScore,
    queuePressureScore: ladderPressure.competitionPressureScore
  }
}

function buildSellPriceSurfaceCandidates(args) {
  var settings = args && typeof args === "object" ? args : {}
  var currentListings = normalizeListings(settings.currentListings)
  var referenceListings = normalizeListings(settings.referenceListings)
  var salePrices = Array.isArray(settings.salePrices)
    ? settings.salePrices.filter(function (value) {
      return Number.isFinite(value) && value > 0
    })
    : []
  var stackMedian = Number.isFinite(settings.stackMedian) && settings.stackMedian > 0
    ? settings.stackMedian
    : 1
  var sellWindowDays = Number.isFinite(settings.sellWindowDays) && settings.sellWindowDays > 0
    ? settings.sellWindowDays
    : 2
  var targetCapRatio = Number.isFinite(settings.targetCapRatio) ? Math.max(0, settings.targetCapRatio) : 0.12
  var currentMin = getMinListingPrice(currentListings)
  var referenceMin = getMinListingPrice(referenceListings)
  var recentMedian = pickPercentile(salePrices, 0.5)
  var recentHigh = pickPercentile(salePrices, 0.8)
  var sellerDepthCap = getDepthCapPrice(currentListings, stackMedian)
  var referenceDepthCap = getDepthCapPrice(referenceListings, stackMedian)
  var depthLift = clamp((sellWindowDays * 0.04) + 0.02, 0, targetCapRatio)
  var sellerDepthSoftCap = Number.isFinite(sellerDepthCap) ? sellerDepthCap * (1 + depthLift) : null
  var medianCap = Number.isFinite(recentMedian) ? recentMedian * (1 + targetCapRatio) : null
  var upperCapCandidates = [sellerDepthSoftCap, medianCap].filter(function (value) {
    return Number.isFinite(value) && value > 0
  })
  var softUpper = upperCapCandidates.length
    ? Math.min.apply(Math, upperCapCandidates)
    : null
  var fallbackUpper = Number.isFinite(softUpper)
    ? softUpper
    : (Number.isFinite(recentHigh)
      ? recentHigh
      : (Number.isFinite(referenceDepthCap) ? referenceDepthCap : currentMin))
  var upperBound = Math.max(
    1,
    roundGilPrice(Math.max(
      Number.isFinite(currentMin) ? currentMin : 0,
      Number.isFinite(referenceMin) ? referenceMin : 0,
      Number.isFinite(fallbackUpper) ? fallbackUpper : 0
    )) || 1
  )
  var lowerSeed = Math.min.apply(Math, [
    Number.isFinite(currentMin) ? currentMin : Number.POSITIVE_INFINITY,
    Number.isFinite(referenceMin) ? referenceMin : Number.POSITIVE_INFINITY,
    Number.isFinite(pickPercentile(salePrices, 0.05)) ? pickPercentile(salePrices, 0.05) : Number.POSITIVE_INFINITY
  ])
  var lowerBound = Number.isFinite(lowerSeed)
    ? Math.max(1, roundGilPrice(lowerSeed * 0.9) || 1)
    : 1
  if (Number.isFinite(currentMin)) {
    var normalUndercutFloor = Math.max(1, Math.min(currentMin - 2, currentMin * 0.98))
    lowerBound = Math.max(lowerBound, roundGilPrice(normalUndercutFloor) || lowerBound)
  }
  var prices = {}

  addCandidate(currentMin)
  addCandidate(Number.isFinite(currentMin) ? currentMin - 1 : null)
  addCandidate(Number.isFinite(currentMin) ? currentMin - 2 : null)
  addCandidate(referenceMin)
  addCandidate(sellerDepthCap)
  addCandidate(sellerDepthSoftCap)
  addCandidate(referenceDepthCap)
  addCandidate(medianCap)

  ;[0.05, 0.1, 0.2, 0.35, 0.5, 0.6, 0.75, 0.85].forEach(function (percentile) {
    var value = pickPercentile(salePrices, percentile)
    addCandidate(value)
    addCandidate(Number.isFinite(value) ? value - 1 : null)
    addCandidate(Number.isFinite(value) ? value + 1 : null)
    addCandidate(Number.isFinite(value) ? value * 0.99 : null)
    addCandidate(Number.isFinite(value) ? value * 1.01 : null)
  })

  currentListings.slice(0, Math.min(currentListings.length, 14)).forEach(function (listing) {
    addListingLadderCandidates(listing.pricePerUnit, true)
  })

  referenceListings.slice(0, Math.min(referenceListings.length, 10)).forEach(function (listing) {
    addListingLadderCandidates(listing.pricePerUnit, false)
  })

  return Object.keys(prices).map(function (key) {
    return Number(key)
  }).filter(function (price) {
    return price >= lowerBound && price <= upperBound
  }).sort(function (a, b) {
    return a - b
  })

  function addListingLadderCandidates(price, includeHardUndercuts) {
    addCandidate(price)
    addCandidate(price - 1)
    if (includeHardUndercuts) {
      addCandidate(price - 2)
    }
    addCandidate(price + 1)
    addCandidate(price * 0.995)
    addCandidate(price * 1.005)
  }

  function addCandidate(value) {
    var rounded = roundGilPrice(value)
    if (!Number.isFinite(rounded) || rounded < lowerBound || rounded > upperBound) {
      return
    }
    prices[rounded] = true
  }
}

function buildSellPriceGuideConfidence(args) {
  var settings = args && typeof args === "object" ? args : {}
  var bandSalesCount = Number.isFinite(settings.bandSalesCount) ? Math.max(0, settings.bandSalesCount) : 0
  var saleRatePerDay = Number.isFinite(settings.saleRatePerDay) ? Math.max(0, settings.saleRatePerDay) : 0
  var activeDays = Number.isFinite(settings.activeDays) ? Math.max(0, settings.activeDays) : 0
  var lookbackDays = Number.isFinite(settings.lookbackDays) && settings.lookbackDays > 0 ? settings.lookbackDays : 7
  var sparseFallback = !!settings.sparseFallback
  var score = 0
  var reasons = []

  if (bandSalesCount >= 8) {
    score += 2
  } else if (bandSalesCount >= 3) {
    score += 1
  } else {
    reasons.push("only " + bandSalesCount + " relevant sale" + (bandSalesCount === 1 ? "" : "s") + " in the lookback")
  }

  if (activeDays >= 4) {
    score += 1
  } else if (bandSalesCount > 0) {
    reasons.push("sales are concentrated into few days")
  }

  if (saleRatePerDay >= 0.7) {
    score += 1
  } else if (saleRatePerDay < 0.35) {
    reasons.push("thin demand near this price")
  }

  if (!sparseFallback) {
    score += 1
  } else {
    reasons.push("limited timestamp coverage")
  }

  if (lookbackDays <= 7) {
    reasons.push("short " + Math.round(lookbackDays) + "-day lookback")
  }

  if (score >= 4) {
    return {
      label: "High",
      score: score,
      reasons: reasons
    }
  }

  if (score >= 2) {
    return {
      label: "Medium",
      score: score,
      reasons: reasons
    }
  }

  return {
    label: "Low",
    score: score,
    reasons: reasons
  }
}

function buildSellPriceParetoFrontier(plans) {
  var safePlans = Array.isArray(plans) ? plans : []
  return safePlans.filter(function (candidate) {
    return !safePlans.some(function (other) {
      if (other === candidate) {
        return false
      }

      var otherDays = Number.isFinite(other.estimatedDaysToFirstSale) ? other.estimatedDaysToFirstSale : Number.POSITIVE_INFINITY
      var candidateDays = Number.isFinite(candidate.estimatedDaysToFirstSale) ? candidate.estimatedDaysToFirstSale : Number.POSITIVE_INFINITY
      var dominates =
        other.expectedGilPerDay >= candidate.expectedGilPerDay - 0.0001 &&
        other.sellWithinWindowProbability >= candidate.sellWithinWindowProbability - 0.0001 &&
        other.targetSell >= candidate.targetSell - 0.0001 &&
        otherDays <= candidateDays + 0.0001
      var strict =
        other.expectedGilPerDay > candidate.expectedGilPerDay + 0.0001 ||
        other.sellWithinWindowProbability > candidate.sellWithinWindowProbability + 0.0001 ||
        other.targetSell > candidate.targetSell + 0.0001 ||
        otherDays < candidateDays - 0.0001
      return dominates && strict
    })
  })
}

function buildFireSaleSurfaceCandidatePrices(currentMinListing, salePrices, sellWindowDays) {
  var currentMin = Number.isFinite(currentMinListing) && currentMinListing > 0
    ? currentMinListing
    : null
  if (!Number.isFinite(currentMin)) {
    return []
  }

  var prices = {}
  var window = Number.isFinite(sellWindowDays) && sellWindowDays > 0 ? sellWindowDays : 2
  var crowdingDiscount = clamp(0.03 + (0.03 / Math.max(1, window)), 0.03, 0.08)
  var historicalFloor = pickPercentile(salePrices, 0.2)
  var historicalGuardrail = pickPercentile(salePrices, 0.05)
  var rawTarget = currentMin * (1 - crowdingDiscount)
  var guardedTarget = Number.isFinite(historicalFloor)
    ? Math.min(rawTarget, historicalFloor)
    : rawTarget

  if (Number.isFinite(historicalGuardrail) && guardedTarget < (historicalGuardrail * 0.9)) {
    guardedTarget = historicalGuardrail * 0.9
  }

  add(guardedTarget)
  add(currentMin * 0.95)
  add(currentMin * 0.92)

  return Object.keys(prices).map(function (key) {
    return Number(key)
  }).filter(function (price) {
    return price > 0 && price < currentMin * 0.99
  }).sort(function (a, b) {
    return a - b
  })

  function add(value) {
    var rounded = roundGilPrice(value)
    if (Number.isFinite(rounded)) {
      prices[rounded] = true
    }
  }
}

function pickSellPriceGuideTiers(frontier, candidates, sellWindowDays) {
  var usable = (Array.isArray(frontier) && frontier.length ? frontier : candidates).filter(function (plan) {
    return plan && Number.isFinite(plan.targetSell) && plan.targetSell > 0
  })
  if (!usable.length) {
    return []
  }

  var window = Number.isFinite(sellWindowDays) && sellWindowDays > 0 ? sellWindowDays : 2
  var topProbability = usable.reduce(function (best, plan) {
    return Math.max(best, Number.isFinite(plan.sellWithinWindowProbability) ? plan.sellWithinWindowProbability : 0)
  }, 0)
  var usedTargets = {}
  var selected = []
  var fastFloor = Math.max(0.18, topProbability * 0.62)
  var fast = pickUniquePlan(sortForFastTier(usable.filter(function (plan) {
    return plan.sellWithinWindowProbability >= fastFloor
  })), usedTargets) || pickUniquePlan(sortForFastTier(usable), usedTargets)
  if (fast) {
    selected.push(cloneSellPricePlanAsTier(fast, "fast", "Fast"))
  }

  var balanced = pickUniquePlan(sortForBalancedTier(usable), usedTargets)
  if (balanced) {
    selected.push(cloneSellPricePlanAsTier(balanced, "balanced", "Balanced"))
  }

  var patientFloor = Math.max(0.16, Math.min(0.55, topProbability * 0.55))
  var patientPool = usable.filter(function (plan) {
    return plan.sellWithinWindowProbability >= patientFloor &&
      (!Number.isFinite(plan.estimatedDaysToFirstSale) || plan.estimatedDaysToFirstSale <= (window * 1.75))
  })
  var patient = pickUniquePlan(sortForPatientTier(patientPool), usedTargets) ||
    pickUniquePlan(sortForPatientTier(usable), usedTargets)
  if (patient) {
    selected.push(cloneSellPricePlanAsTier(patient, "patient", "Patient"))
  }

  return selected
}

function sortForFastTier(plans) {
  return plans.slice().sort(function (a, b) {
    var aDays = Number.isFinite(a.estimatedDaysToFirstSale) ? a.estimatedDaysToFirstSale : Number.POSITIVE_INFINITY
    var bDays = Number.isFinite(b.estimatedDaysToFirstSale) ? b.estimatedDaysToFirstSale : Number.POSITIVE_INFINITY
    if (aDays !== bDays) {
      return aDays - bDays
    }
    if (a.sellWithinWindowProbability !== b.sellWithinWindowProbability) {
      return b.sellWithinWindowProbability - a.sellWithinWindowProbability
    }
    return b.targetSell - a.targetSell
  })
}

function sortForBalancedTier(plans) {
  return plans.slice().sort(function (a, b) {
    if (a.riskAdjustedGilPerDay !== b.riskAdjustedGilPerDay) {
      return b.riskAdjustedGilPerDay - a.riskAdjustedGilPerDay
    }
    if (a.expectedGilPerDay !== b.expectedGilPerDay) {
      return b.expectedGilPerDay - a.expectedGilPerDay
    }
    return b.sellWithinWindowProbability - a.sellWithinWindowProbability
  })
}

function sortForPatientTier(plans) {
  return plans.slice().sort(function (a, b) {
    if (a.targetSell !== b.targetSell) {
      return b.targetSell - a.targetSell
    }
    return b.riskAdjustedGilPerDay - a.riskAdjustedGilPerDay
  })
}

function pickUniquePlan(plans, usedTargets) {
  var safePlans = Array.isArray(plans) ? plans : []
  for (var i = 0; i < safePlans.length; i++) {
    var key = String(safePlans[i].targetSell)
    if (!usedTargets[key]) {
      usedTargets[key] = true
      return safePlans[i]
    }
  }
  return null
}

function cloneSellPricePlanAsTier(plan, exitStyle, exitLabel) {
  var out = {}
  Object.keys(plan).forEach(function (key) {
    out[key] = plan[key]
  })
  out.exitStyle = exitStyle
  out.exitLabel = exitLabel
  return out
}

function pickFireSaleGuidePlan(candidates, fastPlan, sellWindowDays) {
  if (!Array.isArray(candidates) || !candidates.length) {
    return null
  }

  var window = Number.isFinite(sellWindowDays) && sellWindowDays > 0 ? sellWindowDays : 2
  if (!fastPlan) {
    var fallback = candidates.slice().sort(function (a, b) {
      if (a.sellWithinWindowProbability !== b.sellWithinWindowProbability) {
        return b.sellWithinWindowProbability - a.sellWithinWindowProbability
      }
      return b.expectedGilPerDay - a.expectedGilPerDay
    })[0]
    if (fallback && (fallback.sellWithinWindowProbability >= 0.1 || fallback.bandSaleRate > 0)) {
      return cloneSellPricePlanAsTier(fallback, "firesale", "Fire Sale")
    }
    return null
  }

  var fastDays = Number.isFinite(fastPlan.estimatedDaysToFirstSale)
    ? fastPlan.estimatedDaysToFirstSale
    : Number.POSITIVE_INFINITY
  var fireSaleCandidates = candidates.filter(function (candidate) {
    if (!candidate || !(candidate.targetSell < fastPlan.targetSell * 0.99)) {
      return false
    }

    var probabilityGain = candidate.sellWithinWindowProbability - fastPlan.sellWithinWindowProbability
    var candidateDays = Number.isFinite(candidate.estimatedDaysToFirstSale)
      ? candidate.estimatedDaysToFirstSale
      : Number.POSITIVE_INFINITY
    var speedGainDays = fastDays - candidateDays
    return (candidateDays <= window && fastDays > window) ||
      probabilityGain >= 0.12 ||
      speedGainDays >= Math.max(0.5, window * 0.25)
  })

  if (!fireSaleCandidates.length) {
    return null
  }

  var picked = fireSaleCandidates.slice().sort(function (a, b) {
    if (a.sellWithinWindowProbability !== b.sellWithinWindowProbability) {
      return b.sellWithinWindowProbability - a.sellWithinWindowProbability
    }
    var aDays = Number.isFinite(a.estimatedDaysToFirstSale) ? a.estimatedDaysToFirstSale : Number.POSITIVE_INFINITY
    var bDays = Number.isFinite(b.estimatedDaysToFirstSale) ? b.estimatedDaysToFirstSale : Number.POSITIVE_INFINITY
    if (aDays !== bDays) {
      return aDays - bDays
    }
    return b.targetSell - a.targetSell
  })[0]

  return cloneSellPricePlanAsTier(picked, "firesale", "Fire Sale")
}

export function analyzeTargetCompetition(listings, targetSell, options) {
  var settings = options && typeof options === "object" ? options : {}
  var safeListings = normalizeListings(listings)

  if (!safeListings.length || !Number.isFinite(targetSell) || targetSell <= 0) {
    return {
      cheaperUnitsAhead: 0,
      cheaperListingCount: 0,
      samePriceUnitsAhead: 0,
      samePriceListingCount: 0,
      priceBandCrowdingUnits: 0,
      priceBandListingCount: 0,
      rawCompetitionUnits: 0,
      effectiveCompetitionUnits: 0,
      rawCompetitionSaleEvents: 0,
      effectiveCompetitionSaleEvents: 0,
      undercutBandListingCount: 0,
      undercutBandUnits: 0,
      undercutRisk: 0,
      competitionPressureScore: 0,
      priceLadderPressureScore: 0,
      queueAheadUnits: 0,
      queueAheadListingCount: 0,
      nearTargetListingCount: 0,
      nearTargetUnits: 0,
      medianQueueStackQty: null,
      maxQueueStackQty: null,
      effectiveQueueAheadUnits: 0,
      oversizeQueueUnits: 0,
      frontHeavyQueueUnits: 0,
      nearTargetSpreadPct: null
    }
  }

  var preferredStackQty = Number.isFinite(settings.preferredStackQty) && settings.preferredStackQty > 0
    ? Math.max(1, Math.round(settings.preferredStackQty))
    : 1
  var averageUnitsPerSale = Number.isFinite(settings.averageUnitsPerSale) && settings.averageUnitsPerSale > 0
    ? settings.averageUnitsPerSale
    : preferredStackQty
  var nearTargetRatio = Number.isFinite(settings.nearTargetRatio) && settings.nearTargetRatio > 0
    ? settings.nearTargetRatio
    : 0.01
  var samePriceFillWeight = clamp(
    Number.isFinite(settings.samePriceFillWeight) ? settings.samePriceFillWeight : 0.5,
    0.2,
    0.85
  )
  var directAheadListings = []
  var cheaperListings = []
  var samePriceListings = []
  var nearTargetListings = []
  var undercutBandListings = []
  var nearTargetLow = targetSell * (1 - nearTargetRatio)
  var nearTargetHigh = targetSell * (1 + nearTargetRatio)

  safeListings.forEach(function (listing) {
    if (listing.pricePerUnit <= targetSell) {
      directAheadListings.push(listing)
    }
    if (listing.pricePerUnit < targetSell) {
      cheaperListings.push(listing)
    }
    if (listing.pricePerUnit >= nearTargetLow && listing.pricePerUnit <= nearTargetHigh) {
      nearTargetListings.push(listing)
    }
    if (listing.pricePerUnit === targetSell) {
      samePriceListings.push(listing)
    }
    if (listing.pricePerUnit > targetSell && listing.pricePerUnit <= nearTargetHigh) {
      undercutBandListings.push(listing)
    }
  })

  var cheaperUnitsAhead = cheaperListings.reduce(function (sum, listing) {
    return sum + listing.quantity
  }, 0)
  var samePriceUnitsAhead = samePriceListings.reduce(function (sum, listing) {
    return sum + listing.quantity
  }, 0)
  var nearTargetUnits = nearTargetListings.reduce(function (sum, listing) {
    return sum + listing.quantity
  }, 0)
  var undercutBandUnits = undercutBandListings.reduce(function (sum, listing) {
    return sum + listing.quantity
  }, 0)
  var queueAheadUnits = cheaperUnitsAhead + samePriceUnitsAhead
  var queueStackQtys = directAheadListings.map(function (listing) {
    return listing.quantity
  })
  var oversizeQueueUnits = directAheadListings.reduce(function (sum, listing) {
    return sum + Math.max(0, listing.quantity - preferredStackQty)
  }, 0)
  var frontBookListings = directAheadListings.slice(0, Math.min(directAheadListings.length, 5))
  var frontHeavyQueueUnits = frontBookListings.reduce(function (sum, listing) {
    return sum + Math.max(0, listing.quantity - preferredStackQty)
  }, 0)
  var frontBookUnits = frontBookListings.reduce(function (sum, listing) {
    return sum + listing.quantity
  }, 0)
  var competitionBaseQty = Math.max(1, averageUnitsPerSale, preferredStackQty)
  var stackMismatchRatio = oversizeQueueUnits / Math.max(competitionBaseQty, queueAheadUnits || competitionBaseQty)
  var frontBookPressureRatio = frontBookUnits / Math.max(competitionBaseQty, queueAheadUnits || competitionBaseQty)
  var listingDensityRatio = directAheadListings.length / Math.max(1, queueAheadUnits / competitionBaseQty)
  var nearTargetCrowdingRatio = nearTargetUnits / competitionBaseQty
  var samePriceCrowdingRatio = samePriceUnitsAhead / competitionBaseQty
  var priceBandCrowdingUnits = Math.max(0, nearTargetUnits - queueAheadUnits)
  var cheaperSaleEventsAhead = cheaperUnitsAhead / competitionBaseQty
  var samePriceSaleEventsAhead = samePriceUnitsAhead / competitionBaseQty
  var priceBandCrowdingSaleEvents = priceBandCrowdingUnits / competitionBaseQty
  var nearTargetSpreadPct = null

  if (nearTargetListings.length > 1) {
    var minNear = nearTargetListings[0].pricePerUnit
    var maxNear = nearTargetListings[nearTargetListings.length - 1].pricePerUnit
    if (Number.isFinite(minNear) && minNear > 0 && Number.isFinite(maxNear)) {
      nearTargetSpreadPct = (maxNear - minNear) / minNear
    }
  }

  var tightSpreadFactor = nearTargetSpreadPct === null
    ? 0.2
    : clamp((nearTargetRatio - nearTargetSpreadPct) / Math.max(nearTargetRatio, 0.0001), 0, 1)
  var samePriceShapeDiscount = clamp(1 - (Math.max(0, samePriceListings.length - 1) * 0.02), 0.8, 1)
  var samePriceEffectiveUnits = samePriceUnitsAhead * samePriceFillWeight * samePriceShapeDiscount
  var stackWallUnits = oversizeQueueUnits * 0.18
  var undercutRisk = clamp(
    (samePriceListings.length * 0.08) +
    (undercutBandListings.length * 0.06) +
    (Math.max(0, nearTargetCrowdingRatio - 1) * 0.12) +
    (tightSpreadFactor * 0.25),
    0,
    0.95
  )
  var undercutPenaltyUnits = undercutRisk * competitionBaseQty * 0.8
  var rawCompetitionUnits = queueAheadUnits
  var effectiveCompetitionUnits =
    cheaperUnitsAhead +
    samePriceEffectiveUnits +
    (priceBandCrowdingUnits * 0.22) +
    stackWallUnits +
    undercutPenaltyUnits
  var rawCompetitionSaleEvents = cheaperSaleEventsAhead + (samePriceSaleEventsAhead * 0.5)
  var effectiveCompetitionSaleEvents =
    cheaperSaleEventsAhead +
    (samePriceSaleEventsAhead * samePriceFillWeight * samePriceShapeDiscount) +
    (priceBandCrowdingSaleEvents * 0.22) +
    (stackWallUnits / competitionBaseQty) +
    (undercutRisk * 0.8)
  var competitionPressureScore =
    ((samePriceEffectiveUnits / competitionBaseQty) * 0.5) +
    ((priceBandCrowdingUnits / competitionBaseQty) * 0.35) +
    ((stackWallUnits / competitionBaseQty) * 0.25) +
    (undercutRisk * 1.4)

  return {
    cheaperUnitsAhead: cheaperUnitsAhead,
    cheaperListingCount: cheaperListings.length,
    samePriceUnitsAhead: samePriceUnitsAhead,
    samePriceListingCount: samePriceListings.length,
    priceBandCrowdingUnits: priceBandCrowdingUnits,
    priceBandListingCount: nearTargetListings.length,
    rawCompetitionUnits: rawCompetitionUnits,
    effectiveCompetitionUnits: effectiveCompetitionUnits,
    cheaperSaleEventsAhead: cheaperSaleEventsAhead,
    samePriceSaleEventsAhead: samePriceSaleEventsAhead,
    priceBandCrowdingSaleEvents: priceBandCrowdingSaleEvents,
    rawCompetitionSaleEvents: rawCompetitionSaleEvents,
    effectiveCompetitionSaleEvents: effectiveCompetitionSaleEvents,
    undercutBandListingCount: undercutBandListings.length,
    undercutBandUnits: undercutBandUnits,
    undercutRisk: undercutRisk,
    competitionPressureScore: competitionPressureScore,
    priceLadderPressureScore: competitionPressureScore,
    queueAheadUnits: queueAheadUnits,
    queueAheadListingCount: directAheadListings.length,
    nearTargetListingCount: nearTargetListings.length,
    nearTargetUnits: nearTargetUnits,
    medianQueueStackQty: pickPercentile(queueStackQtys, 0.5),
    maxQueueStackQty: queueStackQtys.length ? Math.max.apply(Math, queueStackQtys) : null,
    effectiveQueueAheadUnits: effectiveCompetitionUnits,
    oversizeQueueUnits: oversizeQueueUnits,
    frontHeavyQueueUnits: frontHeavyQueueUnits,
    frontBookUnits: frontBookUnits,
    samePriceUnits: samePriceUnitsAhead,
    stackMismatchRatio: stackMismatchRatio,
    frontBookPressureRatio: frontBookPressureRatio,
    listingDensityRatio: listingDensityRatio,
    nearTargetCrowdingRatio: nearTargetCrowdingRatio,
    queuePressureScore: competitionPressureScore,
    samePriceCrowdingRatio: samePriceCrowdingRatio,
    nearTargetSpreadPct: nearTargetSpreadPct
  }
}

export function buildPriceLadderPressureModel(args) {
  var settings = args && typeof args === "object" ? args : {}
  var competition = settings.competition && typeof settings.competition === "object"
    ? settings.competition
    : {}
  var demandModel = settings.demandModel && typeof settings.demandModel === "object"
    ? settings.demandModel
    : {}
  var holdingDays = Number.isFinite(settings.holdingDays) && settings.holdingDays > 0
    ? settings.holdingDays
    : 2
  var preferredStackQty = Number.isFinite(settings.preferredStackQty) && settings.preferredStackQty > 0
    ? settings.preferredStackQty
    : (Number.isFinite(demandModel.suggestedStackQty) && demandModel.suggestedStackQty > 0
      ? demandModel.suggestedStackQty
      : 1)
  var averageUnitsPerSale = Number.isFinite(demandModel.averageUnitsPerSale) && demandModel.averageUnitsPerSale > 0
    ? demandModel.averageUnitsPerSale
    : preferredStackQty
  var unitRatePerDay = Number.isFinite(demandModel.unitRatePerDay) && demandModel.unitRatePerDay > 0
    ? demandModel.unitRatePerDay
    : 0
  var saleRatePerDay = Number.isFinite(demandModel.saleRatePerDay) && demandModel.saleRatePerDay > 0
    ? demandModel.saleRatePerDay
    : 0
  var meanUnits = Number.isFinite(demandModel.meanUnits) && demandModel.meanUnits > 0
    ? demandModel.meanUnits
    : (unitRatePerDay * Math.max(holdingDays, 1))
  var meanSales = Number.isFinite(demandModel.meanSales) && demandModel.meanSales > 0
    ? demandModel.meanSales
    : (saleRatePerDay * Math.max(holdingDays, 1))
  var demandUnitsWindow = Math.max(1, meanUnits, averageUnitsPerSale, preferredStackQty)
  var demandSalesWindow = Math.max(1, meanSales)
  var cheaperUnitsAhead = Number.isFinite(competition.cheaperUnitsAhead) ? Math.max(0, competition.cheaperUnitsAhead) : 0
  var samePriceUnitsAhead = Number.isFinite(competition.samePriceUnitsAhead) ? Math.max(0, competition.samePriceUnitsAhead) : 0
  var samePriceListingCount = Number.isFinite(competition.samePriceListingCount) ? Math.max(0, competition.samePriceListingCount) : 0
  var cheaperListingCount = Number.isFinite(competition.cheaperListingCount) ? Math.max(0, competition.cheaperListingCount) : 0
  var nearTargetUnits = Number.isFinite(competition.nearTargetUnits) ? Math.max(0, competition.nearTargetUnits) : 0
  var nearTargetListingCount = Number.isFinite(competition.nearTargetListingCount) ? Math.max(0, competition.nearTargetListingCount) : 0
  var priceBandCrowdingUnits = Number.isFinite(competition.priceBandCrowdingUnits) ? Math.max(0, competition.priceBandCrowdingUnits) : 0
  var effectiveCompetitionUnits = Number.isFinite(competition.effectiveCompetitionUnits)
    ? Math.max(0, competition.effectiveCompetitionUnits)
    : (cheaperUnitsAhead + samePriceUnitsAhead)
  var effectiveCompetitionSaleEvents = Number.isFinite(competition.effectiveCompetitionSaleEvents)
    ? Math.max(0, competition.effectiveCompetitionSaleEvents)
    : ((cheaperUnitsAhead + samePriceUnitsAhead) / Math.max(averageUnitsPerSale, preferredStackQty, 1))
  var stackMismatchRatio = Number.isFinite(competition.stackMismatchRatio) ? Math.max(0, competition.stackMismatchRatio) : 0
  var frontBookPressureRatio = Number.isFinite(competition.frontBookPressureRatio) ? Math.max(0, competition.frontBookPressureRatio) : 0
  var listingDensityRatio = Number.isFinite(competition.listingDensityRatio) ? Math.max(0, competition.listingDensityRatio) : 0
  var samePriceCrowdingRatio = Number.isFinite(competition.samePriceCrowdingRatio) ? Math.max(0, competition.samePriceCrowdingRatio) : 0
  var undercutRisk = clamp(Number.isFinite(competition.undercutRisk) ? competition.undercutRisk : 0, 0, 1)
  var cheaperUnitPressureScore = clamp(
    cheaperUnitsAhead / Math.max(demandUnitsWindow * 0.9, averageUnitsPerSale, preferredStackQty, 1),
    0,
    2.5
  )
  var samePriceUnitPressureScore = clamp(
    samePriceUnitsAhead / Math.max(demandUnitsWindow * 1.15, preferredStackQty, 1),
    0,
    2.25
  )
  var samePriceCongestionScore = clamp(
    (samePriceUnitPressureScore * 0.72) +
    (Math.max(0, samePriceListingCount - 1) / Math.max(2, Math.min(6, Math.ceil(holdingDays * 2)))) * 0.28,
    0,
    1.8
  )
  var nearPriceCrowdingScore = clamp(
    ((priceBandCrowdingUnits / Math.max(demandUnitsWindow, averageUnitsPerSale, 1)) * 0.65) +
    ((Math.max(0, nearTargetListingCount - Math.max(1, cheaperListingCount + samePriceListingCount)) / 4) * 0.35),
    0,
    1.6
  )
  var effectiveUnitPressureScore = clamp(
    effectiveCompetitionUnits / Math.max(demandUnitsWindow * 1.05, averageUnitsPerSale, preferredStackQty, 1),
    0,
    3
  )
  var effectiveSalePressureScore = clamp(
    effectiveCompetitionSaleEvents / Math.max(demandSalesWindow * 1.1, 1),
    0,
    2.5
  )
  var frontBookFragilityScore = clamp(
    (Math.max(0, samePriceCrowdingRatio - 0.55) * 0.3) +
    (Math.max(0, stackMismatchRatio - 0.2) * 0.28) +
    (Math.max(0, frontBookPressureRatio - 1) * 0.24) +
    (Math.max(0, listingDensityRatio - 1) * 0.16),
    0,
    1.8
  )
  var stackWallRiskScore = clamp(Math.max(0, stackMismatchRatio - 0.15), 0, 1.5)
  var supplyOverhangNearTargetDays = unitRatePerDay > 0
    ? (nearTargetUnits / unitRatePerDay)
    : Number.POSITIVE_INFINITY
  var supplyOverhangNearTargetScore = Number.isFinite(supplyOverhangNearTargetDays)
    ? clamp(
      (supplyOverhangNearTargetDays - Math.max(holdingDays * 0.65, 1)) /
      Math.max(holdingDays * 1.75, 1.5),
      0,
      1.5
    )
    : 0
  var exitFragilityScore = clamp(
    (undercutRisk * 0.52) +
    (nearPriceCrowdingScore * 0.2) +
    (samePriceCongestionScore * 0.16) +
    (frontBookFragilityScore * 0.14) +
    (Math.max(0, supplyOverhangNearTargetScore - 0.2) * 0.12),
    0,
    1.5
  )
  var competitionPressureScore = clamp(
    (cheaperUnitPressureScore * 0.36) +
    (samePriceCongestionScore * 0.24) +
    (nearPriceCrowdingScore * 0.16) +
    (effectiveUnitPressureScore * 0.12) +
    (effectiveSalePressureScore * 0.08) +
    (frontBookFragilityScore * 0.14) +
    (supplyOverhangNearTargetScore * 0.12) +
    (exitFragilityScore * 0.12),
    0,
    3
  )
  var targetConfidenceScale = clamp(
    1 - Math.min(
      0.28,
      (undercutRisk * 0.12) +
      (nearPriceCrowdingScore * 0.05) +
      (samePriceCongestionScore * 0.045) +
      (stackWallRiskScore * 0.06) +
      (effectiveSalePressureScore * 0.025) +
      (frontBookFragilityScore * 0.03) +
      (Math.max(0, supplyOverhangNearTargetScore - 0.35) * 0.03)
    ),
    0.72,
    1
  )
  var saleRateScale = clamp(
    1 - Math.min(
      0.68,
      (cheaperUnitPressureScore * 0.14) +
      (samePriceCongestionScore * 0.08) +
      (nearPriceCrowdingScore * 0.05) +
      (stackWallRiskScore * 0.03) +
      (effectiveSalePressureScore * 0.035) +
      (frontBookFragilityScore * 0.05) +
      (exitFragilityScore * 0.08)
    ),
    0.32,
    1
  )
  var unitRateScale = clamp(
    1 - Math.min(
      0.74,
      (cheaperUnitPressureScore * 0.17) +
      (samePriceCongestionScore * 0.11) +
      (nearPriceCrowdingScore * 0.06) +
      (stackWallRiskScore * 0.08) +
      (effectiveUnitPressureScore * 0.07) +
      (frontBookFragilityScore * 0.08) +
      (supplyOverhangNearTargetScore * 0.07) +
      (exitFragilityScore * 0.06)
    ),
    0.25,
    1
  )
  var probabilityScale = clamp((unitRateScale * 0.68) + (targetConfidenceScale * 0.32), 0.28, 1)
  var firstSaleScale = clamp((saleRateScale * 0.74) + (targetConfidenceScale * 0.26), 0.3, 1)
  var qtyScale = clamp(
    1 - Math.min(
      0.42,
      (competitionPressureScore * 0.12) +
      (stackWallRiskScore * 0.05) +
      (frontBookFragilityScore * 0.05) +
      (supplyOverhangNearTargetScore * 0.06) +
      (undercutRisk * 0.06)
    ),
    0.58,
    1
  )
  var holdWindowScale = clamp(
    1 - Math.min(
      0.22,
      (exitFragilityScore * 0.08) +
      (stackWallRiskScore * 0.02) +
      (frontBookFragilityScore * 0.03) +
      (nearPriceCrowdingScore * 0.04) +
      (samePriceCongestionScore * 0.03)
    ),
    0.8,
    1
  )
  var estimatedDaysToFirstSale = saleRatePerDay > 0 && firstSaleScale > 0
    ? (1 / Math.max(saleRatePerDay * firstSaleScale, 0.0001))
    : Number.POSITIVE_INFINITY
  var estimatedDaysToClearSuggestedStack = unitRatePerDay > 0 && unitRateScale > 0
    ? (preferredStackQty / Math.max(unitRatePerDay * unitRateScale * targetConfidenceScale, 0.0001))
    : Number.POSITIVE_INFINITY
  var baseDaysToClearSuggestedStack = unitRatePerDay > 0
    ? (preferredStackQty / unitRatePerDay)
    : Number.POSITIVE_INFINITY
  var competitionDelayDays =
    Number.isFinite(estimatedDaysToClearSuggestedStack) && Number.isFinite(baseDaysToClearSuggestedStack)
      ? Math.max(0, estimatedDaysToClearSuggestedStack - baseDaysToClearSuggestedStack)
      : 0

  return {
    cheaperUnitPressureScore: cheaperUnitPressureScore,
    samePriceUnitPressureScore: samePriceUnitPressureScore,
    samePriceCongestionScore: samePriceCongestionScore,
    nearPriceCrowdingScore: nearPriceCrowdingScore,
    effectiveUnitPressureScore: effectiveUnitPressureScore,
    effectiveSalePressureScore: effectiveSalePressureScore,
    frontBookFragilityScore: frontBookFragilityScore,
    stackWallRiskScore: stackWallRiskScore,
    supplyOverhangNearTargetDays: supplyOverhangNearTargetDays,
    supplyOverhangNearTargetScore: supplyOverhangNearTargetScore,
    demandUnitsWindow: demandUnitsWindow,
    demandSalesWindow: demandSalesWindow,
    saleRateScale: saleRateScale,
    unitRateScale: unitRateScale,
    probabilityScale: probabilityScale,
    firstSaleScale: firstSaleScale,
    targetConfidenceScale: targetConfidenceScale,
    qtyScale: qtyScale,
    holdWindowScale: holdWindowScale,
    competitionPressureScore: competitionPressureScore,
    priceLadderPressureScore: competitionPressureScore,
    exitFragilityScore: exitFragilityScore,
    estimatedDaysToFirstSale: estimatedDaysToFirstSale,
    estimatedDaysToClearSuggestedStack: estimatedDaysToClearSuggestedStack,
    competitionDelayDays: competitionDelayDays
  }
}

var HIGH_CONFIDENCE_TAPER_SETTINGS = {
  startProbability: 0.8,
  hotBucketWeight: 1,
  steadyBucketWeight: 0.84,
  maxReduction: 0.13
}

export function applyHighConfidenceLiquidityTaper(probability, options) {
  var baseProbability = clamp(Number.isFinite(probability) ? probability : 0, 0, 1)
  var settings = options && typeof options === "object" ? options : {}
  var bucketKey = settings.liquidityBucketKey || null

  if ((bucketKey !== "hot" && bucketKey !== "steady") || baseProbability <= HIGH_CONFIDENCE_TAPER_SETTINGS.startProbability) {
    return {
      probability: baseProbability,
      scale: 1,
      applied: false,
      bucketKey: bucketKey,
      stressScore: 0,
      excessConfidence: 0,
      hotInstabilityScore: 0
    }
  }

  var queuePressureScore = Number.isFinite(settings.queuePressureScore) ? settings.queuePressureScore : 0
  var exitFragilityScore = Number.isFinite(settings.exitFragilityScore) ? settings.exitFragilityScore : 0
  var samePriceCongestionScore = Number.isFinite(settings.samePriceCongestionScore) ? settings.samePriceCongestionScore : 0
  var nearPriceCrowdingScore = Number.isFinite(settings.nearPriceCrowdingScore) ? settings.nearPriceCrowdingScore : 0
  var frontBookFragilityScore = Number.isFinite(settings.frontBookFragilityScore) ? settings.frontBookFragilityScore : 0
  var stackWallRiskScore = Number.isFinite(settings.stackWallRiskScore) ? settings.stackWallRiskScore : 0
  var targetConfidenceScale = Number.isFinite(settings.targetConfidenceScale) ? settings.targetConfidenceScale : 1
  var demandDispersionRatio = Number.isFinite(settings.demandDispersionRatio) ? settings.demandDispersionRatio : 1
  var demandRecencyBlendWeight = Number.isFinite(settings.demandRecencyBlendWeight) ? settings.demandRecencyBlendWeight : 0
  var demandModelType = typeof settings.demandModelType === "string" ? settings.demandModelType : ""
  var queueStress = clamp(queuePressureScore / 0.9, 0, 1)
  var exitStress = clamp(exitFragilityScore / 0.9, 0, 1)
  var samePriceStress = clamp(samePriceCongestionScore / 1.35, 0, 1)
  var nearPriceStress = clamp(nearPriceCrowdingScore / 1, 0, 1)
  var frontBookStress = clamp(frontBookFragilityScore / 1.2, 0, 1)
  var stackWallStress = clamp(stackWallRiskScore / 1.05, 0, 1)
  var supportGap = clamp((0.97 - targetConfidenceScale) / 0.24, 0, 1)
  var dispersionStress = clamp((demandDispersionRatio - 1.4) / 3.6, 0, 1)
  var recencyStress = clamp((demandRecencyBlendWeight - 0.28) / 0.38, 0, 1)
  var hotInstabilityScore = bucketKey === "hot"
    ? clamp(
      ((demandModelType === "negative_binomial" ? 0.58 : 0.14) * dispersionStress) +
      (recencyStress * 0.42),
      0,
      1
    )
    : 0
  var stressScore = clamp(
    (queueStress * 0.2) +
    (exitStress * 0.2) +
    (samePriceStress * 0.14) +
    (nearPriceStress * 0.08) +
    (frontBookStress * 0.12) +
    (stackWallStress * 0.08) +
    (supportGap * 0.18) +
    (hotInstabilityScore * 0.22),
    0,
    1
  )
  var excessConfidence = clamp(
    (baseProbability - HIGH_CONFIDENCE_TAPER_SETTINGS.startProbability) / 0.17,
    0,
    1
  )
  var confidencePressure = 0.35 + (0.65 * excessConfidence)
  var bucketWeight = bucketKey === "hot"
    ? HIGH_CONFIDENCE_TAPER_SETTINGS.hotBucketWeight
    : HIGH_CONFIDENCE_TAPER_SETTINGS.steadyBucketWeight
  var reduction = Math.min(
    HIGH_CONFIDENCE_TAPER_SETTINGS.maxReduction,
    HIGH_CONFIDENCE_TAPER_SETTINGS.maxReduction * bucketWeight * stressScore * confidencePressure
  )
  var scale = clamp(1 - reduction, 0.87, 1)

  return {
    probability: clamp(baseProbability * scale, 0, 1),
    scale: scale,
    applied: reduction > 0.0005,
    bucketKey: bucketKey,
    stressScore: stressScore,
    excessConfidence: excessConfidence,
    hotInstabilityScore: hotInstabilityScore
  }
}

const purchasePlanCache = new WeakMap()

export function buildPurchasePlan(listings, desiredQty, travelFee, options) {
  const settings = options || {}
  const targetQty = Number.isInteger(desiredQty) && desiredQty > 0 ? desiredQty : 1
  const fixedFee = Math.max(0, Number(settings.fixedTravelFee) || 0)
  const unitFee = Math.max(0, Number(travelFee) || 0)
  const cacheKey = [targetQty, unitFee, fixedFee].join(":")
  let cached = Array.isArray(listings) ? purchasePlanCache.get(listings) : null
  if (cached && cached.has(cacheKey)) return structuredClone(cached.get(cacheKey))
  const safe = normalizeListings(listings)
  if (!safe.length) return null
  // Keep cheap and small stacks so a cheap large stack cannot hide an executable small purchase.
  const pool = Array.from(new Set(safe.slice(0, 20).concat(safe.slice().sort((a,b) => a.quantity-b.quantity || a.pricePerUnit-b.pricePerUnit).slice(0, 20))))
  let states = new Map([[0, { qty: 0, cost: 0, used: [] }]])
  let best = null
  for (const listing of pool) {
    for (const state of Array.from(states.values())) {
      const qty = state.qty + listing.quantity
      const cost = state.cost + listing.quantity * (listing.pricePerUnit + unitFee)
      const next = { qty, cost, used: state.used.concat(listing) }
      if (qty >= targetQty) {
        if (!best || cost < best.cost || (cost === best.cost && qty < best.qty)) best = next
      } else if ((!best || cost < best.cost) && (!states.has(qty) || cost < states.get(qty).cost)) states.set(qty, next)
    }
    if (states.size > 1024) states = new Map(Array.from(states.entries()).sort((a,b) => a[1].cost/Math.max(a[0],1)-b[1].cost/Math.max(b[0],1)).slice(0,1024))
  }
  if (!best) best = Array.from(states.values()).sort((a,b) => b.qty-a.qty || a.cost-b.cost)[0]
  if (!best || !best.qty) return null
  const used = best.used.slice().sort((a,b) => a.pricePerUnit-b.pricePerUnit)
  const firstUnitPrice = used[0].pricePerUnit
  const marginalUnitPrice = used[used.length-1].pricePerUnit
  const totalListingCost = used.reduce((sum,row) => sum + row.quantity*row.pricePerUnit,0)
  const totalCost = best.cost + fixedFee
  const fulfilledQty = Math.min(targetQty,best.qty)
  const priceImpactRatio = Math.max(0,(marginalUnitPrice-firstUnitPrice)/firstUnitPrice)
  const distinctPrices = new Set(used.map(row => row.pricePerUnit))
  const plan = {
    requestedQty:targetQty, fulfilledQty, purchasedQty:best.qty, excessQty:Math.max(0,best.qty-targetQty),
    availableQty:safe.reduce((sum,row)=>sum+row.quantity,0), firstUnitPrice, marginalUnitPrice,
    qtyAtBestPrice:used.filter(row=>row.pricePerUnit===firstUnitPrice).reduce((sum,row)=>sum+row.quantity,0),
    listingsUsed:used.length, partialListingsUsed:0, distinctPriceCount:distinctPrices.size,
    priceImpactRatio, executionBurden:Math.max(0,used.length-1)*0.35 + Math.max(0,distinctPrices.size-1)*0.12 + priceImpactRatio*4,
    avgListingPrice:totalListingCost/best.qty, avgUnitCost:totalCost/fulfilledQty,
    purchasedUnitCost:totalCost/best.qty, totalListingCost, totalCost,
    usedListings:used.map(row=>({listingId:row.listingId || null,worldName:row.worldName || "",quantity:row.quantity,pricePerUnit:row.pricePerUnit,hq:row.hq,lastReviewTimeSec:row.lastReviewTimeSec || null}))
  }
  if (Array.isArray(listings)) {
    if (!cached) { cached=new Map();purchasePlanCache.set(listings,cached) }
    cached.set(cacheKey,plan)
  }
  return structuredClone(plan)
}

export function pickBestRemotePurchasePlan(worldMap, homeWorld, desiredQty, travelFee, options) {
  var safeWorldMap = worldMap && typeof worldMap === "object" ? worldMap : {}
  var settings = options && typeof options === "object" ? options : {}
  var allowPartial = settings.allowPartial !== false
  var executionPenaltyGil = Number.isFinite(settings.executionPenaltyGil) && settings.executionPenaltyGil >= 0
    ? settings.executionPenaltyGil
    : 0
  var maxExecutionBurden = Number.isFinite(settings.maxExecutionBurden) && settings.maxExecutionBurden >= 0
    ? settings.maxExecutionBurden
    : Number.POSITIVE_INFINITY
  var bestPlan = null

  Object.keys(safeWorldMap).forEach(function (worldName) {
    if (!worldName || worldName === homeWorld) {
      return
    }

    var worldData = safeWorldMap[worldName] || {}
    var plan = buildPurchasePlan(worldData.listings, desiredQty, travelFee, settings)
    if (!plan) {
      return
    }
    if (!allowPartial && plan.fulfilledQty < plan.requestedQty) {
      return
    }
    if (plan.executionBurden > maxExecutionBurden) {
      return
    }

    plan.worldName = worldName
    plan.comparisonCost = plan.totalCost + (plan.executionBurden * executionPenaltyGil)
    if (!bestPlan) {
      bestPlan = plan
      return
    }

    var planComplete = plan.fulfilledQty >= plan.requestedQty
    var bestComplete = bestPlan.fulfilledQty >= bestPlan.requestedQty
    if (planComplete !== bestComplete) {
      if (planComplete) {
        bestPlan = plan
      }
      return
    }

    if (plan.fulfilledQty !== bestPlan.fulfilledQty) {
      if (plan.fulfilledQty > bestPlan.fulfilledQty) {
        bestPlan = plan
      }
      return
    }

    if (plan.comparisonCost !== bestPlan.comparisonCost) {
      if (plan.comparisonCost < bestPlan.comparisonCost) {
        bestPlan = plan
      }
      return
    }

    if (plan.executionBurden !== bestPlan.executionBurden) {
      if (plan.executionBurden < bestPlan.executionBurden) {
        bestPlan = plan
      }
      return
    }

    if (plan.avgUnitCost !== bestPlan.avgUnitCost) {
      if (plan.avgUnitCost < bestPlan.avgUnitCost) {
        bestPlan = plan
      }
      return
    }

    if (plan.firstUnitPrice < bestPlan.firstUnitPrice) {
      bestPlan = plan
    }
  })

  return bestPlan
}

export function buildScannerBasket(rows, options) {
  var settings = options && typeof options === "object" ? options : {}
  var safeRows = Array.isArray(rows)
    ? rows.filter(function (row) {
      return row &&
        Number.isFinite(row.planExpectedProfit) &&
        row.planExpectedProfit > 0 &&
        Number.isFinite(row.planCost) &&
        row.planCost > 0
    })
    : []
  var budgetGil = Number.isFinite(settings.budgetGil) && settings.budgetGil > 0 ? settings.budgetGil : null
  var maxPicks = Number.isInteger(settings.maxPicks) && settings.maxPicks > 0 ? settings.maxPicks : (budgetGil ? 5 : 3)
  var maxPerWorld = Number.isInteger(settings.maxPerWorld) && settings.maxPerWorld > 0 ? settings.maxPerWorld : 2
  var maxWorldsVisited = Number.isInteger(settings.maxWorldsVisited) && settings.maxWorldsVisited > 0
    ? settings.maxWorldsVisited
    : (budgetGil ? 3 : 2)
  var searchCandidateLimit = Number.isInteger(settings.searchCandidateLimit) && settings.searchCandidateLimit > 0
    ? settings.searchCandidateLimit
    : (budgetGil ? 24 : 16)
  var capitalEfficiencyValues = safeRows.map(function (row) {
    return Number.isFinite(row.capitalEfficiency) ? row.capitalEfficiency : 0
  })
  var gilPerDayValues = safeRows.map(function (row) {
    return Number.isFinite(row.expectedGilPerDay) ? row.expectedGilPerDay : 0
  })
  capitalEfficiencyValues.sort((a,b)=>a-b)
  gilPerDayValues.sort((a,b)=>a-b)
  var candidates = safeRows.map(function (row) {
    var evaluation = evaluateScannerBasketRow(row, {
      capitalEfficiencyValues: capitalEfficiencyValues,
      gilPerDayValues: gilPerDayValues
    })
    return {
      row: row,
      baseScore: Number.isFinite(evaluation.score) ? evaluation.score : 0,
      confidence: Number.isFinite(evaluation.confidence) ? evaluation.confidence : 0
    }
  }).filter(function (candidate) {
    return candidate.baseScore > 0
  }).sort(function (a, b) {
    var aValue = (Number(a.row.planExpectedProfit) || 0) * (0.7 + (0.3 * a.confidence))
    var bValue = (Number(b.row.planExpectedProfit) || 0) * (0.7 + (0.3 * b.confidence))
    if (aValue !== bValue) {
      return bValue - aValue
    }
    if (a.baseScore !== b.baseScore) {
      return b.baseScore - a.baseScore
    }
    return (Number(b.row.expectedGilPerDay) || 0) - (Number(a.row.expectedGilPerDay) || 0)
  }).slice(0, searchCandidateLimit)

  var bestSummary = summarizeScannerBasketSelections([], budgetGil)
  var current = []

  exploreSelections(0, 0, {})

  return bestSummary

  function exploreSelections(startIndex, totalCost, sameWorldCounts) {
    if (current.length) {
      var currentSummary = summarizeScannerBasketSelections(current, budgetGil)
      if (isBetterScannerBasketSummary(currentSummary, bestSummary)) {
        bestSummary = currentSummary
      }
    }

    if (current.length >= maxPicks) {
      return
    }

    for (var i = startIndex; i < candidates.length; i++) {
      var candidate = candidates[i]
      var row = candidate.row
      if (!row) {
        continue
      }

      var worldName = row.buyWorld || ""
      if ((sameWorldCounts[worldName] || 0) >= maxPerWorld) {
        continue
      }
      if (!sameWorldCounts[worldName] && Object.keys(sameWorldCounts).length >= maxWorldsVisited) {
        continue
      }

      var candidateCost = Number(row.planCost) || 0
      if (budgetGil && (totalCost + candidateCost) > budgetGil) {
        continue
      }

      if (current.some(picked => picked.row.id === row.id && picked.row.quality === row.quality)) continue
      current.push(candidate)
      sameWorldCounts[worldName] = (sameWorldCounts[worldName] || 0) + 1
      exploreSelections(i + 1, totalCost + candidateCost, sameWorldCounts)
      current.pop()
      sameWorldCounts[worldName] -= 1
      if (!sameWorldCounts[worldName]) {
        delete sameWorldCounts[worldName]
      }
    }
  }
}

export function selectSmartScannerProfile(profileResults, options) {
  var settings = options && typeof options === "object" ? options : {}
  var safeResults = Array.isArray(profileResults)
    ? profileResults.filter(function (entry) {
      return entry &&
        entry.profile &&
        Array.isArray(entry.rows) &&
        entry.rows.length
    })
    : []
  var best = null

  safeResults.forEach(function (entry) {
    var basket = buildScannerBasket(entry.rows, settings)
    var visibleRows = Array.isArray(entry.rows) ? entry.rows : []
    var rowCoverage = Math.min(visibleRows.length, 8)
    var basketPicks = basket.rows.length
    var score = Number.isFinite(basket.basketScore) ? basket.basketScore : 0
    var coverageBonus = 0.85 + (rowCoverage * 0.05)
    var pickBonus = 0.9 + (Math.min(4, basketPicks) * 0.08)
    var confidenceBonus = 0.75 + clamp(Number(basket.averageSellProb) || 0, 0, 1)
    var profileScore = score * coverageBonus * pickBonus * confidenceBonus

    if (visibleRows.length) {
      var topValue = Math.sqrt(
        Math.max(0, Number(visibleRows[0].expectedGilPerDay) || 0) *
        Math.max(0, Number(visibleRows[0].planExpectedProfit) || 0)
      )
      profileScore += Math.min(25, topValue / 2500)
    }

    if (settings.budgetGil && !basket.rows.length) return
    if (visibleRows.length < 4) {
      profileScore *= 0.7
    }

    if (
      !best ||
      profileScore > best.profileScore ||
      (
        profileScore === best.profileScore &&
        (basket.totalExpectedGilPerDay || 0) > (best.basket.totalExpectedGilPerDay || 0)
      )
    ) {
      best = {
        profile: entry.profile,
        rows: visibleRows,
        basket: basket,
        profileScore: profileScore
      }
    }
  })

  return best
}

function summarizeScannerBasketSelections(selections, budgetGil) {
  var sameWorldCounts = {}
  var lowConfidencePicked = 0
  var totalCost = 0
  var totalExpectedProfit = 0
  var totalExpectedGilPerDay = 0
  var totalSellProb = 0
  var totalBacktestBias = 0
  var totalExecutionBurden = 0
  var totalScore = 0
  var picked = Array.isArray(selections) ? selections.slice() : []

  picked.forEach(function (selection) {
    var row = selection && selection.row ? selection.row : null
    if (!row) {
      return
    }

    var worldCount = sameWorldCounts[row.buyWorld] || 0
    var diversificationPenalty = 1 / (1 + (0.75 * worldCount))
    var lowConfidencePenalty = selection.confidence < 0.75
      ? Math.pow(0.6, lowConfidencePicked)
      : 1
    var executionPenalty = 1 / (1 + (Math.max(0, Number(row.executionBurden) || 0) * 0.25))
    var candidateScore = selection.baseScore * diversificationPenalty * lowConfidencePenalty * executionPenalty

    totalCost += Number(row.planCost) || 0
    totalExpectedProfit += Number(row.planExpectedProfit) || 0
    totalExpectedGilPerDay += Number(row.expectedGilPerDay) || 0
    totalSellProb += Number(row.sellProbAtRecommended) || 0
    totalBacktestBias += Number(row.backtestBias) || 0
    totalExecutionBurden += Number(row.executionBurden) || 0
    totalScore += Number(candidateScore) || 0

    sameWorldCounts[row.buyWorld] = worldCount + 1
    if (selection.confidence < 0.75) {
      lowConfidencePicked += 1
    }
  })

  return {
    rows: picked.map(function (selection) { return selection.row }),
    selections: picked.slice(),
    budgetGil: budgetGil,
    remainingBudgetGil: budgetGil ? Math.max(0, budgetGil - totalCost) : null,
    totalCost: totalCost,
    totalExpectedProfit: totalExpectedProfit,
    totalExpectedGilPerDay: totalExpectedGilPerDay,
    averageSellProb: picked.length ? (totalSellProb / picked.length) : 0,
    averageBacktestBias: picked.length ? (totalBacktestBias / picked.length) : 0,
    totalExecutionBurden: totalExecutionBurden,
    averageExecutionBurden: picked.length ? (totalExecutionBurden / picked.length) : 0,
    distinctWorldsVisited: Object.keys(sameWorldCounts).length,
    basketScore: totalScore
  }
}

function isBetterScannerBasketSummary(candidate, best) {
  if (!candidate || !candidate.rows || !candidate.rows.length) {
    return !!(!best || !best.rows || !best.rows.length)
  }
  if (!best || !best.rows || !best.rows.length) {
    return true
  }
  if ((candidate.totalExpectedProfit || 0) !== (best.totalExpectedProfit || 0)) {
    return (candidate.totalExpectedProfit || 0) > (best.totalExpectedProfit || 0)
  }
  if ((candidate.totalExpectedGilPerDay || 0) !== (best.totalExpectedGilPerDay || 0)) {
    return (candidate.totalExpectedGilPerDay || 0) > (best.totalExpectedGilPerDay || 0)
  }
  if ((candidate.basketScore || 0) !== (best.basketScore || 0)) {
    return (candidate.basketScore || 0) > (best.basketScore || 0)
  }
  if ((candidate.averageSellProb || 0) !== (best.averageSellProb || 0)) {
    return (candidate.averageSellProb || 0) > (best.averageSellProb || 0)
  }
  if ((candidate.totalCost || 0) !== (best.totalCost || 0)) {
    return (candidate.totalCost || 0) > (best.totalCost || 0)
  }
  return (candidate.rows.length || 0) > (best.rows.length || 0)
}

function getDepthCapPrice(listings, preferredStackQty) {
  var safeListings = normalizeListings(listings)

  if (!safeListings.length) {
    return null
  }

  var qtyDepthTarget = Math.max(2, Math.round(preferredStackQty))
  var listingDepthTarget = Math.min(safeListings.length, Math.max(2, Math.min(5, qtyDepthTarget)))
  var cumulativeQty = 0
  var priceByQtyDepth = null

  for (var i = 0; i < safeListings.length; i++) {
    cumulativeQty += safeListings[i].quantity
    if (cumulativeQty >= qtyDepthTarget) {
      priceByQtyDepth = safeListings[i].pricePerUnit
      break
    }
  }

  var priceByListingDepth = safeListings[listingDepthTarget - 1].pricePerUnit
  if (Number.isFinite(priceByQtyDepth) && Number.isFinite(priceByListingDepth)) {
    return Math.min(priceByQtyDepth, priceByListingDepth)
  }
  if (Number.isFinite(priceByQtyDepth)) {
    return priceByQtyDepth
  }
  return Number.isFinite(priceByListingDepth) ? priceByListingDepth : safeListings[0].pricePerUnit
}

function buildHybridExitPlanVariant(args) {
  var settings = args && typeof args === "object" ? args : {}
  var exitStyle = settings.exitStyle === "fast" || settings.exitStyle === "patient"
    ? settings.exitStyle
    : "balanced"
  var holdingDays = Number.isFinite(settings.holdingDays) && settings.holdingDays > 0
    ? settings.holdingDays
    : 2
  var targetCapRatio = Number.isFinite(settings.targetCapRatio) ? Math.max(0, settings.targetCapRatio) : 0.1
  var preferredStackQty = Number.isFinite(settings.preferredStackQty) && settings.preferredStackQty > 0
    ? settings.preferredStackQty
    : 1
  var homeListings = Array.isArray(settings.homeListings) ? settings.homeListings : []
  var referencePoint = settings.referencePoint && typeof settings.referencePoint === "object"
    ? settings.referencePoint
    : {
      source: null,
      min: null,
      median: null,
      anchor: null,
      anchorFast: null,
      anchorBalanced: null,
      anchorPatient: null
    }
  var marketFragilityScore = clamp(Number.isFinite(settings.marketFragilityScore) ? settings.marketFragilityScore : 0, 0, 1)
  var fragilityDiscountRatio = getMarketFragilityTargetDiscountRatio(exitStyle, marketFragilityScore)
  var anchorInfo = pickExitAnchor(referencePoint, exitStyle)
  var targetSell = Number.isFinite(anchorInfo.value) ? anchorInfo.value : null
  var targetBasis = anchorInfo.basis
  var depthCapPrice = getDepthCapPrice(homeListings, preferredStackQty)
  var depthLiftRatio = getExitDepthLiftRatio(exitStyle, holdingDays, targetCapRatio) * (1 - (marketFragilityScore * 0.65))
  var depthSoftCapPrice = Number.isFinite(depthCapPrice)
    ? depthCapPrice * (1 + depthLiftRatio)
    : null
  var targetWasCapped = false
  var targetCapReason = null

  if (!Number.isFinite(targetSell) && Number.isFinite(depthSoftCapPrice)) {
    targetSell = depthSoftCapPrice
    targetBasis = exitStyle === "fast" ? "front_book" : "depth_cap"
  }

  if (Number.isFinite(depthSoftCapPrice) && Number.isFinite(targetSell) && targetSell > depthSoftCapPrice) {
    targetSell = depthSoftCapPrice
    targetWasCapped = true
    targetCapReason = "depth"
  }

  if (Number.isFinite(referencePoint.median) && Number.isFinite(targetSell)) {
    var medianCapPrice = referencePoint.median * (1 + targetCapRatio)
    if (targetSell > medianCapPrice) {
      targetSell = medianCapPrice
      targetWasCapped = true
      if (!targetCapReason) {
        targetCapReason = "median"
      }
    }
  }

  if (Number.isFinite(targetSell) && fragilityDiscountRatio > 0) {
    targetSell = targetSell * (1 - fragilityDiscountRatio)
  }

  targetSell = roundGilPrice(targetSell)
  var upperBound = Math.max(
    1,
    roundGilPrice(Number.isFinite(referencePoint.median)
      ? referencePoint.median * (1 + targetCapRatio)
      : (Number.isFinite(depthSoftCapPrice) ? depthSoftCapPrice : targetSell))
  )
  if (fragilityDiscountRatio > 0 && Number.isFinite(upperBound)) {
    upperBound = Math.max(1, roundGilPrice(upperBound * (1 - (fragilityDiscountRatio * 0.9))))
  }
  var lowerBound = Math.max(
    1,
    roundGilPrice(Number.isFinite(referencePoint.min)
      ? Math.min(referencePoint.min, targetSell)
      : targetSell)
  )
  var candidatePrices = buildDiscretePriceCandidates({
    exitStyle: exitStyle,
    baseTarget: targetSell,
    lowerBound: exitStyle === "fast" && Number.isFinite(referencePoint.min)
      ? Math.max(1, referencePoint.min - 2)
      : lowerBound,
    upperBound: upperBound,
    homeListings: homeListings,
    referencePoint: referencePoint,
    depthCapPrice: depthCapPrice,
    depthSoftCapPrice: depthSoftCapPrice
  })

  return {
    exitStyle: exitStyle,
    exitLabel: getExitStyleLabel(exitStyle),
    referenceSource: referencePoint.source,
    referenceMin: Number.isFinite(referencePoint.min) ? referencePoint.min : null,
    referenceMedian: Number.isFinite(referencePoint.median) ? referencePoint.median : null,
    saleAnchor: Number.isFinite(referencePoint.anchorBalanced) ? referencePoint.anchorBalanced : (Number.isFinite(referencePoint.anchor) ? referencePoint.anchor : null),
    depthCapPrice: Number.isFinite(depthCapPrice) ? depthCapPrice : null,
    depthSoftCapPrice: Number.isFinite(depthSoftCapPrice) ? depthSoftCapPrice : null,
    targetSell: Number.isFinite(targetSell) && targetSell > 0 ? targetSell : null,
    candidatePrices: candidatePrices,
    marketFragilityScore: marketFragilityScore,
    targetBasis: targetBasis,
    targetWasCapped: targetWasCapped,
    targetCapReason: targetCapReason
  }
}

function evaluateScannerBasketRow(row, stats) {
  var safeStats = stats && typeof stats === "object" ? stats : {}
  var planExpectedProfit = Number.isFinite(row && row.planExpectedProfit) ? row.planExpectedProfit : 0
  var planCost = Number.isFinite(row && row.planCost) && row.planCost > 0 ? row.planCost : 0
  var capitalEfficiencyRank = getPercentileRank(
    Number.isFinite(row && row.capitalEfficiency) ? row.capitalEfficiency : 0,
    safeStats.capitalEfficiencyValues
  )
  var gilPerDayRank = getPercentileRank(
    Number.isFinite(row && row.expectedGilPerDay) ? row.expectedGilPerDay : 0,
    safeStats.gilPerDayValues
  )
  var baseScore = planCost > 0
    ? (planExpectedProfit / Math.sqrt(Math.max(planCost, 1))) *
      (0.9 + (0.2 * capitalEfficiencyRank)) *
      (0.9 + (0.1 * gilPerDayRank))
    : 0
  var signalWeight = getScannerSignalWeight(row && row.signal)
  var sellProb = clamp(Number.isFinite(row && row.sellProbAtRecommended) ? row.sellProbAtRecommended : 0, 0, 1)
  var queueDays = Number.isFinite(row && row.competitionDelayDays) && row.competitionDelayDays >= 0
    ? row.competitionDelayDays
    : (Number.isFinite(row && row.queueDays) && row.queueDays >= 0 ? row.queueDays : 7)
  var backtestBias = clamp(Number.isFinite(row && row.backtestBias) ? row.backtestBias : 0, -0.18, 0.18)
  var localAdaptiveBias = clamp(Number.isFinite(row && row.localAdaptiveBias) ? row.localAdaptiveBias : 0, -0.1, 0.08)
  var calibrationScale = clamp(Number.isFinite(row && row.modelCalibrationScale) ? row.modelCalibrationScale : 1, 0.4, 1)
  var queuePressure = Number.isFinite(row && (row.priceLadderPressureScore || row.competitionPressureScore || row.queuePressureScore))
    ? Math.max(0, row.priceLadderPressureScore || row.competitionPressureScore || row.queuePressureScore)
    : 0
  var exitFragility = Number.isFinite(row && row.exitFragilityScore) ? Math.max(0, row.exitFragilityScore) : 0
  var executionBurden = Number.isFinite(row && row.executionBurden) ? Math.max(0, row.executionBurden) : 0
  var listingSlotPressure = Number.isFinite(row && row.listingSlotPressure) ? Math.max(0, row.listingSlotPressure) : 0
  var confidence = signalWeight *
    (0.6 + (0.4 * sellProb)) *
    (1 / (1 + (queueDays / 6))) *
    (1 / (1 + (queuePressure / 5))) *
    (1 / (1 + (exitFragility / 3.5))) *
    (1 / (1 + (executionBurden * 0.35))) *
    (1 / (1 + (listingSlotPressure * 0.4))) *
    (0.85 + (0.15 * calibrationScale)) *
    Math.max(0.7, 1 + backtestBias) *
    Math.max(0.82, 1 + localAdaptiveBias)

  return {
    confidence: confidence,
    score: baseScore * confidence
  }
}

function getPercentileRank(value,values) {
  if(!values.length)return 0
  if(values.length===1)return value>=values[0]?1:0
  let low=0,high=values.length
  while(low<high){const mid=(low+high)>>1;if(values[mid]<=value)low=mid+1;else high=mid}
  return clamp((low-1)/(values.length-1),0,1)
}

function getScannerSignalWeight(signal) {
  var normalized = typeof signal === "string" ? signal.trim().toLowerCase() : ""
  if (normalized === "very strong" || normalized === "almost guaranteed") {
    return 1.08
  }
  if (normalized === "strong") {
    return 1
  }
  if (normalized === "good") {
    return 0.9
  }
  if (normalized === "test small") {
    return 0.72
  }
  if (normalized === "thin") {
    return 0.58
  }
  return 0.68
}

const sortedListingCache = new WeakMap()
function normalizeListings(listings) {
  if (!Array.isArray(listings)) return []
  if (sortedListingCache.has(listings)) return sortedListingCache.get(listings)
  const sorted = listings.filter(listing => listing && Number.isFinite(listing.pricePerUnit) && listing.pricePerUnit > 0 && Number.isFinite(listing.quantity) && listing.quantity > 0)
    .slice().sort((a,b) => a.pricePerUnit - b.pricePerUnit || a.quantity - b.quantity)
  sortedListingCache.set(listings, sorted)
  return sorted
}

function normalizeSalesEntries(entries) {
  return Array.isArray(entries)
    ? entries.filter(function (entry) {
      return entry &&
        Number.isFinite(entry.pricePerUnit) &&
        entry.pricePerUnit > 0 &&
        Number.isFinite(entry.quantity) &&
        entry.quantity > 0
    })
    : []
}

function buildDailyDemandBuckets(entries, lookbackDays, nowSec, valueProjector) {
  var bucketCount = Math.max(1, Math.ceil(lookbackDays))
  var buckets = []
  var activeDays = 0
  var projector = typeof valueProjector === "function"
    ? valueProjector
    : function (entry) {
      return entry.quantity
    }
  for (var i = 0; i < bucketCount; i++) {
    buckets.push(0)
  }

  if (!Number.isFinite(nowSec)) {
    return {
      buckets: buckets,
      activeDays: activeDays
    }
  }

  entries.forEach(function (entry) {
    var ageDays = (nowSec - entry.timestampSec) / 86400
    if (!Number.isFinite(ageDays) || ageDays < 0 || ageDays >= lookbackDays) {
      return
    }
    var bucketIndex = Math.min(bucketCount - 1, Math.max(0, Math.floor(ageDays)))
    var bucketValue = Number(projector(entry))
    buckets[bucketIndex] += Number.isFinite(bucketValue) && bucketValue > 0 ? bucketValue : 0
  })

  buckets.forEach(function (value) {
    if (value > 0) {
      activeDays += 1
    }
  })

  return {
    buckets: buckets,
    activeDays: activeDays
  }
}

function computeWeightedDailyDemandStats(dailyBuckets, halfLifeDays) {
  var safeBuckets = Array.isArray(dailyBuckets) ? dailyBuckets : []
  if (!safeBuckets.length) {
    return {
      mean: 0,
      variance: 0
    }
  }

  var decay = Math.log(2) / Math.max(halfLifeDays, 0.25)
  var weightedSum = 0
  var weightedSquares = 0
  var weightTotal = 0

  safeBuckets.forEach(function (units, index) {
    var bucketUnits = Number.isFinite(units) ? Math.max(0, units) : 0
    var weight = Math.exp(-(index + 0.5) * decay)
    weightedSum += bucketUnits * weight
    weightedSquares += bucketUnits * bucketUnits * weight
    weightTotal += weight
  })

  if (!(weightTotal > 0)) {
    return {
      mean: 0,
      variance: 0
    }
  }

  var mean = weightedSum / weightTotal
  var variance = Math.max(0, (weightedSquares / weightTotal) - (mean * mean))
  return {
    mean: mean,
    variance: variance
  }
}

function buildDemandSeriesModel(args) {
  var settings = args && typeof args === "object" ? args : {}
  var totalValue = Number.isFinite(settings.totalValue) ? Math.max(0, settings.totalValue) : 0
  var sampleCount = Number.isFinite(settings.sampleCount) ? Math.max(0, settings.sampleCount) : 0
  var activeDays = Number.isFinite(settings.activeDays) ? Math.max(0, settings.activeDays) : 0
  var lookbackDays = Number.isFinite(settings.lookbackDays) && settings.lookbackDays > 0
    ? settings.lookbackDays
    : 7
  var horizonDays = Number.isFinite(settings.horizonDays) && settings.horizonDays > 0
    ? settings.horizonDays
    : 1
  var weightedStats = settings.weightedStats && typeof settings.weightedStats === "object"
    ? settings.weightedStats
    : {}
  var sparseTiming = !!settings.sparseTiming
  var simpleRatePerDay = totalValue / Math.max(lookbackDays, 1)
  var weightedRatePerDay = Number.isFinite(weightedStats.mean) ? Math.max(0, weightedStats.mean) : simpleRatePerDay
  var weightedVarianceRatePerDay = Number.isFinite(weightedStats.variance)
    ? Math.max(weightedRatePerDay, weightedStats.variance)
    : weightedRatePerDay
  var coverageRatio = clamp(activeDays / Math.max(lookbackDays, 1), 0, 1)
  var sampleDensity = clamp(sampleCount / Math.max(activeDays || 1, 1) / 3, 0, 1)
  var sparseFallback = sparseTiming || activeDays < 2
  var recencyBlendWeight = sparseFallback
    ? 0
    : clamp(0.25 + (coverageRatio * 0.35) + (sampleDensity * 0.25), 0.25, 0.85)
  var modeledRatePerDay = sparseFallback
    ? simpleRatePerDay
    : blend(simpleRatePerDay, weightedRatePerDay, recencyBlendWeight)
  var varianceRatePerDay = sparseFallback
    ? modeledRatePerDay
    : Math.max(
      modeledRatePerDay,
      blend(modeledRatePerDay, weightedVarianceRatePerDay, recencyBlendWeight)
    )
  var dispersionRatio = modeledRatePerDay > 0
    ? (varianceRatePerDay / modeledRatePerDay)
    : 1
  var negativeBinomialSizePerDay =
    dispersionRatio > 1.1 && varianceRatePerDay > modeledRatePerDay && modeledRatePerDay > 0
      ? ((modeledRatePerDay * modeledRatePerDay) / Math.max(varianceRatePerDay - modeledRatePerDay, 0.0001))
      : null
  var negativeBinomialSize = Number.isFinite(negativeBinomialSizePerDay)
    ? Math.max(0.0001, negativeBinomialSizePerDay * horizonDays)
    : null
  var meanValue = modeledRatePerDay * horizonDays
  var varianceValue = negativeBinomialSize
    ? (meanValue + ((meanValue * meanValue) / negativeBinomialSize))
    : meanValue

  return {
    modelType: negativeBinomialSize ? "negative_binomial" : "poisson",
    sparseFallback: sparseFallback,
    recencyBlendWeight: recencyBlendWeight,
    simpleRatePerDay: simpleRatePerDay,
    weightedRatePerDay: weightedRatePerDay,
    modeledRatePerDay: modeledRatePerDay,
    varianceRatePerDay: varianceRatePerDay,
    meanValue: meanValue,
    varianceValue: varianceValue,
    dispersionRatio: dispersionRatio,
    negativeBinomialSize: negativeBinomialSize
  }
}

function getScaledDemandModel(model, scale, metric) {
  var safeModel = model && typeof model === "object" ? model : {}
  var metricKey = metric === "sales" ? "sales" : "units"
  var appliedScale = Number.isFinite(scale) ? Math.max(0, scale) : 1
  var baseModelType = metricKey === "sales"
    ? (safeModel.saleModelType || safeModel.modelType)
    : (safeModel.unitModelType || safeModel.modelType)
  var baseMeanValue = metricKey === "sales"
    ? (Number(safeModel.meanSales) || 0)
    : (Number(safeModel.meanUnits) || 0)
  var baseModeledRate = metricKey === "sales"
    ? (Number(safeModel.saleRatePerDay) || 0)
    : (Number(safeModel.unitRatePerDay) || Number(safeModel.modeledRatePerDay) || 0)
  var baseSize = metricKey === "sales"
    ? (Number.isFinite(safeModel.saleNegativeBinomialSize) ? safeModel.saleNegativeBinomialSize : null)
    : (Number.isFinite(safeModel.unitNegativeBinomialSize)
      ? safeModel.unitNegativeBinomialSize
      : (Number.isFinite(safeModel.negativeBinomialSize) ? safeModel.negativeBinomialSize : null))
  var meanUnits = Math.max(0, baseMeanValue * appliedScale)
  var modeledRatePerDay = Math.max(0, baseModeledRate * appliedScale)
  var size = Number.isFinite(baseSize) && baseSize > 0 ? (baseSize * appliedScale) : null
  var varianceUnits = baseModelType === "negative_binomial" && size > 0
    ? (meanUnits + ((meanUnits * meanUnits) / size))
    : meanUnits

  return {
    metric: metricKey,
    modelType: baseModelType === "negative_binomial" ? "negative_binomial" : "poisson",
    negativeBinomialSize: size,
    meanValue: meanUnits,
    varianceValue: varianceUnits,
    meanUnits: meanUnits,
    modeledRatePerDay: modeledRatePerDay,
    varianceUnits: varianceUnits
  }
}

const probabilityCache = new Map()

function getDemandModelProbabilityForMetric(model, threshold, scale, metric) {
  if (!(Number.isFinite(threshold) && threshold > 0)) {
    return 1
  }

  var scaled = getScaledDemandModel(model, scale, metric)
  var safeThreshold = Math.max(1, Math.ceil(threshold))
  const key=[scaled.modelType,scaled.meanValue,scaled.negativeBinomialSize,safeThreshold].join(":")
  if(probabilityCache.has(key))return probabilityCache.get(key)
  const result=scaled.modelType === "negative_binomial" && scaled.negativeBinomialSize > 0
    ? negativeBinomialProbAtLeast(scaled.meanValue,scaled.negativeBinomialSize,safeThreshold)
    : poissonProbAtLeast(scaled.meanValue,safeThreshold)
  if(probabilityCache.size>=20000)probabilityCache.clear()
  probabilityCache.set(key,result)
  return result
}

function logGamma(z) {
  const coefficients = [676.5203681218851,-1259.1392167224028,771.3234287776531,-176.6150291621406,12.507343278686905,-0.13857109526572012,9.984369578019572e-6,1.5056327351493116e-7]
  if (z < 0.5) return Math.log(Math.PI)-Math.log(Math.sin(Math.PI*z))-logGamma(1-z)
  z--
  let x=0.99999999999980993
  coefficients.forEach((c,i)=>{x+=c/(z+i+1)})
  const t=z+7.5
  return 0.5*Math.log(2*Math.PI)+(z+0.5)*Math.log(t)-t+Math.log(x)
}
function betaFraction(a,b,x) {
  const tiny=1e-300
  let c=1,d=1-(a+b)*x/(a+1)
  if(Math.abs(d)<tiny)d=tiny
  d=1/d
  let h=d
  for(let m=1;m<=300;m++) {
    let aa=m*(b-m)*x/((a+2*m-1)*(a+2*m))
    d=1+aa*d;if(Math.abs(d)<tiny)d=tiny
    c=1+aa/c;if(Math.abs(c)<tiny)c=tiny
    d=1/d;h*=d*c
    aa=-(a+m)*(a+b+m)*x/((a+2*m)*(a+2*m+1))
    d=1+aa*d;if(Math.abs(d)<tiny)d=tiny
    c=1+aa/c;if(Math.abs(c)<tiny)c=tiny
    d=1/d;const delta=d*c;h*=delta
    if(Math.abs(delta-1)<3e-12)break
  }
  return h
}
function regularizedBeta(x,a,b) {
  if(x<=0)return 0
  if(x>=1)return 1
  const front=Math.exp(logGamma(a+b)-logGamma(a)-logGamma(b)+a*Math.log(x)+b*Math.log1p(-x))
  return clamp(x<(a+1)/(a+b+2) ? front*betaFraction(a,b,x)/a : 1-front*betaFraction(b,a,1-x)/b,0,1)
}
function negativeBinomialProbAtLeast(meanUnits,size,threshold) {
  if(threshold<=0)return 1
  if(!(meanUnits>0)||!(size>0))return 0
  return regularizedBeta(meanUnits/(size+meanUnits),threshold,size)
}

function blend(a, b, weight) {
  var safeWeight = clamp(Number.isFinite(weight) ? weight : 0.5, 0, 1)
  var aValue = Number.isFinite(a) ? a : 0
  var bValue = Number.isFinite(b) ? b : aValue
  return (aValue * (1 - safeWeight)) + (bValue * safeWeight)
}

function roundGilPrice(value) {
  if (!Number.isFinite(value) || value <= 0) {
    return null
  }
  return Math.max(1, Math.round(value))
}

function getMinListingPrice(listings) {
  var safeListings = normalizeListings(listings)
  return safeListings.length ? safeListings[0].pricePerUnit : null
}

function poissonProbAtLeast(mu, q) {
  if (q <= 0) {
    return 1
  }
  if (!(mu > 0)) {
    return 0
  }
  return clamp(1 - poissonCdf(q - 1, mu), 0, 1)
}

function poissonCdf(k, mu) {
  if (k < 0) {
    return 0
  }
  if (!(mu > 0)) {
    return 1
  }
  if (mu > 700) {
    return poissonCdfLogSumExp(k, mu)
  }

  var pmf = Math.exp(-mu)
  var cdf = pmf

  for (var i = 1; i <= k; i++) {
    pmf = (pmf * mu) / i
    cdf += pmf
    if (cdf >= 1) {
      return 1
    }
  }

  return clamp(cdf, 0, 1)
}

function poissonCdfLogSumExp(k, mu) {
  var logTerm = -mu
  var maxLog = logTerm
  var logs = [logTerm]

  for (var i = 1; i <= k; i++) {
    logTerm = logTerm + Math.log(mu) - Math.log(i)
    logs.push(logTerm)
    if (logTerm > maxLog) {
      maxLog = logTerm
    }
  }

  var scaledSum = 0
  for (var j = 0; j < logs.length; j++) {
    scaledSum += Math.exp(logs[j] - maxLog)
  }

  return clamp(Math.exp(maxLog) * scaledSum, 0, 1)
}

function clamp(value, min, max) {
  if (value < min) {
    return min
  }
  if (value > max) {
    return max
  }
  return value
}

function pickExitAnchor(referencePoint, exitStyle) {
  var fallbacks = exitStyle === "fast"
    ? [
      { value: referencePoint.anchorFast, basis: "fast_anchor" },
      { value: referencePoint.min, basis: "reference_min" },
      { value: referencePoint.anchorBalanced, basis: "sale_anchor" },
      { value: referencePoint.anchor, basis: "sale_anchor" },
      { value: referencePoint.median, basis: "reference_median" }
    ]
    : (exitStyle === "patient"
      ? [
        { value: referencePoint.anchorPatient, basis: "patient_anchor" },
        { value: referencePoint.anchorBalanced, basis: "sale_anchor" },
        { value: referencePoint.anchor, basis: "sale_anchor" },
        { value: referencePoint.median, basis: "reference_median" },
        { value: referencePoint.min, basis: "reference_min" }
      ]
      : [
        { value: referencePoint.anchorBalanced, basis: "sale_anchor" },
        { value: referencePoint.anchor, basis: "sale_anchor" },
        { value: referencePoint.median, basis: "reference_median" },
        { value: referencePoint.min, basis: "reference_min" }
      ])

  for (var i = 0; i < fallbacks.length; i++) {
    if (Number.isFinite(fallbacks[i].value) && fallbacks[i].value > 0) {
      return fallbacks[i]
    }
  }

  return {
    value: null,
    basis: "reference_min"
  }
}

function getExitDepthLiftRatio(exitStyle, holdingDays, targetCapRatio) {
  var safeDays = Number.isFinite(holdingDays) && holdingDays > 0 ? holdingDays : 2
  if (exitStyle === "fast") {
    return 0
  }
  if (exitStyle === "patient") {
    return clamp((safeDays * 0.04) + 0.02, 0, targetCapRatio)
  }
  return clamp(safeDays * 0.02, 0, targetCapRatio)
}

function getMarketFragilityTargetDiscountRatio(exitStyle, marketFragilityScore) {
  var safeScore = clamp(Number.isFinite(marketFragilityScore) ? marketFragilityScore : 0, 0, 1)
  if (exitStyle === "fast") {
    return clamp(safeScore * 0.0125, 0, 0.0125)
  }
  if (exitStyle === "patient") {
    return clamp(safeScore * 0.04, 0, 0.04)
  }
  return clamp(safeScore * 0.025, 0, 0.025)
}

function createMarketFragilityOverlayResult(values) {
  var settings = values && typeof values === "object" ? values : {}
  return {
    stressScore: clamp(Number.isFinite(settings.stressScore) ? settings.stressScore : 0, 0, 1),
    floorBreakRatio: Number.isFinite(settings.floorBreakRatio) ? settings.floorBreakRatio : 0,
    floorBreakScore: Number.isFinite(settings.floorBreakScore) ? settings.floorBreakScore : 0,
    nearPriceCrowdingScore: Number.isFinite(settings.nearPriceCrowdingScore) ? settings.nearPriceCrowdingScore : 0,
    frontBandListingCount: Number.isFinite(settings.frontBandListingCount) ? settings.frontBandListingCount : 0,
    frontBandUnits: Number.isFinite(settings.frontBandUnits) ? settings.frontBandUnits : 0,
    frontSpreadPct: Number.isFinite(settings.frontSpreadPct) ? settings.frontSpreadPct : null,
    supplyOverhangScore: Number.isFinite(settings.supplyOverhangScore) ? settings.supplyOverhangScore : 0,
    postedSupplyDays: Number.isFinite(settings.postedSupplyDays) ? settings.postedSupplyDays : null,
    totalPostedSupply: Number.isFinite(settings.totalPostedSupply) ? settings.totalPostedSupply : 0,
    historyConfidence: Number.isFinite(settings.historyConfidence) ? settings.historyConfidence : 0,
    targetDiscountRatioFast: Number.isFinite(settings.targetDiscountRatioFast) ? settings.targetDiscountRatioFast : 0,
    targetDiscountRatioBalanced: Number.isFinite(settings.targetDiscountRatioBalanced) ? settings.targetDiscountRatioBalanced : 0,
    targetDiscountRatioPatient: Number.isFinite(settings.targetDiscountRatioPatient) ? settings.targetDiscountRatioPatient : 0,
    holdWindowScale: Number.isFinite(settings.holdWindowScale) ? settings.holdWindowScale : 1,
    qtyScale: Number.isFinite(settings.qtyScale) ? settings.qtyScale : 1,
    signalPenalty: Number.isFinite(settings.signalPenalty) ? settings.signalPenalty : 0
  }
}

function getExitStyleLabel(exitStyle) {
  if (exitStyle === "fast") {
    return "Fast"
  }
  if (exitStyle === "patient") {
    return "Patient"
  }
  return "Balanced"
}
