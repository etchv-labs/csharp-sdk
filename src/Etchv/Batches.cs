using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
namespace Etchv;

/// <summary>One file to watermark in a batch, with its own forensic data.</summary>
public sealed class BatchItem
{
    /// <summary>A file held in memory.</summary>
    /// <param name="filename">The file's name (1–255 characters); its extension sets the media type.</param>
    /// <param name="file">File bytes (images and PDFs up to 50 MB; DOCX, PPTX and videos up to 20 MB).</param>
    /// <param name="data">Non-empty JSON object of forensic data for this file.</param>
    public BatchItem(string filename, byte[] file, IReadOnlyDictionary<string, object?> data)
    {
        Filename = filename; File = file ?? throw new ArgumentNullException(nameof(file)); Data = data;
    }

    private BatchItem(string filename, string path, IReadOnlyDictionary<string, object?> data)
    {
        Filename = filename; Path = path; Data = data;
    }

    /// <summary>A file on disk, read only while it uploads.</summary>
    /// <param name="path">Path of the file.</param>
    /// <param name="data">Non-empty JSON object of forensic data for this file.</param>
    /// <param name="filename">The name to record; defaults to the path's file name.</param>
    /// <returns>The batch item.</returns>
    public static BatchItem FromPath(string path, IReadOnlyDictionary<string, object?> data, string? filename = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new(filename ?? System.IO.Path.GetFileName(path), path, data);
    }

    /// <summary>The file's name; its extension sets the media type.</summary>
    public string Filename { get; }
    /// <summary>The forensic data for this file.</summary>
    public IReadOnlyDictionary<string, object?> Data { get; }
    /// <summary>File bytes, or null for a file on disk.</summary>
    public byte[]? File { get; }
    /// <summary>Path of a file on disk, or null for bytes.</summary>
    public string? Path { get; }

    internal long Size => File?.LongLength ?? new FileInfo(Path!).Length;
    internal Stream Open() => File is not null ? new MemoryStream(File, false) : System.IO.File.OpenRead(Path!);
}

/// <summary>Options for <see cref="EtchvClient.SubmitBatchAsync"/> and <see cref="EtchvClient.SubmitBatchZipAsync"/>.</summary>
/// <param name="Archive">Also zip every result into one download (<see cref="EtchvClient.DownloadBatchArchiveAsync(string, Stream, TimeSpan?, CancellationToken)"/>).</param>
/// <param name="WebhookId">Enabled webhook endpoint (<c>wh_…</c>) that receives one <c>watermark.batch.*</c> event when the batch ends.</param>
/// <param name="Accelerator">Processing hardware for every file; omitted by default (CPU).</param>
/// <param name="StorageDestinationId">Verified customer storage destination (<c>dst_…</c>) for every result. Not with <paramref name="Archive"/>.</param>
/// <param name="IdempotencyKey">8–128 letters, digits, hyphens or underscores. Generated when omitted; submitting again with the same key resumes the same batch.</param>
/// <param name="UploadConcurrency">Files uploaded at the same time (default 4).</param>
public sealed record BatchOptions(bool Archive = false, string? WebhookId = null, Accelerator? Accelerator = null, string? StorageDestinationId = null,
    string? IdempotencyKey = null, int UploadConcurrency = 4)
{
    /// <summary>
    /// For every file, as for <see cref="RequestOptions.ActiveContent"/>; omitted by default (<see cref="Etchv.ActiveContent.Keep"/>).
    /// With <see cref="Etchv.ActiveContent.RemoveAutoRun"/> every file must be a PDF (HTTP 422 otherwise).
    /// </summary>
    public ActiveContent? ActiveContent { get; init; }
}

/// <summary>One zip member in <see cref="EtchvClient.SubmitBatchZipAsync"/>.</summary>
/// <param name="Filename">The member's path, exactly as stored in the zip.</param>
/// <param name="Data">Non-empty JSON object of forensic data for this file.</param>
public sealed record BatchZipItem(string Filename, IReadOnlyDictionary<string, object?> Data);

/// <summary>Item counts of a batch.</summary>
/// <param name="Pending">Not yet admitted.</param>
/// <param name="Accepted">Admitted and charged.</param>
/// <param name="Rejected">Rejected at admission, never charged.</param>
/// <param name="Succeeded">Watermarked.</param>
/// <param name="Failed">Failed after admission and refunded.</param>
/// <param name="InProgress">Accepted and not yet finished.</param>
public sealed record BatchCounts(int Pending, int Accepted, int Rejected, int Succeeded, int Failed, int InProgress);

/// <summary>Credits of a batch.</summary>
/// <param name="Reserved">Reserved for accepted files.</param>
/// <param name="Charged">Charged for succeeded files.</param>
/// <param name="Refunded">Refunded for failed or canceled files.</param>
public sealed record BatchCredits(int Reserved, int Charged, int Refunded);

