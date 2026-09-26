using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

internal sealed class FixtureServer : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly JsonElement[] formats;
    private readonly Dictionary<string, int> counts = [];
    private readonly Dictionary<string, string> keys = [];
    public string BaseUrl { get; }
    private readonly string id = new('a', 64), job = "req_" + new string('b', 64);
    public FixtureServer(JsonElement[] formats)
    {
        this.formats = formats;
        var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        BaseUrl = $"http://127.0.0.1:{number}"; listener.Prefixes.Add(BaseUrl + "/"); listener.Start(); worker = Task.Run(RunAsync);
    }
    private async Task RunAsync()
    {
        try { while (!stop.IsCancellationRequested) { var context = await listener.GetContextAsync().WaitAsync(stop.Token); await ServeAsync(context); } }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (HttpListenerException) when (stop.IsCancellationRequested) { }
    }
    private async Task ServeAsync(HttpListenerContext context)
    {
        var request = context.Request; var response = context.Response;
        try
        {
            Assert.Equal("test-key", request.Headers["X-API-Key"]);
            var parts = request.Url!.AbsolutePath.Trim('/').Split('/'); Assert.True(parts.Length >= 3);
            string scenario = parts[0], ext = parts[1], route = string.Join('/', parts.Skip(2)), method = request.HttpMethod;
            var f = formats.First(f => f.GetProperty("extension").GetString() == ext); var expected = Convert.FromBase64String(f.GetProperty("base64").GetString()!);
            bool detection = route.EndsWith("/detect") || route.Contains("detection-jobs/");
            string countKey = $"{scenario}/{ext}/{method}"; counts[countKey] = counts.GetValueOrDefault(countKey) + 1;
            if (method == "POST")
            {
                using var buffer = new MemoryStream(); await request.InputStream.CopyToAsync(buffer); var body = buffer.ToArray();
                Assert.True(body.AsSpan().IndexOf(expected) >= 0);
                string text = Encoding.UTF8.GetString(body);
                Assert.True(text.Contains($"filename=\"input.{ext}\"") || text.Contains($"filename=input.{ext}"));
                Assert.Equal($"watermarks/{f.GetProperty("media").GetString()}{(detection ? "/detect" : "")}", route);
                if (!detection) Assert.Contains("{\"asset\":\"example\"}", text);
                if (!detection || f.GetProperty("media").GetString() == "videos")
                {
                    var key = request.Headers["Idempotency-Key"]; Assert.False(string.IsNullOrEmpty(key)); string k = scenario + ext + route;
                    if (keys.TryGetValue(k, out var previous)) Assert.Equal(previous, key); else keys[k] = key;
                }
            }
            else Assert.Equal($"watermarks/{(detection ? "detection-jobs" : "jobs")}/{job}/result", route);
            int status = 200; byte[] payload = expected; string mime = f.GetProperty("mime").GetString()!; JsonNode? json = null;
            response.Headers["X-Request-ID"] = job;
            switch (scenario)
            {
                case "retry" when counts[countKey] == 1: status = 503; json = new JsonObject { ["detail"] = "temporary" }; break;
                case "failed": status = 503; json = new JsonObject { ["status"] = "failed" }; break;
                case "redirect": status = 307; response.Headers["Location"] = "/forbidden"; json = new JsonObject(); break;
                case "invalid": status = 422; json = new JsonObject { ["detail"] = "unsupported profile" }; break;
                case "bad-job": status = 202; json = new JsonObject { ["request_id"] = "../../forbidden" }; break;
                default:
                    if ((scenario is "embed-job" or "detect-job" or "deadline") && (method == "POST" || scenario == "deadline"))
                    {
                        status = 202; response.Headers["Retry-After"] = "0.01"; response.Headers["Location"] = "https://untrusted.example/steal";
                        json = new JsonObject { ["request_id"] = job, ["result_url"] = "https://untrusted.example/steal" };
                    }
                    else if (detection)
                    {
                        JsonObject Unit(int i) => new() { ["index"] = i, ["watermarked"] = true, ["confidence"] = .99, ["watermark_id"] = id };
                        json = new JsonObject { ["watermarked"] = true, ["confidence"] = scenario == "bad-detection" ? 1.5 : .99, ["watermark_id"] = id, ["units"] = new JsonArray(Unit(0), Unit(scenario == "bad-units" ? 4 : 1)) };
                    }
                    break;
            }
            if (json is not null) { payload = Encoding.UTF8.GetBytes(json.ToJsonString()); mime = "application/json"; }
            else { response.Headers["X-Watermark-ID"] = scenario == "bad-id" ? "invalid" : id; response.Headers["Content-Disposition"] = $"attachment; filename=\"protected.{ext}\""; }
            response.StatusCode = status; response.ContentType = mime; response.ContentLength64 = payload.Length;
            try { await response.OutputStream.WriteAsync(payload); } catch (HttpListenerException) { }
        }
        finally { response.Close(); }
    }
    public void Verify()
    {
        foreach (var f in formats) Assert.Equal(2, counts[$"formats/{f.GetProperty("extension").GetString()}/POST"]);
        Assert.Equal(2, counts["retry/png/POST"]); Assert.Equal(1, counts["failed/png/POST"]);
        Assert.True(counts["embed-job/pdf/GET"] >= 1 && counts["detect-job/mp4/GET"] >= 1);
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); worker.GetAwaiter().GetResult(); listener.Close(); stop.Dispose(); }
}
