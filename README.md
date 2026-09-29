# Etchv C# SDK

Server-side .NET client for [Etchv](https://etchv.com): embed and detect invisible forensic watermarks in images, PDFs and videos.

## Install

```sh
# From your project folder: clone next to it, not inside it
git clone --branch v1.0.0 https://github.com/etchv-labs/csharp-sdk.git ../etchv-csharp-sdk
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
client timeout.

## Also included

- API key check: `GetApiKeyInfoAsync`
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
