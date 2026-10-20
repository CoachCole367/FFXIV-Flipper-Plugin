using System.Diagnostics;
using System.Text.Json;
namespace FFXIVFlipper;
public static class JournalRegression
{
    public static async Task<int> Run()
    {
        var checks=0;
        void Check(bool condition,string message) { if(!condition) throw new Exception(message); checks++; }
        var root=Path.Combine(Path.GetTempPath(),"FFXIVFlipper-journal-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var clock=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        PurchaseLot Lot(ulong listing=1,int qty=10,long price=100,long tax=50,ulong owner=1) => new(Guid.NewGuid(),clock,owner,10373,false,53,qty,price,tax,ListingId:listing);
        try
        {
            var directory=Path.Combine(root,"main");
            using(var journal=new TradeJournal(directory,()=>clock))
            {
                Check(!File.Exists(journal.JournalPath),"Idle journal created a file before any trade");
                var tracker=new PurchaseTracker(journal);
                var request=new PurchaseRequest(1,53,10373,false,10,100,50,1,clock);
                tracker.Request(request);
                Check(journal.Snapshot(1).Length==0,"Purchase request was treated as a completed purchase");
                Check(tracker.Confirm(10373,10,1,53,clock),"Matching server confirmation was not recorded");
                Check(!tracker.Confirm(10373,10,1,53,clock),"Duplicate confirmation added a purchase");
                tracker.Request(request); Check(!tracker.Confirm(10373,10,1,53,clock),"Duplicate listing was added twice");
                var lot=journal.Snapshot(1).Single();
                Check(lot.TotalCost==1050,"Purchase tax or full stack cost was omitted");
                Check(!journal.RecordSale(lot.Id,2,1,200),"Other character modified purchase");
                Check(!journal.RecordSale(lot.Id,1,11,2000),"Sale exceeded owned units");
                Check(!journal.RecordSale(lot.Id,1,1,-1),"Negative sale receipts accepted");
                Check(journal.RecordSale(lot.Id,1,4,800),"Actual partial sale was not recorded");
                lot=journal.Snapshot(1).Single();
                Check(lot.Remaining==6&&lot.MatchedCost==420&&lot.RealizedProfit==380&&lot.CashRecovered==-250,"Partial-sale margin and cash recovery were conflated");
                Check(journal.RecordSale(lot.Id,1,6,1200)&&journal.Snapshot(1).Single().RealizedProfit==950,"Full-sale profit not cumulative");
                tracker.Request(request with {ListingId=2}); Check(!tracker.Confirm(999,10,1,53,clock),"Mismatched item confirmation recorded");
                tracker.Request(request with {ListingId=2}); Check(!tracker.Confirm(10373,9,1,53,clock),"Mismatched stack confirmation recorded");
                tracker.Request(request with {ListingId=2}); Check(!tracker.Confirm(10373,10,1,53,clock+31),"Expired request recorded");
                tracker.Request(request with {ListingId=2}); Check(!tracker.Confirm(10373,10,2,53,clock),"Character change misattributed purchase");
                tracker.Request(request with {ListingId=2}); Check(!tracker.Confirm(10373,10,1,64,clock),"World change misattributed purchase");
                tracker.Request(request with {ListingId=2,Hq=true}); Check(tracker.Confirm(1010373,10,1,53,clock)&&journal.Snapshot(1).Any(x=>x.Hq),"HQ confirmation lost quality");
                tracker.Request(request with {ListingId=3}); Check(!tracker.Confirm(1010373,10,1,53,clock),"HQ confirmation matched NQ request");
                tracker.Request(request with {ListingId=3}); tracker.Request(request with {ListingId=4});
                Check(!tracker.Confirm(10373,10,1,53,clock),"Overlapping requests were guessed");
                tracker.Clear(); tracker.Request(request with {ListingId=3}); tracker.Clear(); Check(!tracker.Confirm(10373,10,1,53,clock),"Disabled/cleared tracker retained a request");
                Check(journal.Snapshot(2).Length==0,"History leaked between character views");
                await journal.FlushAsync();
                Check(File.Exists(journal.JournalPath)&&new FileInfo(journal.JournalPath).Length<=TradeJournal.MaxFileBytes&&!File.Exists(journal.JournalPath+".tmp"),"Atomic bounded save failed");
                await journal.ExportAsync(1); var export=File.ReadAllText(journal.ExportPath);
                Check(export.Contains("1050")&&export.Contains("950.00")&&!export.Contains("characterId"),"CSV omitted actual accounting or exported character identity");
                await journal.ExportAsync(1);
                Check(Directory.GetFiles(directory).Length==2,"Exports/backups accumulated extra files");
                var other = Lot(100, owner: 2) with { ItemId = 20000 };
                Check(journal.Add(other), "Second-character purchase was not retained");
                var firstCharacterExport = journal.ExportAsync(1);
                Check(!journal.RecordSale(other.Id, 1, 1, 100), "Sale review from another character changed a purchase");
                Check(journal.RecordSale(other.Id, 2, 1, 300), "Current-character sale was rejected");
                await firstCharacterExport;
                var firstCsv = File.ReadAllText(journal.ExportPath);
                Check(firstCsv.Contains(",10373,") && !firstCsv.Contains(",20000,"), "In-flight export mixed character records");
                await journal.ExportAsync(2);
                var secondCsv = File.ReadAllText(journal.ExportPath);
                Check(secondCsv.Contains(",20000,") && !secondCsv.Contains(",10373,"), "Second-character export retained first-character rows");
                Check(!secondCsv.Contains("characterId", StringComparison.OrdinalIgnoreCase), "Export included character identifiers");
            }
            using(var loaded=new TradeJournal(directory,()=>clock)) Check(loaded.Snapshot(1).Length==2&&loaded.Snapshot(1).Any(x=>x.SoldQuantity==10),"Journal did not survive reload");
            using(var journal=new TradeJournal(Path.Combine(root,"bounded"),()=>clock))
            {
                var watch=Stopwatch.StartNew();
                for(var i=1;i<=TradeJournal.MaxEntries+20;i++) { clock++; if(!journal.Add(Lot((ulong)i))) throw new Exception("Valid bounded purchase rejected"); }
                await journal.FlushAsync();
                Check(journal.Snapshot(1).Length==TradeJournal.MaxEntries,"Journal exceeded the record cap");
                Check(new FileInfo(journal.JournalPath).Length<=TradeJournal.MaxFileBytes,"Journal exceeded hard byte limit");
                Check(!journal.Add(Lot(5000) with {PurchasedAt=clock-91*86400L}),"Expired purchase was retained");
                Check(!journal.Add(Lot(5000) with {Quantity=1000}),"Invalid oversized stack retained");
                Check(!journal.Add(Lot(5000) with {UnitPrice=long.MaxValue}),"Overflowing monetary value accepted");
                Console.WriteLine($"Journal capacity check: {journal.Snapshot(1).Length} purchases, {journal.FileBytes:N0} bytes; ingest + save {watch.ElapsedMilliseconds} ms.");
                clock+=91*86400L;
            }
            using(var expired=new TradeJournal(Path.Combine(root,"bounded"),()=>clock)) { await expired.FlushAsync(); Check(expired.Snapshot(1).Length==0,"Expired entries survived reload"); }
            using(var maximal=new TradeJournal(Path.Combine(root,"maximal"),()=>clock))
            {
                for(var i=0;i<TradeJournal.MaxEntries;i++)
                    if(!maximal.Add(new(Guid.NewGuid(),clock,ulong.MaxValue,999999,true,uint.MaxValue,999,999999999,999999999,999,1000000000000,clock,ulong.MaxValue-(ulong)i))) throw new Exception("Maximal valid lot rejected");
                await maximal.FlushAsync();
                Check(maximal.FileBytes<=TradeJournal.MaxFileBytes&&maximal.Snapshot(ulong.MaxValue).Length==TradeJournal.MaxEntries,"Worst-case bounded records exceeded 512 KB");
                Check(maximal.Snapshot(ulong.MaxValue)[0].TotalCost==999999999000L,"Large full-stack cost overflowed");
                await maximal.ExportAsync(ulong.MaxValue);
                Check(new FileInfo(maximal.ExportPath).Length<=TradeJournal.MaxFileBytes,"Worst-case CSV exceeded hard limit");
            }
            var corruptDir=Path.Combine(root,"corrupt"); Directory.CreateDirectory(corruptDir);
            var corruptPath=Path.Combine(corruptDir,"purchase-journal.json"); File.WriteAllText(corruptPath,"not-json");
            using(var corrupt=new TradeJournal(corruptDir,()=>clock)) { Check(!corrupt.Add(Lot())&&File.ReadAllText(corruptPath)=="not-json","Corrupt existing history was overwritten"); }
            var hugeDir=Path.Combine(root,"oversized"); Directory.CreateDirectory(hugeDir);
            var hugePath=Path.Combine(hugeDir,"purchase-journal.json"); File.WriteAllBytes(hugePath,new byte[TradeJournal.MaxFileBytes+1]);
            using(var huge=new TradeJournal(hugeDir,()=>clock)) Check(!huge.Add(Lot())&&new FileInfo(hugePath).Length==TradeJournal.MaxFileBytes+1,"Oversized existing file was loaded or overwritten");
            var blockedDir=Path.Combine(root,"blocked"); File.WriteAllText(blockedDir,"not a directory");
            using(var blocked=new TradeJournal(blockedDir,()=>clock)) { Check(blocked.Add(Lot()),"In-memory recording failed unnecessarily"); await blocked.FlushAsync(); Check(blocked.Status.Contains("could not be saved"),"Write failure was hidden"); }
            return checks;
        }
        finally
        {
            // Only remove this test's verified, uniquely named directory inside the OS temp directory.
            var resolved=Path.GetFullPath(root); var temp=Path.GetFullPath(Path.GetTempPath());
            if(resolved.StartsWith(temp,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(resolved).StartsWith("FFXIVFlipper-journal-tests-",StringComparison.Ordinal)) Directory.Delete(resolved,true);
        }
    }
}

