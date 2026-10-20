import { pickLatestTimestampSec } from "./api.js"
import { getScannerLiquidityBucketKey } from "./scanner-history.js"
import { applyHighConfidenceLiquidityTaper, applyMarketFragilityQtyCap, analyzeTargetCompetition, buildMarketFragilityOverlay, buildPriceLadderPressureModel, buildDemandModel, buildDiscretePriceCandidates, getDemandModelExpectedUnitsAfterCompetition, getDemandModelUnitProbabilityAtLeast, getDemandModelExpectedUnitsCapped, pickPercentile, pickBestRemotePurchasePlan } from "./scanner-strategy.js"

import { estimateExecution, buildHybridExitPlans } from "./scanner-strategy.js"
import { filterQuality, historyCoverage, freshness, inventoryEconomics } from "./market-data.js"

const buildHybridExitPlansHelper = buildHybridExitPlans
let intFormatter;
const MAX_Q = 999
function getScannerCalibrationMetrics(calibration, liquidityPerDay) {
  if (!calibration || !Number.isFinite(calibration.probabilityScale)) {
    return {
      scale: 1,
      signalPenalty: 0,
      bucketKey: getScannerLiquidityBucketKey(liquidityPerDay),
      bucketEvaluated: 0,
      confidence: 0
    };
  }

  var bucketKey = getScannerLiquidityBucketKey(liquidityPerDay);
  var bucket = calibration.liquidityBuckets && calibration.liquidityBuckets[bucketKey]
    ? calibration.liquidityBuckets[bucketKey]
    : null;
  var scale = clamp(calibration.probabilityScale, 0.4, 1);
  var signalPenalty = Number.isFinite(calibration.signalPenalty) ? calibration.signalPenalty : 0;
  var confidence = Number.isFinite(calibration.confidence) ? calibration.confidence : 0;
  var bucketEvaluated = 0;

  if (bucket && Number.isFinite(bucket.probabilityScale)) {
    scale = clamp(bucket.probabilityScale, 0.4, 1);
    signalPenalty = Number.isFinite(bucket.signalPenalty) ? bucket.signalPenalty : signalPenalty;
    confidence = Number.isFinite(bucket.confidence) ? bucket.confidence : confidence;
    bucketEvaluated = Number.isFinite(bucket.evaluated) ? bucket.evaluated : 0;
  }

  return {
    scale: scale,
    signalPenalty: signalPenalty,
    bucketKey: bucketKey,
    bucketEvaluated: bucketEvaluated,
    confidence: confidence
  };
}

