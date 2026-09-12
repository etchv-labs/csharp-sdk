using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Etchv;

public sealed class EtchvException(int statusCode, string detail, string? requestId = null, string? idempotencyKey = null)
    : Exception($"Etchv request failed (HTTP {statusCode})")
{
    public int StatusCode { get; } = statusCode;
    public string Detail { get; } = detail;
    public string? RequestId { get; } = requestId;
    public string? IdempotencyKey { get; } = idempotencyKey;
}
public sealed record RequestOptions(string? Filename = null, string? IdempotencyKey = null);
public sealed record EmbedResult(byte[] Bytes, string WatermarkId, string? RequestId, string ContentType, string Filename, string? AssetId = null, string? SourceAssetId = null);
public sealed record DetectionUnit(int Index, bool Watermarked, double Confidence, string? WatermarkId);
public sealed record DetectionResult(bool Watermarked, double Confidence, string? WatermarkId, string? RequestId, IReadOnlyList<DetectionUnit> Units);

/// <summary>Server-side client. Disposing the client releases its HTTP connection pool.</summary>
public sealed partial class EtchvClient : IDisposable
{
    public const int MaxFileSize = 20 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly string key;
    private readonly string baseUrl;
    private readonly TimeSpan timeout;
    private static bool ValidId(string? id) => id is not null && Regex.IsMatch(id, "\\A[0-9a-fA-F]{64}\\z");
    private static bool ValidJob(string id) => Regex.IsMatch(id, "\\Areq_[0-9a-f]{64}\\z");
    public EtchvClient(string apiKey, string baseUrl = "https://api.etchv.com", TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.IndexOfAny(['\r', '\n']) >= 0) throw new ArgumentException("API key required");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" ||
            !(uri.Scheme == "https" || (uri.Scheme == "http" && uri.Host is "localhost" or "127.0.0.1" or "[::1]")))
            throw new ArgumentException("Base URL must use HTTPS (HTTP allowed for localhost)");
        this.timeout = timeout ?? TimeSpan.FromMinutes(2);
        if (this.timeout <= TimeSpan.Zero || this.timeout.TotalMilliseconds > uint.MaxValue - 1) throw new ArgumentException("Invalid timeout");
        key = apiKey; this.baseUrl = baseUrl.TrimEnd('/');
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public void Dispose() => http.Dispose();
    public Task<JsonElement> SubmitEmbedAsync(string media, byte[] file, IReadOnlyDictionary<string, object?> data, RequestOptions? options = null, string? webhookId = null, CancellationToken cancellationToken = default) {
        if (data is null || data.Count == 0) throw new ArgumentException("data must be a non-empty JSON object");
        return SubmitAsync(media, file, JsonSerializer.Serialize(data), options, webhookId, cancellationToken);
    }
    public Task<JsonElement> SubmitDetectionAsync(string media, byte[] file, RequestOptions? options = null, string? webhookId = null, CancellationToken cancellationToken = default) => SubmitAsync(media, file, null, options, webhookId, cancellationToken);
    private async Task<JsonElement> SubmitAsync(string media, byte[] file, string? data, RequestOptions? options, string? webhookId, CancellationToken ct) {
        if (media is not ("images" or "documents" or "videos") || file is null || file.Length == 0 || file.Length > MaxFileSize) throw new ArgumentException("Invalid media or file size");
        if (webhookId is not null && !Regex.IsMatch(webhookId, @"\Awh_[a-f0-9]{32}\z")) throw new ArgumentException("Invalid webhook ID");
        options ??= new(); options = options with { IdempotencyKey = string.IsNullOrEmpty(options.IdempotencyKey) ? Guid.NewGuid().ToString() : options.IdempotencyKey };
        var r = await RequestAsync($"watermarks/{media}" + (data is null ? "/detect" : "") + "/async" + (webhookId is null ? "" : "?webhook_id=" + webhookId), file, data, options, true, data is null, ct);
        using var document = JsonDocument.Parse(r.Bytes); return document.RootElement.Clone();
    }
    public async Task<JsonElement> GetJobAsync(string requestId, bool detect = false, CancellationToken cancellationToken = default) {
        if (!ValidJob(requestId)) throw new ArgumentException("Invalid request ID");
        var r = await RequestAsync($"watermarks/{(detect ? "detection-jobs" : "jobs")}/{requestId}", null, null, new(), false, detect, cancellationToken);
        using var document = JsonDocument.Parse(r.Bytes); return document.RootElement.Clone();
    }
    public Task<EmbedResult> EmbedImageAsync(byte[] file, IReadOnlyDictionary<string, object?> data, RequestOptions? options = null, CancellationToken cancellationToken = default) => EmbedAsync("images", file, data, options, cancellationToken);
    public Task<EmbedResult> EmbedDocumentAsync(byte[] file, IReadOnlyDictionary<string, object?> data, RequestOptions? options = null, CancellationToken cancellationToken = default) => EmbedAsync("documents", file, data, options, cancellationToken);
    public Task<EmbedResult> EmbedVideoAsync(byte[] file, IReadOnlyDictionary<string, object?> data, RequestOptions? options = null, CancellationToken cancellationToken = default) => EmbedAsync("videos", file, data, options, cancellationToken);
    public Task<DetectionResult> DetectImageAsync(byte[] file, RequestOptions? options = null, CancellationToken cancellationToken = default) => DetectAsync("images", file, options, cancellationToken);
    public Task<DetectionResult> DetectDocumentAsync(byte[] file, RequestOptions? options = null, CancellationToken cancellationToken = default) => DetectAsync("documents", file, options, cancellationToken);
    public Task<DetectionResult> DetectVideoAsync(byte[] file, RequestOptions? options = null, CancellationToken cancellationToken = default) => DetectAsync("videos", file, options, cancellationToken);
    public async Task<EmbedResult> GetEmbedResultAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (!ValidJob(requestId)) throw new ArgumentException("Invalid request ID");
        return Embedding(await RequestAsync($"watermarks/jobs/{requestId}/result", null, null, new(), true, false, cancellationToken));
    }
    public async Task<DetectionResult> GetDetectionResultAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (!ValidJob(requestId)) throw new ArgumentException("Invalid request ID");
        return Detection(await RequestAsync($"watermarks/detection-jobs/{requestId}/result", null, null, new(), true, true, cancellationToken));
    }
    private async Task<EmbedResult> EmbedAsync(string media, byte[] file, IReadOnlyDictionary<string, object?> data, RequestOptions? options, CancellationToken ct)
    {
        if (data is null || data.Count == 0) throw new ArgumentException("data must be a non-empty JSON object");
        return Embedding(await PostAsync(media, file, JsonSerializer.Serialize(data), options, ct));
    }
    private async Task<DetectionResult> DetectAsync(string media, byte[] file, RequestOptions? options, CancellationToken ct) => Detection(await PostAsync(media, file, null, options, ct));
    private Task<Reply> PostAsync(string media, byte[] file, string? data, RequestOptions? options, CancellationToken ct)
    {
        if (file is null || file.Length == 0 || file.Length > MaxFileSize) throw new ArgumentException("file must contain 1 byte to 20 MB");
        bool durable = data is not null || media == "videos";
        options ??= new();
        options = options with
        {
            Filename = options.Filename ?? (media switch { "documents" => "document.pdf", "videos" => "video.mp4", _ => "image.png" }),
            IdempotencyKey = durable && string.IsNullOrEmpty(options.IdempotencyKey) ? Guid.NewGuid().ToString() : options.IdempotencyKey
        };
        return RequestAsync($"watermarks/{media}" + (data is null ? "/detect" : ""), file, data, options, durable, data is null && media == "videos", ct);
    }
    private sealed record Reply(byte[] Bytes, string? RequestId, string? WatermarkId, string ContentType, string Disposition, string? AssetId, string? SourceAssetId);
    private async Task<Reply> RequestAsync(string path, byte[]? file, string? data, RequestOptions options, bool durable, bool detectionJob, CancellationToken caller)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        deadline.CancelAfter(timeout); var ct = deadline.Token; string? requestId = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(file is null ? HttpMethod.Get : HttpMethod.Post, baseUrl + "/" + path);
                request.Headers.Add("X-API-Key", key);
                if (options.IdempotencyKey is not null) request.Headers.Add("Idempotency-Key", options.IdempotencyKey);
                if (file is not null)
                {
                    var form = new MultipartFormDataContent();
                    form.Add(new ByteArrayContent(file), "file", options.Filename ?? "file");
                    if (data is not null) form.Add(new StringContent(data, Encoding.UTF8), "data");
                    request.Content = form;
                }
                try
                {
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
                    requestId = Header("X-Request-ID") ?? requestId;
                    using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var buffer = new MemoryStream();
                    var chunk = new byte[8192]; int n;
                    while ((n = await stream.ReadAsync(chunk, ct)) > 0)
                    {
                        if (buffer.Length + n > MaxFileSize) throw new EtchvException((int)response.StatusCode, "Response exceeds 20 MB", requestId);
                        buffer.Write(chunk, 0, n);
                    }
                    var bytes = buffer.ToArray(); int status = (int)response.StatusCode;
                    if (status == 200 || (status == 202 && path.Split('?')[0].EndsWith("/async", StringComparison.Ordinal))) return new(bytes, requestId, Header("X-Watermark-ID"), response.Content.Headers.ContentType?.MediaType ?? "", response.Content.Headers.ContentDisposition?.ToString() ?? "", Header("X-Asset-ID"), Header("X-Source-Asset-ID"));
                    JsonElement detail = default;
                    try { using var document = JsonDocument.Parse(bytes); detail = document.RootElement.Clone(); } catch (JsonException) { }
                    if (durable && status == 202)
                    {
                        if (detail.ValueKind != JsonValueKind.Object || !detail.TryGetProperty("request_id", out var id) || id.ValueKind != JsonValueKind.String || !ValidJob(id.GetString()!))
                            throw new EtchvException(202, "Invalid job response", requestId);
                        requestId = id.GetString();
                        path = $"watermarks/{(detectionJob ? "detection-jobs" : "jobs")}/{requestId}/result"; file = null;
                        double seconds = double.TryParse(Header("Retry-After"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var delay) && double.IsFinite(delay) ? Math.Clamp(delay, .01, 5) : 1;
                        await Task.Delay(TimeSpan.FromSeconds(seconds), ct); continue;
                    }
                    bool failed = detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("status", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "failed";
                    if (durable && status is 429 or 502 or 503 or 504 && !failed) { await Task.Delay(1000, ct); continue; }
                    throw new EtchvException(status, Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 10000))), requestId, options.IdempotencyKey);
                }
                catch (Exception e) when (durable && e is HttpRequestException or IOException) { await Task.Delay(1000, ct); }
            }
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        { throw new EtchvException(0, "Client deadline exceeded; job may still complete", requestId, options.IdempotencyKey); }
    }
    private static EmbedResult Embedding(Reply r)
    {
        string? ext = Extension(r.Bytes, r.ContentType);
        if (ext is null || !ValidId(r.WatermarkId)) throw new EtchvException(200, "Invalid embedding response", r.RequestId);
        var match = Regex.Match(r.Disposition, "filename=\"?([A-Za-z0-9._-]+)\"?(?:;|$)");
        return new(r.Bytes, r.WatermarkId!, r.RequestId, r.ContentType, match.Success ? match.Groups[1].Value : $"watermarked.{ext}", r.AssetId, r.SourceAssetId);
    }
    private static DetectionUnit Unit(JsonElement v, int index)
    {
        bool w = v.GetProperty("watermarked").GetBoolean(); double c = v.GetProperty("confidence").GetDouble();
        string? id = v.GetProperty("watermark_id").GetString();
        if (!double.IsFinite(c) || c < 0 || c > 1 || (w ? !ValidId(id) : id is not null)) throw new FormatException();
        return new(index, w, c, id);
    }
    private static DetectionResult Detection(Reply r)
    {
        try
        {
            using var doc = JsonDocument.Parse(r.Bytes); var v = doc.RootElement; var top = Unit(v, 0); var units = new List<DetectionUnit>();
            if (v.TryGetProperty("units", out var list))
            {
                foreach (var u in list.EnumerateArray()) { if (u.GetProperty("index").GetInt32() != units.Count) throw new FormatException(); units.Add(Unit(u, units.Count)); }
                if (units.Count == 0) throw new FormatException();
            }
            else units.Add(top);
            return new(top.Watermarked, top.Confidence, top.WatermarkId, r.RequestId, units.AsReadOnly());
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw new EtchvException(200, "Invalid detection response", r.RequestId); }
    }
    private static string? Extension(byte[] b, string mime)
    {
        bool Starts(byte[] prefix, int offset = 0) => b.Length >= offset + prefix.Length && b.AsSpan(offset, prefix.Length).SequenceEqual(prefix);
        bool Text(string s, int offset = 0) => Starts(Encoding.ASCII.GetBytes(s), offset);
        return mime switch
        {
            "image/png" when Starts([137, 80, 78, 71, 13, 10, 26, 10]) => "png",
            "image/jpeg" when Starts([255, 216, 255]) => "jpg",
            "image/gif" when Text("GIF87a") || Text("GIF89a") => "gif",
            "image/tiff" when Starts([73, 73, 42, 0]) || Starts([77, 77, 0, 42]) => "tiff",
            "image/bmp" when Text("BM") => "bmp",
            "image/x-portable-pixmap" when Text("P6") || Text("P3") => "ppm",
            "image/webp" when Text("RIFF") && Text("WEBP", 8) => "webp",
            "image/vnd.adobe.photoshop" when Starts([56, 66, 80, 83, 0, 1]) => "psd",
            "image/vnd.adobe.photoshop" when Starts([56, 66, 80, 83, 0, 2]) => "psb",
            "application/pdf" when Text("%PDF-") => "pdf",
            "video/mp4" when Text("ftyp", 4) => "mp4",
            "video/quicktime" when Text("ftyp", 4) => "mov",
            _ => null
        };
    }
}
