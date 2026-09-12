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
                var bytes = Encoding.UTF8.GetBytes("{\"status\":\"queued\",\"request_id\":\"req_" + new string('b', 64) + "\"}");
                context.Response.StatusCode = 202; context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
            }
        });
        using var client = new EtchvClient("test-key", url, TimeSpan.FromSeconds(2));
        foreach (var media in new[] { "images", "documents", "videos" }) {
            var opts = new RequestOptions(IdempotencyKey: "stable_test_key");
            var embed = await client.SubmitEmbedAsync(media, [1,2,3], new Dictionary<string, object?> { ["asset"] = "test" }, opts with { StorageDestinationId = "dst_" + new string('c',32), StorageKey = "a b/#file.pdf" }, webhook);
            Assert.Equal("queued", embed.GetProperty("status").GetString());
            var detect = await client.SubmitDetectionAsync(media, [1,2,3], opts, webhook);
            Assert.Equal("queued", detect.GetProperty("status").GetString());
        }
        await server;
    }
}