export function buildScannerOpportunitiesForSettings(args) {
  var settings = args && typeof args === "object" ? args : {};
  var safeItems = (Array.isArray(settings.items) ? settings.items : []).flatMap(item => [
    { ...item, quality: "NQ" }, { ...item, quality: "HQ" }
  ]);
  var nowSec = Number.isFinite(settings.nowSec) ? settings.nowSec : (Date.now() / 1000);
  var homeWorld = settings.homeWorld || "";
  var lookbackDays = Number.isFinite(settings.lookbackDays) && settings.lookbackDays > 0 ? settings.lookbackDays : 7;
  var holdingDays = Number.isFinite(settings.holdingDays) && settings.holdingDays > 0 ? settings.holdingDays : 2;
  var taxRate = Number.isFinite(settings.taxRate) ? settings.taxRate : 0;
  var travelFee = Number.isFinite(settings.travelFee) ? settings.travelFee : 0;
  var minProfit = Number.isFinite(settings.minProfit) ? settings.minProfit : 0;
  var minRoi = Number.isFinite(settings.minRoi) ? settings.minRoi : 0;
  var referenceMarket = settings.referenceMarket || "home";
  var targetCapRatio = Number.isFinite(settings.targetCapRatio) ? settings.targetCapRatio : 0.1;
  var priceBandRatio = Number.isFinite(settings.priceBandRatio) ? settings.priceBandRatio : 0.1;
  var minTargetProb = Number.isFinite(settings.minTargetProb) ? settings.minTargetProb : 0;
  var qtyConfidence = Number.isFinite(settings.qtyConfidence) ? settings.qtyConfidence : 0.7;
  var maxBuyQty = Number.isInteger(settings.maxBuyQty) && settings.maxBuyQty > 0 ? settings.maxBuyQty : 20;
  var listingsByItemWorld = settings.listingsByItemWorld || {};
  var historyByItemDc = settings.historyByItemDc || {};
  var historyByItemHome = settings.historyByItemHome || {};
  var historyByItemRegion = settings.historyByItemRegion || {};
  var allowHomeHistoryFallback = !!settings.allowHomeHistoryFallback;
  var dcMinByItem = settings.dcMinByItem || {};
  var regionMinByItem = settings.regionMinByItem || {};
  var marketCalibration = settings.marketCalibration || null;
  var itemDataUpdatedByItem = settings.itemDataUpdatedByItem || {};
  var backtestBiasByItem = settings.backtestBiasByItem || {};
  var itemNameById = settings.itemNameById || {};
  var cutoffSec = nowSec - (lookbackDays * 86400);
  var opportunities = [];

  safeItems.forEach(function (item) {
    var id = item.id;
    var quality = item.quality;
    var worldMap = {};
    Object.entries(listingsByItemWorld[id] || {}).forEach(([world, data]) => {
      var listings = filterQuality(data.listings, quality);
      worldMap[world] = { ...data, listings, minPrice: listings.length ? Math.min(...listings.map(row => row.pricePerUnit)) : null };
    });
    var dcHistoryEntries = filterQuality(historyByItemDc[id] ? historyByItemDc[id].entries : [], quality);
    var coverage = historyCoverage(historyByItemHome[id] && historyByItemHome[id].entries, {
      nowSec, lookbackDays, requestedCount: settings.historyEntriesRequested || 1800,
      available: !!historyByItemHome[id] && historyByItemHome[id].available !== false,
      rawCount: historyByItemHome[id]?.rawCount, invalidCount: historyByItemHome[id]?.invalidCount
    });
    if (!coverage.available) return;
    var homeHistoryEntriesRaw = filterQuality(historyByItemHome[id] ? historyByItemHome[id].entries : [], quality);
    var homeHistoryEntries = homeHistoryEntriesRaw;
    if (allowHomeHistoryFallback && !homeHistoryEntries.length && dcHistoryEntries.length) {
      homeHistoryEntries = dcHistoryEntries.filter(entry => entry.worldName === homeWorld);
    }
    var regionHistoryEntries = filterQuality(historyByItemRegion[id] ? historyByItemRegion[id].entries : [], quality);

    var homeData = worldMap[homeWorld];
    if (!homeData) return;
    var homeFreshness = freshness(homeData.lastUpdatedSec || getLatestListingReviewSec(homeData.listings), nowSec);
    if (homeFreshness.stale && !settings.allowStaleData) return;
    Object.keys(worldMap).forEach(world => {
      var data = worldMap[world];
      var age = freshness(data.lastUpdatedSec || getLatestListingReviewSec(data.listings), nowSec);
      if (age.stale && !settings.allowStaleData && world !== homeWorld) delete worldMap[world];
    });
    var homeMin = Number.isFinite(homeData.minPrice) ? homeData.minPrice : null;
    var qualityDcListings = collectListingsFromWorldMap(worldMap);
    var dcMin = qualityDcListings.length ? Math.min(...qualityDcListings.map(row => row.pricePerUnit)) : null;
    var regionPricesForQuality = regionHistoryEntries.map(row => row.pricePerUnit).filter(price => price > 0);
    var regionMin = regionPricesForQuality.length ? Math.min(...regionPricesForQuality) : null;
    var homeListings = Array.isArray(homeData.listings) ? homeData.listings : [];

    var hasRemoteSupply = Object.keys(worldMap).some(function (worldName) {
      if (worldName === homeWorld) {
        return false;
      }
      var worldData = worldMap[worldName] || {};
      return Array.isArray(worldData.listings) && worldData.listings.length > 0;
    });

    if (!hasRemoteSupply) {
      return;
    }

    var dcWindowSales = dcHistoryEntries.filter(function (entry) {
      return Number.isFinite(entry.timestampSec) && entry.timestampSec >= cutoffSec;
    });

    var homeWindowSales = homeHistoryEntries.filter(function (entry) {
      return Number.isFinite(entry.timestampSec) && entry.timestampSec >= cutoffSec;
    });
    var regionWindowSales = regionHistoryEntries.filter(function (entry) {
      return Number.isFinite(entry.timestampSec) && entry.timestampSec >= cutoffSec;
    });

    var itemListings = collectListingsFromWorldMap(worldMap);
    var itemMarketDataUpdatedSec = pickLatestTimestampSec([
      itemDataUpdatedByItem[id],
      historyByItemDc[id] ? historyByItemDc[id].lastUpdatedSec : null,
      historyByItemHome[id] ? historyByItemHome[id].lastUpdatedSec : null,
      historyByItemRegion[id] ? historyByItemRegion[id].lastUpdatedSec : null,
      getLatestListingReviewSec(itemListings),
      getLatestEntryTimestampSec(dcHistoryEntries),
      getLatestEntryTimestampSec(homeHistoryEntries),
      getLatestEntryTimestampSec(regionHistoryEntries)
    ]);
    var itemQualitySummary = buildQualitySummary("Listings", itemListings) || buildQualitySummary("Sales", homeHistoryEntries.length ? homeHistoryEntries : dcHistoryEntries);

    var dcUnitsSold = dcWindowSales.reduce(function (sum, entry) {
      return sum + (Number.isFinite(entry.quantity) ? entry.quantity : 0);
    }, 0);

    var homeUnitsSold = homeWindowSales.reduce(function (sum, entry) {
      return sum + (Number.isFinite(entry.quantity) ? entry.quantity : 0);
    }, 0);

    function cachedDemand(scope, entries, options) {
      const cache = settings.sharedCalculationCache;
      if (!cache) return buildDemandModel(entries, options);
      const key = [id, quality, scope, lookbackDays, options.lookbackDays, options.horizonDays, nowSec].join(":");
      if (!cache.has(key)) cache.set(key, buildDemandModel(entries, options));
      return cache.get(key);
    }
    var dcDemandModel = cachedDemand("dc", dcWindowSales, {
      lookbackDays: coverage.observedDays,
      horizonDays: 1,
      nowSec: nowSec
    });
    var homeDemandModel = cachedDemand("home", homeWindowSales, {
      lookbackDays: coverage.observedDays,
      horizonDays: 1,
      nowSec: nowSec
    });
    var dcLambda = dcDemandModel.modeledRatePerDay;
    var homeLambda = homeDemandModel.modeledRatePerDay;

    var homePrices = homeWindowSales.map(function (entry) {
      return entry.pricePerUnit;
    }).filter(function (v) {
      return Number.isFinite(v) && v > 0;
    });

    var dcPrices = dcWindowSales.map(function (entry) {
      return entry.pricePerUnit;
    }).filter(function (v) {
      return Number.isFinite(v) && v > 0;
    });
    var regionPrices = regionWindowSales.map(function (entry) {
      return entry.pricePerUnit;
    }).filter(function (v) {
      return Number.isFinite(v) && v > 0;
    });

    var medianSaleHome = median(homePrices);
    var medianSaleDc = median(dcPrices);
    var medianSaleRegion = median(regionPrices);

    var homeSaleStackMedian = pickPercentile(homeWindowSales.map(function (entry) {
      return entry.quantity;
    }), 0.5);
    var statsBySource = {
      home: {
        min: homeMin,
        median: medianSaleHome,
        anchor: pickPercentile(homePrices, 0.6),
        anchorFast: pickPercentile(homePrices, 0.35),
        anchorBalanced: pickPercentile(homePrices, 0.6),
        anchorPatient: pickPercentile(homePrices, 0.75)
      },
      dc: {
        min: dcMin,
        median: medianSaleDc,
        anchor: pickPercentile(dcPrices, 0.6),
        anchorFast: pickPercentile(dcPrices, 0.35),
        anchorBalanced: pickPercentile(dcPrices, 0.6),
        anchorPatient: pickPercentile(dcPrices, 0.75)
      },
      region: {
        min: regionMin,
        median: medianSaleRegion,
        anchor: pickPercentile(regionPrices, 0.6),
        anchorFast: pickPercentile(regionPrices, 0.35),
        anchorBalanced: pickPercentile(regionPrices, 0.6),
        anchorPatient: pickPercentile(regionPrices, 0.75)
      }
    };
    var backtestMetrics = backtestBiasByItem[id + ":" + quality] || null;
    var backtestBias = backtestMetrics && Number.isFinite(backtestMetrics.bias) ? backtestMetrics.bias : 0;
    var marketFragility = typeof buildMarketFragilityOverlay === "function"
      ? buildMarketFragilityOverlay({
        homeListings: homeListings,
        recentExecutedMedian: medianSaleHome,
        unitRatePerDay: homeDemandModel.unitRatePerDay || homeDemandModel.modeledRatePerDay,
        holdingDays: holdingDays,
        saleCount: homeWindowSales.length
      })
      : {
        stressScore: 0,
        holdWindowScale: 1,
        qtyScale: 1,
        signalPenalty: 0
      };
    var exitPlans = buildHybridExitPlansHelper({
      referenceMarket: referenceMarket,
      targetCapRatio: targetCapRatio,
      preferredStackQty: homeSaleStackMedian,
      holdingDays: holdingDays,
      homeListings: homeListings,
      statsBySource: statsBySource,
      marketFragilityScore: marketFragility.stressScore
    });
    var selectedPlan = null;
    var priceModels = new Map();

    exitPlans.forEach(function (exitPlan) {
      var candidatePrices = Array.isArray(exitPlan.candidatePrices) && exitPlan.candidatePrices.length
        ? exitPlan.candidatePrices.slice()
        : buildDiscretePriceCandidates({
          exitStyle: exitPlan.exitStyle,
          baseTarget: exitPlan.targetSell,
          lowerBound: 1,
          upperBound: exitPlan.targetSell,
          homeListings: homeListings,
          referencePoint: {
            min: exitPlan.referenceMin,
            median: exitPlan.referenceMedian,
            anchorFast: exitPlan.saleAnchor,
            anchorBalanced: exitPlan.saleAnchor,
            anchorPatient: exitPlan.saleAnchor
          }
        });
      if (!candidatePrices.length) {
        return;
      }

      candidatePrices.forEach(function (candidatePrice) {
        var targetSell = Number.isFinite(candidatePrice) ? Math.max(1, Math.round(candidatePrice)) : null;
        if (!Number.isFinite(targetSell) || targetSell <= 0) {
          return;
        }

        var demandModel = priceModels.get(targetSell);
        if (!demandModel) {
          var bandSales = homeWindowSales.filter(entry => entry.pricePerUnit >= targetSell * (1 - priceBandRatio) && entry.pricePerUnit <= targetSell * (1 + priceBandRatio));
          demandModel = cachedDemand("band:" + targetSell + ":" + priceBandRatio, bandSales, { lookbackDays: coverage.observedDays, horizonDays: holdingDays, nowSec });
          priceModels.set(targetSell, demandModel);
        }
        var bandSaleRate = demandModel.saleRatePerDay;
        var bandUnitRate = demandModel.unitRatePerDay;
        var bandLambda = bandUnitRate;
        var targetUnitsExpected = demandModel.meanUnits;

        var suggestedStackQty = Math.max(1, Math.round(
          demandModel.suggestedStackQty ||
          homeSaleStackMedian ||
          1
        ));
        var competition = analyzeTargetCompetition(homeListings, targetSell, {
          preferredStackQty: suggestedStackQty,
          nearTargetRatio: 0.01,
          averageUnitsPerSale: demandModel.averageUnitsPerSale
        });
        var ladderPressure = buildPriceLadderPressureModel({
          competition: competition,
          demandModel: demandModel,
          holdingDays: holdingDays,
          preferredStackQty: suggestedStackQty
        });
        var calibrationMetrics = getScannerCalibrationMetrics(marketCalibration && marketCalibration[quality] ? marketCalibration[quality] : marketCalibration, bandLambda);
        var calibrationScale = calibrationMetrics.scale;
        var probabilityTaperContext = {
          liquidityBucketKey: calibrationMetrics.bucketKey,
          queuePressureScore: ladderPressure.competitionPressureScore,
          exitFragilityScore: ladderPressure.exitFragilityScore,
          samePriceCongestionScore: ladderPressure.samePriceCongestionScore,
          nearPriceCrowdingScore: ladderPressure.nearPriceCrowdingScore,
          frontBookFragilityScore: ladderPressure.frontBookFragilityScore,
          stackWallRiskScore: ladderPressure.stackWallRiskScore,
          targetConfidenceScale: ladderPressure.targetConfidenceScale,
          demandDispersionRatio: demandModel.unitDispersionRatio || demandModel.dispersionRatio,
          demandRecencyBlendWeight: demandModel.recencyBlendWeight,
          demandModelType: demandModel.unitModelType || demandModel.modelType
        };
        var targetConfidenceScale = ladderPressure.targetConfidenceScale;
        var probabilityScale = ladderPressure.probabilityScale;
        var unitRateScale = ladderPressure.unitRateScale;
        var firstSaleScale = ladderPressure.firstSaleScale;
        var calibratedTargetUnitsExpected = getDemandModelExpectedUnitsAfterCompetition(
          demandModel,
          0,
          null,
          calibrationScale
        );
        var rawPAtLeastOneTarget = getDemandModelUnitProbabilityAtLeast(demandModel, 1, targetConfidenceScale);
        var pAtLeastOneTarget = getDemandModelUnitProbabilityAtLeast(
          demandModel,
          1,
          targetConfidenceScale * calibrationScale
        );
        var targetProbabilityTaper = typeof applyHighConfidenceLiquidityTaper === "function"
          ? applyHighConfidenceLiquidityTaper(pAtLeastOneTarget, probabilityTaperContext)
          : {
            probability: pAtLeastOneTarget,
            scale: 1,
            applied: false,
            bucketKey: calibrationMetrics.bucketKey,
            stressScore: 0
          };
        pAtLeastOneTarget = targetProbabilityTaper.probability;

        if (pAtLeastOneTarget < minTargetProb) {
          return;
        }

        var queueAheadUnits = Number.isFinite(competition.rawCompetitionUnits)
          ? competition.rawCompetitionUnits
          : competition.queueAheadUnits;
        var effectiveQueueAheadUnits = Number.isFinite(competition.effectiveCompetitionUnits)
          ? competition.effectiveCompetitionUnits
          : queueAheadUnits;
        var rawCompetitionSaleEvents = Number.isFinite(competition.rawCompetitionSaleEvents)
          ? competition.rawCompetitionSaleEvents
          : 0;
        var effectiveCompetitionSaleEvents = Number.isFinite(competition.effectiveCompetitionSaleEvents)
          ? competition.effectiveCompetitionSaleEvents
          : rawCompetitionSaleEvents;
        var rawQueueDays = bandUnitRate > 0 ? (competition.cheaperUnitsAhead / bandUnitRate) : Number.POSITIVE_INFINITY;
        var rawDaysToFirstSale = bandSaleRate > 0
          ? (1 / bandSaleRate)
          : Number.POSITIVE_INFINITY;
        var rawTargetUnitsAfterQueue = getDemandModelExpectedUnitsAfterCompetition(demandModel, effectiveQueueAheadUnits, null, unitRateScale);
        var calibratedTargetUnitsAfterQueue = getDemandModelExpectedUnitsAfterCompetition(
          demandModel,
          effectiveQueueAheadUnits,
          null,
          unitRateScale * calibrationScale
        );
        var demandCapQty = findQMaxConfidence(demandModel, qtyConfidence, maxBuyQty, function (candidateTargetQty) {
          return getDemandModelUnitProbabilityAtLeast(
            demandModel,
            candidateTargetQty,
            probabilityScale * calibrationScale
          );
        });
        var competitionAdjustedCapQty = demandCapQty > 0
          ? Math.max(1, Math.floor((demandCapQty - 1) * ladderPressure.qtyScale) + 1)
          : 0;
        var stressAdjustedCapQty = typeof applyMarketFragilityQtyCap === "function"
          ? applyMarketFragilityQtyCap(competitionAdjustedCapQty, marketFragility.qtyScale)
          : competitionAdjustedCapQty;
        var effectiveHoldingDays =
          holdingDays *
          (Number.isFinite(marketFragility.holdWindowScale) ? marketFragility.holdWindowScale : 1) *
          (Number.isFinite(ladderPressure.holdWindowScale) ? ladderPressure.holdWindowScale : 1);
        if (stressAdjustedCapQty < 1) {
          return;
        }

        var netSell = targetSell * (1 - taxRate);

        for (var candidateQty = 1; candidateQty <= stressAdjustedCapQty; candidateQty++) {
          var purchasePlan = pickBestRemotePurchasePlan(worldMap, homeWorld, candidateQty, travelFee, {
            allowPartial: false,
            maxExecutionBurden: 4.5
          });
          if (purchasePlan && !settings.allowStaleData && purchasePlan.usedListings.some(row => freshness(row.lastReviewTimeSec, nowSec).stale)) continue;
          if (settings.budgetGil > 0 && purchasePlan && purchasePlan.totalCost > settings.budgetGil) continue;
          if (!purchasePlan || purchasePlan.fulfilledQty < candidateQty) {
            continue;
          }

          var executionMetrics = buildExecutionMetrics({
            purchasePlan: purchasePlan,
            recommendedQty: candidateQty,
            suggestedStackQty: suggestedStackQty,
            travelFee: travelFee
          });
          if (
            purchasePlan.listingsUsed > 5 ||
            executionMetrics.listingSlotPressure > 4 ||
            executionMetrics.executionBurden > 4.4
          ) {
            continue;
          }

          var candidateBuyCostPerUnit = purchasePlan.avgUnitCost;
          var candidateProfit = netSell - candidateBuyCostPerUnit;
          if (!(candidateProfit > 0) || candidateProfit < minProfit) {
            continue;
          }

          var candidateRoi = candidateBuyCostPerUnit > 0 ? (candidateProfit / candidateBuyCostPerUnit) : -1;
          if (candidateRoi < minRoi) {
            continue;
          }

          var rawCandidateSellProb = getDemandModelUnitProbabilityAtLeast(demandModel, candidateQty, probabilityScale);
          var rawCandidateExpectedUnitsSold = getDemandModelExpectedUnitsCapped(demandModel, candidateQty, unitRateScale);
          var execution = estimateExecution(demandModel, competition, holdingDays, candidateQty, calibrationScale);
          var candidateSellProb = execution.clearProbability;
          var candidateSellProbTaper = typeof applyHighConfidenceLiquidityTaper === "function"
            ? applyHighConfidenceLiquidityTaper(candidateSellProb, probabilityTaperContext)
            : {
              probability: candidateSellProb,
              scale: 1,
              applied: false,
              bucketKey: calibrationMetrics.bucketKey,
              stressScore: 0
            };
          candidateSellProb = candidateSellProbTaper.probability;
          if (candidateSellProb < qtyConfidence || (!coverage.complete && candidateSellProb * 0.8 < qtyConfidence)) continue;
          var candidateExpectedUnitsSold = execution.expectedUnits;
          var candidateFirstSaleProb = execution.firstSaleProbability;
          var candidateFirstSaleTaper = typeof applyHighConfidenceLiquidityTaper === "function"
            ? applyHighConfidenceLiquidityTaper(candidateFirstSaleProb, probabilityTaperContext)
            : {
              probability: candidateFirstSaleProb,
              scale: 1,
              applied: false,
              bucketKey: calibrationMetrics.bucketKey,
              stressScore: 0
            };
          candidateFirstSaleProb = candidateFirstSaleTaper.probability;
          var candidatePlanCost = purchasePlan.totalCost;
          var economics = inventoryEconomics({ purchasedQty: purchasePlan.purchasedQty,
            expectedUnitsSold: candidateExpectedUnitsSold, totalCost: candidatePlanCost, netSalePrice: netSell });
          var candidatePlanExpectedProfit = economics.cashRecovery;
          if (candidatePlanExpectedProfit <= 0) continue;
          var adjustedSaleRate = bandSaleRate > 0
            ? (bandSaleRate * firstSaleScale)
            : 0;
          var adjustedUnitRate = bandUnitRate > 0
            ? (bandUnitRate * unitRateScale * targetConfidenceScale)
            : 0;
          var daysToFirstSale = adjustedSaleRate > 0
            ? (1 / adjustedSaleRate)
            : Number.POSITIVE_INFINITY;
          var throughputDaysToClear = adjustedUnitRate > 0
            ? (candidateQty / adjustedUnitRate)
            : Number.POSITIVE_INFINITY;
          var candidateDaysToClear = execution.clearDays;
          daysToFirstSale = execution.firstSaleDays;
          if (!Number.isFinite(candidateDaysToClear) || candidateDaysToClear > (effectiveHoldingDays * 1.25)) {
            continue;
          }

          var baseThroughputDaysToClear = bandUnitRate > 0
            ? (candidateQty / bandUnitRate)
            : Number.POSITIVE_INFINITY;
          var competitionDelayDays =
            Number.isFinite(baseThroughputDaysToClear) && Number.isFinite(candidateDaysToClear)
              ? Math.max(0, candidateDaysToClear - baseThroughputDaysToClear)
              : 0;
          var queueDays = competitionDelayDays;
          var candidateExecutionDays = Math.max(1, Math.min(holdingDays, candidateDaysToClear));
          var candidateExpectedGilPerDay = candidatePlanExpectedProfit / candidateExecutionDays;
          var candidateCapitalEfficiency = candidatePlanCost > 0 ? (candidatePlanExpectedProfit / candidatePlanCost) : 0;
          var candidateLiquidityQuality = Math.max(
            0.05,
            candidateFirstSaleProb *
            Math.max(0.35, candidateExpectedUnitsSold / Math.max(candidateQty, 1)) *
            Math.max(0.55, targetConfidenceScale)
          );
          var candidateCompetitionPenalty = 1 +
            Math.max(0, candidateDaysToClear - effectiveHoldingDays) +
            (ladderPressure.competitionPressureScore * 0.06) +
            (ladderPressure.exitFragilityScore * 0.05);
          var candidateExecutionPenalty = 1 + (executionMetrics.executionBurden * 0.18);
          var candidateCalibrationPenalty = 0.85 + (0.15 * calibrationScale);
          var candidateRiskAdjusted = candidateExpectedGilPerDay *
            Math.max(0.25, 0.7 + candidateCapitalEfficiency + backtestBias) *
            candidateLiquidityQuality *
            candidateCalibrationPenalty /
            (candidateCompetitionPenalty * candidateExecutionPenalty);

          if (
            !selectedPlan ||
            candidateRiskAdjusted > selectedPlan.riskAdjusted ||
            (
              candidateRiskAdjusted === selectedPlan.riskAdjusted &&
              candidatePlanExpectedProfit > selectedPlan.planExpectedProfit
            ) ||
            (
              candidateRiskAdjusted === selectedPlan.riskAdjusted &&
              candidatePlanExpectedProfit === selectedPlan.planExpectedProfit &&
              candidateQty < selectedPlan.recommendedQty
            )
          ) {
            selectedPlan = {
                  purchasedQty: purchasePlan.purchasedQty, excessQty: purchasePlan.excessQty,
                  usedListings: purchasePlan.usedListings, economics: economics,
            exitStyle: exitPlan.exitStyle,
            exitLabel: exitPlan.exitLabel,
            referenceSource: exitPlan.referenceSource,
            referenceMin: exitPlan.referenceMin,
            referenceMedian: exitPlan.referenceMedian,
            targetSell: targetSell,
            targetWasCapped: !!exitPlan.targetWasCapped && targetSell >= exitPlan.targetSell,
            targetCapReason: targetSell >= exitPlan.targetSell ? exitPlan.targetCapReason : null,
            targetBasis: targetSell === exitPlan.targetSell ? exitPlan.targetBasis : "price_surface",
              netSell: netSell,
              bandSaleRate: bandSaleRate,
              bandUnitRate: bandUnitRate,
              bandLambda: bandLambda,
              bandLambdaSimple: demandModel.simpleRatePerDay,
              targetUnitsExpected: targetUnitsExpected,
              calibratedTargetUnitsExpected: calibratedTargetUnitsExpected,
              pAtLeastOneTargetRaw: rawPAtLeastOneTarget,
              pAtLeastOneTarget: pAtLeastOneTarget,
              firstSaleProbAtRecommended: candidateFirstSaleProb,
              queueAheadUnits: queueAheadUnits,
              effectiveQueueAheadUnits: effectiveQueueAheadUnits,
              rawCompetitionUnits: queueAheadUnits,
              effectiveCompetitionUnits: effectiveQueueAheadUnits,
              rawCompetitionSaleEvents: rawCompetitionSaleEvents,
              effectiveCompetitionSaleEvents: effectiveCompetitionSaleEvents,
              rawQueueDays: rawQueueDays,
              queueDays: queueDays,
              rawDaysToFirstSale: rawDaysToFirstSale,
              daysToFirstSale: daysToFirstSale,
              targetUnitsAfterQueue: calibratedTargetUnitsAfterQueue,
              rawTargetUnitsAfterQueue: rawTargetUnitsAfterQueue,
              calibratedTargetUnitsAfterQueue: calibratedTargetUnitsAfterQueue,
              suggestedStackQty: suggestedStackQty,
              averageUnitsPerSale: demandModel.averageUnitsPerSale,
              cheaperUnitsAhead: competition.cheaperUnitsAhead,
              samePriceUnitsAhead: competition.samePriceUnitsAhead,
              samePriceListingCount: competition.samePriceListingCount,
              priceBandCrowdingUnits: competition.priceBandCrowdingUnits,
              undercutRisk: competition.undercutRisk,
              nearTargetListings: competition.nearTargetListingCount,
              nearTargetSpreadPct: competition.nearTargetSpreadPct,
              competitionPressureScore: ladderPressure.competitionPressureScore,
              priceLadderPressureScore: ladderPressure.competitionPressureScore,
              queuePressureScore: ladderPressure.competitionPressureScore,
              cheaperUnitPressureScore: ladderPressure.cheaperUnitPressureScore,
              samePriceCongestionScore: ladderPressure.samePriceCongestionScore,
              nearPriceCrowdingScore: ladderPressure.nearPriceCrowdingScore,
              supplyOverhangNearTargetScore: ladderPressure.supplyOverhangNearTargetScore,
              targetConfidenceScale: ladderPressure.targetConfidenceScale,
              competitionProbabilityScale: ladderPressure.probabilityScale,
              competitionSaleRateScale: ladderPressure.saleRateScale,
              competitionUnitRateScale: ladderPressure.unitRateScale,
              competitionQtyScale: ladderPressure.qtyScale,
              exitFragilityScore: ladderPressure.exitFragilityScore,
              competitionDelayDays: competitionDelayDays,
              queueAheadListingCount: competition.queueAheadListingCount,
              worldName: purchasePlan.worldName,
              buyPrice: purchasePlan.avgListingPrice,
              buyPriceFirst: purchasePlan.firstUnitPrice,
              buyPriceMarginal: purchasePlan.marginalUnitPrice,
              buyQtyAtBestPrice: purchasePlan.qtyAtBestPrice,
              buyListingsUsed: purchasePlan.listingsUsed,
              buyDistinctPriceCount: purchasePlan.distinctPriceCount,
              buyPriceImpactRatio: purchasePlan.priceImpactRatio,
              availableQty: purchasePlan.availableQty,
              recommendedQty: candidateQty,
              buyCostPerUnit: candidateBuyCostPerUnit,
              profit: candidateProfit,
              roi: candidateRoi,
              sellProbRawAtRecommended: rawCandidateSellProb,
              sellProbAtRecommended: candidateSellProb,
              expectedUnitsSoldRawAtRecommended: rawCandidateExpectedUnitsSold,
              expectedUnitsSoldAtRecommended: candidateExpectedUnitsSold,
              planCost: candidatePlanCost,
              planExpectedProfit: candidatePlanExpectedProfit,
              daysToClearRecommended: candidateDaysToClear,
              listingSlotPressure: executionMetrics.listingSlotPressure,
              stackFragmentation: executionMetrics.stackFragmentation,
              travelFrictionBurden: executionMetrics.travelFrictionBurden,
              executionBurden: executionMetrics.executionBurden,
              expectedGilPerDay: candidateExpectedGilPerDay,
              capitalEfficiency: candidateCapitalEfficiency,
              riskAdjusted: candidateRiskAdjusted,
              demandModelType: demandModel.unitModelType || demandModel.modelType,
              demandDispersionRatio: demandModel.unitDispersionRatio || demandModel.dispersionRatio,
              demandRecencyBlendWeight: demandModel.recencyBlendWeight,
              calibrationScale: calibrationScale,
              calibrationSignalPenalty: calibrationMetrics.signalPenalty,
              calibrationBucket: calibrationMetrics.bucketKey,
              calibrationBucketEvaluated: calibrationMetrics.bucketEvaluated,
              probabilityTaperScale: candidateSellProbTaper.scale,
              probabilityTaperApplied: !!candidateSellProbTaper.applied,
              probabilityTaperStressScore: candidateSellProbTaper.stressScore,
              probabilityTaperBucket: candidateSellProbTaper.bucketKey,
              marketFragilityScore: marketFragility.stressScore,
              marketFragilityFloorBreakScore: marketFragility.floorBreakScore,
              marketFragilityNearPriceCrowdingScore: marketFragility.nearPriceCrowdingScore,
              marketFragilitySupplyOverhangScore: marketFragility.supplyOverhangScore,
              marketFragilityHoldWindowScale: marketFragility.holdWindowScale,
              marketFragilitySignalPenalty: marketFragility.signalPenalty,
              effectiveHoldingDays: effectiveHoldingDays
            };
          }
        }
      });
    });

    if (!selectedPlan) {
      return;
    }

    var referenceSource = selectedPlan.referenceSource;
    var referenceMin = selectedPlan.referenceMin;
    var referenceMedian = selectedPlan.referenceMedian;
    var targetSell = selectedPlan.targetSell;
    var targetWasCapped = !!selectedPlan.targetWasCapped;
    var targetCapReason = selectedPlan.targetCapReason;
    var netSell = selectedPlan.netSell;
    var bandSaleRate = selectedPlan.bandSaleRate;
    var bandLambda = selectedPlan.bandLambda;
    var targetUnitsExpected = selectedPlan.targetUnitsExpected;
    var calibratedTargetUnitsExpected = selectedPlan.calibratedTargetUnitsExpected;
    var rawPAtLeastOneTarget = selectedPlan.pAtLeastOneTargetRaw;
    var pAtLeastOneTarget = selectedPlan.pAtLeastOneTarget;
    var firstSaleProbAtRecommended = selectedPlan.firstSaleProbAtRecommended;
    var queueAheadUnits = selectedPlan.queueAheadUnits;
    var effectiveQueueAheadUnits = selectedPlan.effectiveQueueAheadUnits;
    var rawQueueDays = selectedPlan.rawQueueDays;
    var queueDays = selectedPlan.queueDays;
    var rawDaysToFirstSale = selectedPlan.rawDaysToFirstSale;
    var daysToFirstSale = selectedPlan.daysToFirstSale;
    var targetUnitsAfterQueue = selectedPlan.targetUnitsAfterQueue;
    var rawTargetUnitsAfterQueue = selectedPlan.rawTargetUnitsAfterQueue;
    var calibratedTargetUnitsAfterQueue = selectedPlan.calibratedTargetUnitsAfterQueue;
    var suggestedStackQty = selectedPlan.suggestedStackQty;
    var cheaperUnitsAhead = selectedPlan.cheaperUnitsAhead;
    var samePriceUnitsAhead = selectedPlan.samePriceUnitsAhead;
    var priceBandCrowdingUnits = selectedPlan.priceBandCrowdingUnits;
    var undercutRisk = selectedPlan.undercutRisk;
    var nearTargetListings = selectedPlan.nearTargetListings;
    var nearTargetSpreadPct = selectedPlan.nearTargetSpreadPct;
    var buyCostPerUnit = selectedPlan.buyCostPerUnit;
    var profit = selectedPlan.profit;
    var roi = selectedPlan.roi;
    var recommendedQty = selectedPlan.recommendedQty;
    var sellProbRawAtRecommended = selectedPlan.sellProbRawAtRecommended;
    var sellProbAtRecommended = selectedPlan.sellProbAtRecommended;
    var expectedUnitsSoldRawAtRecommended = selectedPlan.expectedUnitsSoldRawAtRecommended;
    var expectedUnitsSoldAtRecommended = selectedPlan.expectedUnitsSoldAtRecommended;
    var planCost = selectedPlan.planCost;
    var planExpectedProfit = selectedPlan.planExpectedProfit;
    var daysToClearRecommended = selectedPlan.daysToClearRecommended;
    var effectiveHoldingDays = Number.isFinite(selectedPlan.effectiveHoldingDays)
      ? selectedPlan.effectiveHoldingDays
      : holdingDays;
    var listingSlotPressure = selectedPlan.listingSlotPressure;
    var stackFragmentation = selectedPlan.stackFragmentation;
    var executionBurden = selectedPlan.executionBurden;
    var expectedGilPerDay = selectedPlan.expectedGilPerDay;
    var capitalEfficiency = selectedPlan.capitalEfficiency;
    var marketFragilityScore = Number.isFinite(selectedPlan.marketFragilityScore)
      ? selectedPlan.marketFragilityScore
      : 0;

    if (
      !(profit > 0) ||
      recommendedQty < 1 ||
      !Number.isFinite(daysToClearRecommended) ||
      daysToClearRecommended > (effectiveHoldingDays * 1.25)
    ) {
      return;
    }

    var pricePremiumToMedian = Number.isFinite(referenceMedian) && referenceMedian > 0
      ? ((targetSell - referenceMedian) / referenceMedian)
      : null;

    var queuePenalty = Number.isFinite(daysToClearRecommended)
      ? (1 + Math.max(0, daysToClearRecommended - effectiveHoldingDays))
      : 50;
    var executionPenalty = 1 + (Math.max(0, executionBurden || 0) * 0.15);
    var riskAdjusted = selectedPlan.riskAdjusted || (
      expectedGilPerDay *
      Math.max(0.25, 0.75 + capitalEfficiency + backtestBias) *
      Math.max(0.4, sellProbAtRecommended) /
      (queuePenalty * executionPenalty)
    );
    var signal = classifyScannerSignal({
      roi: roi,
      pTarget: pAtLeastOneTarget,
      pQty: sellProbAtRecommended,
      pFirstSale: firstSaleProbAtRecommended,
      recommendedQty: recommendedQty,
      homeLambda: homeLambda,
      bandLambda: bandLambda,
      bandSaleRate: bandSaleRate,
      queueDays: queueDays,
      competitionDelayDays: selectedPlan.competitionDelayDays,
      targetUnitsAfterQueue: targetUnitsAfterQueue,
      holdingDays: holdingDays,
      effectiveHoldingDays: effectiveHoldingDays,
      pricePremium: pricePremiumToMedian,
      daysToClearRecommended: daysToClearRecommended,
      daysToFirstSale: daysToFirstSale,
      nearTargetListings: nearTargetListings,
      nearTargetSpreadPct: nearTargetSpreadPct,
      historicalBias: backtestBias,
      calibrationPenalty: selectedPlan.calibrationSignalPenalty,
      calibrationScale: selectedPlan.calibrationScale,
      demandModelType: selectedPlan.demandModelType,
      demandDispersionRatio: selectedPlan.demandDispersionRatio,
      competitionPressureScore: selectedPlan.competitionPressureScore,
      priceLadderPressureScore: selectedPlan.priceLadderPressureScore,
      queuePressureScore: selectedPlan.queuePressureScore,
      targetConfidenceScale: selectedPlan.targetConfidenceScale,
      exitFragilityScore: selectedPlan.exitFragilityScore,
      cheaperUnitPressureScore: selectedPlan.cheaperUnitPressureScore,
      samePriceCongestionScore: selectedPlan.samePriceCongestionScore,
      nearPriceCrowdingScore: selectedPlan.nearPriceCrowdingScore,
      marketFragilityScore: marketFragilityScore,
      marketFragilitySignalPenalty: selectedPlan.marketFragilitySignalPenalty,
      executionBurden: executionBurden,
      listingSlotPressure: listingSlotPressure,
      capitalEfficiency: capitalEfficiency
    });

    opportunities.push({
      quality: quality, historyCoverage: coverage, homeFreshness: homeFreshness,
      buyFreshness: freshness(Math.min(...selectedPlan.usedListings.map(row => row.lastReviewTimeSec || 0)), nowSec),
      purchasedQty: selectedPlan.purchasedQty, excessQty: selectedPlan.excessQty,
      usedListings: selectedPlan.usedListings, ...selectedPlan.economics,
      listingSampleLimited: !!homeData.sampleLimited || !!listingsByItemWorld[id]?.[selectedPlan.worldName]?.sampleLimited,
      id: id,
      itemName: (itemNameById[id] || ("Item " + id)) + " (" + quality + ")",
      qualitySummary: itemQualitySummary,
      marketDataUpdatedSec: itemMarketDataUpdatedSec,
      buyWorld: selectedPlan.worldName,
      buyPrice: selectedPlan.buyPrice,
      buyPriceFirst: selectedPlan.buyPriceFirst,
      buyPriceMarginal: selectedPlan.buyPriceMarginal,
      buyWorldAvailableQty: selectedPlan.availableQty,
      recommendedQty: recommendedQty,
      sellProbAtRecommended: sellProbAtRecommended,
      expectedUnitsSoldAtRecommended: expectedUnitsSoldAtRecommended,
      planCost: planCost,
      planExpectedProfit: planExpectedProfit,
      homeMin: homeMin,
      dcMin: dcMin,
      regionMin: regionMin,
      medianSaleHome: medianSaleHome,
      medianSaleDc: medianSaleDc,
      medianSaleRegion: medianSaleRegion,
      referenceSource: referenceSource,
      refMin: referenceMin,
      refMedian: referenceMedian,
      exitStyle: selectedPlan.exitStyle,
      exitLabel: selectedPlan.exitLabel,
      targetSell: targetSell,
      targetWasCapped: targetWasCapped,
      targetCapReason: targetCapReason,
      netSell: netSell,
      profit: profit,
      roi: roi,
      homeLambda: homeLambda,
      dcLambda: dcLambda,
      bandSaleRate: bandSaleRate,
      bandLambda: bandLambda,
      bandLambdaSimple: selectedPlan.bandLambdaSimple,
      targetUnitsExpected: targetUnitsExpected,
      calibratedTargetUnitsExpected: calibratedTargetUnitsExpected,
      pAtLeastOneTargetRaw: rawPAtLeastOneTarget,
      pAtLeastOneTarget: pAtLeastOneTarget,
      firstSaleProbAtRecommended: firstSaleProbAtRecommended,
      queueAheadUnits: queueAheadUnits,
      effectiveQueueAheadUnits: effectiveQueueAheadUnits,
      rawCompetitionUnits: selectedPlan.rawCompetitionUnits,
      effectiveCompetitionUnits: selectedPlan.effectiveCompetitionUnits,
      rawCompetitionSaleEvents: selectedPlan.rawCompetitionSaleEvents,
      effectiveCompetitionSaleEvents: selectedPlan.effectiveCompetitionSaleEvents,
      rawQueueDays: rawQueueDays,
      queueDays: queueDays,
      rawDaysToFirstSale: rawDaysToFirstSale,
      daysToFirstSale: daysToFirstSale,
      targetUnitsAfterQueue: targetUnitsAfterQueue,
      rawTargetUnitsAfterQueue: rawTargetUnitsAfterQueue,
      calibratedTargetUnitsAfterQueue: calibratedTargetUnitsAfterQueue,
      daysToClearRecommended: daysToClearRecommended,
      sellProbRawAtRecommended: sellProbRawAtRecommended,
      expectedGilPerDay: expectedGilPerDay,
      expectedUnitsSoldRawAtRecommended: expectedUnitsSoldRawAtRecommended,
      capitalEfficiency: capitalEfficiency,
      suggestedStackQty: suggestedStackQty,
      averageUnitsPerSale: selectedPlan.averageUnitsPerSale,
      cheaperUnitsAhead: cheaperUnitsAhead,
      samePriceUnitsAhead: samePriceUnitsAhead,
      samePriceListingCount: selectedPlan.samePriceListingCount,
      priceBandCrowdingUnits: priceBandCrowdingUnits,
      undercutRisk: undercutRisk,
      nearTargetListings: nearTargetListings,
      nearTargetSpreadPct: nearTargetSpreadPct,
      competitionPressureScore: selectedPlan.competitionPressureScore,
      priceLadderPressureScore: selectedPlan.priceLadderPressureScore,
      queuePressureScore: selectedPlan.queuePressureScore,
      targetConfidenceScale: selectedPlan.targetConfidenceScale,
      competitionProbabilityScale: selectedPlan.competitionProbabilityScale,
      competitionSaleRateScale: selectedPlan.competitionSaleRateScale,
      competitionUnitRateScale: selectedPlan.competitionUnitRateScale,
      competitionQtyScale: selectedPlan.competitionQtyScale,
      competitionDelayDays: selectedPlan.competitionDelayDays,
      cheaperUnitPressureScore: selectedPlan.cheaperUnitPressureScore,
      samePriceCongestionScore: selectedPlan.samePriceCongestionScore,
      nearPriceCrowdingScore: selectedPlan.nearPriceCrowdingScore,
      supplyOverhangNearTargetScore: selectedPlan.supplyOverhangNearTargetScore,
      exitFragilityScore: selectedPlan.exitFragilityScore,
      queueAheadListingCount: selectedPlan.queueAheadListingCount,
      buyQtyAtBestPrice: selectedPlan.buyQtyAtBestPrice,
      buyListingsUsed: selectedPlan.buyListingsUsed,
      buyDistinctPriceCount: selectedPlan.buyDistinctPriceCount,
      buyPriceImpactRatio: selectedPlan.buyPriceImpactRatio,
      listingSlotPressure: listingSlotPressure,
      stackFragmentation: stackFragmentation,
      travelFrictionBurden: selectedPlan.travelFrictionBurden,
      executionBurden: executionBurden,
      backtestBias: backtestBias,
      backtestEvaluated: backtestMetrics ? backtestMetrics.evaluated : 0,
      backtestFillRate: backtestMetrics ? backtestMetrics.fillRate : null,
      modelCalibrationScale: selectedPlan.calibrationScale,
      modelCalibrationEvaluated: marketCalibration ? marketCalibration.evaluated : 0,
      modelCalibrationConfidence: marketCalibration ? marketCalibration.confidence : 0,
      modelObservedRate: marketCalibration ? marketCalibration.weightedObservedRate : null,
      modelCalibrationBucket: selectedPlan.calibrationBucket,
      modelCalibrationBucketEvaluated: selectedPlan.calibrationBucketEvaluated,
      probabilityTaperScale: selectedPlan.probabilityTaperScale,
      probabilityTaperApplied: selectedPlan.probabilityTaperApplied,
      probabilityTaperStressScore: selectedPlan.probabilityTaperStressScore,
      probabilityTaperBucket: selectedPlan.probabilityTaperBucket,
      demandModelType: selectedPlan.demandModelType,
      demandDispersionRatio: selectedPlan.demandDispersionRatio,
      demandRecencyBlendWeight: selectedPlan.demandRecencyBlendWeight,
      holdingDays: holdingDays,
      effectiveHoldingDays: effectiveHoldingDays,
      pricePremiumToMedian: pricePremiumToMedian,
      marketFragilityScore: marketFragilityScore,
      marketFragilityFloorBreakScore: selectedPlan.marketFragilityFloorBreakScore,
      marketFragilityNearPriceCrowdingScore: selectedPlan.marketFragilityNearPriceCrowdingScore,
      marketFragilitySupplyOverhangScore: selectedPlan.marketFragilitySupplyOverhangScore,
      riskAdjusted: riskAdjusted,
      signal: signal
    });
  });

  return opportunities;
}

