using Etchv;
using System.Diagnostics;
using System.Net;
using System.Text;
using Xunit;

public sealed class RetriesTests
{
    private const string Base = "https://api.etchv.com";
    private static readonly string Id = new('a', 64), Asset = "ast_" + new string('a', 64), Delivery = "std_" + new string('d', 64),
        BatchId = "bat_" + new string('a', 32);
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, .. new byte[200]];
    private static readonly string Detected = $"{{\"watermarked\":true,\"confidence\":0.99,\"watermark_id\":\"{Id}\"}}";

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private static HttpResponseMessage Conflict(string code, string? retryAfter = null) =>
        Json(HttpStatusCode.Conflict, $"{{\"detail\":{{\"code\":\"{code}\",\"message\":\"{code}\"}}}}", retryAfter);

    private static string? Key(HttpRequestMessage request) =>
        request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null;

    [Fact]
    public async Task ADroppedDetectionIsResentWithTheSameGeneratedKey()
    {
        var keys = new List<string?>();
        var handler = new RecordingHandler((request, _) =>
        {
            keys.Add(Key(request));
            if (keys.Count % 2 == 1) throw new HttpRequestException("connection reset");
            return Json(HttpStatusCode.OK, Detected);
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        Assert.Equal(Id, (await client.DetectImageAsync(Png)).WatermarkId);
        Assert.Equal(Id, (await client.DetectDocumentAsync(Png)).WatermarkId);
        Assert.Equal(4, keys.Count);
        Assert.Matches("^[0-9a-f-]{36}$", keys[0]!);
        Assert.Equal(keys[0], keys[1]);
        Assert.Equal(keys[2], keys[3]);
        Assert.NotEqual(keys[0], keys[2]);

        keys.Clear();
        await client.DetectImageAsync(Png, new RequestOptions(IdempotencyKey: "scan-0001"));
        Assert.Equal(["scan-0001", "scan-0001"], keys);
    }

    [Fact]
    public async Task RequestInProgressWaitsForRetryAfterAndIsBounded()
    {
        var handler = new QueueHandler(() => Conflict("request_in_progress", "0.2"), () => Json(HttpStatusCode.OK, Detected));
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var watch = Stopwatch.StartNew();
        Assert.True((await client.DetectImageAsync(Png)).Watermarked);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(190) && watch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(2, handler.Requests.Count);

        // A long Retry-After is capped by the client deadline.
        var waiting = new QueueHandler(() => Conflict("request_in_progress", "600"));
        using var bounded = new EtchvClient("test-key", Base, TimeSpan.FromMilliseconds(300), new HttpClient(waiting));
        var error = await Assert.ThrowsAsync<EtchvException>(() => bounded.DetectDocumentAsync(Png, new RequestOptions(IdempotencyKey: "scan-0002")));
        Assert.Equal(0, error.StatusCode);
        Assert.Equal("scan-0002", error.IdempotencyKey);
        Assert.Single(waiting.Requests);
    }

    [Fact]
    public async Task ReplayExpiredAndRequestFailedAreNotRetried()
    {
        foreach (var code in new[] { "replay_expired", "request_failed" })
        {
            var keys = new List<string?>();
            var handler = new RecordingHandler((request, _) => { keys.Add(Key(request)); return Conflict(code); });
            using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
            var error = await Assert.ThrowsAsync<EtchvException>(() => client.DetectImageAsync(Png));
            Assert.Equal((409, code), (error.StatusCode, error.Code));
            Assert.Single(keys);
            Assert.Equal(keys[0], error.IdempotencyKey);
        }
    }

    [Fact]
    public async Task KeyedDetectionOutOfTimeCarriesTheKey()
    {
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("connection reset"));
        using var client = new EtchvClient("test-key", Base, TimeSpan.FromMilliseconds(1500), new HttpClient(handler));
        var error = await Assert.ThrowsAsync<EtchvException>(() => client.DetectImageAsync(Png, new RequestOptions(IdempotencyKey: "scan-0003")));
        Assert.Equal(0, error.StatusCode);
        Assert.Equal("scan-0003", error.IdempotencyKey);
        Assert.StartsWith("Client deadline exceeded", error.Detail);
        Assert.True(handler.Requests.Count >= 2, "a dropped detection is sent again before the deadline");
    }

    [Fact]
    public async Task KeepResultIsAQueryParameterOnInstantDetectionOnly()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK, Detected));
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        await client.DetectImageAsync(Png);
        await client.DetectImageAsync(Png, new RequestOptions() { KeepResult = false });
        await client.DetectDocumentAsync(Png, new RequestOptions(Accelerator: Accelerator.Gpu) { KeepResult = true });
        Assert.Equal(["", "?keep_result=false", "?accelerator=gpu&keep_result=true"], handler.Requests.Select(r => r.Uri.Query));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SubmitDetectionAsync("images", Png, new RequestOptions() { KeepResult = false }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.EmbedImageAsync(Png, new Dictionary<string, object?> { ["a"] = "b" }, new RequestOptions() { KeepResult = false }));
    }

    [Fact]
    public async Task AConsumedUploadIsReplacedByANewUploadWithTheSameKey()
    {
        var uploads = new Queue<string>(["upl_" + new string('1', 32), "upl_" + new string('2', 32)]);
        var posts = new List<(string? Key, string Body)>();
        var handler = new RecordingHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath == "/uploads")
            {
                var upload = uploads.Dequeue();
                return Json(HttpStatusCode.Created, $"{{\"upload_id\":\"{upload}\",\"kind\":\"detect\",\"filename\":\"image.png\",\"size\":{Png.Length},\"status\":\"pending\"," +
                    $"\"expires_at\":\"x\",\"upload\":{{\"method\":\"PUT\",\"url\":\"https://uploads.etchv.com/detect/{upload}.png\",\"expires_at\":\"x\"}}}}");
            }
            if (request.RequestUri.Host == "uploads.etchv.com") return new HttpResponseMessage(HttpStatusCode.OK);
            Assert.Equal("?keep_result=false", request.RequestUri.Query);
            posts.Add((Key(request), body));
            return posts.Count switch
            {
                1 => throw new HttpRequestException("connection reset"),
                2 => Conflict("upload_consumed"),
                _ => Json(HttpStatusCode.OK, Detected),
            };
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler)) { LargeFileThreshold = 100 };
        await client.DetectImageAsync(Png, new RequestOptions() { KeepResult = false });
        Assert.Equal(3, posts.Count);
        Assert.All(posts, post => Assert.Equal(posts[0].Key, post.Key));
        Assert.Contains("upl_" + new string('1', 32), posts[1].Body);
        Assert.Contains("upl_" + new string('2', 32), posts[2].Body);
        Assert.Equal(2, handler.Requests.Count(r => r.Uri.Host == "uploads.etchv.com"));
    }

    private static HttpResponseMessage Session(string upload) =>
        Json(HttpStatusCode.Created, $"{{\"upload_id\":\"{upload}\",\"kind\":\"detect\",\"filename\":\"image.png\",\"size\":{Png.Length},\"status\":\"pending\"," +
            $"\"expires_at\":\"x\",\"upload\":{{\"method\":\"PUT\",\"url\":\"https://uploads.etchv.com/detect/{upload}.png\",\"expires_at\":\"x\"}}}}");

    [Fact]
    public async Task AnUploadStillHeldByTheFirstCopyIsWaitedFor()
    {
        var upload = "upl_" + new string('1', 32);
        var posts = new List<(string? Key, string Body)>();
        var handler = new RecordingHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath == "/uploads") return Session(upload);
            if (request.RequestUri.Host == "uploads.etchv.com") return new HttpResponseMessage(HttpStatusCode.OK);
            posts.Add((Key(request), body));
            return posts.Count switch
            {
                1 => throw new HttpRequestException("Server disconnected without sending a response."),
                2 => Conflict("upload_in_use"),
                _ => Json(HttpStatusCode.OK, Detected),
            };
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler)) { LargeFileThreshold = 100 };
        Assert.True((await client.DetectImageAsync(Png)).Watermarked);
        Assert.Equal(3, posts.Count);
        Assert.All(posts, post => Assert.Equal(posts[0].Key, post.Key));
        Assert.All(posts, post => Assert.Contains(upload, post.Body));
        Assert.Single(handler.Requests, r => r.Uri.AbsolutePath == "/uploads");
    }

    [Fact]
    public async Task UploadInUseIsNotWaitedForOnAFileInTheRequestBody()
    {
        var keys = new List<string?>();
        var handler = new RecordingHandler((request, _) => { keys.Add(Key(request)); return Conflict("upload_in_use"); });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var error = await Assert.ThrowsAsync<EtchvException>(() => client.DetectImageAsync(Png));
        Assert.Equal((409, "upload_in_use"), (error.StatusCode, error.Code));
        Assert.Single(keys);
        Assert.Matches("^[0-9a-f-]{36}$", error.IdempotencyKey!);
        Assert.Equal(keys[0], error.IdempotencyKey);
    }

    [Fact]
    public async Task TheResendAfterANewUploadKeepsTheFirstDeadline()
    {
        var uploads = new Queue<string>(["upl_" + new string('1', 32), "upl_" + new string('2', 32)]);
        int posts = 0, sessions = 0, puts = 0;
        var handler = new RecordingHandler((request, _) =>
        {
            // The re-upload is slow: its session and PUT together outlast the 2-second deadline, each
            // well within it. A slow runner only adds time, so it can only reach the deadline sooner.
            if (request.RequestUri!.AbsolutePath == "/uploads")
            {
                if (++sessions == 2) Thread.Sleep(1200);
                return Session(uploads.Dequeue());
            }
            if (request.RequestUri.Host == "uploads.etchv.com")
            {
                if (++puts == 2) Thread.Sleep(1200);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            // The second send is reached only with a fresh window per send, the regression this catches.
            return ++posts == 1 ? Conflict("upload_consumed") : Json(HttpStatusCode.OK, Detected);
        });
        using var client = new EtchvClient("test-key", Base, TimeSpan.FromSeconds(2), new HttpClient(handler)) { LargeFileThreshold = 100 };
        var error = await Assert.ThrowsAsync<EtchvException>(() => client.DetectImageAsync(Png, new RequestOptions(IdempotencyKey: "scan-0004") { KeepResult = false }));
        Assert.Equal((0, "scan-0004"), (error.StatusCode, error.IdempotencyKey));
        Assert.Equal((1, 2, 2), (posts, sessions, puts));
    }

    /// <summary>Drops the first request; later requests hang until the client deadline cancels them.</summary>
    private sealed class DropThenHangHandler : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Calls) == 1) throw new HttpRequestException("connection reset");
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    [Fact]
    public async Task ADownloadOutOfTimeAfterADropThrowsTheNetworkError()
    {
        var handler = new DropThenHangHandler();
        using var client = new EtchvClient("test-key", Base, TimeSpan.FromMilliseconds(1500), new HttpClient(handler));
        var error = await Assert.ThrowsAsync<EtchvException>(() => client.DownloadStorageDeliveryAsync(Delivery));
        Assert.Equal(("Network error", 0, 2), (error.Detail, error.StatusCode, handler.Calls));
        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    [Fact]
    public async Task DownloadsRetryADroppedConnectionButNotErrors()
    {
        var calls = 0;
        var handler = new RecordingHandler((_, _) =>
            ++calls % 2 == 1 ? throw new HttpRequestException("peer closed connection") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        Assert.Equal([1, 2, 3], await client.DownloadAssetAsync(Asset));
        Assert.Equal([1, 2, 3], await client.DownloadStorageDeliveryAsync(Delivery));
        Assert.Equal([$"/assets/{Asset}/content", $"/assets/{Asset}/content", $"/storage/deliveries/{Delivery}/content", $"/storage/deliveries/{Delivery}/content"],
            handler.Requests.Select(r => r.Uri.AbsolutePath));

        var busy = new QueueHandler(() => Json(HttpStatusCode.ServiceUnavailable, "{\"detail\":\"busy\"}"));
        using var once = new EtchvClient("test-key", Base, httpClient: new HttpClient(busy));
        Assert.Equal(503, (await Assert.ThrowsAsync<EtchvException>(() => once.DownloadAssetAsync(Asset))).StatusCode);
        Assert.Single(busy.Requests);

        // Out of time, the network error itself is thrown.
        var down = new RecordingHandler((_, _) => throw new HttpRequestException("connection reset"));
        using var late = new EtchvClient("test-key", Base, TimeSpan.FromMilliseconds(500), new HttpClient(down));
        var error = await Assert.ThrowsAsync<EtchvException>(() => late.DownloadAssetAsync(Asset));
        Assert.Equal(("Network error", 0), (error.Detail, error.StatusCode));
        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    /// <summary>A body that breaks off after <paramref name="head"/>, as a reset connection does.</summary>
    private sealed class DroppedStream(byte[] head) : MemoryStream(head)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int n = await base.ReadAsync(buffer, cancellationToken);
            return n > 0 ? n : throw new IOException("connection reset");
        }
    }

    /// <summary>A write-only stream that cannot seek, like a pipe.</summary>
    private sealed class PipeStream : Stream
    {
        public readonly MemoryStream Received = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Received.Write(buffer, offset, count);
    }

    private static HttpResponseMessage Archive(Stream body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        response.Content.Headers.ContentType = new("application/zip");
        return response;
    }

    [Fact]
    public async Task ADroppedArchiveStartsAgainFromTheBeginning()
    {
        byte[] zip = [80, 75, .. Enumerable.Range(0, 300_000).Select(i => (byte)i)];
        var calls = 0;
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.Null(request.Headers.Range);
            return Archive(++calls % 2 == 1 ? new DroppedStream(zip[..100_000]) : new MemoryStream(zip));
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        Assert.Equal(zip, await client.DownloadBatchArchiveAsync(BatchId));

        using var destination = new MemoryStream();
        destination.Write("header"u8);
        Assert.Equal(zip.Length, await client.DownloadBatchArchiveAsync(BatchId, destination));
        Assert.Equal([.. "header"u8, .. zip], destination.ToArray());

        var path = Path.Combine(Path.GetTempPath(), $"etchv-archive-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var file = File.Create(path)) Assert.Equal(zip.Length, await client.DownloadBatchArchiveAsync(BatchId, file));
            Assert.Equal(zip, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task ADroppedArchiveIntoAStreamThatCannotSeekThrows()
    {
        byte[] head = [80, 75, 1, 2, 3];
        var handler = new RecordingHandler((_, _) => Archive(new DroppedStream(head)));
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        using var pipe = new PipeStream();
        var error = await Assert.ThrowsAsync<EtchvException>(() => client.DownloadBatchArchiveAsync(BatchId, pipe));
        Assert.Equal(("Network error during the archive download", 0), (error.Detail, error.StatusCode));
        Assert.Equal(head, pipe.Received.ToArray());
        Assert.Single(handler.Requests);
    }
}
