using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Etchv;

/// <summary>
/// An Etchv API error. <see cref="Exception.Message"/> never contains the API key, request bodies,
/// or server-provided text; inspect <see cref="Detail"/> for the (truncated) response body.
/// </summary>
public sealed class EtchvException : Exception
{
    /// <summary>Creates an exception for an HTTP status (0 for a client, transport or deadline failure).</summary>
    /// <param name="statusCode">HTTP status code, or 0 when no HTTP response was received.</param>
    /// <param name="detail">Response body (truncated to 10,000 characters) or a client-side description.</param>
    /// <param name="requestId">The Etchv request or job ID, when known.</param>
    /// <param name="idempotencyKey">The idempotency key sent with the request, when any.</param>
    /// <param name="innerException">The underlying transport exception, when any.</param>
    public EtchvException(int statusCode, string detail, string? requestId = null, string? idempotencyKey = null, Exception? innerException = null)
        : base(statusCode == 0 ? "Etchv request failed (client or transport error)" : $"Etchv request failed (HTTP {statusCode})", innerException)
    {
        StatusCode = statusCode; Detail = detail; RequestId = requestId; IdempotencyKey = idempotencyKey;
    }
    /// <summary>HTTP status code; 0 indicates a client, transport or deadline failure.</summary>
    public int StatusCode { get; }
    /// <summary>Response body (truncated) or a client-side description of the failure.</summary>
    public string Detail { get; }
    /// <summary>The Etchv request or job ID (<c>X-Request-ID</c>), when known. Quote it when contacting support.</summary>
    public string? RequestId { get; }
    /// <summary>The idempotency key used for the request. Retry with the same key to recover a durable job without another charge.</summary>
    public string? IdempotencyKey { get; }
    /// <summary>The <c>error_code</c> from a JSON error body (for example a failed job), when present.</summary>
    public string? ErrorCode => Field("error_code");
    /// <summary>The <c>status</c> from a JSON error body (for example <c>expired</c> or <c>deleted</c> on HTTP 410), when present.</summary>
    public string? ErrorStatus => Field("status");
    private string? Field(string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(Detail);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>Per-request options for embedding, detection and job submission.</summary>
/// <param name="Filename">Original filename sent with the upload; the extension should match the file format.</param>
/// <param name="IdempotencyKey">Stable key for durable operations. Generated when omitted; persist your own to recover across restarts.</param>
/// <param name="StorageDestinationId">Verified customer storage destination (<c>dst_…</c>) for the watermarked result. Embedding only.</param>
/// <param name="StorageKey">Relative object key beneath the destination prefix. Requires <paramref name="StorageDestinationId"/>.</param>
public sealed record RequestOptions(string? Filename = null, string? IdempotencyKey = null, string? StorageDestinationId = null, string? StorageKey = null);

/// <summary>A verified watermarked file.</summary>
/// <param name="Bytes">Watermarked file bytes in the original format.</param>
/// <param name="WatermarkId">The embedded 256-bit watermark ID as 64 hexadecimal characters.</param>
/// <param name="RequestId">The Etchv request (job) ID.</param>
/// <param name="ContentType">MIME type of <paramref name="Bytes"/>.</param>
/// <param name="Filename">Suggested filename for the result.</param>
/// <param name="AssetId">Asset ID of the watermarked output, when saved.</param>
/// <param name="SourceAssetId">Asset ID of the original upload, when saved.</param>
/// <param name="StorageDeliveryId">Customer storage delivery ID, when a destination was selected.</param>
public sealed record EmbedResult(byte[] Bytes, string WatermarkId, string? RequestId, string ContentType, string Filename, string? AssetId = null, string? SourceAssetId = null, string? StorageDeliveryId = null);

/// <summary>Detection result for one frame, page or composite.</summary>
/// <param name="Index">Zero-based unit index.</param>
/// <param name="Watermarked">Whether this unit carries a watermark.</param>
/// <param name="Confidence">Certainty between 0 and 1.</param>
/// <param name="WatermarkId">Decoded watermark ID, or null.</param>
public sealed record DetectionUnit(int Index, bool Watermarked, double Confidence, string? WatermarkId);

/// <summary>Detection result for a file.</summary>
/// <param name="Watermarked">True only when every unit carries the same watermark.</param>
/// <param name="Confidence">Lowest per-unit confidence.</param>
/// <param name="WatermarkId">Watermark ID shared by all units, or null.</param>
/// <param name="RequestId">The Etchv request ID.</param>
/// <param name="Units">Per-frame, per-page or per-composite results.</param>
public sealed record DetectionResult(bool Watermarked, double Confidence, string? WatermarkId, string? RequestId, IReadOnlyList<DetectionUnit> Units);

/// <summary>A durable watermarking or detection job receipt.</summary>
/// <param name="RequestId">Job ID (<c>req_…</c>).</param>
/// <param name="Status"><c>queued</c>, <c>running</c>, <c>retrying</c>, <c>succeeded</c> or <c>failed</c>.</param>
/// <param name="Operation"><c>embed</c> or <c>detect</c>.</param>
/// <param name="StatusUrl">Relative, authenticated status URL.</param>
/// <param name="ResultUrl">Relative, authenticated result URL.</param>
/// <param name="WebhookId">Webhook endpoint notified on completion, if any.</param>
/// <param name="AssetId">Watermarked output asset ID, when available.</param>
/// <param name="SourceAssetId">Original upload asset ID, when available.</param>
/// <param name="Format">Detected file format, such as <c>PNG</c> or <c>PDF</c>.</param>
/// <param name="FrameCount">Frames or pages processed.</param>
/// <param name="Credits">Credits reserved or charged.</param>
/// <param name="Attempts">Processing attempts so far.</param>
/// <param name="ErrorCode">Failure reason when <paramref name="Status"/> is <c>failed</c>.</param>
/// <param name="ResultExpiresAt">ISO 8601 time the saved result expires, when known.</param>
/// <param name="StorageProvider"><c>etchv</c>, or the selected customer storage provider.</param>
/// <param name="StorageDeliveryId">Customer storage delivery ID, if a destination was selected.</param>
/// <param name="StorageDestinationId">Customer storage destination ID, if selected.</param>
public sealed record JobReceipt(string RequestId, string Status, string Operation, string StatusUrl, string ResultUrl,
    string? WebhookId, string? AssetId, string? SourceAssetId, string Format, int FrameCount, int Credits, int Attempts,
    string? ErrorCode, string? ResultExpiresAt, string StorageProvider = "etchv", string? StorageDeliveryId = null, string? StorageDestinationId = null)
{
    /// <summary>True when the job has <c>succeeded</c> or <c>failed</c>.</summary>
    public bool IsTerminal => Status is "succeeded" or "failed";
}

/// <summary>The API key identity returned by the connection check.</summary>
/// <param name="OrganizationId">Organization that owns the key.</param>
/// <param name="KeyId">Key identifier (not the secret).</param>
/// <param name="Scopes">Scopes granted to the key.</param>
public sealed record ApiKeyInfo(string OrganizationId, string KeyId, IReadOnlyList<string> Scopes);

/// <summary>
/// Server-side Etchv API client, authenticated with an API key sent as <c>X-API-Key</c>.
/// Thread-safe; create one per application. Disposing releases the HTTP connection pool unless
/// an <see cref="HttpClient"/> was supplied by the caller.
/// </summary>
public sealed partial class EtchvClient : IDisposable
{
    /// <summary>SDK version.</summary>
    public const string Version = "1.0.0";
    /// <summary>User-Agent sent with every request.</summary>
    public const string UserAgent = "etchv-csharp/" + Version;
    /// <summary>Maximum upload size (50 MB). The API rejects PDFs and videos over 20 MB with status 413.</summary>
    public const int MaxFileSize = 50 * 1024 * 1024;
    /// <summary>Maximum response or result file size (256 MB).</summary>
    public const int MaxDownloadSize = 256 * 1024 * 1024;
    private const int MaxDetail = 10000;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly string key;
    private readonly string baseUrl;
    private readonly TimeSpan timeout;
    private static bool ValidId(string? id) => id is not null && Regex.IsMatch(id, "\\A[0-9a-fA-F]{64}\\z");
    private static bool ValidJob(string? id) => id is not null && Regex.IsMatch(id, "\\Areq_[0-9a-f]{64}\\z");
    private static string Check(string? id, string pattern, string name) =>
        id is not null && Regex.IsMatch(id, "\\A" + pattern + "\\z") ? id : throw new ArgumentException("Invalid " + name);

    /// <summary>Creates a client.</summary>
    /// <param name="apiKey">Server-side API key. Never embed it in client applications.</param>
    /// <param name="baseUrl">API base URL. HTTPS is required except for localhost.</param>
    /// <param name="timeout">Overall deadline per operation, including polling and retries (default 2 minutes).</param>
    /// <param name="httpClient">
    /// Optional caller-owned client (for example from <c>IHttpClientFactory</c>); it is not disposed by this client.
    /// Its primary handler must not follow redirects (<c>AllowAutoRedirect = false</c>): the SDK rejects redirected
    /// responses, but a redirect-following handler would already have forwarded the API key.
    /// </param>
    /// <exception cref="ArgumentException">The key, base URL or timeout is invalid.</exception>
    public EtchvClient(string apiKey, string baseUrl = "https://api.etchv.com", TimeSpan? timeout = null, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl)) throw new ArgumentException("API key required", nameof(apiKey));
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" ||
            !(uri.Scheme == "https" || (uri.Scheme == "http" && uri.Host is "localhost" or "127.0.0.1" or "[::1]")))
            throw new ArgumentException("Base URL must use HTTPS (HTTP allowed for localhost)", nameof(baseUrl));
        this.timeout = timeout ?? TimeSpan.FromMinutes(2);
        if (this.timeout <= TimeSpan.Zero || this.timeout.TotalMilliseconds > uint.MaxValue - 1) throw new ArgumentException("Invalid timeout", nameof(timeout));
        key = apiKey; this.baseUrl = baseUrl.TrimEnd('/');
        ownsHttp = httpClient is null;
        http = httpClient ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Releases the owned HTTP connection pool. A caller-supplied <see cref="HttpClient"/> is left open.</summary>
    public void Dispose() { if (ownsHttp) http.Dispose(); }

