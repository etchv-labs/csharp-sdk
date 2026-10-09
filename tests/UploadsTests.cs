using Etchv;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

/// <summary>Records each request (method, URI, API key header, body) and answers with a handler function.</summary>
internal sealed class RecordingHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public readonly List<(string Method, Uri Uri, bool ApiKey, string Body)> Requests = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : Encoding.Latin1.GetString(await request.Content.ReadAsByteArrayAsync(cancellationToken));
        lock (Requests) Requests.Add((request.Method.Method, request.RequestUri!, request.Headers.Contains("X-API-Key"), body));
        var response = respond(request, body);
        response.RequestMessage = request;
        return response;
    }
}

public sealed class UploadsTests
{
    private const string Base = "https://api.etchv.com";
    private static readonly string Id = new('a', 64), Upload = "upl_" + new string('1', 32), Job = "req_" + new string('c', 64);
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, .. new byte[200]];
    private static readonly Dictionary<string, object?> Data = new() { ["recipient"] = "test" };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Session(string kind) => Json(HttpStatusCode.Created, JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["upload_id"] = Upload, ["kind"] = kind, ["filename"] = "photo.png", ["size"] = Png.Length, ["status"] = "pending",
        ["expires_at"] = "2026-10-10T00:00:00Z",
        ["upload"] = new Dictionary<string, string> { ["method"] = "PUT", ["url"] = $"https://uploads.etchv.com/{kind}/{Upload}.png?Signature=s", ["expires_at"] = "x" },
    }));

    private static HttpResponseMessage Image()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
        response.Content.Headers.ContentType = new("image/png");
        response.Headers.TryAddWithoutValidation("X-Watermark-ID", Id);
        return response;
    }

    [Fact]
    public async Task LargeEmbedUploadsOnceWithoutTheApiKeyThenSendsTheUploadId()
    {
        var handler = new RecordingHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath == "/uploads")
            {
                using var json = JsonDocument.Parse(body);
                Assert.Equal("image", json.RootElement.GetProperty("kind").GetString());
                Assert.Equal(Png.Length, json.RootElement.GetProperty("size").GetInt32());
                return Session("image");
            }
            if (request.RequestUri.Host == "uploads.etchv.com") return new HttpResponseMessage(HttpStatusCode.OK);
            Assert.Contains("name=upload_id", body);
            Assert.Contains(Upload, body);
            Assert.DoesNotContain("name=file", body);
            return Image();
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler)) { LargeFileThreshold = 100 };
        var result = await client.EmbedImageAsync(Png, Data, new RequestOptions(Filename: "photo.png"));
        Assert.Equal(Id, result.WatermarkId);
        Assert.Equal(["POST /uploads", $"PUT /image/{Upload}.png", "POST /watermarks/images"], handler.Requests.Select(r => $"{r.Method} {r.Uri.AbsolutePath}"));
        Assert.Equal([true, false, true], handler.Requests.Select(r => r.ApiKey));
    }

    [Fact]
    public async Task RetriesResendTheSameUploadWithoutUploadingAgain()
    {
        int posts = 0;
        var handler = new RecordingHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath == "/uploads") return Session("image");
            if (request.RequestUri.Host == "uploads.etchv.com") return new HttpResponseMessage(HttpStatusCode.OK);
            Assert.Contains(Upload, body);
            return ++posts == 1 ? Json(HttpStatusCode.ServiceUnavailable, "{\"detail\":\"busy\"}") : Image();
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler)) { LargeFileThreshold = 100 };
        await client.EmbedImageAsync(Png, Data);
        Assert.Equal(1, handler.Requests.Count(r => r.Method == "PUT"));
        Assert.Equal(2, posts);
    }

    [Fact]
    public async Task LargeSyncImageDetectionRunsAsAJob()
    {
        var big = new byte[95 * 1024 * 1024 + 1]; Png.CopyTo(big, 0);
        var handler = new RecordingHandler((request, body) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/uploads") { Assert.Contains("\"detect\"", body); return Session("detect"); }
            if (request.RequestUri.Host == "uploads.etchv.com") return new HttpResponseMessage(HttpStatusCode.OK);
            if (path == "/watermarks/images/detect/async") return Json(HttpStatusCode.Accepted, $"{{\"request_id\":\"{Job}\",\"status\":\"queued\",\"operation\":\"detect\",\"status_url\":\"x\",\"result_url\":\"y\"}}");
            return Json(HttpStatusCode.OK, $"{{\"watermarked\":true,\"confidence\":0.99,\"watermark_id\":\"{Id}\"}}");
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var result = await client.DetectImageAsync(big);
        Assert.Equal(Id, result.WatermarkId);
        Assert.Equal(["/uploads", $"/detect/{Upload}.png", "/watermarks/images/detect/async", $"/watermarks/detection-jobs/{Job}/result"],
            handler.Requests.Select(r => r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task SmallFilesStayInTheBodyAndLimitsFollowTheOperation()
    {
        var handler = new RecordingHandler((request, body) => { Assert.Contains("name=file", body); return Image(); });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        await client.EmbedImageAsync(Png, Data);
        var embed = await Assert.ThrowsAsync<ArgumentException>(() => client.EmbedImageAsync(new byte[EtchvClient.MaxFileSize + 1], Data));
        Assert.Contains("50 MB", embed.Message);
        var detect = await Assert.ThrowsAsync<ArgumentException>(() => client.DetectImageAsync(new byte[EtchvClient.MaxDetectionFileSize + 1]));
        Assert.Contains("192 MB", detect.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => client.UploadFileAsync("audio", Png));
        Assert.Throws<ArgumentException>(() => new EtchvClient("test-key") { LargeFileThreshold = 0 });
    }

    [Fact]
    public async Task ARefusedUploadRaisesWithItsStatus()
    {
        var handler = new RecordingHandler((request, _) => request.RequestUri!.AbsolutePath == "/uploads"
            ? Session("image") : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Forbidden") });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var error = await Assert.ThrowsAsync<EtchvException>(() => client.UploadFileAsync("image", Png, "photo.png"));
        Assert.Equal(403, error.StatusCode);
    }

    [Fact]
    public async Task UploadFileReturnsTheReceivedSession()
    {
        var handler = new RecordingHandler((request, _) => request.RequestUri!.AbsolutePath == "/uploads" ? Session("detect") : new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var session = await client.UploadFileAsync("detect", Png, "photo.png");
        Assert.Equal(new UploadSession(Upload, "detect", "photo.png", Png.Length, "received", "2026-10-10T00:00:00Z"), session);
    }
}
