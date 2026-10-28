# Etchv C# SDK

Server-side .NET client for [Etchv](https://etchv.com): embed and detect invisible forensic watermarks in images, PDFs and videos.

## Install

```sh
# From your project folder: clone next to it, not inside it
git clone --branch v1.5.0 https://github.com/etchv-labs/csharp-sdk.git ../etchv-csharp-sdk
dotnet add reference ../etchv-csharp-sdk/src/Etchv/Etchv.csproj
```

The SDK is not published on NuGet; install it from the tagged GitHub release as shown. Don't install NuGet packages that claim to be the Etchv SDK.

Requires .NET 10. `EtchvClient` is thread-safe; create one and dispose it on shutdown.

## Quickstart

```csharp
using Etchv;

using var client = new EtchvClient(Environment.GetEnvironmentVariable("ETCHV_API_KEY")!);

var result = await client.EmbedImageAsync(await File.ReadAllBytesAsync("photo.jpg"),
    new Dictionary<string, object?> { ["recipient"] = "customer-123" },
    new RequestOptions(Filename: "photo.jpg"));
await File.WriteAllBytesAsync(result.Filename, result.Bytes);

var detection = await client.DetectImageAsync(result.Bytes, new RequestOptions(Filename: result.Filename));
Console.WriteLine($"{detection.Watermarked} {detection.Confidence:P0}");
```

## Async jobs

```csharp
var job = await client.SubmitEmbedAsync("documents", pdfBytes,
    new Dictionary<string, object?> { ["delivery"] = "delivery_001" },
    new RequestOptions(Filename: "report.pdf", IdempotencyKey: "delivery_001"));

var status = await client.GetJobAsync(job.RequestId);
if (status.Status == "succeeded")
{
    var output = await client.GetEmbedResultAsync(job.RequestId);
}
```

Resending the same request with the same `IdempotencyKey` returns the existing job without another charge.
Detection uses `SubmitDetectionAsync`, `GetJobAsync(id, detect: true)` and `GetDetectionResultAsync`.

## Limits and large files

Image and PDF uploads are limited to 50 MB; DOCX, PPTX and video uploads to 20 MB. A PDF has 1 to 2,000
pages, and its watermarked copy must fit in 95 MB with 48 KB per page, so a 2,000-page PDF can be up to about
1 MB. Detection takes the files Etchv delivered: up to 192 MB for images, 95 MB for PDFs and 100 MB for DOCX,
PPTX and video.

Files over 40 MB are uploaded once to a signed URL and then referenced by ID, so the request never
carries the file. This is automatic in every embed, detect and submit method; retries reuse the same
upload. The upload has no total deadline; it fails only when no bytes move for the client timeout.
Image and document detection above 95 MB runs as a background job and the call waits for it.
Change the threshold with `new EtchvClient(key) { LargeFileThreshold = ... }`, or upload explicitly:

```csharp
var upload = await client.UploadFileAsync("detect", delivered, "delivered.tiff");
// upload.UploadId: send as the upload_id form field instead of file
```

## Many files at once

A batch watermarks up to 100 files in one call, each with its own data:

```csharp
var items = Directory.GetFiles("in", "*.pdf")
    .Select(path => BatchItem.FromPath(path, new Dictionary<string, object?> { ["recipient"] = Path.GetFileNameWithoutExtension(path) }))
    .ToList();
var batch = await client.SubmitBatchAsync(items, new BatchOptions(Archive: true));

// Waits for the batch to finish (up to 30 minutes here; 1 hour by default), then downloads as you go.
await foreach (var item in client.IterBatchResultsAsync(batch.BatchId, TimeSpan.FromMinutes(30)))
{
    if (item.Ok) await File.WriteAllBytesAsync(Path.Combine("out", item.Result!.Filename), item.Result.Bytes);
    else Console.WriteLine($"{item.Filename}: {item.ErrorCode}"); // credits refunded
}

// Or everything in one zip, streamed to disk.
await using var zip = File.Create("out.zip");
long bytes = await client.DownloadBatchArchiveAsync(batch.BatchId, zip);
```

`SubmitBatchAsync` sends each file straight to its own signed upload URL (never with your API key), up to four at a
time (`BatchOptions.UploadConcurrency`), then starts the batch. Files can be bytes (`new BatchItem(filename, bytes, data)`)
or paths (`BatchItem.FromPath`). The `Idempotency-Key` makes resubmitting safe: if an upload fails, submit again with the
same `BatchOptions.IdempotencyKey` (also on the exception) and only the missing files are uploaded. A file that changes
size after the batch is created fails at once. Uploads have no total deadline: one fails only when no bytes move for the
client timeout. Files that are rejected or fail are refunded and reported with an `ErrorCode` (`cancelled` or `expired`
for files of a canceled or expired batch). Upload URLs last 6 hours and a batch must start within 24 hours; resubmitting
an expired batch's idempotency key raises HTTP 410 with `Code` `batch_expired`, so use a new key. Results and the archive (`Archive: true`, up to
1 GB of results) are kept for 24 hours.