    /// <summary>Non-billable connection check (<c>GET /auth/api-key</c>). Requires an active key; no particular scope.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The key's organization, key ID and scopes.</returns>
    /// <exception cref="EtchvException">HTTP 401 for a missing, invalid or expired key.</exception>
    public Task<ApiKeyInfo> GetApiKeyInfoAsync(CancellationToken cancellationToken = default) => JsonAsync<ApiKeyInfo>(HttpMethod.Get, "auth/api-key", null, cancellationToken);

    /// <summary>Submits a background embedding job and returns its receipt without waiting (<c>POST /watermarks/{media}/async</c>).</summary>
    /// <param name="media"><c>images</c>, <c>documents</c> or <c>videos</c>.</param>
    /// <param name="file">File bytes (1 byte to 50 MB; PDFs and videos up to 20 MB).</param>
    /// <param name="data">Non-empty JSON object of forensic data; its SHA-256 digest is embedded.</param>
    /// <param name="options">Filename, idempotency key and optional storage destination.</param>
    /// <param name="webhookId">Optional enabled webhook endpoint (<c>wh_…</c>) to notify on completion.</param>
    /// <param name="cancellationToken">Cancels the request; server work continues.</param>
    /// <returns>The job receipt. Replays with the same idempotency key return the existing job.</returns>
    public Task<JobReceipt> SubmitEmbedAsync(string media, byte[] file, IReadOnlyDictionary<string, object?> data, RequestOptions? options = null, string? webhookId = null, CancellationToken cancellationToken = default)
    {
        if (data is null || data.Count == 0) throw new ArgumentException("data must be a non-empty JSON object", nameof(data));
        return SubmitAsync(media, file, JsonSerializer.Serialize(data), options, webhookId, cancellationToken);
    }