function buildQualitySummary(label, rows) {
  var counts = countQualityUnits(rows);
  var parts = [];
  if (counts.nq > 0) {
    parts.push("NQ " + formatInt(counts.nq));
  }
  if (counts.hq > 0) {
    parts.push("HQ " + formatInt(counts.hq));
  }
  if (counts.unknown > 0) {
    parts.push("Unknown " + formatInt(counts.unknown));
  }
  return parts.length ? (label + ": " + parts.join(" / ")) : "";
}

function countQualityUnits(rows) {
  var counts = { hq: 0, nq: 0, unknown: 0 };
  var safeRows = Array.isArray(rows) ? rows : [];
  safeRows.forEach(function (row) {
    var quantity = Number.isFinite(row && row.quantity) ? row.quantity : 0;
    if (row && row.hq === true) {
      counts.hq += quantity;
    } else if (row && row.hq === false) {
      counts.nq += quantity;
    } else {
      counts.unknown += quantity;
    }
  });
  return counts;
}

function collectListingsFromWorldMap(worldMap) {
  var rows = [];
  var safeMap = worldMap && typeof worldMap === "object" ? worldMap : {};
  Object.keys(safeMap).forEach(function (worldName) {
    var worldData = safeMap[worldName] || {};
    if (Array.isArray(worldData.listings)) {
      rows = rows.concat(worldData.listings);
    }
  });
  return rows;
}

