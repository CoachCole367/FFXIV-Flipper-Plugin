using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFXIVFlipper;

public sealed record PurchaseLot(Guid Id, long PurchasedAt, ulong CharacterId, uint ItemId, bool Hq, uint WorldId,
    int Quantity, long UnitPrice, long PurchaseTax, int SoldQuantity = 0, long NetRevenue = 0, long LastSaleAt = 0, ulong ListingId = 0)
{
    [JsonIgnore] public long TotalCost => checked(UnitPrice * Quantity + PurchaseTax);
    [JsonIgnore] public int Remaining => Quantity - SoldQuantity;
    [JsonIgnore] public decimal MatchedCost => TotalCost * ((decimal)SoldQuantity / Quantity);
    [JsonIgnore] public decimal RealizedProfit => NetRevenue - MatchedCost;
    [JsonIgnore] public long CashRecovered => NetRevenue - TotalCost;
}

// One bounded, minified file; no market snapshots, ever-growing event stream or database.
public sealed class TradeJournal : IDisposable
{
    public const int MaxEntries = 1000;
    public const int RetentionDays = 90;
    public const int MaxFileBytes = 512 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
    private readonly object gate = new();
    private readonly SemaphoreSlim fileGate = new(1);
    private readonly string directory;
    private readonly Func<long> now;
    private readonly List<PurchaseLot> lots = [];
    private Task writer = Task.CompletedTask;
    private Task exportWriter = Task.CompletedTask;
    private bool writing;
    private bool closed;
    private bool readOnly;
    private long revision;
    private long persistedRevision;
    private string error = "";
    private long fileBytes;
    private sealed record Document(int Version, PurchaseLot[] Lots);