/// <summary>Where a pending file is uploaded.</summary>
/// <param name="Method">Always <c>PUT</c>.</param>
/// <param name="Url">Signed upload URL; it carries its own authorization.</param>
/// <param name="ExpiresAt">When the URL expires (ISO 8601).</param>
public sealed record BatchUpload(string Method, string Url, string? ExpiresAt);

/// <summary>One file of a batch and its state.</summary>
/// <param name="Index">Zero-based position in the batch.</param>
/// <param name="Filename">The file's name.</param>
/// <param name="Size">Declared size in bytes.</param>
/// <param name="UploadId">Upload session ID (<c>upl_…</c>), when the file is uploaded separately.</param>
/// <param name="RequestId">Job ID (<c>req_…</c>) once the file is accepted.</param>
/// <param name="Status"><c>pending</c>, <c>rejected</c>, <c>queued</c>, <c>running</c>, <c>retrying</c>, <c>succeeded</c> or <c>failed</c>.</param>
/// <param name="ErrorCode">Why the file was rejected or failed, such as <c>upload_not_received</c> or <c>insufficient_credits</c>.</param>
/// <param name="ErrorDetail">Human-readable detail for <paramref name="ErrorCode"/>.</param>
/// <param name="Credits">Credits charged or reserved (0 when failed and refunded), or null before acceptance.</param>
/// <param name="StatusUrl">Relative job status URL, once accepted.</param>
/// <param name="ResultUrl">Relative job result URL, once accepted.</param>
/// <param name="ResultExpiresAt">When the result expires (ISO 8601).</param>
/// <param name="Upload">Signed upload target, only on pending files of a draft batch that have not arrived yet.</param>
/// <param name="UploadReceived">True on a draft batch's file that has already arrived (it has no <paramref name="Upload"/>); null or false otherwise.</param>
public sealed record BatchItemStatus(int Index, string Filename, long? Size, string? UploadId, string? RequestId, string Status,
    string? ErrorCode, string? ErrorDetail, int? Credits, string? StatusUrl = null, string? ResultUrl = null, string? ResultExpiresAt = null,
    BatchUpload? Upload = null, bool? UploadReceived = null);

/// <summary>A batch of up to 100 files.</summary>
/// <param name="BatchId">Batch ID (<c>bat_…</c>).</param>
/// <param name="Status"><c>draft</c>, <c>starting</c>, <c>processing</c>, <c>assembling</c>, <c>completed</c>, <c>failed</c>, <c>cancelled</c> or <c>expired</c>.</param>
/// <param name="ItemCount">Number of files.</param>
/// <param name="Archive">Whether a zip of all results was requested.</param>
/// <param name="Accelerator">Requested processing hardware.</param>
/// <param name="WebhookId">Webhook endpoint notified when the batch ends, if any.</param>
/// <param name="StorageDestinationId">Customer storage destination, if any.</param>
/// <param name="Counts">Item counts.</param>
/// <param name="Credits">Credits reserved, charged and refunded.</param>
/// <param name="CancelRequested">Whether the batch was canceled.</param>
/// <param name="CreatedAt">ISO 8601 creation time.</param>
/// <param name="StartedAt">ISO 8601 start time, or null.</param>
/// <param name="CompletedAt">ISO 8601 end time, or null.</param>
/// <param name="UploadExpiresAt">Deadline to start the batch (24 hours after creation).</param>
/// <param name="StatusUrl">Relative status URL.</param>
/// <param name="ArchiveStatus">State of the zip when <paramref name="Archive"/> is true.</param>
/// <param name="ArchiveUrl">Relative zip download URL when <paramref name="Archive"/> is true.</param>
/// <param name="ArchiveExpiresAt">When the zip expires, once ready.</param>
/// <param name="Items">Every file in index order; empty in <see cref="EtchvClient.ListBatchesAsync"/>.</param>
public sealed record Batch(string BatchId, string Status, int ItemCount, bool Archive, Accelerator? Accelerator, string? WebhookId,
    string? StorageDestinationId, BatchCounts Counts, BatchCredits Credits, bool CancelRequested, string CreatedAt, string? StartedAt,
    string? CompletedAt, string UploadExpiresAt, string StatusUrl, string? ArchiveStatus = null, string? ArchiveUrl = null,
    string? ArchiveExpiresAt = null, IReadOnlyList<BatchItemStatus>? Items = null)
{
    /// <summary>Every file in index order; empty in <see cref="EtchvClient.ListBatchesAsync"/>.</summary>
    public IReadOnlyList<BatchItemStatus> Items { get; init; } = Items ?? [];
    /// <summary>What the batch's PDFs keep of their scripts and actions, or null when not reported.</summary>
    public ActiveContent? ActiveContent { get; init; }
    /// <summary>True when the batch is <c>completed</c>, <c>failed</c>, <c>cancelled</c> or <c>expired</c>.</summary>
    public bool IsTerminal => Status is "completed" or "failed" or "cancelled" or "expired";
}