    /// <summary>Submits a background detection job and returns its receipt without waiting (<c>POST /watermarks/{media}/detect/async</c>).</summary>
    /// <param name="media"><c>images</c>, <c>documents</c> or <c>videos</c>.</param>
    /// <param name="file">File bytes (1 byte to 50 MB; PDFs and videos up to 20 MB).</param>
    /// <param name="options">Filename and idempotency key. Storage options do not apply to detection.</param>
    /// <param name="webhookId">Optional enabled webhook endpoint (<c>wh_…</c>) to notify on completion.</param>
    /// <param name="cancellationToken">Cancels the request; server work continues.</param>
    /// <returns>The job receipt.</returns>
    public Task<JobReceipt> SubmitDetectionAsync(string media, byte[] file, RequestOptions? options = null, string? webhookId = null, CancellationToken cancellationToken = default) => SubmitAsync(media, file, null, options, webhookId, cancellationToken);

    private async Task<JobReceipt> SubmitAsync(string media, byte[] file, string? data, RequestOptions? options, string? webhookId, CancellationToken ct)
    {
        if (media is not ("images" or "documents" or "videos")) throw new ArgumentException("media must be images, documents or videos", nameof(media));
        if (webhookId is not null) Check(webhookId, "wh_[a-f0-9]{32}", "webhook ID");
        options = Prepare(media, file, options, true);
        var r = await RequestAsync($"watermarks/{media}" + (data is null ? "/detect" : "") + "/async" + (webhookId is null ? "" : "?webhook_id=" + webhookId), file, data, options, true, data is null, ct).ConfigureAwait(false);
        return Parse<JobReceipt>(r.Bytes, 202, r.RequestId);
    }