    public TradeJournal(string directory, Func<long>? clock = null)
    {
        this.directory = Path.GetFullPath(directory);
        now = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Load();
    }
    public string JournalPath => Path.Combine(directory, "purchase-journal.json");
    public string ExportPath => Path.Combine(directory, "purchase-journal.csv");
    public long Revision { get { lock (gate) return revision; } }
    public long FileBytes { get { lock (gate) return fileBytes; } }
    public string Status { get { lock (gate) return error.Length > 0 ? error : revision != persistedRevision ? "Saving locally..." : "Saved locally"; } }
    public PurchaseLot[] Snapshot(ulong characterId) { lock (gate) return lots.Where(x => x.CharacterId == characterId).OrderByDescending(x => x.PurchasedAt).ThenBy(x => x.Id).ToArray(); }
    public bool Add(PurchaseLot lot)
    {
        lock (gate)
        {
            if (closed || readOnly || !Valid(lot) || lot.PurchasedAt < now() - RetentionDays * 86400L || lot.PurchasedAt > now() + 300) return false;
            if (lots.Any(x => x.Id == lot.Id || (lot.ListingId != 0 && x.CharacterId == lot.CharacterId && x.ListingId == lot.ListingId))) return false;
            lots.Add(lot); Prune(); revision++; QueueSave(); return true;
        }
    }
    public bool RecordSale(Guid purchaseId, ulong characterId, int quantity, long netReceived)
    {
        lock (gate)
        {
            if (closed || readOnly || quantity <= 0 || netReceived < 0 || netReceived > 1_000_000_000_000L) return false;
            var index = lots.FindIndex(x => x.Id == purchaseId && x.CharacterId == characterId);
            if (index < 0) return false;
            var lot = lots[index];
            if (quantity > lot.Remaining || lot.NetRevenue > 1_000_000_000_000L - netReceived) return false;
            lots[index] = lot with { SoldQuantity = lot.SoldQuantity + quantity, NetRevenue = lot.NetRevenue + netReceived, LastSaleAt = now() };
            Prune(); revision++; QueueSave(); return true;
        }
    }
    public void RetrySave() { lock (gate) { if (!readOnly && !closed && revision != persistedRevision) QueueSave(); } }
    public Task FlushAsync() { lock (gate) return writer; }
    private void Load()
    {
        try
        {
            if (!File.Exists(JournalPath)) return;
            var length = new FileInfo(JournalPath).Length;
            if (length > MaxFileBytes) throw new InvalidDataException("Journal exceeds the 512 KB limit.");
            var bytes = File.ReadAllBytes(JournalPath);
            if (bytes.Length > MaxFileBytes) throw new InvalidDataException("Journal exceeds the 512 KB limit.");
            var document = JsonSerializer.Deserialize<Document>(bytes, JsonOptions) ?? throw new InvalidDataException("Empty journal.");
            if (document.Version != 1 || document.Lots == null || document.Lots.Any(x => x == null || !Valid(x))) throw new InvalidDataException("Unsupported or invalid journal.");
            if (document.Lots.Select(x => x.Id).Distinct().Count() != document.Lots.Length) throw new InvalidDataException("Duplicate journal identifiers.");
            lots.AddRange(document.Lots); fileBytes = bytes.Length;
            var count = lots.Count; Prune();
            if (count != lots.Count) { revision++; QueueSave(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or OverflowException)
        {
            readOnly = true; error = "History could not be loaded; existing file preserved. " + ex.Message;
        }
    }
    private static bool Valid(PurchaseLot lot) => lot.Id != Guid.Empty && lot.CharacterId != 0 && lot.ItemId is > 0 and < 1_000_000 && lot.WorldId != 0
        && lot.Quantity is >= 1 and <= 999 && lot.UnitPrice is > 0 and <= 999_999_999 && lot.PurchaseTax is >= 0 and <= 999_999_999
        && lot.SoldQuantity >= 0 && lot.SoldQuantity <= lot.Quantity && lot.NetRevenue is >= 0 and <= 1_000_000_000_000L
        && (lot.SoldQuantity != 0 || lot.NetRevenue == 0) && lot.PurchasedAt > 0 && lot.LastSaleAt >= 0;
    private void Prune()
    {
        var cutoff = now() - RetentionDays * 86400L;
        lots.RemoveAll(x => x.PurchasedAt < cutoff || x.PurchasedAt > now() + 300);
        lots.Sort((a,b) => { var time=a.PurchasedAt.CompareTo(b.PurchasedAt); return time!=0 ? time : a.Id.CompareTo(b.Id); });
        if (lots.Count > MaxEntries) lots.RemoveRange(0, lots.Count - MaxEntries);
    }
    // Called under gate. A single worker coalesces changes and exits when caught up; no idle polling.
    private void QueueSave()
    {
        if (writing) return;
        writing = true; error = ""; writer = Task.Run(SaveLoop);
    }
    private async Task SaveLoop()
    {
        try
        {
            while (true)
            {
                PurchaseLot[] snapshot; long target;
                lock (gate) { snapshot = lots.ToArray(); target = revision; }
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, snapshot), JsonOptions);
                if (bytes.Length > MaxFileBytes) throw new InvalidDataException("Journal reached its hard size limit; new data has not been saved.");
                await fileGate.WaitAsync().ConfigureAwait(false);
                try { AtomicWrite(JournalPath, bytes); }
                finally { fileGate.Release(); }
                lock (gate)
                {
                    persistedRevision = target; fileBytes = bytes.Length;
                    if (revision == target) { writing = false; error = ""; return; }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            lock (gate) { error = "History is in memory but could not be saved: " + ex.Message; writing = false; }
        }
    }
    private void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, true);
    }
    public Task ExportAsync(ulong characterId)
    {
        lock (gate)
        {
            if (closed) return Task.FromException(new ObjectDisposedException(nameof(TradeJournal)));
            if (!exportWriter.IsCompleted) return exportWriter;
            exportWriter = Task.Run(() => ExportCoreAsync(characterId)); return exportWriter;
        }
    }
    private async Task ExportCoreAsync(ulong characterId)
    {
        var snapshot = Snapshot(characterId);
        var csv = new StringBuilder("purchase_utc,item_id,hq,world_id,bought,unit_gil,purchase_tax,total_cost,sold,net_sale_receipts,matched_profit,cash_recovered\n");
        foreach (var lot in snapshot)
            csv.Append(CultureInfo.InvariantCulture, $"{DateTimeOffset.FromUnixTimeSeconds(lot.PurchasedAt):O},{lot.ItemId},{lot.Hq},{lot.WorldId},{lot.Quantity},{lot.UnitPrice},{lot.PurchaseTax},{lot.TotalCost},{lot.SoldQuantity},{lot.NetRevenue},{lot.RealizedProfit:0.00},{lot.CashRecovered}\n");
        var bytes = Encoding.UTF8.GetBytes(csv.ToString());
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("Export exceeds its size limit.");
        await fileGate.WaitAsync().ConfigureAwait(false);
        try { AtomicWrite(ExportPath, bytes); }
        finally { fileGate.Release(); }
    }
    public void Dispose()
    {
        Task pending;
        lock (gate) { closed = true; pending = Task.WhenAll(writer, exportWriter); }
        try { pending.GetAwaiter().GetResult(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { lock (gate) error = ex.Message; }
        // fileGate stays alive for any explicit export already in progress.
    }
}

public sealed record PurchaseRequest(ulong CharacterId, uint WorldId, uint ItemId, bool Hq, int Quantity, long UnitPrice, long Tax, ulong ListingId, long RequestedAt);
public sealed class PurchaseTracker(TradeJournal journal)
{
    // The game normally has one purchase in flight. Replacing a request prevents old confirmations being misattributed.
    private PurchaseRequest? pending;
    private long ambiguousUntil;
    public void Clear() { pending = null; ambiguousUntil = 0; }
    public void Request(PurchaseRequest request)
    {
        if (request.RequestedAt < ambiguousUntil) { pending = null; return; }
        if (pending != null && request.RequestedAt >= pending.RequestedAt && request.RequestedAt - pending.RequestedAt <= 30)
        {
            // Server acknowledgements do not identify a listing. Never guess between overlapping requests.
            pending = null; ambiguousUntil = request.RequestedAt + 30; return;
        }
        pending = request.CharacterId != 0 && request.WorldId != 0 && request.ListingId != 0 ? request : null;
    }
    public bool Confirm(uint catalogId, uint quantity, ulong characterId, uint worldId, long now)
    {
        var request = pending; pending = null;
        if (request == null || characterId != request.CharacterId || worldId != request.WorldId || now < request.RequestedAt || now - request.RequestedAt > 30) return false;
        var baseId = catalogId >= 1_000_000 ? catalogId - 1_000_000 : catalogId;
        var isHq = catalogId >= 1_000_000;
        if (baseId != request.ItemId || quantity != request.Quantity || (isHq && !request.Hq)) return false;
        return journal.Add(new(Guid.NewGuid(), now, characterId, request.ItemId, request.Hq, worldId,
            request.Quantity, request.UnitPrice, request.Tax, ListingId: request.ListingId));
    }
}


