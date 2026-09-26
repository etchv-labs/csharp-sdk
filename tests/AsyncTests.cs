using Etchv;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

public sealed class AsyncTests {
    [Fact]
    public async Task SubmissionReturnsReceiptWithoutPolling() {
        var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        using var listener = new HttpListener(); var url = $"http://127.0.0.1:{number}"; listener.Prefixes.Add(url + "/"); listener.Start();
        var webhook = "wh_" + new string('a', 32);
        var server = Task.Run(async () => {
            for (int i = 0; i < 6; i++) {
                var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (!context.Request.Url!.AbsolutePath.Contains("/detect/")) {
                    Assert.Equal("dst_" + new string('c',32), context.Request.QueryString["storage_destination_id"]);
                    Assert.Equal("a b/#file.pdf", context.Request.QueryString["storage_key"]);
                }
                Assert.Equal("POST", context.Request.HttpMethod);
                Assert.EndsWith("/async", context.Request.Url!.AbsolutePath);
                Assert.Equal(webhook, context.Request.QueryString["webhook_id"]);
                Assert.Equal("stable_test_key", context.Request.Headers["Idempotency-Key"]);
                await context.Request.InputStream.CopyToAsync(Stream.Null);
                var job = "req_" + new string('b', 64);
                var bytes = Encoding.UTF8.GetBytes("{\"status\":\"queued\",\"request_id\":\"" + job + "\",\"operation\":\"embed\",\"status_url\":\"/watermarks/jobs/" + job + "\",\"result_url\":\"/watermarks/jobs/" + job + "/result\",\"webhook_id\":\"" + webhook + "\",\"asset_id\":null,\"source_asset_id\":null,\"format\":\"PNG\",\"frame_count\":1,\"credits\":1,\"attempts\":0,\"error_code\":null,\"result_expires_at\":null,\"storage_provider\":\"etchv\"}");
                context.Response.StatusCode = 202; context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
            }
        });
        using var client = new EtchvClient("test-key", url, TimeSpan.FromSeconds(2));
        foreach (var media in new[] { "images", "documents", "videos" }) {
            var opts = new RequestOptions(IdempotencyKey: "stable_test_key");
            var embed = await client.SubmitEmbedAsync(media, [1,2,3], new Dictionary<string, object?> { ["asset"] = "test" }, opts with { StorageDestinationId = "dst_" + new string('c',32), StorageKey = "a b/#file.pdf" }, webhook);
            Assert.Equal("queued", embed.Status); Assert.Equal(webhook, embed.WebhookId); Assert.False(embed.IsTerminal);
            var detect = await client.SubmitDetectionAsync(media, [1,2,3], opts, webhook);
            Assert.Equal("queued", detect.Status); Assert.Equal(1, detect.FrameCount);
        }
        await server;
    }
}