`WaitForBatchAsync`, `IterBatchResultsAsync` and `DownloadBatchArchiveAsync` take a wait timeout (default 1 hour) and
wait as long as the API's `Retry-After` asks (at least 1 second) between polls. The archive download fails with status 0 and an inner
`TimeoutException` when the archive is not ready in time or when the download stalls; it raises HTTP 409 with `Code`
`batch_not_started`, `archive_not_requested`, `archive_too_large` or `archive_unavailable`, and HTTP 410 once expired.
`DownloadBatchArchiveAsync(id)` without a stream returns the bytes.

Files already together in a zip (up to 55 MB) go in one request with a manifest naming every member:

```csharp
var batch = await client.SubmitBatchZipAsync(await File.ReadAllBytesAsync("contracts.zip"),
    [new BatchZipItem("contracts/acme.pdf", new Dictionary<string, object?> { ["recipient"] = "acme" })]);
```

`BatchOptions` takes `WebhookId`, `Accelerator`, `StorageDestinationId` and `ActiveContent` as for single files.
Also: `GetBatchAsync`, `CancelBatchAsync` (files not yet running are refunded) and `ListBatchesAsync`.

## Traceable links

Share one file so each recipient downloads their own watermarked copy. Use the
source asset of a file you've watermarked; send the returned `Url` yourself.

```csharp
var link = await client.CreateLinkAsync(new LinkRequest(result.SourceAssetId!, "Board pack, October",
    Recipients: ["alex@example.com", "sam@example.com"], DownloadLimit: 3));
Console.WriteLine(link.Url);
```

Give `AllowedDomains: ["example.com"]` instead of `Recipients` to let anyone at
those domains open it. `ExpiresAt` (a `DateTimeOffset`), `Prepare`
(`LinkPrepare.OnOpen` or `LinkPrepare.OnCreate`) and `WebhookId` are optional.
Retries reuse one `Idempotency-Key` (pass your own as the second argument), so a
retry returns the same link. Without the credits for the copies, the call throws
`EtchvException` with HTTP 402 and `Code` `insufficient_credits`.
`EstimateLinkAsync` takes the same request and reports the credits the copies
would take without creating anything. Also `GetLinkAsync` (each recipient, their
opens and their copy), `ListLinksAsync`, `ListLinkEventsAsync` and
`RevokeLinkAsync`.

When a detected file is one of your links' copies, `DetectionResult.Copy` names
the link, the recipient and when they first opened it (needs `links:read`);
`DetectionResult.Asset` names any other watermarked asset of yours (needs
`assets:read`). See https://etchv.com/docs/api/links.

## PDF scripts and actions

