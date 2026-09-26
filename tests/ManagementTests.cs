using Etchv;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

/// <summary>Serves canned responses keyed by "METHOD /path" and records every request.</summary>
internal sealed class RouteServer : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Task worker;
    public readonly Dictionary<string, (int Status, string Body)> Routes = [];
    public readonly List<(string Method, string Path, string Query, string Body, NameValueCollectionView Headers)> Requests = [];
    public string BaseUrl { get; }
    public RouteServer()
    {
        var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        BaseUrl = $"http://127.0.0.1:{number}"; listener.Prefixes.Add(BaseUrl + "/"); listener.Start();
        worker = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var context = await listener.GetContextAsync(); var request = context.Request; var response = context.Response;
                    using var reader = new StreamReader(request.InputStream); var body = await reader.ReadToEndAsync();
                    lock (Requests) Requests.Add((request.HttpMethod, request.Url!.AbsolutePath, request.Url.Query, body, new(request.Headers)));
                    var (status, output) = Routes.TryGetValue(request.HttpMethod + " " + request.Url.AbsolutePath, out var route) ? route : (404, "{\"detail\":\"Not Found\"}");
                    response.StatusCode = status; response.Headers["X-Request-ID"] = "req_" + new string('9', 64);
                    var bytes = Encoding.UTF8.GetBytes(output); response.ContentLength64 = status == 204 ? 0 : bytes.Length;
                    if (status != 204) await response.OutputStream.WriteAsync(bytes);
                    response.Close();
                }
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
        });
    }
    public void Dispose() { listener.Stop(); listener.Close(); worker.Wait(TimeSpan.FromSeconds(5)); }
}

internal sealed class NameValueCollectionView(System.Collections.Specialized.NameValueCollection headers)
{
    public string? this[string name] => headers[name];
}

public sealed class ManagementTests
{
    private static readonly string Hook = "wh_" + new string('a', 32), Event = "evt_" + new string('e', 64), Dest = "dst_" + new string('d', 32),
        Delivery = "std_" + new string('f', 64), Asset = "ast_" + new string('1', 64), Job = "req_" + new string('b', 64);

    private static string Receipt(string status, string operation = "embed") => new JsonObject
    {
        ["request_id"] = Job, ["status"] = status, ["operation"] = operation, ["status_url"] = "/watermarks/jobs/" + Job, ["result_url"] = "/watermarks/jobs/" + Job + "/result",
        ["webhook_id"] = null, ["asset_id"] = Asset, ["source_asset_id"] = null, ["format"] = "PDF", ["frame_count"] = 2, ["credits"] = 1, ["attempts"] = 1,
        ["error_code"] = null, ["result_expires_at"] = "2026-09-27T00:00:00+00:00", ["storage_provider"] = "etchv", ["storage_delivery_id"] = null, ["storage_destination_id"] = null
    }.ToJsonString();

    private static JsonObject StorageDeliveryJson(string status) => new()
    {
        ["id"] = Delivery, ["asset_id"] = Asset, ["request_id"] = Job, ["destination_id"] = Dest, ["provider"] = "s3", ["key"] = "etchv/out.png",
        ["uri"] = "s3://bucket/etchv/out.png", ["public_url"] = null, ["status"] = status, ["attempts"] = 1, ["created_at"] = "2026-09-26T00:00:00",
        ["next_attempt_at"] = "2026-09-26T00:00:30", ["completed_at"] = null, ["error_code"] = null,
        ["history"] = new JsonArray(new JsonObject { ["at"] = "2026-09-26T00:00:01", ["status"] = "retrying", ["error_code"] = "upload_failed" }), ["expires_at"] = null
    };