/// <summary>A page of batches, newest first.</summary>
/// <param name="Data">Batches on this page, without items.</param>
/// <param name="NextCursor">Pass as <c>before</c> for the next page, or null on the last page.</param>
public sealed record BatchPage(IReadOnlyList<Batch> Data, string? NextCursor);

/// <summary>The outcome of one batch file.</summary>
/// <param name="Index">Zero-based position in the batch.</param>
/// <param name="Filename">The file's name.</param>
/// <param name="Status">The file's final status.</param>
/// <param name="RequestId">Job ID, once accepted.</param>
/// <param name="ErrorCode">
/// Why the file did not succeed (its credits are refunded): the file's own error code, else <c>cancelled</c> or
/// <c>expired</c> for a canceled or expired batch, else the file's status.
/// </param>
/// <param name="ErrorDetail">Human-readable detail for <paramref name="ErrorCode"/>.</param>
/// <param name="Credits">Credits charged, or 0 or null when none.</param>
/// <param name="Result">The watermarked file, for succeeded files.</param>
/// <param name="Error">Why a succeeded file's result could not be downloaded (for example HTTP 410 after 24 hours).</param>
public sealed record BatchItemResult(int Index, string Filename, string Status, string? RequestId, string? ErrorCode, string? ErrorDetail,
    int? Credits, EmbedResult? Result = null, EtchvException? Error = null)
{
    /// <summary>True when <see cref="Result"/> holds the watermarked file.</summary>
    public bool Ok => Result is not null;
}

public sealed partial class EtchvClient
{
    /// <summary>Most files in one batch (100).</summary>
    public const int MaxBatchItems = 100;
    /// <summary>Largest zip <see cref="SubmitBatchZipAsync"/> sends (55 MB).</summary>
    public const int MaxBatchZipSize = 55 * 1024 * 1024;
    /// <summary>Largest batch archive the SDK downloads: 1 GB of results plus room for the zip structure and manifest (64 MB).</summary>
    public const long MaxBatchArchiveSize = 1024L * 1024 * 1024 + 64L * 1024 * 1024;
    private static readonly TimeSpan DefaultBatchWait = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultBatchPoll = TimeSpan.FromSeconds(2), MinBatchPoll = TimeSpan.FromSeconds(1);

    private static string BatchPath(string batchId) => "watermarks/batches/" + Check(batchId, "bat_[a-f0-9]{32}", "batch ID");