function getLatestListingReviewSec(listings) {
  var latest = null;
  var safeListings = Array.isArray(listings) ? listings : [];
  safeListings.forEach(function (listing) {
    if (Number.isFinite(listing && listing.lastReviewTimeSec)) {
      latest = latest === null ? listing.lastReviewTimeSec : Math.max(latest, listing.lastReviewTimeSec);
    }
  });
  return latest;
}

function getLatestEntryTimestampSec(entries) {
  var latest = null;
  var safeEntries = Array.isArray(entries) ? entries : [];
  safeEntries.forEach(function (entry) {
    if (Number.isFinite(entry && entry.timestampSec)) {
      latest = latest === null ? entry.timestampSec : Math.max(latest, entry.timestampSec);
    }
  });
  return latest;
}

function getCompetitionPressureValue(metrics) {
  if (!metrics || typeof metrics !== "object") {
    return 0;
  }
  var pressure = Number.isFinite(metrics.priceLadderPressureScore)
    ? metrics.priceLadderPressureScore
    : (
      Number.isFinite(metrics.competitionPressureScore)
        ? metrics.competitionPressureScore
        : metrics.queuePressureScore
    );
  return Number.isFinite(pressure) ? Math.max(0, pressure) : 0;
}

function estimateListingSlotPressure(totalQty, stackQty) {
  var qty = Number.isFinite(totalQty) && totalQty > 0 ? Math.ceil(totalQty) : 0;
  var stack = Number.isFinite(stackQty) && stackQty > 0 ? Math.ceil(stackQty) : 1;
  if (!qty) {
    return 0;
  }
  return Math.max(0, Math.ceil(qty / stack) - 1);
}