    [Fact]
    public async Task ConnectionCheckAndJobStatus()
    {
        using var server = new RouteServer();
        server.Routes["GET /auth/api-key"] = (200, "{\"organization_id\":\"org_1\",\"key_id\":\"key_1\",\"scopes\":[\"watermarks:embed\",\"assets:read\"]}");
        server.Routes["GET /watermarks/jobs/" + Job] = (200, Receipt("running"));
        server.Routes["GET /watermarks/detection-jobs/" + Job] = (200, Receipt("succeeded", "detect"));
        server.Routes["GET /watermarks/jobs/" + Job + "/result"] = (410, "{\"status\":\"expired\",\"request_id\":\"" + Job + "\",\"detail\":\"Saved result has expired; this key will not be charged again\"}");
        using var client = new EtchvClient("test-key", server.BaseUrl, TimeSpan.FromSeconds(5));
        var info = await client.GetApiKeyInfoAsync();
        Assert.Equal("org_1", info.OrganizationId); Assert.Equal("key_1", info.KeyId); Assert.Equal(["watermarks:embed", "assets:read"], info.Scopes);
        var job = await client.GetJobAsync(Job);
        Assert.Equal("running", job.Status); Assert.False(job.IsTerminal); Assert.Equal(2, job.FrameCount); Assert.Equal(Asset, job.AssetId);
        var detect = await client.GetJobAsync(Job, detect: true);
        Assert.Equal("detect", detect.Operation); Assert.True(detect.IsTerminal);
        var gone = await Assert.ThrowsAsync<EtchvException>(() => client.GetEmbedResultAsync(Job));
        Assert.Equal(410, gone.StatusCode); Assert.Equal("expired", gone.ErrorStatus); Assert.Equal("req_" + new string('9', 64), gone.RequestId);
        Assert.DoesNotContain("test-key", gone.Message); Assert.DoesNotContain("test-key", gone.ToString());
        Assert.All(server.Requests, r => { Assert.Equal("test-key", r.Headers["X-API-Key"]); Assert.Equal(EtchvClient.UserAgent, r.Headers["User-Agent"]); });
        server.Routes["GET /auth/api-key"] = (401, "{\"detail\":\"Invalid API key\"}");
        var denied = await Assert.ThrowsAsync<EtchvException>(() => client.GetApiKeyInfoAsync());
        Assert.Equal(401, denied.StatusCode); Assert.Equal("req_" + new string('9', 64), denied.RequestId);
    }

    [Fact]
    public async Task Webhooks()
    {
        using var server = new RouteServer();
        string secret = "whsec" + "_" + new string('a', 64);
        var endpoint = new JsonObject { ["id"] = Hook, ["url"] = "https://example.com/hook", ["enabled"] = true, ["created_at"] = "2026-09-26T00:00:00" };
        var created = endpoint.DeepClone(); created["signing_secret"] = secret;
        var disabled = endpoint.DeepClone(); disabled["enabled"] = false;
        server.Routes["GET /webhooks"] = (200, new JsonArray(endpoint.DeepClone()).ToJsonString());
        server.Routes["POST /webhooks"] = (201, created.ToJsonString());
        server.Routes["PATCH /webhooks/" + Hook] = (200, disabled.ToJsonString());
        server.Routes["DELETE /webhooks/" + Hook] = (204, "");
        server.Routes["GET /webhooks/" + Hook + "/deliveries"] = (200, new JsonObject
        {
            ["data"] = new JsonArray(new JsonObject
            {
                ["id"] = Event, ["request_id"] = Job, ["status"] = "delivered", ["attempts"] = 1, ["created_at"] = "2026-09-26T00:00:00",
                ["history"] = new JsonArray(new JsonObject { ["at"] = "2026-09-26T00:00:01", ["status_code"] = 200, ["error"] = null }),
                ["next_attempt_at"] = "2026-09-26T00:00:30", ["payload"] = new JsonObject { ["id"] = Event, ["type"] = "watermark.embed.succeeded" }
            }), ["next_cursor"] = Event
        }.ToJsonString());
        server.Routes["POST /webhooks/" + Hook + "/deliveries/" + Event + "/redeliver"] = (202, "{\"id\":\"" + Event + "\",\"status\":\"queued\"}");
        using var client = new EtchvClient("test-key", server.BaseUrl, TimeSpan.FromSeconds(5));

        Assert.Single(await client.ListWebhooksAsync());
        var hook = await client.CreateWebhookAsync("https://example.com/hook");
        Assert.Equal(secret, hook.SigningSecret); Assert.DoesNotContain(secret, hook.ToString()); Assert.Contains("[redacted]", hook.ToString());
        Assert.False((await client.UpdateWebhookAsync(Hook, false)).Enabled);
        var page = await client.ListWebhookDeliveriesAsync(Hook, after: Event);
        Assert.Equal(Event, page.NextCursor); Assert.Equal(200, page.Data[0].History[0].StatusCode); Assert.Equal("watermark.embed.succeeded", page.Data[0].Payload.GetProperty("type").GetString());
        await client.RedeliverWebhookAsync(Hook, Event);
        await client.DeleteWebhookAsync(Hook);
        await Assert.ThrowsAsync<ArgumentException>(() => client.UpdateWebhookAsync("../assets", true));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RedeliverWebhookAsync(Hook, "evt_../x"));