    /// <summary>Reads a job's status without waiting (<c>GET /watermarks/jobs/{id}</c> or <c>/watermarks/detection-jobs/{id}</c>).</summary>
    /// <param name="requestId">Job ID (<c>req_…</c>).</param>
    /// <param name="detect">True for a detection job.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The current job receipt.</returns>
    public async Task<JobReceipt> GetJobAsync(string requestId, bool detect = false, CancellationToken cancellationToken = default)
    {
        if (!ValidJob(requestId)) throw new ArgumentException("Invalid request ID", nameof(requestId));
        var r = await RequestAsync($"watermarks/{(detect ? "detection-jobs" : "jobs")}/{requestId}", null, null, new(), false, detect, cancellationToken).ConfigureAwait(false);
        return Parse<JobReceipt>(r.Bytes, 200, r.RequestId);
    }

    /// <summary>Watermarks an image and waits for the verified result, polling if processing continues.</summary>
    /// <param name="file">Image bytes (1 byte to 50 MB).</param>
    /// <param name="data">Non-empty JSON object of forensic data.</param>
    /// <param name="options">Filename, idempotency key and optional storage destination.</param>
    /// <param name="cancellationToken">Cancels waiting; server work continues.</param>
    /// <returns>The verified watermarked file.</returns>
    public Task<EmbedResult> EmbedImageAsync(byte[] file, IReadOnlyDictionary<string, object?> data, RequestOptions? options = null, CancellationToken cancellationToken = default) => EmbedAsync("images", file, data, options, cancellationToken);
    /// <summary>Watermarks a PDF and waits for the verified result, polling if processing continues.</summary>
    /// <param name="file">PDF bytes (1 byte to 20 MB).</param>
    /// <param name="data">Non-empty JSON object of forensic data.</param>
    /// <param name="options">Filename, idempotency key and optional storage destination.</param>
    /// <param name="cancellationToken">Cancels waiting; server work continues.</param>
    /// <returns>The verified watermarked file.</returns>
    public Task<EmbedResult> EmbedDocumentAsync(byte[] file, IReadOnlyDictionary<string, object?> data, RequestOptions? options = null, CancellationToken cancellationToken = default) => EmbedAsync("documents", file, data, options, cancellationToken);
    /// <summary>Watermarks a video and waits for the verified result, polling if processing continues.</summary>
    /// <param name="file">MP4 or MOV bytes (1 byte to 20 MB).</param>
    /// <param name="data">Non-empty JSON object of forensic data.</param>
    /// <param name="options">Filename, idempotency key and optional storage destination.</param>
    /// <param name="cancellationToken">Cancels waiting; server work continues.</param>
    /// <returns>The verified watermarked file.</returns>
    public Task<EmbedResult> EmbedVideoAsync(byte[] file, IReadOnlyDictionary<string, object?> data, RequestOptions? options = null, CancellationToken cancellationToken = default) => EmbedAsync("videos", file, data, options, cancellationToken);
    /// <summary>Detects a watermark in an image synchronously.</summary>
    /// <param name="file">Image bytes (1 byte to 50 MB).</param>
    /// <param name="options">Filename and optional idempotency key.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The detection result.</returns>
    public Task<DetectionResult> DetectImageAsync(byte[] file, RequestOptions? options = null, CancellationToken cancellationToken = default) => DetectAsync("images", file, options, cancellationToken);
    /// <summary>Detects watermarks in a PDF synchronously, page by page.</summary>
    /// <param name="file">PDF bytes (1 byte to 20 MB).</param>
    /// <param name="options">Filename and optional idempotency key.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The detection result.</returns>
    public Task<DetectionResult> DetectDocumentAsync(byte[] file, RequestOptions? options = null, CancellationToken cancellationToken = default) => DetectAsync("documents", file, options, cancellationToken);
    /// <summary>Detects watermarks in a video, frame by frame, waiting for the durable job to finish.</summary>
    /// <param name="file">MP4 or MOV bytes (1 byte to 20 MB).</param>
    /// <param name="options">Filename and optional idempotency key.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The detection result.</returns>
    public Task<DetectionResult> DetectVideoAsync(byte[] file, RequestOptions? options = null, CancellationToken cancellationToken = default) => DetectAsync("videos", file, options, cancellationToken);

