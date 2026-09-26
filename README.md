# Etchv C# SDK

Server-side .NET client for [Etchv](https://etchv.com): embed and detect invisible forensic watermarks in images, PDFs and videos.

## Install

```sh
dotnet add package Etchv
```

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

## Also included

- API key check: `GetApiKeyInfoAsync`
- Assets: `ListAssetsAsync`, `GetAssetAsync`, `UpdateAssetAsync`, `DeleteAssetAsync`, `DeleteAssetsAsync`, `DownloadAssetAsync`
- Webhooks: `ListWebhooksAsync`, `CreateWebhookAsync`, `UpdateWebhookAsync`, `DeleteWebhookAsync`, `ListWebhookDeliveriesAsync`, `RedeliverWebhookAsync`, and `WebhookSignature.Verify`
- Customer storage: `ListStorageDestinationsAsync`, `CreateStorageDestinationAsync`, `UpdateStorageDestinationAsync`, `DeleteStorageDestinationAsync`, `VerifyStorageDestinationAsync`, `ListStorageDeliveriesAsync`, `CreateStorageDeliveryAsync`, `GetStorageDeliveryAsync`, `RetryStorageDeliveryAsync`, `DownloadStorageDeliveryAsync`

`WebhookSignature.Verify` checks only the signature and timestamp; your handler must also compare the body `id` with `X-Etchv-Event-ID`.

## Errors

API and transport failures throw `EtchvException` with `StatusCode` (`0` when no HTTP response), `RequestId`,
`IdempotencyKey`, `Detail`, `ErrorCode` and `ErrorStatus`. Include the request ID when contacting support.

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