        Assert.Equal("{\"url\":\"https://example.com/hook\"}", server.Requests.Single(r => r.Method == "POST" && r.Path == "/webhooks").Body);
        Assert.Equal("{\"enabled\":false}", server.Requests.Single(r => r.Method == "PATCH").Body);
        Assert.Equal("?after=" + Event, server.Requests.Single(r => r.Path.EndsWith("/deliveries")).Query);
    }

    [Fact]
    public void WebhookSignatureVerification()
    {
        string secret = "whsec" + "_" + new string('a', 64);
        var body = Encoding.UTF8.GetBytes("{\"id\":\"evt_1\",\"type\":\"watermark.embed.succeeded\"}");
        const string signature = "v1=b81ae4f60ffc2efd8cf69b4b78d81a4b4370293d14d479cc70d0f78caf901082"; // Python reference implementation
        var now = DateTimeOffset.FromUnixTimeSeconds(1700000000 + 60);
        Assert.True(WebhookSignature.Verify(body, "1700000000", signature, secret, now: now));
        Assert.False(WebhookSignature.Verify(body, "1700000000", signature, secret, now: now.AddMinutes(10)));
        Assert.False(WebhookSignature.Verify(body, "1700000001", signature, secret, now: now));
        Assert.False(WebhookSignature.Verify(body.Append((byte)' ').ToArray(), "1700000000", signature, secret, now: now));
        Assert.False(WebhookSignature.Verify(body, "1700000000", signature, secret + "x", now: now));
        Assert.False(WebhookSignature.Verify(body, "1700000000", signature.ToUpperInvariant(), secret, now: now));
        Assert.False(WebhookSignature.Verify(body, " 1700000000", signature, secret, now: now));
        Assert.False(WebhookSignature.Verify(body, null, signature, secret, now: now));
        Assert.False(WebhookSignature.Verify(body, "1700000000", null, secret, now: now));
    }

    [Fact]
    public async Task Storage()
    {
        using var server = new RouteServer();
        var destination = new JsonObject
        {
            ["id"] = Dest, ["name"] = "Archive", ["provider"] = "azure", ["bucket"] = "results", ["prefix"] = "etchv", ["visibility"] = "private",
            ["region"] = null, ["role_arn"] = null, ["account"] = "acct123", ["external_id"] = "etchv-x", ["enabled"] = true, ["verified_at"] = null,
            ["created_at"] = "2026-09-26T00:00:00", ["last_error"] = null, ["credential_expires_at"] = "2027-01-01T00:00:00", ["gcs_auth"] = null,
            ["gcs_workload_identity_provider"] = null, ["gcs_service_account"] = null, ["aws_principal_arn"] = null, ["gcs_subject"] = null
        };
        var verified = destination.DeepClone(); verified["verified_at"] = "2026-09-26T00:01:00";
        server.Routes["GET /storage/destinations"] = (200, new JsonArray(destination.DeepClone()).ToJsonString());
        server.Routes["POST /storage/destinations"] = (201, destination.ToJsonString());
        server.Routes["PATCH /storage/destinations/" + Dest] = (200, destination.ToJsonString());
        server.Routes["DELETE /storage/destinations/" + Dest] = (204, "");
        server.Routes["POST /storage/destinations/" + Dest + "/verify"] = (200, verified.ToJsonString());
        server.Routes["GET /storage/destinations/" + Dest + "/deliveries"] = (200, new JsonObject { ["items"] = new JsonArray(StorageDeliveryJson("stored")), ["next_cursor"] = null }.ToJsonString());
        server.Routes["POST /storage/destinations/" + Dest + "/deliveries"] = (202, StorageDeliveryJson("queued").ToJsonString());
        server.Routes["GET /storage/deliveries/" + Delivery] = (200, StorageDeliveryJson("failed").ToJsonString());
        server.Routes["POST /storage/deliveries/" + Delivery + "/retry"] = (202, StorageDeliveryJson("queued").ToJsonString());
        server.Routes["GET /storage/deliveries/" + Delivery + "/content"] = (200, "stored-bytes");
        using var client = new EtchvClient("test-key", server.BaseUrl, TimeSpan.FromSeconds(5));

        Assert.Equal("acct123", (await client.ListStorageDestinationsAsync())[0].Account);
        var settings = new StorageDestinationCreate("Archive", "azure", "results", Account: "acct123", Credentials: "sv=2025&sig=private-sas-signature");
        Assert.DoesNotContain("private-sas-signature", settings.ToString());
        Assert.Null((await client.CreateStorageDestinationAsync(settings)).VerifiedAt);
        var create = JsonNode.Parse(server.Requests.Single(r => r.Method == "POST" && r.Path == "/storage/destinations").Body)!.AsObject();
        Assert.Equal(["name", "provider", "bucket", "account", "credentials"], create.Select(p => p.Key));
        await client.UpdateStorageDestinationAsync(Dest, enabled: false);
        Assert.Equal("{\"enabled\":false}", server.Requests.Single(r => r.Method == "PATCH").Body);
        Assert.NotNull((await client.VerifyStorageDestinationAsync(Dest)).VerifiedAt);
        Assert.Equal("stored", (await client.ListStorageDeliveriesAsync(Dest)).Items[0].Status);
        var queued = await client.CreateStorageDeliveryAsync(Dest, Asset, "out.png");
        Assert.Equal("queued", queued.Status); Assert.Equal("upload_failed", queued.History![0].ErrorCode);
        Assert.Equal("{\"asset_id\":\"" + Asset + "\",\"key\":\"out.png\"}", server.Requests.Single(r => r.Method == "POST" && r.Path.EndsWith("/deliveries")).Body);
        Assert.Equal("failed", (await client.GetStorageDeliveryAsync(Delivery)).Status);
        Assert.Equal("queued", (await client.RetryStorageDeliveryAsync(Delivery)).Status);
        Assert.Equal("stored-bytes", Encoding.UTF8.GetString(await client.DownloadStorageDeliveryAsync(Delivery)));
        await client.DeleteStorageDestinationAsync(Dest);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetStorageDeliveryAsync("std_../../assets"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.UpdateStorageDestinationAsync(Dest));
        server.Routes["POST /storage/destinations/" + Dest + "/verify"] = (422, "{\"detail\":\"Connection check failed.\"}");
        Assert.Equal(422, (await Assert.ThrowsAsync<EtchvException>(() => client.VerifyStorageDestinationAsync(Dest))).StatusCode);
    }

    [Fact]
    public async Task InjectedHttpClientIsNotDisposed()
    {
        using var server = new RouteServer();
        server.Routes["GET /auth/api-key"] = (200, "{\"organization_id\":\"org_1\",\"key_id\":\"key_1\",\"scopes\":[]}");
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        using (var client = new EtchvClient("test-key", server.BaseUrl, httpClient: http)) Assert.Equal("org_1", (await client.GetApiKeyInfoAsync()).OrganizationId);
        using (var client = new EtchvClient("test-key", server.BaseUrl, httpClient: http)) Assert.Empty((await client.GetApiKeyInfoAsync()).Scopes);
    }

    [Fact]
    public async Task ClientValidationAndErrors()
    {
        Assert.Equal(typeof(EtchvClient).Assembly.GetName().Version, new Version(EtchvClient.Version + ".0"));
        Assert.Throws<ArgumentException>(() => new EtchvClient("key\nX-Injected: 1"));
        using var client = new EtchvClient("test-key", "http://127.0.0.1:9", TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ArgumentException>(() => client.EmbedImageAsync([1], new Dictionary<string, object?> { ["a"] = 1 }, new RequestOptions(IdempotencyKey: "bad key\r\n")));
        await Assert.ThrowsAsync<ArgumentException>(() => client.EmbedImageAsync([1], new Dictionary<string, object?> { ["a"] = 1 }, new RequestOptions(IdempotencyKey: "short")));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SubmitDetectionAsync("images", [1], new RequestOptions(StorageDestinationId: Dest)));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SubmitEmbedAsync("audio", [1], new Dictionary<string, object?> { ["a"] = 1 }));
        // Nothing listens on port 9: transport failures surface as status 0 without leaking the key.
        var error = await Assert.ThrowsAsync<EtchvException>(() => client.GetApiKeyInfoAsync());
        Assert.Equal(0, error.StatusCode); Assert.DoesNotContain("test-key", error.ToString());
        var detect = await Assert.ThrowsAsync<EtchvException>(() => client.DetectImageAsync([1]));
        Assert.Equal(0, detect.StatusCode);
    }
}
