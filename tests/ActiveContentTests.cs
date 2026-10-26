using Etchv;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

public sealed class ActiveContentTests
{
    private const string Base = "https://api.etchv.com";
    private static readonly string BatchId = "bat_" + new string('a', 32), Id = new('a', 64), Job = "req_" + new string('b', 64);
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7 test");
    private static readonly Dictionary<string, object?> Data = new() { ["recipient"] = "counsel-7" };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Document()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Pdf) };
        response.Content.Headers.ContentType = new("application/pdf");
        response.Headers.TryAddWithoutValidation("X-Watermark-ID", Id);
        return response;
    }

    private static string View(string status, string? activeContent, string items = "") =>
        $"{{\"batch_id\":\"{BatchId}\",\"status\":\"{status}\",\"item_count\":1,\"archive\":false,\"accelerator\":\"cpu\",\"webhook_id\":null," +
        "\"storage_destination_id\":null,\"counts\":{\"pending\":1,\"accepted\":0,\"rejected\":0,\"succeeded\":0,\"failed\":0,\"in_progress\":0}," +
        "\"credits\":{\"reserved\":0,\"charged\":0,\"refunded\":0},\"cancel_requested\":false,\"created_at\":\"2026-10-26T00:00:00Z\"," +
        $"\"started_at\":null,\"completed_at\":null,\"upload_expires_at\":\"2026-10-27T00:00:00Z\",\"status_url\":\"/watermarks/batches/{BatchId}\"," +
        (activeContent is null ? "" : $"\"active_content\":\"{activeContent}\",") + $"\"items\":[{items}]}}";

    [Fact]
    public async Task SendsActiveContentOnDocumentEmbeddingOnlyWhenSet()
    {
        var handler = new RecordingHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/async", StringComparison.Ordinal)
            ? Json(HttpStatusCode.Accepted, $"{{\"request_id\":\"{Job}\",\"status\":\"queued\"}}") : Document());
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        await client.EmbedDocumentAsync(Pdf, Data, new RequestOptions(Accelerator: Accelerator.Gpu) { ActiveContent = ActiveContent.RemoveAutoRun });
        await client.SubmitEmbedAsync("documents", Pdf, Data, new RequestOptions { ActiveContent = ActiveContent.Keep });
        await client.EmbedDocumentAsync(Pdf, Data);
        Assert.Equal("?accelerator=gpu&active_content=remove_auto_run", handler.Requests[0].Uri.Query);
        Assert.Equal("/watermarks/documents/async", handler.Requests[1].Uri.AbsolutePath);
        Assert.Equal("?active_content=keep", handler.Requests[1].Uri.Query);
        Assert.Equal("", handler.Requests[2].Uri.Query);
    }

    [Fact]
    public async Task RefusesOtherMediaDetectionAndUnknownValuesBeforeAnyRequest()
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("no request expected"));
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var remove = new RequestOptions { ActiveContent = ActiveContent.RemoveAutoRun };
        await Assert.ThrowsAsync<ArgumentException>(() => client.EmbedImageAsync(Pdf, Data, remove));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SubmitEmbedAsync("videos", Pdf, Data, remove));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DetectDocumentAsync(Pdf, remove));
        await Assert.ThrowsAsync<ArgumentException>(() => client.EmbedDocumentAsync(Pdf, Data, new RequestOptions { ActiveContent = (ActiveContent)7 }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SubmitBatchZipAsync(Pdf, [new BatchZipItem("a.pdf", Data)],
            new BatchOptions { ActiveContent = (ActiveContent)7 }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task BatchesSendTheOptionAndReportIt()
    {
        var upload = $"{{\"index\":0,\"filename\":\"a.pdf\",\"size\":{Pdf.Length},\"upload_id\":\"upl_{new string('0', 32)}\",\"request_id\":null,\"status\":\"pending\"," +
                     "\"error_code\":null,\"error_detail\":null,\"credits\":null,\"upload\":{\"method\":\"PUT\",\"url\":\"https://uploads.etchv.com/document/upl_0.pdf?Signature=s\",\"expires_at\":\"x\"}}";
        var bodies = new List<string>();
        var handler = new RecordingHandler((request, body) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "uploads.etchv.com") return new HttpResponseMessage(HttpStatusCode.OK);
            if (path == "/watermarks/batches") { bodies.Add(body); return Json(HttpStatusCode.Created, View("draft", "remove_auto_run", upload)); }
            if (path == "/watermarks/batches/zip") bodies.Add(body);
            if (path == $"/watermarks/batches/{BatchId}") return Json(HttpStatusCode.OK, View("draft", null));
            return Json(HttpStatusCode.Accepted, View("starting", "remove_auto_run"));
        });
        using var client = new EtchvClient("test-key", Base, httpClient: new HttpClient(handler));
        var options = new BatchOptions { ActiveContent = ActiveContent.RemoveAutoRun };
        var started = await client.SubmitBatchAsync([new BatchItem("a.pdf", Pdf, Data)], options);
        var zip = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0 };
        var zipped = await client.SubmitBatchZipAsync(zip, [new BatchZipItem("a.pdf", Data)], options);
        using (var json = JsonDocument.Parse(bodies[0])) Assert.Equal("remove_auto_run", json.RootElement.GetProperty("active_content").GetString());
        Assert.Contains("\"active_content\":\"remove_auto_run\"", bodies[1]);
        Assert.Equal(ActiveContent.RemoveAutoRun, started.ActiveContent);
        Assert.Equal(ActiveContent.RemoveAutoRun, zipped.ActiveContent);
        // A batch from an API that does not report the option has none.
        Assert.Null((await client.GetBatchAsync(BatchId)).ActiveContent);
    }
}
