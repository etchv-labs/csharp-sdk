using Etchv;
using System.Text.Json;
using Xunit;
public sealed class ContractTests
{
    [Fact]
    public async Task AllFormatsAndDurableProtocol()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "formats.json")));
        var formats = doc.RootElement.EnumerateArray().ToArray();
        using var server = new FixtureServer(formats);
        var baseUrl = server.BaseUrl;
        var data = new Dictionary<string, object?> { ["asset"] = "example" };
        void Check(bool condition) { if (!condition) throw new Exception("Contract assertion failed"); }
        foreach (var f in formats)
        {
            var ext = f.GetProperty("extension").GetString()!; var bytes = Convert.FromBase64String(f.GetProperty("base64").GetString()!);
            var options = new RequestOptions("input." + ext);
            using var c = new EtchvClient("test-key", $"{baseUrl}/formats/{ext}");
            var result = f.GetProperty("media").GetString() switch
            {
                "documents" => await c.EmbedDocumentAsync(bytes, data, options),
                "videos" => await c.EmbedVideoAsync(bytes, data, options),
                _ => await c.EmbedImageAsync(bytes, data, options)
            };
            Check(result.Bytes.SequenceEqual(bytes) && result.ContentType == f.GetProperty("mime").GetString() && result.Filename == "protected." + ext);
            var detection = f.GetProperty("media").GetString() switch
            {
                "documents" => await c.DetectDocumentAsync(bytes, options),
                "videos" => await c.DetectVideoAsync(bytes, options),
                _ => await c.DetectImageAsync(bytes, options)
            };
            Check(detection.Watermarked && detection.Units.Count == 2);
        }
        foreach (var scenario in new[] { "retry", "embed-job", "detect-job", "failed", "redirect", "invalid", "bad-job", "bad-id", "bad-detection", "bad-units", "deadline" })
        {
            var ext = scenario switch { "embed-job" => "pdf", "detect-job" => "mp4", _ => "png" };
            var bytes = Convert.FromBase64String(formats.First(f => f.GetProperty("extension").GetString() == ext).GetProperty("base64").GetString()!);
            using var c = new EtchvClient("test-key", $"{baseUrl}/{scenario}/{ext}", TimeSpan.FromMilliseconds(scenario == "deadline" ? 80 : 10000));
            var options = new RequestOptions("input." + ext, "stable-key");
            EtchvException? error = null;
            try
            {
                switch (scenario)
                {
                    case "embed-job": await c.EmbedDocumentAsync(bytes, data, options); break;
                    case "detect-job": await c.DetectVideoAsync(bytes, options); break;
                    case "bad-detection": case "bad-units": await c.DetectImageAsync(bytes, options); break;
                    default: await c.EmbedImageAsync(bytes, data, options); break;
                }
            }
            catch (EtchvException e) { error = e; }
            if (scenario is "retry" or "embed-job" or "detect-job") Check(error is null);
            else { Check(error is not null); if (scenario == "deadline") Check(error!.StatusCode == 0 && error.IdempotencyKey == "stable-key" && error.RequestId is not null); }
        }
        foreach (var url in new[] { "http://example.com", "https://user:pass@example.com", "https://example.com?x=1" })
        {
            bool rejected = false; try { using var c = new EtchvClient("key", url); } catch (ArgumentException) { rejected = true; }
            Check(rejected);
        }
        using (var c = new EtchvClient("key"))
        {
            bool rejected = false; try { await c.GetEmbedResultAsync("../../steal"); } catch (ArgumentException) { rejected = true; }
            Check(rejected);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            rejected = false; try { await c.EmbedImageAsync([1], data, cancellationToken: cancel.Token); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected);
        }
        server.Verify();
    }
}