function estimateStackFragmentation(totalQty, stackQty) {
  var qty = Number.isFinite(totalQty) && totalQty > 0 ? Math.ceil(totalQty) : 0;
  var stack = Number.isFinite(stackQty) && stackQty > 1 ? Math.ceil(stackQty) : 0;
  if (!qty || !stack || qty <= stack) {
    return 0;
  }
  var remainder = qty % stack;
  if (!remainder) {
    return 0;
  }
  return remainder / stack;
}

function buildExecutionMetrics(args) {
  var settings = args && typeof args === "object" ? args : {};
  var purchasePlan = settings.purchasePlan && typeof settings.purchasePlan === "object"
    ? settings.purchasePlan
    : null;
  var recommendedQty = Number.isFinite(settings.recommendedQty) && settings.recommendedQty > 0
    ? Math.ceil(settings.recommendedQty)
    : 0;
  var suggestedStackQty = Number.isFinite(settings.suggestedStackQty) && settings.suggestedStackQty > 0
    ? Math.ceil(settings.suggestedStackQty)
    : 1;
  var listingSlotPressure = estimateListingSlotPressure(recommendedQty, suggestedStackQty);
  var stackFragmentation = estimateStackFragmentation(recommendedQty, suggestedStackQty);
  var travelFee = Number.isFinite(settings.travelFee) && settings.travelFee > 0 ? settings.travelFee : 0;
  var avgUnitCost = purchasePlan && Number.isFinite(purchasePlan.avgUnitCost) ? purchasePlan.avgUnitCost : 0;
  var travelFrictionBurden = avgUnitCost > 0
    ? Math.min(0.75, (travelFee / avgUnitCost) * 10)
    : 0;
  var listingTouchBurden = purchasePlan && Number.isFinite(purchasePlan.executionBurden)
    ? purchasePlan.executionBurden
    : 0;
  var executionBurden =
    listingTouchBurden +
    (listingSlotPressure * 0.4) +
    (stackFragmentation * 0.8) +
    travelFrictionBurden;

  return {
    listingSlotPressure: listingSlotPressure,
    stackFragmentation: stackFragmentation,
    travelFrictionBurden: travelFrictionBurden,
    executionBurden: executionBurden
  };
}