A watermarked PDF keeps its scripts, actions, form fields and attachments exactly as uploaded. Set
`ActiveContent.RemoveAutoRun` on `RequestOptions` (for `EmbedDocumentAsync` or `SubmitEmbedAsync("documents", ...)`)
or on `BatchOptions` to also remove what a PDF viewer runs by itself: the open action, document and page actions,
document-level JavaScript and XFA forms. Links, form fields and attachments stay. `ActiveContent.RemoveAutoRun` is
for PDFs only: the API refuses it for a DOCX or PPTX, and for a batch unless every file is a PDF. Keeping active
content is accepted for any document and any batch.
[Scripts and actions](https://etchv.com/docs/api/documents#scripts-and-actions).

```csharp
var result = await client.EmbedDocumentAsync(await File.ReadAllBytesAsync("contract.pdf"), data,
    new RequestOptions(Filename: "contract.pdf") { ActiveContent = ActiveContent.RemoveAutoRun });
```

## GPU processing

Business and Enterprise plans can request GPU processing for any embed, detect or job submission. Other plans get HTTP 403.

```csharp
var result = await client.EmbedVideoAsync(videoBytes, data,
    new RequestOptions(Filename: "clip.mp4", Accelerator: Accelerator.Gpu));
Console.WriteLine(result.Accelerator); // Gpu, or Cpu if no GPU was ready
```

GPU operations cost 3× credits. When no GPU is ready, the file is processed on CPU at normal credits.
`EmbedResult.Accelerator` and `DetectionResult.Accelerator` report the hardware actually used; job receipts
include `AcceleratorRequested` and `Accelerator`. Omit the option for CPU (the default).

## Retries

Durable operations (embedding, video detection, job results) retry network errors and HTTP 429, 502, 503 and 504
with the same idempotency key, waiting for `Retry-After` (up to 5 seconds per wait) when the API sends it, until the
client timeout. Batch calls and `CreateLinkAsync` do the same, except that creating a batch does not retry HTTP 503
(batch uploads unavailable; send a zip instead).

## Also included

- API key check: `GetApiKeyInfoAsync`
- Traceable links: `CreateLinkAsync`, `EstimateLinkAsync`, `ListLinksAsync`, `GetLinkAsync`, `ListLinkEventsAsync`, `RevokeLinkAsync`
- Batches: `SubmitBatchAsync`, `SubmitBatchZipAsync`, `GetBatchAsync`, `WaitForBatchAsync`, `IterBatchResultsAsync`, `DownloadBatchArchiveAsync`, `CancelBatchAsync`, `ListBatchesAsync`
- Assets: `ListAssetsAsync`, `GetAssetAsync`, `UpdateAssetAsync`, `DeleteAssetAsync`, `DeleteAssetsAsync`, `DownloadAssetAsync`
- Webhooks: `ListWebhooksAsync`, `CreateWebhookAsync`, `UpdateWebhookAsync`, `DeleteWebhookAsync`, `ListWebhookDeliveriesAsync`, `RedeliverWebhookAsync`, and `WebhookSignature.Verify`
- Customer storage: `ListStorageDestinationsAsync`, `CreateStorageDestinationAsync`, `UpdateStorageDestinationAsync`, `DeleteStorageDestinationAsync`, `VerifyStorageDestinationAsync`, `ListStorageDeliveriesAsync`, `CreateStorageDeliveryAsync`, `GetStorageDeliveryAsync`, `RetryStorageDeliveryAsync`, `DownloadStorageDeliveryAsync`

`WebhookSignature.Verify` checks only the signature and timestamp; your handler must also compare the body `id` with `X-Etchv-Event-ID`.

## Errors

API and transport failures throw `EtchvException` with `StatusCode` (`0` when no HTTP response), `RequestId`,
`IdempotencyKey`, `Detail`, `ErrorCode` and `ErrorStatus`. `Code`, `DetailMessage` and `Limit` carry the API's
`detail` (for example `rate_limited` or `concurrency_limited` and the limit on HTTP 429), and `RetryAfter` the requested
wait. A structured `detail.message` is appended to the exception message. Include the request ID when contacting
support.

```csharp
catch (EtchvException e)
{
    Console.Error.WriteLine($"HTTP {e.StatusCode}, request {e.RequestId}: {e.Detail}");
}
```

## Links

- Full guide: https://etchv.com/docs/sdks/csharp
- API reference: https://etchv.com/docs
- Support: hello@etchv.com

License: MIT.

Questions or bug reports: open an issue here or email hello@etchv.com.