    /// <summary>Retrieves a saved embedding result (<c>GET /watermarks/jobs/{id}/result</c>), polling while the job is still processing (HTTP 202).</summary>
    /// <param name="requestId">Job ID (<c>req_…</c>).</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The verified watermarked file.</returns>
    /// <exception cref="EtchvException">HTTP 410 when the saved result expired or its asset was deleted (see <see cref="EtchvException.ErrorStatus"/>).</exception>
    public async Task<EmbedResult> GetEmbedResultAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (!ValidJob(requestId)) throw new ArgumentException("Invalid request ID", nameof(requestId));
        return Embedding(await RequestAsync($"watermarks/jobs/{requestId}/result", null, null, new(), true, false, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Retrieves a saved detection result (<c>GET /watermarks/detection-jobs/{id}/result</c>), polling while processing (HTTP 202).</summary>
    /// <param name="requestId">Job ID (<c>req_…</c>).</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The detection result.</returns>
    /// <exception cref="EtchvException">HTTP 410 when the saved result expired.</exception>
    public async Task<DetectionResult> GetDetectionResultAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (!ValidJob(requestId)) throw new ArgumentException("Invalid request ID", nameof(requestId));
        return Detection(await RequestAsync($"watermarks/detection-jobs/{requestId}/result", null, null, new(), true, true, cancellationToken).ConfigureAwait(false));
    }

    private async Task<EmbedResult> EmbedAsync(string media, byte[] file, IReadOnlyDictionary<string, object?> data, RequestOptions? options, CancellationToken ct)
    {
        if (data is null || data.Count == 0) throw new ArgumentException("data must be a non-empty JSON object", nameof(data));
        options = Prepare(media, file, options, true);
        return Embedding(await RequestAsync($"watermarks/{media}", file, JsonSerializer.Serialize(data), options, true, false, ct).ConfigureAwait(false));
    }

    private async Task<DetectionResult> DetectAsync(string media, byte[] file, RequestOptions? options, CancellationToken ct)
    {
        bool durable = media == "videos";
        options = Prepare(media, file, options, durable);
        return Detection(await RequestAsync($"watermarks/{media}/detect", file, null, options, durable, durable, ct).ConfigureAwait(false));
    }

    private static RequestOptions Prepare(string media, byte[] file, RequestOptions? options, bool durable)
    {
        if (file is null || file.Length == 0 || file.Length > MaxFileSize) throw new ArgumentException("file must contain 1 byte to 50 MB", nameof(file));
        options ??= new();
        return options with
        {
            Filename = options.Filename ?? (media switch { "documents" => "document.pdf", "videos" => "video.mp4", _ => "image.png" }),
            IdempotencyKey = durable && string.IsNullOrEmpty(options.IdempotencyKey) ? Guid.NewGuid().ToString() : options.IdempotencyKey
        };
    }

    private sealed record Reply(byte[] Bytes, int Status, string? RequestId, string? WatermarkId, string ContentType, string Disposition, string? AssetId, string? SourceAssetId, string? StorageDeliveryId, string? RetryAfter);

    /// <summary>Sends one request, reading at most 256 MB. Never follows redirects.</summary>
    private async Task<Reply> SendAsync(HttpMethod method, string path, HttpContent? content, string? idempotencyKey, CancellationToken ct)
    {
        var target = new Uri(baseUrl + "/" + path);
        using var request = new HttpRequestMessage(method, target) { Content = content };
        request.Headers.TryAddWithoutValidation("X-API-Key", key);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (idempotencyKey is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        string? requestId = Header("X-Request-ID");
        if (response.RequestMessage?.RequestUri is { } final && final != target)
            throw new EtchvException((int)response.StatusCode, "Redirects are not followed; configure the HttpClient with AllowAutoRedirect = false", requestId, idempotencyKey);
        if (response.Content.Headers.ContentLength > MaxDownloadSize) throw new EtchvException((int)response.StatusCode, "Response exceeds 256 MB", requestId, idempotencyKey);
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920]; int n;
        while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + n > MaxDownloadSize) throw new EtchvException((int)response.StatusCode, "Response exceeds 256 MB", requestId, idempotencyKey);
            buffer.Write(chunk, 0, n);
        }
        return new(buffer.ToArray(), (int)response.StatusCode, requestId, Header("X-Watermark-ID"), response.Content.Headers.ContentType?.MediaType ?? "",
            response.Content.Headers.ContentDisposition?.ToString() ?? "", Header("X-Asset-ID"), Header("X-Source-Asset-ID"), Header("X-Storage-Delivery-ID"), Header("Retry-After"));
    }

    private static string Truncate(byte[] bytes) => Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, MaxDetail)));

    /// <summary>Single-attempt JSON request for management endpoints; applies the client deadline and maps failures to <see cref="EtchvException"/>.</summary>
    private async Task<Reply> ManageAsync(HttpMethod method, string path, string? json, CancellationToken caller)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller); deadline.CancelAfter(timeout);
        try
        {
            var content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json");
            var r = await SendAsync(method, path, content, null, deadline.Token).ConfigureAwait(false);
            if (r.Status is < 200 or > 299) throw new EtchvException(r.Status, Truncate(r.Bytes), r.RequestId);
            return r;
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { throw new EtchvException(0, "Client deadline exceeded"); }
        catch (Exception e) when (e is HttpRequestException or IOException) { throw new EtchvException(0, "Network error", null, null, e); }
    }

    /// <summary>Serializes an SDK-defined request body with snake_case names; user-supplied values use <see cref="JsonSerializer"/> defaults.</summary>
    private static string Body(object body) => JsonSerializer.Serialize(body, Json);

    private async Task<T> JsonAsync<T>(HttpMethod method, string path, string? json, CancellationToken ct)
    {
        var r = await ManageAsync(method, path, json, ct).ConfigureAwait(false);
        return Parse<T>(r.Bytes, r.Status, r.RequestId);
    }

    private static T Parse<T>(byte[] bytes, int status, string? requestId)
    {
        try { return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new EtchvException(status, "Invalid JSON response", requestId); }
    }

    private async Task<Reply> RequestAsync(string path, byte[]? file, string? data, RequestOptions options, bool durable, bool detectionJob, CancellationToken caller)
    {
        if (options.StorageKey is not null && options.StorageDestinationId is null) throw new ArgumentException("Storage key requires a storage destination ID");
        if (options.StorageDestinationId is not null)
        {
            if (data is null) throw new ArgumentException("Storage destinations apply to embedding only");
            Check(options.StorageDestinationId, "dst_[a-f0-9]{32}", "storage destination ID");
            path += (path.Contains('?') ? "&" : "?") + "storage_destination_id=" + Uri.EscapeDataString(options.StorageDestinationId);
            if (options.StorageKey is not null) path += "&storage_key=" + Uri.EscapeDataString(options.StorageKey);
        }
        if (options.IdempotencyKey is not null) Check(options.IdempotencyKey, "[A-Za-z0-9_-]{8,128}", "idempotency key (use 8–128 letters, digits, hyphens or underscores)");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        deadline.CancelAfter(timeout); var ct = deadline.Token; string? requestId = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                MultipartFormDataContent? form = null;
                if (file is not null)
                {
                    form = new MultipartFormDataContent { { new ByteArrayContent(file), "file", options.Filename ?? "file" } };
                    if (data is not null) form.Add(new StringContent(data, Encoding.UTF8), "data");
                }
                try
                {
                    var r = await SendAsync(file is null ? HttpMethod.Get : HttpMethod.Post, path, form, options.IdempotencyKey, ct).ConfigureAwait(false);
                    requestId = r.RequestId ?? requestId;
                    r = r with { RequestId = requestId };
                    if (r.Status == 200 || (r.Status == 202 && path.Split('?')[0].EndsWith("/async", StringComparison.Ordinal))) return r;
                    JsonElement detail = default;
                    try { using var document = JsonDocument.Parse(r.Bytes); detail = document.RootElement.Clone(); } catch (JsonException) { }
                    if (durable && r.Status == 202)
                    {
                        if (detail.ValueKind != JsonValueKind.Object || !detail.TryGetProperty("request_id", out var id) || id.ValueKind != JsonValueKind.String || !ValidJob(id.GetString()))
                            throw new EtchvException(202, "Invalid job response", requestId, options.IdempotencyKey);
                        requestId = id.GetString();
                        // Poll a path built from the validated ID, never from server-provided URLs.
                        path = $"watermarks/{(detectionJob ? "detection-jobs" : "jobs")}/{requestId}/result"; file = null; data = null;
                        double seconds = double.TryParse(r.RetryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var delay) && double.IsFinite(delay) ? Math.Clamp(delay, .01, 5) : 1;
                        await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false); continue;
                    }
                    bool failed = detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("status", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "failed";
                    if (durable && r.Status is 429 or 502 or 503 or 504 && !failed) { await Task.Delay(1000, ct).ConfigureAwait(false); continue; }
                    throw new EtchvException(r.Status, Truncate(r.Bytes), requestId, options.IdempotencyKey);
                }
                catch (Exception e) when (e is HttpRequestException or IOException)
                {
                    if (!durable) throw new EtchvException(0, "Network error", requestId, options.IdempotencyKey, e);
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
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
        return new(r.Bytes, r.WatermarkId!, r.RequestId, r.ContentType, match.Success ? match.Groups[1].Value : $"watermarked.{ext}", r.AssetId, r.SourceAssetId, r.StorageDeliveryId);
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