function findQMaxConfidence(mu, confidence, maxQ, probabilityResolver) {
  var resolver = typeof probabilityResolver === "function"
    ? probabilityResolver
    : null;
  if (!resolver && !(mu > 0)) {
    return 0;
  }

  var bestQ = 0;
  for (var q = 1; q <= maxQ; q++) {
    var p = resolver
      ? clamp(resolver(q, mu), 0, 1)
      : poissonProbAtLeast(mu, q);
    if (p >= confidence) {
      bestQ = q;
    } else {
      break;
    }
  }
  return bestQ;
}

function poissonProbAtLeast(mu, q) {
  if (q <= 0) {
    return 1;
  }
  if (!(mu > 0)) {
    return 0;
  }
  var cdf = poissonCdf(q - 1, mu);
  return clamp(1 - cdf, 0, 1);
}

function poissonCdf(k, mu) {
  if (k < 0) {
    return 0;
  }
  if (!(mu > 0)) {
    return 1;
  }

  if (mu > 700) {
    return poissonCdfLogSumExp(k, mu);
  }

  var pmf = Math.exp(-mu);
  var cdf = pmf;

  for (var i = 1; i <= k; i++) {
    pmf = (pmf * mu) / i;
    cdf += pmf;
    if (cdf >= 1) {
      return 1;
    }
  }

  return clamp(cdf, 0, 1);
}

