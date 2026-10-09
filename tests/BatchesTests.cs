using Etchv;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

public sealed class BatchesTests
{
    private const string Base = "https://api.etchv.com";
    private static readonly string BatchId = "bat_" + new string('a', 32), Id = new('a', 64), Job = "req_" + new string('c', 64);
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, .. new byte[50]];
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7 test");

    private static Dictionary<string, object?> Data(string recipient) => new() { ["recipient"] = recipient };

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private static JsonObject Item(int index, string filename, string status, JsonObject? extra = null)
    {
        var item = new JsonObject
        {
            ["index"] = index, ["filename"] = filename, ["size"] = 10, ["upload_id"] = $"upl_{index}{new string('0', 31)}", ["request_id"] = null,
            ["status"] = status, ["error_code"] = null, ["error_detail"] = null, ["credits"] = null,
        };
        foreach (var (k, v) in extra ?? []) item[k] = v?.DeepClone();
        return item;
    }

    private static JsonObject View(string status, params JsonObject[] items) => new()
    {
        ["batch_id"] = BatchId, ["status"] = status, ["item_count"] = items.Length, ["archive"] = false, ["accelerator"] = "cpu",
        ["webhook_id"] = null, ["storage_destination_id"] = null,
        ["counts"] = new JsonObject { ["pending"] = 0, ["accepted"] = 1, ["rejected"] = 1, ["succeeded"] = 1, ["failed"] = 0, ["in_progress"] = 0 },
        ["credits"] = new JsonObject { ["reserved"] = 1, ["charged"] = 1, ["refunded"] = 0 }, ["cancel_requested"] = false,
        ["created_at"] = "2026-10-09T00:00:00Z", ["started_at"] = null, ["completed_at"] = null, ["upload_expires_at"] = "2026-10-10T00:00:00Z",
        ["status_url"] = $"/watermarks/batches/{BatchId}", ["items"] = new JsonArray(items.Select(i => (JsonNode)i).ToArray()),
    };

    private static JsonObject Upload(int index) => new()
    {
        ["upload"] = new JsonObject { ["method"] = "PUT", ["url"] = $"https://uploads.etchv.com/image/upl_{index}.png?Signature=s", ["expires_at"] = "x" },
    };

    [Fact]
    public async Task SubmitCreatesUploadsWithoutTheApiKeyAndStarts()
    {
        var puts = new Dictionary<string, byte[]>();
        string? key = null;
        var handler = new RecordingHandler((request, body) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "uploads.etchv.com")
            {
                Assert.False(request.Headers.Contains("X-API-Key"));
                Assert.Null(request.Headers.Authorization);
                lock (puts) puts[path] = Encoding.Latin1.GetBytes(body);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            Assert.Equal("test-key", request.Headers.GetValues("X-API-Key").Single());
            if (path == "/watermarks/batches")
            {
                key = request.Headers.GetValues("Idempotency-Key").Single();
                using var json = JsonDocument.Parse(body);
                var items = json.RootElement.GetProperty("items");
                Assert.Equal("a.png", items[0].GetProperty("filename").GetString());
                Assert.Equal(Png.Length, items[0].GetProperty("size").GetInt32());
                Assert.Equal("a", items[0].GetProperty("data").GetProperty("recipient").GetString());
                Assert.Equal(Pdf.Length, items[1].GetProperty("size").GetInt32());
                Assert.True(json.RootElement.GetProperty("archive").GetBoolean());
                Assert.Equal("gpu", json.RootElement.GetProperty("accelerator").GetString());
                return Json(HttpStatusCode.Created, View("draft", Item(0, "a.png", "pending", Upload(0)), Item(1, "b.pdf", "pending", Upload(1))));
            }
            Assert.Equal($"/watermarks/batches/{BatchId}/start", path);
            return Json(HttpStatusCode.Accepted, View("starting", Item(0, "a.png", "pending"), Item(1, "b.pdf", "pending")));
        });
        var file = Path.Combine(Path.GetTempPath(), $"etchv-batch-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(file, Pdf);
        try
        {
            using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
            var batch = await client.SubmitBatchAsync([new BatchItem("a.png", Png, Data("a")), BatchItem.FromPath(file, Data("b"), "b.pdf")],
                new BatchOptions(Archive: true, Accelerator: Accelerator.Gpu));
            Assert.Equal("starting", batch.Status);
            Assert.Equal(BatchId, batch.BatchId);
            Assert.Equal(Accelerator.Cpu, batch.Accelerator);
            Assert.False(batch.IsTerminal);
            Assert.Matches("^[A-Za-z0-9_-]{8,128}$", key);
            Assert.Equal(Png, puts["/image/upl_0.png"]);
            Assert.Equal(Pdf, puts["/image/upl_1.png"]);
            Assert.Equal([true, false, false, true], handler.Requests.Select(r => r.ApiKey));
            Assert.Equal("POST /watermarks/batches", $"{handler.Requests[0].Method} {handler.Requests[0].Uri.AbsolutePath}");
            Assert.Equal($"POST /watermarks/batches/{BatchId}/start", $"{handler.Requests[3].Method} {handler.Requests[3].Uri.AbsolutePath}");
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task CreateRetriesWithTheSameIdempotencyKeyButNot503()
    {
        var keys = new List<string>();
        int creates = 0;
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/watermarks/batches")
            {
                keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
                return ++creates == 1
                    ? Json(HttpStatusCode.BadGateway, new JsonObject { ["detail"] = "busy" }, "0.01")
                    : Json(HttpStatusCode.Created, View("draft", Item(0, "a.png", "pending", new JsonObject { ["upload_received"] = true })));
            }
            return Json(HttpStatusCode.Accepted, View("starting", Item(0, "a.png", "pending")));
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        await client.SubmitBatchAsync([new BatchItem("a.png", Png, Data("a"))]);
        Assert.Equal(2, keys.Count);
        Assert.Equal(keys[0], keys[1]);

        var unavailable = new RecordingHandler((_, _) => Json(HttpStatusCode.ServiceUnavailable,
            new JsonObject { ["detail"] = "Batch uploads are unavailable; send one zip to /watermarks/batches/zip instead" }));
        using var other = new EtchvClient("test-key", Base, httpClient: new HttpClient(unavailable));
        var error = await Assert.ThrowsAsync<EtchvException>(() => other.SubmitBatchAsync([new BatchItem("a.png", Png, Data("a"))], new BatchOptions(IdempotencyKey: "batch_key_1")));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal("batch_key_1", error.IdempotencyKey);
        Assert.Contains("zip", error.DetailMessage);
        Assert.Single(unavailable.Requests);
    }

    [Fact]
    public async Task ARefusedUploadNamesTheItemAndDoesNotStart()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "uploads.etchv.com") return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("expired") };
            return Json(HttpStatusCode.Created, View("draft", Item(0, "a.png", "pending", Upload(0))));
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var error = await Assert.ThrowsAsync<EtchvException>(() => client.SubmitBatchAsync([new BatchItem("a.png", Png, Data("a"))], new BatchOptions(IdempotencyKey: "batch_key_2")));
        Assert.Equal(403, error.StatusCode);
        Assert.Equal("batch_key_2", error.IdempotencyKey);
        Assert.Contains("item 0 (a.png)", error.Detail);
        Assert.Contains(BatchId, error.Detail);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.AbsolutePath.EndsWith("/start"));
    }

    [Fact]
    public async Task WaitHonorsRetryAfter()
    {
        int gets = 0;
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.Equal($"/watermarks/batches/{BatchId}", request.RequestUri!.AbsolutePath);
            return ++gets == 1 ? Json(HttpStatusCode.OK, View("processing"), "1.2") : Json(HttpStatusCode.OK, View("completed"));
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var clock = Stopwatch.StartNew();
        var batch = await client.WaitForBatchAsync(BatchId, TimeSpan.FromSeconds(10));
        Assert.Equal("completed", batch.Status);
        Assert.True(batch.IsTerminal);
        Assert.Equal(2, gets);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(1190), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task WaitTimesOutWithTheLastStatus()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK, View("processing"), "0.05"));
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var error = await Assert.ThrowsAsync<EtchvException>(() => client.WaitForBatchAsync(BatchId, TimeSpan.FromMilliseconds(120)));
        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.Contains("processing", error.Detail);
    }

    [Fact]
    public async Task ResultsSurfacePerItemFailures()
    {
        var succeeded = new JsonObject { ["request_id"] = Job, ["credits"] = 1, ["result_url"] = $"/watermarks/jobs/{Job}/result" };
        var rejected = new JsonObject { ["error_code"] = "upload_size_mismatch", ["error_detail"] = "size differs", ["credits"] = 0 };
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == $"/watermarks/jobs/{Job}/result")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
                response.Content.Headers.ContentType = new("image/png");
                response.Headers.TryAddWithoutValidation("X-Watermark-ID", Id);
                return response;
            }
            return Json(HttpStatusCode.OK, View("completed", Item(0, "a.png", "succeeded", succeeded), Item(1, "b.pdf", "rejected", rejected)));
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var results = new List<BatchItemResult>();
        await foreach (var result in client.IterBatchResultsAsync(BatchId)) results.Add(result);
        Assert.Equal(2, results.Count);
        Assert.True(results[0].Ok);
        Assert.Equal(Id, results[0].Result!.WatermarkId);
        Assert.Equal(Png, results[0].Result!.Bytes);
        Assert.False(results[1].Ok);
        Assert.Equal("rejected", results[1].Status);
        Assert.Equal("upload_size_mismatch", results[1].ErrorCode);
        Assert.Equal(0, results[1].Credits);
        Assert.Null(results[1].Error);
    }

    [Fact]
    public async Task ArchivePollsWhileNotReady()
    {
        byte[] zip = [80, 75, 3, 4, 1, 2, 3];
        int gets = 0;
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.Equal($"/watermarks/batches/{BatchId}/archive", request.RequestUri!.AbsolutePath);
            if (++gets == 1) return Json(HttpStatusCode.Accepted, View("assembling"), "0.05");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
            response.Content.Headers.ContentType = new("application/zip");
            return response;
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        Assert.Equal(zip, await client.DownloadBatchArchiveAsync(BatchId));
        Assert.Equal(2, gets);

        var refused = new RecordingHandler((_, _) => Json(HttpStatusCode.Conflict, new JsonObject { ["detail"] = new JsonObject { ["code"] = "archive_not_requested", ["message"] = "no archive" } }));
        using var other = new EtchvClient("test-key", Base, httpClient: new HttpClient(refused));
        var error = await Assert.ThrowsAsync<EtchvException>(() => other.DownloadBatchArchiveAsync(BatchId));
        Assert.Equal("archive_not_requested", error.Code);
    }

    [Fact]
    public async Task MoreThanAHundredItemsSendNothing()
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("no request expected"));
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var items = Enumerable.Range(0, 101).Select(i => new BatchItem($"{i}.png", Png, Data("x"))).ToList();
        var error = await Assert.ThrowsAsync<ArgumentException>(() => client.SubmitBatchAsync(items));
        Assert.Contains("1 to 100", error.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => client.SubmitBatchAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SubmitBatchAsync([new BatchItem("a.png", Png, Data("a"))], new BatchOptions(UploadConcurrency: 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetBatchAsync("bat_bad"));
        Assert.Throws<ArgumentException>(() => client.IterBatchResultsAsync("../x"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ZipSendsTheArchiveAndManifest()
    {
        byte[] zip = [80, 75, 3, 4, 9, 9];
        var handler = new RecordingHandler((request, body) =>
        {
            Assert.Equal("/watermarks/batches/zip", request.RequestUri!.AbsolutePath);
            Assert.Equal("zip_key_123", request.Headers.GetValues("Idempotency-Key").Single());
            Assert.Contains("name=archive", body);
            Assert.Contains("filename=batch.zip", body);
            Assert.Contains("application/zip", body);
            Assert.Contains(Encoding.Latin1.GetString(zip), body);
            Assert.Contains("name=manifest", body);
            Assert.Contains("{\"archive\":true,\"items\":[{\"filename\":\"in/a.png\",\"data\":{\"recipient\":\"a\"}}]}", body);
            return Json(HttpStatusCode.Accepted, View("starting", Item(0, "in/a.png", "pending")));
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var batch = await client.SubmitBatchZipAsync(zip, [new BatchZipItem("in/a.png", Data("a"))], new BatchOptions(Archive: true, IdempotencyKey: "zip_key_123"));
        Assert.Equal("starting", batch.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => client.SubmitBatchZipAsync([], [new BatchZipItem("a.png", Data("a"))]));
    }

    [Fact]
    public async Task CancelAndListUseTheirRoutes()
    {
        var cursor = "bat_" + new string('b', 32);
        var handler = new RecordingHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/cancel")
            ? Json(HttpStatusCode.OK, View("cancelled"))
            : Json(HttpStatusCode.OK, new JsonObject { ["data"] = new JsonArray(View("completed")), ["next_cursor"] = cursor }));
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        Assert.Equal("cancelled", (await client.CancelBatchAsync(BatchId)).Status);
        var page = await client.ListBatchesAsync(10, cursor);
        Assert.Equal(cursor, page.NextCursor);
        Assert.Single(page.Data);
        Assert.Equal(1, page.Data[0].Counts.Succeeded);
        Assert.Equal(["POST /watermarks/batches/" + BatchId + "/cancel", "GET /watermarks/batches?limit=10&before=" + cursor],
            handler.Requests.Select(r => $"{r.Method} {r.Uri.PathAndQuery}"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListBatchesAsync(51));
    }

    private static HttpResponseMessage Zip(byte[] zip)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
        response.Content.Headers.ContentType = new("application/zip");
        return response;
    }

    [Fact]
    public async Task ArchiveWaitOutlastsTheClientTimeoutAndFloorsRetryAfter()
    {
        byte[] zip = [80, 75, 5, 6, 7];
        int gets = 0;
        var handler = new RecordingHandler((_, _) => ++gets == 1 ? Json(HttpStatusCode.Accepted, View("processing"), "0.2") : Zip(zip));
        using var client = new EtchvClient("test-key", Base, TimeSpan.FromMilliseconds(300), new HttpClient(handler));
        var clock = Stopwatch.StartNew();
        Assert.Equal(zip, await client.DownloadBatchArchiveAsync(BatchId, TimeSpan.FromSeconds(10)));
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(990), clock.Elapsed.ToString());
        Assert.Equal(2, gets);
    }

    [Fact]
    public async Task ArchiveWaitTimeoutSaysNotReady()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.Accepted, View("processing"), "0.05"));
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var error = await Assert.ThrowsAsync<EtchvException>(() => client.DownloadBatchArchiveAsync(BatchId, TimeSpan.FromMilliseconds(150)));
        Assert.Equal(0, error.StatusCode);
        Assert.Contains("not ready", error.Detail);
        Assert.IsType<TimeoutException>(error.InnerException);
    }

    [Fact]
    public async Task ArchiveStreamsToADestination()
    {
        byte[] zip = [80, 75, .. Enumerable.Range(0, 200_000).Select(i => (byte)i)];
        var handler = new RecordingHandler((_, _) => Zip(zip));
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        using var destination = new MemoryStream();
        Assert.Equal(zip.Length, await client.DownloadBatchArchiveAsync(BatchId, destination));
        Assert.Equal(zip, destination.ToArray());

        var notZip = new RecordingHandler((_, _) => Zip([1, 2, 3]));
        using var other = new EtchvClient("test-key", Base, httpClient: new HttpClient(notZip));
        using var untouched = new MemoryStream();
        var error = await Assert.ThrowsAsync<EtchvException>(() => other.DownloadBatchArchiveAsync(BatchId, untouched));
        Assert.Equal("Invalid archive response", error.Detail);
        Assert.Equal(0, untouched.Length);
    }

    [Fact]
    public async Task ArchiveOverTheCapIsRefusedAtOnce()
    {
        Assert.Equal(1024L * 1024 * 1024 + 64L * 1024 * 1024, EtchvClient.MaxBatchArchiveSize);
        var handler = new RecordingHandler((_, _) =>
        {
            var response = Zip([80, 75, 1]);
            response.Content.Headers.ContentLength = EtchvClient.MaxBatchArchiveSize + 1;
            return response;
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var error = await Assert.ThrowsAsync<EtchvException>(() => client.DownloadBatchArchiveAsync(BatchId));
        Assert.Equal("archive_too_large", error.Code); Assert.Contains("exceeds", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ASlowUploadSucceedsWhileBytesKeepFlowing()
    {
        var file = new byte[5 * 64 * 1024]; Png.CopyTo(file, 0);
        var handler = new SlowUploadHandler(TimeSpan.FromMilliseconds(100), Respond);
        using var client = new EtchvClient("test-key", Base, TimeSpan.FromMilliseconds(300), new HttpClient(handler));
        var clock = Stopwatch.StartNew();
        await client.SubmitBatchAsync([new BatchItem("a.png", file, Data("a"))]);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(400), clock.Elapsed.ToString());
        Assert.Equal(file.Length, handler.Received);
    }

    [Fact]
    public async Task AStalledUploadFails()
    {
        var handler = new SlowUploadHandler(Timeout.InfiniteTimeSpan, Respond);
        using var client = new EtchvClient("test-key", Base, TimeSpan.FromMilliseconds(300), new HttpClient(handler));
        var error = await Assert.ThrowsAsync<EtchvException>(() => client.SubmitBatchAsync([new BatchItem("a.png", Png, Data("a"))], new BatchOptions(IdempotencyKey: "stalled_key")));
        Assert.Contains("stalled", error.Detail);
        Assert.Contains("item 0 (a.png)", error.Detail);
        Assert.Equal("stalled_key", error.IdempotencyKey);
        Assert.Equal(2, handler.Puts);
    }

    private static HttpResponseMessage Respond(HttpRequestMessage request) => request.RequestUri!.AbsolutePath == "/watermarks/batches"
        ? Json(HttpStatusCode.Created, View("draft", Item(0, "a.png", "pending", Upload(0))))
        : Json(HttpStatusCode.Accepted, View("starting", Item(0, "a.png", "pending")));

    [Fact]
    public async Task ResultsWaitForTheBatchAndReportCancelledFiles()
    {
        int gets = 0;
        var handler = new RecordingHandler((_, _) => ++gets == 1
            ? Json(HttpStatusCode.OK, View("processing", Item(0, "a.png", "queued")), "0.05")
            : Json(HttpStatusCode.OK, View("cancelled", Item(0, "a.png", "pending"))));
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var results = new List<BatchItemResult>();
        await foreach (var result in client.IterBatchResultsAsync(BatchId, TimeSpan.FromSeconds(10))) results.Add(result);
        Assert.Equal(2, gets);
        var only = Assert.Single(results);
        Assert.False(only.Ok);
        Assert.Equal("cancelled", only.ErrorCode);
        Assert.Equal("pending", only.Status);
    }

    [Fact]
    public async Task AnExpiredReplayFailsAndAStartedReplayIsReturned()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.OK, View("expired", Item(0, "a.png", "pending"))));
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var error = await Assert.ThrowsAsync<EtchvException>(() => client.SubmitBatchAsync([new BatchItem("a.png", Png, Data("a"))], new BatchOptions(IdempotencyKey: "expired_key")));
        Assert.Contains("expired before it was started", error.DetailMessage);
        Assert.Contains("expired before it was started", error.Message);
        Assert.Equal(410, error.StatusCode);
        Assert.Equal("batch_expired", error.Code);
        Assert.Equal("expired_key", error.IdempotencyKey);
        Assert.Single(handler.Requests);

        var started = new RecordingHandler((_, _) => Json(HttpStatusCode.OK, View("processing", Item(0, "a.png", "queued"))));
        using var other = new EtchvClient("test-key", Base, httpClient: new HttpClient(started));
        Assert.Equal("processing", (await other.SubmitBatchAsync([new BatchItem("a.png", Png, Data("a"))])).Status);
        Assert.Single(started.Requests);
    }

    [Fact]
    public async Task AReplayUploadsOnlyFilesNotYetReceived()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "uploads.etchv.com") return new HttpResponseMessage(HttpStatusCode.OK);
            if (request.RequestUri.AbsolutePath == "/watermarks/batches")
                return Json(HttpStatusCode.OK, View("draft", Item(0, "a.png", "pending", new JsonObject { ["upload_received"] = true }), Item(1, "b.png", "pending", Upload(1))));
            return Json(HttpStatusCode.Accepted, View("starting"));
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        await client.SubmitBatchAsync([new BatchItem("a.png", Png, Data("a")), new BatchItem("b.png", Png, Data("b"))]);
        Assert.Equal(["POST /watermarks/batches", "PUT /image/upl_1.png", $"POST /watermarks/batches/{BatchId}/start"],
            handler.Requests.Select(r => $"{r.Method} {r.Uri.AbsolutePath}"));
    }

    [Fact]
    public async Task ANullUploadReceivedWithAnUploadUrlUploadsNormally()
    {
        var withNull = Upload(0); withNull["upload_received"] = null;
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "uploads.etchv.com") return new HttpResponseMessage(HttpStatusCode.OK);
            if (request.RequestUri.AbsolutePath == "/watermarks/batches") return Json(HttpStatusCode.Created, View("draft", Item(0, "a.png", "pending", withNull)));
            return Json(HttpStatusCode.Accepted, View("starting"));
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        await client.SubmitBatchAsync([new BatchItem("a.png", Png, Data("a"))]);
        Assert.Equal(["POST /watermarks/batches", "PUT /image/upl_0.png", $"POST /watermarks/batches/{BatchId}/start"],
            handler.Requests.Select(r => $"{r.Method} {r.Uri.AbsolutePath}"));
    }

    [Fact]
    public async Task AnItemWithNeitherAnUploadUrlNorReceiptFailsBeforeAnyUpload()
    {
        var handler = new RecordingHandler((_, _) =>
            Json(HttpStatusCode.Created, View("draft", Item(0, "a.png", "pending", Upload(0)), Item(1, "b.png", "pending"))));
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var error = await Assert.ThrowsAsync<EtchvException>(() => client.SubmitBatchAsync(
            [new BatchItem("a.png", Png, Data("a")), new BatchItem("b.png", Png, Data("b"))], new BatchOptions(IdempotencyKey: "broken_key")));
        Assert.Contains("item 1 (b.png)", error.Detail);
        Assert.Equal("broken_key", error.IdempotencyKey);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AFailedStartCarriesTheIdempotencyKey()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/watermarks/batches")
                return Json(HttpStatusCode.OK, View("draft", Item(0, "a.png", "pending", new JsonObject { ["upload_received"] = true })));
            Assert.False(request.Headers.Contains("Idempotency-Key"));
            return Json(HttpStatusCode.Gone, new JsonObject { ["detail"] = "This batch's uploads expired; create a new batch" });
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var error = await Assert.ThrowsAsync<EtchvException>(() => client.SubmitBatchAsync([new BatchItem("a.png", Png, Data("a"))], new BatchOptions(IdempotencyKey: "start_key")));
        Assert.Equal(410, error.StatusCode);
        Assert.Equal("start_key", error.IdempotencyKey);
        Assert.Equal(["POST /watermarks/batches", $"POST /watermarks/batches/{BatchId}/start"], handler.Requests.Select(r => $"{r.Method} {r.Uri.AbsolutePath}"));
    }

    [Fact]
    public async Task ASlowDestinationDoesNotCountAsAStall()
    {
        byte[] zip = [80, 75, .. new byte[200_000]];
        var handler = new RecordingHandler((_, _) => Zip(zip));
        using var client = new EtchvClient("test-key", Base, TimeSpan.FromMilliseconds(300), new HttpClient(handler));
        using var destination = new SlowWriteStream(TimeSpan.FromMilliseconds(500));
        Assert.Equal(zip.Length, await client.DownloadBatchArchiveAsync(BatchId, destination));
        Assert.Equal(zip, destination.ToArray());
    }

    private sealed class SlowWriteStream(TimeSpan delay) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    [Fact]
    public async Task AFileThatChangedOrVanishedFailsAtOnceWithTheKey()
    {
        var file = Path.Combine(Path.GetTempPath(), $"etchv-batch-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(file, Png);
        try
        {
            foreach (var change in new Action[] { () => File.WriteAllBytes(file, [.. Png, 1]), () => File.Delete(file) })
            {
                var handler = new RecordingHandler((request, _) =>
                {
                    if (request.RequestUri!.AbsolutePath == "/watermarks/batches") { change(); return Json(HttpStatusCode.Created, View("draft", Item(0, "a.png", "pending", Upload(0)))); }
                    return new HttpResponseMessage(HttpStatusCode.OK);
                });
                using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
                var error = await Assert.ThrowsAsync<EtchvException>(() => client.SubmitBatchAsync([BatchItem.FromPath(file, Data("a"), "a.png")], new BatchOptions(IdempotencyKey: "changed_key")));
                Assert.Equal("changed_key", error.IdempotencyKey);
                Assert.Contains("item 0 (a.png)", error.Detail);
                Assert.Contains(BatchId, error.Detail);
                Assert.Single(handler.Requests);
                await File.WriteAllBytesAsync(file, Png);
            }
        }
        finally { File.Delete(file); }
    }
}

/// <summary>Reads PUT bodies one write at a time with a delay (or never, for a stall); answers other requests with a function.</summary>
internal sealed class SlowUploadHandler(TimeSpan delay, Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public long Received;
    public int Puts;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Put) return respond(request);
        Assert.False(request.Headers.Contains("X-API-Key"));
        Interlocked.Increment(ref Puts);
        if (delay == Timeout.InfiniteTimeSpan) await Task.Delay(Timeout.Infinite, cancellationToken);
        var sink = new SlowStream(delay);
        await request.Content!.CopyToAsync(sink, cancellationToken);
        Received = sink.Length;
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private sealed class SlowStream(TimeSpan delay) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
