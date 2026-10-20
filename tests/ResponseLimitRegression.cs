using System.Net;
using System.Text;
namespace FFXIVFlipper;

public static class ResponseLimitRegression
{
    public static async Task<int> Run()
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        const string json = "{\"worldID\":53,\"itemID\":10373,\"lastUploadTime\":1,\"listings\":[]}";
        foreach (var declaredLength in new long?[] { MarketClient.MaxResponseBytes + 1L, null, 1 })
        {
            var stream = new SizedStream(MarketClient.MaxResponseBytes + 100, "");
            var calls = 0;
            using var client = new MarketClient(new Handler(() =>
            {
                if (++calls > 1) return new(HttpStatusCode.OK) { Content = new StringContent(json) };
                var content = new UnknownLengthContent(stream);
                content.Headers.ContentLength = declaredLength;
                return new(HttpStatusCode.OK) { Content = content };
            }));
            try { await client.Fetch(53, 10373, false, default); throw new Exception("Oversized response was accepted."); }
            catch (InvalidDataException ex) { Check(ex.Message.Contains("16 MiB"), "Oversize failure is not explained."); }
            Check(stream.BytesRead <= MarketClient.MaxResponseBytes + 1, "Body read exceeded its limit.");
            if (declaredLength > MarketClient.MaxResponseBytes) Check(stream.BytesRead == 0, "Oversized Content-Length was read.");
            Check((await client.Fetch(53, 10373, false, default)).Listings.Length == 0 && calls == 2,
                "Rejected response entered cache or retained a request slot.");
            Check(stream.Disposed, "Rejected response stream was not disposed.");
        }
        using (var client = new MarketClient(new Handler(() => new(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new SizedStream(MarketClient.MaxResponseBytes, json))
        })))
            Check((await client.Fetch(53, 10373, false, default)).Listings.Length == 0, "Exactly-at-limit valid JSON was rejected.");

        var waiting = new WaitingStream();
        var requestCount = 0;
        using (var client = new MarketClient(new Handler(() => new(HttpStatusCode.OK)
        {
            Content = ++requestCount == 1 ? new UnknownLengthContent(waiting) : new StringContent(json)
        })))
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = client.Fetch(53, 10373, false, cancellation.Token);
            await waiting.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Body ignored cancellation."); }
            catch (OperationCanceledException) { checks++; }
            Check(waiting.Disposed, "Canceled body was not disposed.");
            Check((await client.Fetch(53, 10373, false, default)).Listings.Length == 0, "Cancellation retained a request slot.");
        }
        return checks;
    }
    private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond());
    }
    private sealed class UnknownLengthContent(Stream stream) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => throw new Exception("Body was buffered before its limit was checked.");
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult(stream);
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }
    private class SizedStream(int size, string suffix) : Stream
    {
        private readonly byte[] ending = Encoding.UTF8.GetBytes(suffix);
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, size - BytesRead);
            buffer[..count].Fill((byte)' ');
            var suffixStart = size - ending.Length;
            var start = Math.Max(BytesRead, suffixStart);
            var end = Math.Min(BytesRead + count, size);
            if (end > start) ending.AsSpan(start - suffixStart, end - start).CopyTo(buffer[(start - BytesRead)..]);
            BytesRead += count; return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class WaitingStream() : SizedStream(0, "")
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return 0; }
    }
}
