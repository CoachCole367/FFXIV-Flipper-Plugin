import { readFile, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { resolve, dirname } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
const website = resolve(process.argv[2] ?? '../FFXIV Flipper');
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const { runScannerBenchmark } = await import(pathToFileURL(resolve(website, 'test-harness/benchmark.js')));
const { buildScannerOpportunitiesForSettings } = await import(pathToFileURL(resolve(website, 'assets/modules/scanner-planner.js')));
const { buildScannerBasket, buildDemandModel, buildSellPriceGuide } = await import(pathToFileURL(resolve(website, 'assets/modules/scanner-strategy.js')));
const report = runScannerBenchmark();
const cases = [];
const selected = [...new Map(report.fixtures.map(f => [f.archetype, f])).values()];
for (const f of selected) {
  const worldMap = Object.fromEntries(Object.entries(f.worldMap).map(([world,data]) => [world,{...data,lastUpdatedSec:f.scanSec,listings:data.listings.map((l,i)=>({...l,hq:false,lastReviewTimeSec:f.scanSec,listingId:`${world}-${i}`}))}]));
  worldMap.Leviathan = {lastUpdatedSec:f.scanSec,listings:f.homeListings.map((l,i)=>({...l,hq:false,worldName:'Leviathan',lastReviewTimeSec:f.scanSec,listingId:`home-${i}`}))};
  const input = { nowSec:f.scanSec, homeWorld:'Leviathan', items:[{id:f.id}], itemNameById:{[f.id]:f.itemName}, listingsByItemWorld:{[f.id]:worldMap}, historyByItemHome:{[f.id]:{entries:f.preSales.map(s=>({...s,hq:false})),available:true}}, historyByItemDc:{}, lookbackDays:7,holdingDays:2,taxRate:.05,travelFee:0,minProfit:10,minRoi:.04,minTargetProb:.18,qtyConfidence:.58,maxBuyQty:20,targetCapRatio:.14,priceBandRatio:.1,referenceMarket:'home',budgetGil:1000000 };
  const add = (name, settings) => { const rows=buildScannerOpportunitiesForSettings(settings); cases.push({name,kind:'scanner',input:settings,expected:{rows,basket:buildScannerBasket(rows,{budgetGil:settings.budgetGil})}}); };
  add(f.archetype,input);
  const demandInput={entries:f.preSales,options:{lookbackDays:7,horizonDays:2,nowSec:f.scanSec}};
  cases.push({name:`${f.archetype}-demand`,kind:'demand',input:demandInput,expected:buildDemandModel(demandInput.entries,demandInput.options)});
  const guideInput={currentListings:worldMap.Leviathan.listings,salesEntries:f.preSales,lookbackDays:7,sellWindowDays:2,nowSec:f.scanSec};
  cases.push({name:`${f.archetype}-guide`,kind:'guide',input:guideInput,expected:buildSellPriceGuide(guideInput)});
  if (cases.length===3) {
    add('insufficient-capital',{...input,budgetGil:1});
    add('missing-home-history',{...input,historyByItemHome:{}});
    add('stale-buy-listings',{...input,listingsByItemWorld:{[f.id]:Object.fromEntries(Object.entries(worldMap).map(([world,data])=>[world,world==='Leviathan'?data:{...data,lastUpdatedSec:f.scanSec-172800,listings:data.listings.map(l=>({...l,lastReviewTimeSec:f.scanSec-172800}))}]))}});
    add('quality-isolation',{...input,historyByItemHome:{[f.id]:{entries:f.preSales.map(s=>({...s,hq:true})),available:true}}});
    const hqWorlds=Object.fromEntries(Object.entries(worldMap).map(([world,data])=>[world,{...data,listings:data.listings.map(l=>({...l,hq:true}))}]));
    add('hq-supported',{...input,listingsByItemWorld:{[f.id]:hqWorlds},historyByItemHome:{[f.id]:{entries:f.preSales.map(s=>({...s,hq:true})),available:true}}});
  }
}
const positive = cases.find(c => c.kind==='scanner' && c.expected.rows.length);
if (!positive) throw new Error('Parity fixtures need a positive scanner case.');
for (const variant of ['budget-rejection','hq-positive','hq-mismatch','full-stack-capital','stale-source']) {
  const input = structuredClone(positive.input);
  const id=input.items[0].id;
  if(variant==='budget-rejection') input.budgetGil=1;
  if(variant.startsWith('hq-')) for(const data of Object.values(input.listingsByItemWorld[id])) for(const l of data.listings) l.hq=true;
  if(variant==='hq-positive') for(const s of input.historyByItemHome[id].entries) s.hq=true;
  if(variant==='full-stack-capital') for(const [world,data] of Object.entries(input.listingsByItemWorld[id])) if(world!==input.homeWorld) for(const l of data.listings) l.quantity=999;
  if(variant==='stale-source') for(const [world,data] of Object.entries(input.listingsByItemWorld[id])) if(world!==input.homeWorld) { data.lastUpdatedSec=input.nowSec-172800; for(const l of data.listings) l.lastReviewTimeSec=input.nowSec-172800; }
  const rows=buildScannerOpportunitiesForSettings(input);
  if(variant==='hq-positive'&&!rows.length) throw new Error('Expected a positive HQ fixture.');
  if(variant!=='hq-positive'&&rows.length) throw new Error('Expected rejection: '+variant);
  cases.push({name:variant,kind:'scanner',input,expected:{rows,basket:buildScannerBasket(rows,{budgetGil:input.budgetGil})}});
}
const hashes={};
for (const name of ['scanner-strategy.js','scanner-planner.js','market-data.js']) hashes[name]=createHash('sha256').update(await readFile(resolve(website,'assets/modules',name))).digest('hex');
await writeFile(resolve(root,'tests/Fixtures/browser-parity.json'), JSON.stringify({source:'FFXIV Flipper browser model',hashes,cases}));
console.log(`${cases.length} browser parity cases; ${cases.filter(c=>c.kind==='scanner'&&c.expected.rows.length).length} scanner cases with recommendations.`);