function poissonCdfLogSumExp(k, mu) {
  var logTerm = -mu;
  var maxLog = logTerm;
  var logs = [logTerm];

  for (var i = 1; i <= k; i++) {
    logTerm = logTerm + Math.log(mu) - Math.log(i);
    logs.push(logTerm);
    if (logTerm > maxLog) {
      maxLog = logTerm;
    }
  }

  var scaledSum = 0;
  for (var j = 0; j < logs.length; j++) {
    scaledSum += Math.exp(logs[j] - maxLog);
  }

  var cdf = Math.exp(maxLog) * scaledSum;
  return clamp(cdf, 0, 1);
}

function classifyScannerSignal(metrics) {
  var pQty = Number.isFinite(metrics.pQty) ? metrics.pQty : metrics.pTarget;
  var pFirstSale = Number.isFinite(metrics.pFirstSale) ? metrics.pFirstSale : metrics.pTarget;
  var nearTargetListings = Number.isFinite(metrics.nearTargetListings) ? metrics.nearTargetListings : 0;
  var historicalBias = Number.isFinite(metrics.historicalBias) ? metrics.historicalBias : 0;
  var calibrationPenalty = Number.isFinite(metrics.calibrationPenalty) ? metrics.calibrationPenalty : 0;
  var calibrationScale = Number.isFinite(metrics.calibrationScale) ? metrics.calibrationScale : 1;
  var queuePressureScore = getCompetitionPressureValue(metrics);
  var dispersionRatio = Number.isFinite(metrics.demandDispersionRatio) ? metrics.demandDispersionRatio : 1;
  var daysToClearRecommended = Number.isFinite(metrics.daysToClearRecommended)
    ? metrics.daysToClearRecommended
    : Number.POSITIVE_INFINITY;
  var daysToFirstSale = Number.isFinite(metrics.daysToFirstSale)
    ? metrics.daysToFirstSale
    : Number.POSITIVE_INFINITY;
  var competitionDelayDays = Number.isFinite(metrics.competitionDelayDays)
    ? metrics.competitionDelayDays
    : (Number.isFinite(metrics.queueDays) ? metrics.queueDays : 0);
  var holdingDays = Number.isFinite(metrics.holdingDays) && metrics.holdingDays > 0 ? metrics.holdingDays : 1;
  var effectiveHoldingDays = Number.isFinite(metrics.effectiveHoldingDays) && metrics.effectiveHoldingDays > 0
    ? metrics.effectiveHoldingDays
    : holdingDays;
  var executionBurden = Number.isFinite(metrics.executionBurden) ? Math.max(0, metrics.executionBurden) : 0;
  var listingSlotPressure = Number.isFinite(metrics.listingSlotPressure) ? Math.max(0, metrics.listingSlotPressure) : 0;
  var marketFragilityScore = Number.isFinite(metrics.marketFragilityScore) ? metrics.marketFragilityScore : 0;
  var targetConfidenceScale = Number.isFinite(metrics.targetConfidenceScale) ? metrics.targetConfidenceScale : 1;
  var exitFragilityScore = Number.isFinite(metrics.exitFragilityScore) ? metrics.exitFragilityScore : 0;
  var cheaperUnitPressureScore = Number.isFinite(metrics.cheaperUnitPressureScore) ? metrics.cheaperUnitPressureScore : 0;
  var samePriceCongestionScore = Number.isFinite(metrics.samePriceCongestionScore) ? metrics.samePriceCongestionScore : 0;
  var nearPriceCrowdingScore = Number.isFinite(metrics.nearPriceCrowdingScore) ? metrics.nearPriceCrowdingScore : 0;
  var marketFragilityPenalty = Number.isFinite(metrics.marketFragilitySignalPenalty)
    ? metrics.marketFragilitySignalPenalty
    : clamp(marketFragilityScore * 0.16, 0, 0.16);
  var roiScore = clamp((Number.isFinite(metrics.roi) ? metrics.roi : 0) / 0.2, 0, 2);
  var qtyProbScore = clamp((pQty - 0.25) / 0.6, 0, 1.25);
  var targetProbScore = clamp(((Number.isFinite(metrics.pTarget) ? metrics.pTarget : pQty) - 0.2) / 0.7, 0, 1.2);
  var liquidityScore = clamp((
    ((Number.isFinite(metrics.bandLambda) ? metrics.bandLambda : 0) * 0.75) +
    ((Number.isFinite(metrics.bandSaleRate) ? metrics.bandSaleRate : 0) * 0.6)
  ) / 3.5, 0, 1.2);
  var competitionConfidenceScore = clamp((targetConfidenceScale - 0.55) / 0.45, 0, 1.15);
  var firstSaleScore = Number.isFinite(daysToFirstSale)
    ? clamp(1 - (daysToFirstSale / Math.max(effectiveHoldingDays, 1)), 0, 1.1)
    : 0;
  var clearScore = Number.isFinite(daysToClearRecommended)
    ? clamp(1 - (daysToClearRecommended / Math.max(effectiveHoldingDays * 1.35, 1)), 0, 1.1)
    : 0;
  var postQueueScore = clamp((Number.isFinite(metrics.targetUnitsAfterQueue) ? metrics.targetUnitsAfterQueue : 0) / Math.max(metrics.recommendedQty || 1, 1), 0, 1.3);
  var crowdingPenalty = clamp(((nearTargetListings - 2) / 8) + (nearPriceCrowdingScore * 0.5), 0, 1.2);
  var spreadPenalty = Number.isFinite(metrics.nearTargetSpreadPct) && nearTargetListings >= 3
    ? clamp((0.012 - metrics.nearTargetSpreadPct) / 0.012, 0, 1)
    : 0;
  var premiumPenalty = Number.isFinite(metrics.pricePremium)
    ? clamp(metrics.pricePremium / 0.22, 0, 1.25)
    : 0;
  var demandPenalty = metrics.homeLambda < 0.5 ? 0.25 : 0;
  var dispersionPenalty = metrics.demandModelType === "negative_binomial"
    ? clamp((dispersionRatio - 1) / 3, 0, 0.45)
    : 0;
  var queuePressurePenalty = clamp(queuePressureScore / 5.5, 0, 0.45);
  var competitionDelayPenalty = clamp(competitionDelayDays / Math.max(effectiveHoldingDays, 1), 0, 0.42);
  var exitFragilityPenalty = clamp(exitFragilityScore / 1.35, 0, 0.4);
  var samePricePenalty = clamp(samePriceCongestionScore / 1.7, 0, 0.28);
  var cheaperAheadPenalty = clamp(cheaperUnitPressureScore / 3, 0, 0.28);
  var executionPenalty = clamp((executionBurden / 4.5) + (listingSlotPressure / 8), 0, 0.45);
  var calibrationScalePenalty = clamp((1 - calibrationScale) / 0.45, 0, 0.6);
  var biasBoost = historicalBias * 1.25;
  var score = (
    (roiScore * 0.2) +
    (qtyProbScore * 0.24) +
    (targetProbScore * 0.12) +
    (liquidityScore * 0.09) +
    (competitionConfidenceScore * 0.09) +
    (firstSaleScore * 0.1) +
    (clearScore * 0.11) +
    (postQueueScore * 0.11) +
    biasBoost
  ) - (
    (crowdingPenalty * 0.12) +
    (spreadPenalty * 0.06) +
    (premiumPenalty * 0.16) +
    demandPenalty +
    dispersionPenalty +
    queuePressurePenalty +
    competitionDelayPenalty +
    exitFragilityPenalty +
    samePricePenalty +
    cheaperAheadPenalty +
    executionPenalty +
    marketFragilityPenalty +
    calibrationScalePenalty +
    (calibrationPenalty * 0.18)
  );

  var almostGuaranteed =
    pQty >= 0.9 &&
    pFirstSale >= 0.85 &&
    metrics.pTarget >= 0.98 &&
    metrics.recommendedQty >= 3 &&
    metrics.targetUnitsAfterQueue >= 3 &&
    marketFragilityScore <= 0.2 &&
    targetConfidenceScale >= 0.9 &&
    exitFragilityScore <= 0.32 &&
    competitionDelayDays <= (effectiveHoldingDays * 0.2) &&
    Number.isFinite(daysToFirstSale) &&
    daysToFirstSale <= (effectiveHoldingDays * 0.45) &&
    Number.isFinite(daysToClearRecommended) &&
    daysToClearRecommended <= (effectiveHoldingDays * 0.75) &&
    nearTargetListings <= 2 &&
    metrics.bandLambda >= 2 &&
    metrics.roi >= 0.08 &&
    calibrationScale >= 0.92 &&
    (!Number.isFinite(metrics.pricePremium) || metrics.pricePremium <= 0.1) &&
    queuePressureScore <= 1.15 &&
    samePriceCongestionScore <= 0.45 &&
    cheaperUnitPressureScore <= 0.7 &&
    executionBurden <= 1.4 &&
    listingSlotPressure <= 1 &&
    dispersionRatio <= 1.35;

  if (almostGuaranteed && score >= 0.95) {
    return "Very Strong";
  }

  if (score >= 0.68) {
    return "Strong";
  }
  if (score >= 0.42) {
    return "Good";
  }
  if (score >= 0.18) {
    return "Test Small";
  }
  return "Thin";
}