    /// <summary>
    /// Creates a batch of up to 100 files, uploads each one to its signed URL (never with the API key, up to
    /// <see cref="BatchOptions.UploadConcurrency"/> at a time) and starts it (<c>POST /watermarks/batches</c>, PUT per file,
    /// <c>POST /watermarks/batches/{id}/start</c>). Wait with <see cref="WaitForBatchAsync"/>.
    /// </summary>
    /// <param name="items">1–100 files, each with its own data.</param>
    /// <param name="options">Archive, webhook, accelerator, storage destination, idempotency key and upload concurrency.</param>
    /// <param name="cancellationToken">Cancels the call; files already uploaded stay with the draft batch.</param>
    /// <returns>
    /// The started batch (status <c>starting</c>). A replay of a batch that already started is returned as it is,
    /// without uploading or starting anything.
    /// </returns>
    /// <exception cref="ArgumentException">More than 100 or no files, or an invalid item or option. Nothing is sent.</exception>
    /// <exception cref="EtchvException">
    /// The API refused the batch, an upload failed, or the replayed batch expired before it was started. After a failed
    /// upload, submitting again with the same <see cref="EtchvException.IdempotencyKey"/> resumes the same batch,
    /// uploading only the files it has not received.
    /// </exception>
    public async Task<Batch> SubmitBatchAsync(IReadOnlyList<BatchItem> items, BatchOptions? options = null, CancellationToken cancellationToken = default)
    {
        options = PrepareBatch(items?.Count ?? 0, options);
        if (options.UploadConcurrency < 1) throw new ArgumentException("UploadConcurrency must be at least 1", nameof(options));
        var sizes = new long[items!.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i] ?? throw new ArgumentException($"Item {i} is null", nameof(items));
            CheckBatchItem(item.Filename, item.Data, i);
            sizes[i] = item.Size;
            if (sizes[i] == 0) throw new ArgumentException($"Item {i} ({item.Filename}) is empty", nameof(items));
        }
        var body = BatchBody(options);
        body["items"] = items.Select((item, i) => new Dictionary<string, object?> { ["filename"] = item.Filename, ["size"] = sizes[i], ["data"] = item.Data }).ToList();
        string json = JsonSerializer.Serialize(body);
        string key = options.IdempotencyKey!;
        var created = await BatchCallAsync(HttpMethod.Post, "watermarks/batches", () => new StringContent(json, Encoding.UTF8, "application/json"), key, false, cancellationToken).ConfigureAwait(false);
        var batch = Parse<Batch>(created.Bytes, created.Status, created.RequestId);
        var path = BatchPath(batch.BatchId);
        if (batch.Status == "expired")
        {
            var expired = new { detail = new { code = "batch_expired", batch_id = batch.BatchId, message = $"Batch {batch.BatchId} expired before it was started (24 hours); submit with a new idempotency key" } };
            throw new EtchvException(410, JsonSerializer.Serialize(expired), created.RequestId, key);
        }
        if (batch.Status != "draft") return batch; // A replay of a batch that already started.
        var pending = new List<(BatchItem Item, BatchItemStatus Status, Uri Target)>();
        foreach (var status in batch.Items)
        {
            if (status.UploadReceived == true) continue;
            if (status.Upload is not { } upload)
                throw new EtchvException(created.Status, $"Invalid batch upload response: item {status.Index} ({status.Filename}) has no upload URL and was not received", created.RequestId, key);
            if (status.Index < 0 || status.Index >= items.Count || upload.Method != "PUT" || !Uri.TryCreate(upload.Url, UriKind.Absolute, out var target) || target.Scheme != "https")
                throw new EtchvException(created.Status, "Invalid batch upload response", created.RequestId, key);
            pending.Add((items[status.Index], status, target));
        }
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = options.UploadConcurrency, CancellationToken = cancellationToken }, async (upload, ct) =>
        {
            string which = $"Upload of item {upload.Status.Index} ({upload.Item.Filename}) to batch {batch.BatchId} failed";
            string resume = "; submit again with the same idempotency key to resume";
            try { await PutSignedAsync(upload.Target, upload.Item.Open, sizes[upload.Status.Index], ct).ConfigureAwait(false); }
            catch (EtchvException e) { throw new EtchvException(e.StatusCode, $"{which}: {e.Detail}{resume}", e.RequestId, key, e); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FileChangedException)
            { throw new EtchvException(0, $"{which}: {e.Message}{resume}", null, key, e); }
        }).ConfigureAwait(false);
        Reply started;
        try { started = await BatchCallAsync(HttpMethod.Post, path + "/start", null, null, true, cancellationToken).ConfigureAwait(false); }
        catch (EtchvException e) when (e.IdempotencyKey is null)
        {
            // Carry the key so the caller can resubmit (the start call itself sends none).
            throw new EtchvException(e.StatusCode, e.Detail, e.RequestId, key, e.InnerException) { RetryAfter = e.RetryAfter };
        }
        return Parse<Batch>(started.Bytes, started.Status, started.RequestId);
    }

    /// <summary>
    /// Creates and starts a batch from one zip and a manifest naming every member (<c>POST /watermarks/batches/zip</c>),
    /// for files already together. There is no separate upload or start step.
    /// </summary>
    /// <param name="zip">Zip bytes (up to 55 MB); every member must be listed in <paramref name="items"/> and vice versa.</param>
    /// <param name="items">1–100 members, each with its own data.</param>
    /// <param name="options">Archive, webhook, accelerator, storage destination and idempotency key.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The batch, already <c>starting</c>.</returns>
    /// <exception cref="ArgumentException">An empty or oversized zip, no or more than 100 items, or an invalid option.</exception>
    public async Task<Batch> SubmitBatchZipAsync(byte[] zip, IReadOnlyList<BatchZipItem> items, BatchOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (zip is null || zip.Length == 0 || zip.Length > MaxBatchZipSize) throw new ArgumentException("zip must contain 1 byte to 55 MB", nameof(zip));
        options = PrepareBatch(items?.Count ?? 0, options);
        for (int i = 0; i < items!.Count; i++)
        {
            var item = items[i] ?? throw new ArgumentException($"Item {i} is null", nameof(items));
            CheckBatchItem(item.Filename, item.Data, i);
        }
        var manifest = BatchBody(options);
        manifest["items"] = items.Select(item => new Dictionary<string, object?> { ["filename"] = item.Filename, ["data"] = item.Data }).ToList();
        string json = JsonSerializer.Serialize(manifest);
        HttpContent Form()
        {
            var file = new ByteArrayContent(zip); file.Headers.ContentType = new("application/zip");
            return new MultipartFormDataContent { { file, "archive", "batch.zip" }, { new StringContent(json, Encoding.UTF8), "manifest" } };
        }
        var r = await BatchCallAsync(HttpMethod.Post, "watermarks/batches/zip", Form, options.IdempotencyKey, false, cancellationToken).ConfigureAwait(false);
        return Parse<Batch>(r.Bytes, r.Status, r.RequestId);
    }

    /// <summary>Reads a batch with every file's status (<c>GET /watermarks/batches/{id}</c>).</summary>
    /// <param name="batchId">Batch ID (<c>bat_…</c>).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The batch.</returns>
    public async Task<Batch> GetBatchAsync(string batchId, CancellationToken cancellationToken = default)
    {
        var r = await BatchCallAsync(HttpMethod.Get, BatchPath(batchId), null, null, true, cancellationToken).ConfigureAwait(false);
        return Parse<Batch>(r.Bytes, r.Status, r.RequestId);
    }

    /// <summary>
    /// Polls a batch until it is <c>completed</c>, <c>failed</c>, <c>cancelled</c> or <c>expired</c>, one request per poll,
    /// waiting as long as the API's <c>Retry-After</c> asks, at least 1 second (2 seconds when it sends none).
    /// </summary>
    /// <param name="batchId">Batch ID (<c>bat_…</c>).</param>
    /// <param name="timeout">How long to wait (default 1 hour). Each request is still bounded by the client timeout.</param>
    /// <param name="cancellationToken">Cancels waiting; the batch continues.</param>
    /// <returns>The finished batch.</returns>
    /// <exception cref="EtchvException">Status 0 with an inner <see cref="TimeoutException"/> when the batch is still running after <paramref name="timeout"/>.</exception>
    public async Task<Batch> WaitForBatchAsync(string batchId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var path = BatchPath(batchId);
        var wait = CheckWait(timeout);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var r = await BatchCallAsync(HttpMethod.Get, path, null, null, true, cancellationToken).ConfigureAwait(false);
            var batch = Parse<Batch>(r.Bytes, r.Status, r.RequestId);
            if (batch.IsTerminal) return batch;
            var remaining = wait - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new EtchvException(0, $"Batch {batch.BatchId} is still {batch.Status} after {wait}", r.RequestId, null, new TimeoutException());
            await Task.Delay(Min(PollDelay(r.RetryAfter), remaining), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for a batch to finish (like <see cref="WaitForBatchAsync"/>), then yields each file's outcome in order,
    /// downloading the watermarked result of each succeeded file as you advance (<c>GET /watermarks/jobs/{id}/result</c>).
    /// Other files carry <see cref="BatchItemResult.ErrorCode"/>; a result that cannot be downloaded carries
    /// <see cref="BatchItemResult.Error"/>. Results are kept for 24 hours.
    /// </summary>
    /// <param name="batchId">Batch ID (<c>bat_…</c>).</param>
    /// <param name="timeout">How long to wait for the batch to finish (default 1 hour).</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>One result per file.</returns>
    /// <exception cref="EtchvException">Status 0 with an inner <see cref="TimeoutException"/> when the batch is still running after <paramref name="timeout"/>.</exception>
    public IAsyncEnumerable<BatchItemResult> IterBatchResultsAsync(string batchId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        BatchPath(batchId);
        CheckWait(timeout);
        return BatchResults(batchId, timeout, cancellationToken);
    }

    private async IAsyncEnumerable<BatchItemResult> BatchResults(string batchId, TimeSpan? timeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var batch = await WaitForBatchAsync(batchId, timeout, cancellationToken).ConfigureAwait(false);
        foreach (var item in batch.Items.OrderBy(i => i.Index))
        {
            var outcome = new BatchItemResult(item.Index, item.Filename, item.Status, item.RequestId, item.ErrorCode, item.ErrorDetail, item.Credits);
            if (item.Status == "succeeded" && item.RequestId is { } requestId)
            {
                try { outcome = outcome with { Result = await GetEmbedResultAsync(requestId, cancellationToken).ConfigureAwait(false) }; }
                catch (EtchvException e) { outcome = outcome with { Error = e }; }
            }
            else
                outcome = outcome with { ErrorCode = item.ErrorCode ?? (batch.Status is "cancelled" or "expired" ? batch.Status : item.Status) };
            yield return outcome;
        }
    }

    /// <summary>
    /// Waits for and downloads the zip of every result (<c>GET /watermarks/batches/{id}/archive</c>) of a batch created with
    /// <see cref="BatchOptions.Archive"/>. For large archives prefer the overload that writes to a <see cref="Stream"/>.
    /// </summary>
    /// <param name="batchId">Batch ID (<c>bat_…</c>).</param>
    /// <param name="timeout">How long to wait for the archive to be ready (default 1 hour).</param>
    /// <param name="cancellationToken">Cancels waiting and the download.</param>
    /// <returns>The zip: each result plus <c>manifest.json</c>.</returns>
    /// <exception cref="EtchvException">See <see cref="DownloadBatchArchiveAsync(string, Stream, TimeSpan?, CancellationToken)"/>.</exception>
    public async Task<byte[]> DownloadBatchArchiveAsync(string batchId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await DownloadBatchArchiveAsync(batchId, buffer, timeout, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>
    /// Waits for the zip of every result of a batch created with <see cref="BatchOptions.Archive"/> and writes it to
    /// <paramref name="destination"/> (<c>GET /watermarks/batches/{id}/archive</c>). While the batch runs the API answers
    /// HTTP 202 and the SDK waits as long as its <c>Retry-After</c> asks (at least 1 second), up to <paramref name="timeout"/>. The download
    /// itself has no total deadline; it fails only if no data arrives for the client timeout.
    /// </summary>
    /// <remarks>
    /// The archive route does not resume a download. When the connection drops or stalls mid-download and
    /// <paramref name="timeout"/> has not passed, a seekable <paramref name="destination"/> is cut back to the position it
    /// had when the call began and the zip is streamed again. A stream that cannot seek (a network or pipe stream) has
    /// already passed the bytes on, so the error is thrown instead.
    /// </remarks>
    /// <param name="batchId">Batch ID (<c>bat_…</c>).</param>
    /// <param name="destination">Writable stream that receives the zip; it is not closed.</param>
    /// <param name="timeout">How long to wait for the archive to be ready (default 1 hour).</param>
    /// <param name="cancellationToken">Cancels waiting and the download.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="EtchvException">
    /// HTTP 409 with <see cref="EtchvException.Code"/> <c>batch_not_started</c>, <c>archive_not_requested</c>,
    /// <c>archive_too_large</c> or <c>archive_unavailable</c>; HTTP 410 for an expired batch or after 24 hours. Status 0
    /// with an inner <see cref="TimeoutException"/> when the archive is still not ready after <paramref name="timeout"/>
    /// (<see cref="EtchvException.Detail"/> "Client deadline exceeded; the archive is not ready yet …"), when the download
    /// stalls ("The archive download stalled: no data for …"), or when a request's response headers do not arrive within
    /// the client timeout and <paramref name="timeout"/> has passed ("Client deadline exceeded waiting for the archive
    /// response"; earlier header timeouts are retried). Status 0 with the transport exception inside for a network
    /// error that outlasts <paramref name="timeout"/> ("Network error"), or that cuts off the download of a stream that
    /// cannot seek ("Network error during the archive download"). Exceptions from <paramref name="destination"/>
    /// propagate unchanged.
    /// </exception>
    public async Task<long> DownloadBatchArchiveAsync(string batchId, Stream destination, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var path = BatchPath(batchId) + "/archive";
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("destination must be writable", nameof(destination));
        var wait = CheckWait(timeout);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var target = new Uri(baseUrl + "/" + path);
        long? start = destination.CanSeek ? destination.Position : null;
        while (true)
        {
            TimeSpan delay;
            using (var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                // Each request is bounded by the client timeout until its headers arrive; waiting between requests by the wait timeout.
                headers.CancelAfter(this.timeout);
                HttpResponseMessage response;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, target);
                    request.Headers.TryAddWithoutValidation("X-API-Key", key);
                    request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                    response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is HttpRequestException or IOException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    if (clock.Elapsed >= wait)
                        throw e is OperationCanceledException
                            ? new EtchvException(0, "Client deadline exceeded waiting for the archive response", null, null, new TimeoutException(null, e))
                            : new EtchvException(0, "Network error", null, null, e);
                    await Task.Delay(Min(TimeSpan.FromSeconds(1), wait - clock.Elapsed), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                using (response)
                {
                    headers.CancelAfter(Timeout.InfiniteTimeSpan);
                    string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
                    string? requestId = Header("X-Request-ID"), retryAfter = Header("Retry-After");
                    int status = (int)response.StatusCode;
                    if (response.RequestMessage?.RequestUri is { } final && final != target)
                        throw new EtchvException(status, "Redirects are not followed; configure the HttpClient with AllowAutoRedirect = false", requestId);
                    if (status == 200)
                    {
                        try { return await CopyArchiveAsync(response, destination, requestId, cancellationToken).ConfigureAwait(false); }
                        catch (EtchvException e) when (e.StatusCode == 0 && start is not null && clock.Elapsed < wait)
                        {
                            // The archive route does not serve Range requests: drop what was written and start over.
                            if (!Rewind(destination, start.Value)) throw;
                        }
                        await Task.Delay(Min(TimeSpan.FromSeconds(1), wait - clock.Elapsed), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    byte[] body;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        idle.CancelAfter(this.timeout);
                        try { body = await response.Content.ReadAsByteArrayAsync(idle.Token).ConfigureAwait(false); }
                        catch (Exception e) when (e is HttpRequestException or IOException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested)) { body = []; }
                    }
                    bool waiting = status == 202, transient = status is 429 or 502 or 503 or 504;
                    if (!waiting && !transient) throw new EtchvException(status, Truncate(body), requestId) { RetryAfter = ParseRetryAfter(retryAfter) };
                    if (clock.Elapsed >= wait)
                    {
                        if (waiting) throw new EtchvException(0, $"Client deadline exceeded; the archive is not ready yet after {wait}", requestId, null, new TimeoutException());
                        throw new EtchvException(status, Truncate(body), requestId) { RetryAfter = ParseRetryAfter(retryAfter) };
                    }
                    delay = PollDelay(retryAfter);
                }
            }
            await Task.Delay(Min(delay, wait - clock.Elapsed), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Streams a 200 archive body to <paramref name="destination"/> under an idle timeout that runs only while reading
    /// (time spent writing to the destination does not count), checking its size and zip signature.
    /// </summary>
    private async Task<long> CopyArchiveAsync(HttpResponseMessage response, Stream destination, string? requestId, CancellationToken caller)
    {
        string tooLarge = JsonSerializer.Serialize(new { detail = new { code = "archive_too_large", message = $"The archive exceeds the SDK's {MaxBatchArchiveSize / (1024 * 1024)} MB download limit" } });
        if (response.Content.Headers.ContentLength > MaxBatchArchiveSize) throw new EtchvException(200, tooLarge, requestId);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(caller);
        bool writing = false;
        try
        {
            idle.CancelAfter(timeout);
            using var source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            var chunk = new byte[81920];
            long total = 0;
            int buffered = 0;
            while (true)
            {
                idle.CancelAfter(timeout);
                int n = await source.ReadAsync(chunk.AsMemory(buffered), idle.Token).ConfigureAwait(false);
                // Hold back the first byte until the second arrives, to check the zip signature before writing.
                if (total == 0 && n > 0 && buffered + n < 2) { buffered += n; continue; }
                n += buffered; buffered = 0;
                if (n == 0) break;
                if (total == 0 && (chunk[0] != (byte)'P' || chunk[1] != (byte)'K')) throw new EtchvException(200, "Invalid archive response", requestId);
                if (total + n > MaxBatchArchiveSize) throw new EtchvException(200, tooLarge, requestId);
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                writing = true;
                await destination.WriteAsync(chunk.AsMemory(0, n), caller).ConfigureAwait(false);
                writing = false;
                total += n;
            }
            if (total == 0) throw new EtchvException(200, "Invalid archive response", requestId);
            return total;
        }
        catch (OperationCanceledException e) when (!writing && !caller.IsCancellationRequested)
        { throw new EtchvException(0, $"The archive download stalled: no data for {timeout}", requestId, null, new TimeoutException(null, e)); }
        catch (Exception e) when (!writing && e is HttpRequestException or IOException)
        { throw new EtchvException(0, "Network error during the archive download", requestId, null, e); }
    }

    /// <summary>
    /// Cancels a batch (<c>POST /watermarks/batches/{id}/cancel</c>): files not yet running are refunded and marked
    /// cancelled; files already running finish.
    /// </summary>
    /// <param name="batchId">Batch ID (<c>bat_…</c>).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The batch.</returns>
    public async Task<Batch> CancelBatchAsync(string batchId, CancellationToken cancellationToken = default)
    {
        var r = await BatchCallAsync(HttpMethod.Post, BatchPath(batchId) + "/cancel", null, null, true, cancellationToken).ConfigureAwait(false);
        return Parse<Batch>(r.Bytes, r.Status, r.RequestId);
    }

    /// <summary>Lists batches, newest first, without their files (<c>GET /watermarks/batches</c>).</summary>
    /// <param name="limit">Page size, 1–50 (API default 20).</param>
    /// <param name="before">A <see cref="BatchPage.NextCursor"/> to continue from.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A page of batches.</returns>
    public async Task<BatchPage> ListBatchesAsync(int? limit = null, string? before = null, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 50) throw new ArgumentException("limit must be 1–50", nameof(limit));
        var query = new List<string>();
        if (limit is { } n) query.Add("limit=" + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (before is not null) query.Add("before=" + Check(before, "bat_[a-f0-9]{32}", "batch cursor"));
        var r = await BatchCallAsync(HttpMethod.Get, "watermarks/batches" + (query.Count == 0 ? "" : "?" + string.Join("&", query)), null, null, true, cancellationToken).ConfigureAwait(false);
        return Parse<BatchPage>(r.Bytes, r.Status, r.RequestId);
    }

    private static BatchOptions PrepareBatch(int count, BatchOptions? options)
    {
        if (count is < 1 or > MaxBatchItems) throw new ArgumentException($"A batch takes 1 to {MaxBatchItems} files; split larger sets into several batches");
        options ??= new();
        if (options.WebhookId is not null) Check(options.WebhookId, "wh_[a-f0-9]{32}", "webhook ID");
        if (options.StorageDestinationId is not null) Check(options.StorageDestinationId, "dst_[a-f0-9]{32}", "storage destination ID");
        if (options.Accelerator is { } accelerator) Wire(accelerator);
        if (options.ActiveContent is { } activeContent) Wire(activeContent);
        if (options.IdempotencyKey is not null) Check(options.IdempotencyKey, "[A-Za-z0-9_-]{8,128}", "idempotency key (use 8–128 letters, digits, hyphens or underscores)");
        return options with { IdempotencyKey = options.IdempotencyKey ?? Guid.NewGuid().ToString() };
    }

    private static void CheckBatchItem(string? filename, IReadOnlyDictionary<string, object?>? data, int index)
    {
        if (string.IsNullOrEmpty(filename) || filename.Length > 255) throw new ArgumentException($"Item {index} needs a filename of 1–255 characters");
        if (data is null || data.Count == 0) throw new ArgumentException($"Item {index} ({filename}) needs non-empty data");
    }

    private static Dictionary<string, object?> BatchBody(BatchOptions options)
    {
        var body = new Dictionary<string, object?> { ["archive"] = options.Archive };
        if (options.WebhookId is not null) body["webhook_id"] = options.WebhookId;
        if (options.Accelerator is { } accelerator) body["accelerator"] = Wire(accelerator);
        if (options.StorageDestinationId is not null) body["storage_destination_id"] = options.StorageDestinationId;
        if (options.ActiveContent is { } activeContent) body["active_content"] = Wire(activeContent);
        return body;
    }

    private static TimeSpan CheckWait(TimeSpan? timeout)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentException("timeout must not be negative", nameof(timeout));
        return timeout is null || timeout == TimeSpan.Zero ? DefaultBatchWait : timeout.Value;
    }

    private static TimeSpan PollDelay(string? retryAfter)
    {
        // One request per poll, at least a second apart.
        var delay = ParseRetryAfter(retryAfter) ?? DefaultBatchPoll;
        return delay < MinBatchPoll ? MinBatchPoll : delay;
    }

    /// <summary>The shorter wait, never negative (the deadline may already have passed).</summary>
    private static TimeSpan Min(TimeSpan a, TimeSpan b)
    {
        var shorter = a < b ? a : b;
        return shorter > TimeSpan.Zero ? shorter : TimeSpan.Zero;
    }

    /// <summary>Cuts <paramref name="destination"/> back to <paramref name="origin"/>; false when the stream cannot be truncated.</summary>
    private static bool Rewind(Stream destination, long origin)
    {
        try { destination.SetLength(origin); destination.Position = origin; return true; }
        catch (NotSupportedException) { return false; }
    }

    /// <summary>One batch call under the client deadline; see <see cref="BatchSendAsync"/>.</summary>
    private async Task<Reply> BatchCallAsync(HttpMethod method, string path, Func<HttpContent>? content, string? idempotencyKey, bool retryUnavailable, CancellationToken caller)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller); deadline.CancelAfter(timeout);
        try { return await BatchSendAsync(method, path, content, idempotencyKey, retryUnavailable, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { throw new EtchvException(0, "Client deadline exceeded", null, idempotencyKey); }
    }

    /// <summary>
    /// Sends a batch request, retrying network errors and HTTP 429, 502 and 504 (and 503 when
    /// <paramref name="retryUnavailable"/>) with the same idempotency key. Returns any 2xx reply.
    /// </summary>
    private async Task<Reply> BatchSendAsync(HttpMethod method, string path, Func<HttpContent>? content, string? idempotencyKey, bool retryUnavailable, CancellationToken ct, long maxBytes = MaxDownloadSize)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Reply r;
            try { r = await SendAsync(method, path, content?.Invoke(), idempotencyKey, ct, maxBytes).ConfigureAwait(false); }
            catch (Exception e) when (e is HttpRequestException or IOException) { await Task.Delay(1000, ct).ConfigureAwait(false); continue; }
            if (r.Status is >= 200 and < 300) return r;
            if (r.Status is 429 or 502 or 504 || (retryUnavailable && r.Status == 503)) { await Task.Delay(RetryDelay(r.RetryAfter), ct).ConfigureAwait(false); continue; }
            throw new EtchvException(r.Status, Truncate(r.Bytes), r.RequestId, idempotencyKey) { RetryAfter = ParseRetryAfter(r.RetryAfter) };
        }
    }
}
