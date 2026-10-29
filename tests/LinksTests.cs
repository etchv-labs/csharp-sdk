using Etchv;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

public sealed class LinksTests
{
    private const string Base = "https://api.etchv.com";
    private static readonly string Asset = "ast_" + new string('e', 64), Lnk = "lnk_" + new string('a', 32), Lev = "lev_" + new string('b', 32),
        Hook = "wh_" + new string('c', 32), Lrc = "lrc_" + new string('d', 32), Wid = string.Concat(Enumerable.Repeat("ab", 32)), Job = "req_" + new string('f', 64);
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, .. new byte[32]];

    private static JsonObject LinkJson(string status = "active", JsonArray? recipients = null)
    {
        var link = new JsonObject
        {
            ["id"] = Lnk, ["url"] = "https://etchv.com/l/Yp3kQ8vN2rT6wX1zA4bC7d", ["name"] = "Board pack", ["asset_id"] = Asset, ["media_type"] = "document",
            ["status"] = status, ["prepare"] = "on_open", ["allowed_domains"] = new JsonArray(), ["expires_at"] = "2026-11-26T14:02:00Z", ["download_limit"] = 3,
            ["recipient_count"] = 1, ["credits_per_copy"] = 4, ["estimated_credits"] = 4, ["batch_id"] = null, ["webhook_id"] = null,
            ["created_at"] = "2026-10-27T00:00:00Z", ["revoked_at"] = null,
        };
        if (recipients is not null) link["recipients"] = recipients;
        return link;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static EtchvClient Client(RecordingHandler handler) => new("test-key", Base, TimeSpan.FromSeconds(10), new HttpClient(handler));

    private static readonly LinkRequest Board = new(Asset, "Board pack", Recipients: ["a@b.co"]);

    [Fact]
    public async Task CreateSendsTheBodyAndOneIdempotencyKeyAcrossRetries()
    {
        var keys = new List<string>();
        var handler = new RecordingHandler((request, _) =>
        {
            keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
            return keys.Count == 1 ? Json(HttpStatusCode.ServiceUnavailable, new JsonObject { ["detail"] = "busy" }) : Json(HttpStatusCode.Created, LinkJson());
        });
        using var client = Client(handler);
        var link = await client.CreateLinkAsync(new LinkRequest(Asset, "Board pack", Recipients: ["alex@example.com"],
            ExpiresAt: new DateTimeOffset(2026, 11, 26, 14, 2, 0, TimeSpan.Zero), Prepare: LinkPrepare.OnOpen, DownloadLimit: 3, WebhookId: Hook));
        Assert.Equal("https://etchv.com/l/Yp3kQ8vN2rT6wX1zA4bC7d", link.Url);
        Assert.Equal(LinkPrepare.OnOpen, link.Prepare);
        Assert.Empty(link.Recipients);
        Assert.Equal(["POST /links", "POST /links"], handler.Requests.Select(r => $"{r.Method} {r.Uri.AbsolutePath}"));
        Assert.Equal(keys[0], keys[1]);
        Assert.Matches("^[A-Za-z0-9_-]{8,128}$", keys[0]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(handler.Requests[1].Body), new JsonObject
        {
            ["asset_id"] = Asset, ["name"] = "Board pack", ["recipients"] = new JsonArray("alex@example.com"),
            ["expires_at"] = "2026-11-26T14:02:00+00:00", ["prepare"] = "on_open", ["download_limit"] = 3, ["webhook_id"] = Hook,
        }), handler.Requests[1].Body);
    }

    [Fact]
    public async Task CreateForDomainsWithAGivenKeySendsOnlyWhatWasSet()
    {
        string? key = null;
        var handler = new RecordingHandler((request, _) =>
        {
            key = request.Headers.GetValues("Idempotency-Key").Single();
            return Json(HttpStatusCode.OK, LinkJson()); // a replay
        });
        using var client = Client(handler);
        await client.CreateLinkAsync(new LinkRequest(Asset, "Board pack", AllowedDomains: ["example.com"],
            ExpiresAt: new DateTimeOffset(2026, 11, 26, 9, 2, 0, 500, TimeSpan.FromHours(-5))), "board-pack-2026-10");
        Assert.Equal("board-pack-2026-10", key);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(handler.Requests[0].Body), new JsonObject
        {
            ["asset_id"] = Asset, ["name"] = "Board pack", ["allowed_domains"] = new JsonArray("example.com"), ["expires_at"] = "2026-11-26T09:02:00.5-05:00",
        }), handler.Requests[0].Body);
    }

    [Fact]
    public async Task TooFewCreditsIsA402WithItsCode()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.PaymentRequired, new JsonObject
        {
            ["detail"] = new JsonObject
            {
                ["code"] = "insufficient_credits", ["message"] = "This link needs 8 credits for its copies; 2 are available",
                ["estimated_credits"] = 8, ["available_credits"] = 2,
            }
        }));
        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<EtchvException>(() => client.CreateLinkAsync(Board, "board-pack-2026-10"));
        Assert.Equal(402, error.StatusCode);
        Assert.Equal("insufficient_credits", error.Code);
        Assert.Equal("board-pack-2026-10", error.IdempotencyKey);
        Assert.Contains("8 credits", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task InvalidInputIsRefusedBeforeAnyRequest()
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("no request expected"));
        using var client = Client(handler);
        LinkRequest[] invalid =
        [
            Board with { AssetId = "ast_1" }, Board with { Name = "" }, Board with { Name = "   " }, Board with { Name = new string('x', 201) },
            Board with { Name = string.Concat(Enumerable.Repeat("😀", 201)) },
            Board with { Recipients = null }, Board with { Recipients = [] }, Board with { AllowedDomains = ["b.co"] }, Board with { Recipients = [""] },
            Board with { Prepare = (LinkPrepare)7 }, Board with { DownloadLimit = 0 }, Board with { DownloadLimit = 101 }, Board with { WebhookId = "wh_1" },
        ];
        foreach (var link in invalid)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => client.CreateLinkAsync(link));
            await Assert.ThrowsAsync<ArgumentException>(() => client.EstimateLinkAsync(link));
        }
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.CreateLinkAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateLinkAsync(Board, "short"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateLinkAsync(Board, "has space in it"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetLinkAsync("lnk_1"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RevokeLinkAsync("../links"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteLinkAsync("lnk_1"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RemoveLinkRecipientAsync(Lnk, "lrc_1"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RemoveLinkRecipientAsync(Lnk, Lnk));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RemoveLinkRecipientAsync("lnk_1", Lrc));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListLinksAsync(limit: 0));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListLinksAsync(limit: 51));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListLinksAsync(before: "bat_" + new string('a', 32)));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListLinkEventsAsync(Lnk, limit: 0));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListLinkEventsAsync(Lnk, limit: 101));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListLinkEventsAsync(Lnk, before: Lnk));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListLinkEventsAsync("lnk_1"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ANameIsCountedInCharactersNotUtf16Units()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.Created, LinkJson()));
        using var client = Client(handler);
        string name = string.Concat(Enumerable.Repeat("😀", 200)); // 400 UTF-16 units
        await client.CreateLinkAsync(Board with { Name = name });
        Assert.Equal(name, JsonNode.Parse(handler.Requests[0].Body)!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task EstimateListReadEventsAndRevoke()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/links/estimate") return Json(HttpStatusCode.OK, new JsonObject
            {
                ["asset_id"] = Asset, ["media_type"] = "document", ["prepare"] = "on_create", ["credits_per_copy"] = 4, ["estimated_credits"] = null,
                ["available_credits"] = 100, ["enough_credits"] = true, ["latest_expires_at"] = "2027-10-27T00:00:00Z",
            });
            if (path.EndsWith("/events")) return Json(HttpStatusCode.OK, new JsonObject
            {
                ["data"] = new JsonArray(new JsonObject
                {
                    ["id"] = Lev, ["type"] = "downloaded", ["recipient"] = "alex@example.com", ["created_at"] = "2026-10-27T00:00:00Z",
                    ["verified_by"] = null, ["asset_id"] = Asset,
                }), ["next_cursor"] = null,
            });
            if (path == "/links") return Json(HttpStatusCode.OK, new JsonObject { ["data"] = new JsonArray(LinkJson()), ["next_cursor"] = Lnk });
            var recipients = new JsonArray(new JsonObject
            {
                ["id"] = Lrc, ["email"] = "alex@example.com", ["status"] = "opened", ["opened_at"] = "2026-10-27T00:00:00Z", ["last_opened_at"] = "2026-10-27T01:00:00Z",
                ["open_count"] = 2, ["download_count"] = 1, ["copy"] = new JsonObject { ["status"] = "ready", ["asset_id"] = Asset, ["error_code"] = null },
            });
            return Json(HttpStatusCode.OK, LinkJson(request.Method == HttpMethod.Post ? "revoked" : "active", recipients));
        });
        using var client = Client(handler);
        var estimate = await client.EstimateLinkAsync(Board with { AllowedDomains = ["example.com"], Recipients = null });
        Assert.True(estimate.EnoughCredits); Assert.Null(estimate.EstimatedCredits); Assert.Equal(LinkPrepare.OnCreate, estimate.Prepare);
        var page = await client.ListLinksAsync(5, Lnk);
        Assert.Equal(Lnk, page.NextCursor); Assert.Empty(page.Data[0].Recipients); Assert.Empty(page.Data[0].AllowedDomains);
        var link = await client.GetLinkAsync(Lnk);
        Assert.Equal("active", link.Status); Assert.Equal("ready", link.Recipients[0].Copy.Status); Assert.Equal(Asset, link.Recipients[0].Copy.AssetId);
        Assert.Equal(2, link.Recipients[0].OpenCount); Assert.Equal(Lrc, link.Recipients[0].Id);
        var events = await client.ListLinkEventsAsync(Lnk, before: Lev);
        Assert.Equal("downloaded", events.Data[0].Type); Assert.Equal(Asset, events.Data[0].AssetId); Assert.Null(events.NextCursor);
        await client.ListLinkEventsAsync(Lnk, 100);
        await client.ListLinksAsync();
        Assert.Equal("revoked", (await client.RevokeLinkAsync(Lnk)).Status);
        Assert.Equal(
        [
            "POST /links/estimate", "GET /links?limit=5&before=" + Lnk, "GET /links/" + Lnk, "GET /links/" + Lnk + "/events?before=" + Lev,
            "GET /links/" + Lnk + "/events?limit=100", "GET /links", "POST /links/" + Lnk + "/revoke",
        ], handler.Requests.Select(r => $"{r.Method} {r.Uri.PathAndQuery}"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(handler.Requests[0].Body),
            new JsonObject { ["asset_id"] = Asset, ["name"] = "Board pack", ["allowed_domains"] = new JsonArray("example.com") }));
        Assert.All(handler.Requests, r => Assert.True(r.ApiKey));
    }

    [Fact]
    public async Task DeleteALinkOrRemoveOneRecipient()
    {
        var handler = new RecordingHandler((request, _) => request.RequestUri!.AbsolutePath.Contains("/recipients/")
            ? Json(HttpStatusCode.OK, LinkJson(recipients: new JsonArray()))
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = Client(handler);
        await client.DeleteLinkAsync(Lnk);
        Assert.Empty((await client.RemoveLinkRecipientAsync(Lnk, Lrc)).Recipients);
        Assert.Equal(["DELETE /links/" + Lnk, "DELETE /links/" + Lnk + "/recipients/" + Lrc], handler.Requests.Select(r => $"{r.Method} {r.Uri.PathAndQuery}"));
    }

    [Fact]
    public async Task DeletesSurfaceRefusals()
    {
        var notFound = new RecordingHandler((_, _) => Json(HttpStatusCode.NotFound, new JsonObject { ["detail"] = "Recipient not found" }));
        using (var client = Client(notFound))
            Assert.Equal(404, (await Assert.ThrowsAsync<EtchvException>(() => client.RemoveLinkRecipientAsync(Lnk, Lrc))).StatusCode);
        var forbidden = new RecordingHandler((_, _) => Json(HttpStatusCode.Forbidden, new JsonObject { ["detail"] = "needs assets:delete" }));
        using (var client = Client(forbidden))
            Assert.Equal(403, (await Assert.ThrowsAsync<EtchvException>(() => client.DeleteLinkAsync(Lnk))).StatusCode);
    }

    [Fact]
    public void ARecipientKeepsItsPositionalConstructor()
    {
        // The 1.5.0 positional constructor and Deconstruct still work; Id is set on init.
        var recipient = new LinkRecipient("alex@example.com", "opened", null, null, 1, 0, new LinkCopy("ready", Asset, null)) { Id = Lrc };
        var (email, _, _, _, opens, _, _) = recipient;
        Assert.Equal(("alex@example.com", 1, Lrc), (email, opens, recipient.Id));
    }

    [Fact]
    public async Task EstimateIsNotRetried()
    {
        var handler = new RecordingHandler((_, _) => Json(HttpStatusCode.ServiceUnavailable, new JsonObject { ["detail"] = "busy" }));
        using var client = Client(handler);
        Assert.Equal(503, (await Assert.ThrowsAsync<EtchvException>(() => client.EstimateLinkAsync(Board))).StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DetectionNamesTheCopyOrAssetWhenPresent()
    {
        var copy = new JsonObject { ["asset_id"] = Asset, ["link"] = new JsonObject { ["id"] = Lnk, ["name"] = "Board pack" }, ["recipient"] = "alex@example.com", ["opened_at"] = null };
        var asset = new JsonObject { ["asset_id"] = Asset, ["name"] = "a.png", ["metadata"] = new JsonObject { ["campaign"] = "spring" }, ["created_at"] = "2026-10-27T00:00:00Z" };
        JsonObject Body(string? name, JsonObject? value)
        {
            var body = new JsonObject { ["watermarked"] = true, ["confidence"] = .9, ["watermark_id"] = Wid };
            if (name is not null) body[name] = value?.DeepClone();
            return body;
        }
        var results = new List<DetectionResult>();
        foreach (var body in new[] { Body("copy", copy), Body("asset", asset), Body(null, null), Body("copy", null) })
        {
            using var client = Client(new RecordingHandler((_, _) => Json(HttpStatusCode.OK, body)));
            results.Add(await client.DetectImageAsync(Png));
        }
        Assert.Equal(new DetectedCopy(Asset, new DetectedLink(Lnk, "Board pack"), "alex@example.com", null), results[0].Copy);
        Assert.Null(results[0].Asset);
        Assert.Null(results[1].Copy);
        Assert.Equal("a.png", results[1].Asset!.Name); Assert.Equal("spring", results[1].Asset!.Metadata!.Value.GetProperty("campaign").GetString());
        Assert.Equal("2026-10-27T00:00:00Z", results[1].Asset!.CreatedAt);
        Assert.Null(results[2].Copy); Assert.Null(results[2].Asset);
        Assert.Null(results[3].Copy);
        // The 1.4.0 positional constructor and Deconstruct still work.
        var (watermarked, _, id, _, units, _) = new DetectionResult(true, .9, Wid, null, []);
        Assert.True(watermarked); Assert.Equal(Wid, id); Assert.Empty(units);
    }

    [Fact]
    public async Task AnAsyncDetectionResultNamesTheCopyToo()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.Equal($"/watermarks/detection-jobs/{Job}/result", request.RequestUri!.AbsolutePath);
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["watermarked"] = true, ["confidence"] = .9, ["watermark_id"] = Wid,
                ["copy"] = new JsonObject { ["asset_id"] = Asset, ["link"] = new JsonObject { ["id"] = Lnk, ["name"] = "Board pack" }, ["recipient"] = "alex@example.com", ["opened_at"] = "2026-10-27T00:00:00Z" },
            });
        });
        using var client = Client(handler);
        var result = await client.GetDetectionResultAsync(Job);
        Assert.Equal(Lnk, result.Copy!.Link.Id); Assert.Equal("2026-10-27T00:00:00Z", result.Copy.OpenedAt);
    }
}