function median(values) {
  if (!values.length) {
    return null;
  }

  var sorted = values.slice().sort(function (a, b) { return a - b; });
  var mid = Math.floor(sorted.length / 2);

  if (sorted.length % 2 === 1) {
    return sorted[mid];
  }

  return (sorted[mid - 1] + sorted[mid]) / 2;
}

function clamp(value, min, max) {
  if (!Number.isFinite(value)) {
    return min;
  }
  return Math.min(max, Math.max(min, value));
}

function createNumberFormatter(options) {
  try {
    if (typeof Intl !== "undefined" && Intl && typeof Intl.NumberFormat === "function") {
      return new Intl.NumberFormat("en-US", options);
    }
  } catch (err) {
    // Fall back to simple string formatting below.
  }
  return null;
}

function getIntFormatter() {
  if (!intFormatter) {
    intFormatter = createNumberFormatter({ maximumFractionDigits: 0 });
  }
  return intFormatter;
}

function formatInt(value) {
  if (!Number.isFinite(value)) {
    return "N/A";
  }
  var formatter = getIntFormatter();
  if (formatter && typeof formatter.format === "function") {
    return formatter.format(Math.round(value));
  }
  return String(Math.round(value));
}

export async function buildScannerOpportunitiesAsync(args, signal, onMetrics) {
  const rows = [], items = args.items || []
  const started = performance.now()
  let longestBatchMs = 0
  for (let index = 0; index < items.length; index += 4) {
    if (signal?.aborted) throw new DOMException("Scanner canceled", "AbortError")
    const batchStart = performance.now()
    rows.push(...buildScannerOpportunitiesForSettings({ ...args, items: items.slice(index, index + 4) }))
    longestBatchMs = Math.max(longestBatchMs, performance.now() - batchStart)
    await new Promise(resolve => setTimeout(resolve, 0))
  }
  onMetrics?.({ calculationMs: performance.now() - started, longestBatchMs, evaluatedItems: items.length, qualifiedRows: rows.length })
  return rows
}
