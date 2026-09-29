using Etchv;
using System.Diagnostics;
using System.Net;
using System.Text;
using Xunit;

/// <summary>Answers each request from a queue of canned responses and records the request URIs.</summary>
internal sealed class QueueHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> queue = new(responses);
    public readonly List<Uri> Requests = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null) await request.Content.ReadAsByteArrayAsync(cancellationToken);
        lock (Requests) Requests.Add(request.RequestUri!);
        var response = queue.Dequeue()();
        response.RequestMessage = request;
        return response;
    }
}

public sealed class AcceleratorTests
{
    private const string Base = "http://127.0.0.1:9";
    private static readonly string Id = new('a', 64), Job = "req_" + new string('b', 64);
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 0];
    private static readonly Dictionary<string, object?> Data = new() { ["asset"] = "example" };

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string? accelerator = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (accelerator is not null) response.Headers.TryAddWithoutValidation("X-Etchv-Accelerator", accelerator);
        return response;
    }

    private static HttpResponseMessage Image(string? accelerator)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
        response.Content.Headers.ContentType = new("image/png");
        response.Headers.TryAddWithoutValidation("X-Watermark-ID", Id);
        if (accelerator is not null) response.Headers.TryAddWithoutValidation("X-Etchv-Accelerator", accelerator);
        return response;
    }

    private static string Detection(string? accelerator = null) =>
        $"{{\"watermarked\":true,\"confidence\":0.99,\"watermark_id\":\"{Id}\"{(accelerator is null ? "" : $",\"accelerator\":\"{accelerator}\"")}}}";

    [Fact]
    public async Task SendsAcceleratorOnlyWhenSetAndSurfacesActualHardware()
    {
        var handler = new QueueHandler(() => Image("gpu"), () => Image(null), () => Json(HttpStatusCode.OK, Detection(), "cpu"), () => Image("tpu"));
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var gpu = await client.EmbedImageAsync(Png, Data, new RequestOptions(Accelerator: Accelerator.Gpu));
        Assert.Equal(Accelerator.Gpu, gpu.Accelerator);
        var plain = await client.EmbedImageAsync(Png, Data);
        Assert.Null(plain.Accelerator);
        var detection = await client.DetectImageAsync(Png, new RequestOptions(Accelerator: Accelerator.Gpu));
        Assert.Equal(Accelerator.Cpu, detection.Accelerator);
        var unknown = await client.EmbedDocumentAsync(Png, Data, new RequestOptions(Accelerator: Accelerator.Cpu));
        Assert.Null(unknown.Accelerator);
        Assert.Equal("?accelerator=gpu", handler.Requests[0].Query);
        Assert.Equal("", handler.Requests[1].Query);
        Assert.Equal("/watermarks/images/detect", handler.Requests[2].AbsolutePath);
        Assert.Equal("?accelerator=gpu", handler.Requests[2].Query);
        Assert.Equal("?accelerator=cpu", handler.Requests[3].Query);
    }

    [Fact]
    public async Task CombinesWithOtherQueryParametersAndReadsJobJson()
    {
        string webhook = "wh_" + new string('a', 32), destination = "dst_" + new string('c', 32);
        string receipt = $"{{\"status\":\"queued\",\"request_id\":\"{Job}\",\"operation\":\"embed\",\"status_url\":\"/watermarks/jobs/{Job}\",\"result_url\":\"/watermarks/jobs/{Job}/result\",\"webhook_id\":\"{webhook}\",\"asset_id\":null,\"source_asset_id\":null,\"format\":\"PNG\",\"frame_count\":1,\"credits\":3,\"attempts\":0,\"error_code\":null,\"result_expires_at\":null,\"storage_provider\":\"etchv\",\"accelerator_requested\":\"gpu\",\"accelerator\":null}}";
        string status = receipt.Replace("\"accelerator\":null", "\"accelerator\":\"cpu\"").Replace("queued", "succeeded");
        var handler = new QueueHandler(() => Json(HttpStatusCode.Accepted, receipt), () => Json(HttpStatusCode.Accepted, receipt), () => Json(HttpStatusCode.OK, status),
            () => Json(HttpStatusCode.OK, Detection("gpu")), () => Json(HttpStatusCode.Accepted, $"{{\"request_id\":\"{Job}\"}}"), () => Json(HttpStatusCode.OK, Detection("cpu")));
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var options = new RequestOptions(IdempotencyKey: "stable_test_key", Accelerator: Accelerator.Gpu);
        var embed = await client.SubmitEmbedAsync("images", Png, Data, options with { StorageDestinationId = destination }, webhook);
        Assert.Equal(Accelerator.Gpu, embed.AcceleratorRequested);
        Assert.Null(embed.Accelerator);
        await client.SubmitDetectionAsync("videos", Png, options, webhook);
        var job = await client.GetJobAsync(Job);
        Assert.Equal(Accelerator.Cpu, job.Accelerator);
        Assert.Equal(Accelerator.Gpu, (await client.GetDetectionResultAsync(Job)).Accelerator);
        var video = await client.DetectVideoAsync(Png, options);
        Assert.Equal(Accelerator.Cpu, video.Accelerator);
        Assert.Equal($"?webhook_id={webhook}&storage_destination_id={destination}&accelerator=gpu", handler.Requests[0].Query);
        Assert.Equal("/watermarks/videos/detect/async", handler.Requests[1].AbsolutePath);
        Assert.Equal($"?webhook_id={webhook}&accelerator=gpu", handler.Requests[1].Query);
        Assert.Equal("?accelerator=gpu", handler.Requests[4].Query);
        // The durable video detection polls the detection job's result path, without the submission query.
        Assert.Equal($"/watermarks/detection-jobs/{Job}/result", handler.Requests[5].AbsolutePath);
        Assert.Equal("", handler.Requests[5].Query);
    }

    [Fact]
    public async Task RetriesRateLimitsHonoringRetryAfter()
    {
        HttpResponseMessage Limited(string? retryAfter)
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{\"detail\":{\"message\":\"Rate limit exceeded\",\"code\":\"rate_limited\",\"limit\":60}}");
            if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return response;
        }
        var handler = new QueueHandler(() => Limited("0"), () => Limited(DateTimeOffset.UtcNow.AddSeconds(-5).ToString("R")), () => Image("gpu"));
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var watch = Stopwatch.StartNew();
        var result = await client.EmbedImageAsync(Png, Data, new RequestOptions(Accelerator: Accelerator.Gpu));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), "Retry-After should replace the default one-second delay");
        Assert.Equal(Accelerator.Gpu, result.Accelerator);
        Assert.Equal(3, handler.Requests.Count);

        // The deadline still bounds a long Retry-After.
        var slow = new QueueHandler(() => Limited("30"));
        using var bounded = new EtchvClient("test-key", Base, TimeSpan.FromMilliseconds(200), new HttpClient(slow));
        var error = await Assert.ThrowsAsync<EtchvException>(() => bounded.EmbedImageAsync(Png, Data));
        Assert.Equal(0, error.StatusCode);

        // Synchronous image detection is not retried; 429 surfaces immediately with its details.
        var single = new QueueHandler(() => Limited("7"), () => Limited("12"));
        using var once = new EtchvClient("test-key", Base, httpClient: new HttpClient(single));
        var limited = await Assert.ThrowsAsync<EtchvException>(() => once.DetectImageAsync(Png));
        Assert.Equal(429, limited.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), limited.RetryAfter);
        Assert.Equal("rate_limited", limited.Code);
        Assert.Equal("Rate limit exceeded", limited.DetailMessage);
        Assert.Equal(60, limited.Limit);
        Assert.Equal("Etchv request failed (HTTP 429): Rate limit exceeded", limited.Message);
        // Management calls expose the same fields.
        var management = await Assert.ThrowsAsync<EtchvException>(() => once.GetApiKeyInfoAsync());
        Assert.Equal(TimeSpan.FromSeconds(12), management.RetryAfter);
        Assert.Equal("rate_limited", management.Code);
        Assert.Single(single.Requests, uri => uri.AbsolutePath == "/auth/api-key");
    }

    [Fact]
    public async Task CapsEachRateLimitWaitAtFiveSeconds()
    {
        var handler = new QueueHandler(
            () => { var r = Json(HttpStatusCode.TooManyRequests, "{\"detail\":{\"code\":\"concurrency_limited\"}}"); r.Headers.TryAddWithoutValidation("Retry-After", "30"); return r; },
            () => Image(null));
        using var client = new EtchvClient("test-key", Base, TimeSpan.FromSeconds(20), new HttpClient(handler));
        var watch = Stopwatch.StartNew();
        await client.EmbedImageAsync(Png, Data);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(10));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task StringDetailIsTheMessageWithoutCode()
    {
        var handler = new QueueHandler(() => Json(HttpStatusCode.Forbidden, "{\"detail\":\"GPU processing requires Business or a higher plan\"}"));
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var error = await Assert.ThrowsAsync<EtchvException>(() => client.DetectImageAsync(Png, new RequestOptions(Accelerator: Accelerator.Gpu)));
        Assert.Equal(403, error.StatusCode);
        Assert.Equal("GPU processing requires Business or a higher plan", error.DetailMessage);
        Assert.Null(error.Code);
        Assert.Null(error.Limit);
        Assert.Null(error.RetryAfter);
        Assert.Equal("Etchv request failed (HTTP 403)", error.Message);
    }

    [Fact]
    public void ObjectDetailWithoutMessageKeepsTheDefaultMessage()
    {
        var error = new EtchvException(429, "{\"detail\":{\"code\":\"concurrency_limited\",\"limit\":\"two\"}}");
        Assert.Equal("Etchv request failed (HTTP 429)", error.Message);
        Assert.Equal("concurrency_limited", error.Code);
        Assert.Null(error.Limit);
        Assert.Equal("Etchv request failed (HTTP 500)", new EtchvException(500, "not json").Message);
    }
}
