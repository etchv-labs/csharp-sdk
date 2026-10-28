using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Etchv;

// Traceable links share a source asset so that each recipient opens their own watermarked copy.
// See https://etchv.com/docs/api/links.

/// <summary>When a link's watermarked copies are made, sent as <c>prepare</c>.</summary>
public enum LinkPrepare
{
    /// <summary>When each recipient first opens the link (the API default for images, DOCX, PPTX and short PDFs).</summary>
    OnOpen,
    /// <summary>All at once when the link is created (the API default for video and PDFs over 5 pages).</summary>
    OnCreate,
}

/// <summary>
/// A link for <see cref="EtchvClient.CreateLinkAsync"/> and <see cref="EtchvClient.EstimateLinkAsync"/>. Give exactly one of
/// <paramref name="Recipients"/> and <paramref name="AllowedDomains"/>; null fields are omitted and take the API default.
/// </summary>
/// <param name="AssetId">A source asset (<c>ast_…</c>, kind <c>source</c>); each copy is watermarked from it.</param>
/// <param name="Name">1–200 characters, shown to recipients.</param>
/// <param name="Recipients">Recipients' emails, up to 100.</param>
/// <param name="AllowedDomains">Email domains whose addresses may open the link (exact match), up to 10.</param>
/// <param name="ExpiresAt">
/// Default 30 days from now, or the source file's expiry if sooner; at least five minutes away and at most a year. Sent as
/// ISO 8601 with its offset.
/// </param>
/// <param name="Prepare">When copies are made; null lets the API choose by media type.</param>
/// <param name="DownloadLimit">Downloads allowed per recipient, 1–100; null is unlimited.</param>
/// <param name="WebhookId">Enabled webhook endpoint (<c>wh_…</c>) that receives <c>link.opened</c> and <c>link.locked</c> events.</param>
public sealed record LinkRequest(string AssetId, string Name, IReadOnlyList<string>? Recipients = null, IReadOnlyList<string>? AllowedDomains = null,
    DateTimeOffset? ExpiresAt = null, LinkPrepare? Prepare = null, int? DownloadLimit = null, string? WebhookId = null);

/// <summary>A recipient's watermarked copy.</summary>
/// <param name="Status"><c>not_started</c>, <c>preparing</c>, <c>ready</c> or <c>failed</c>.</param>
/// <param name="AssetId">The copy in the asset library, once ready.</param>
/// <param name="ErrorCode">Why the copy failed, when it did.</param>
public sealed record LinkCopy(string Status, string? AssetId, string? ErrorCode);

/// <summary>One recipient of a link, with their opens and downloads.</summary>
/// <param name="Email">The recipient's email.</param>
/// <param name="Status"><c>not_opened</c> or <c>opened</c>.</param>
/// <param name="OpenedAt">The first open (ISO 8601), after the recipient confirmed their email, or null.</param>
/// <param name="LastOpenedAt">The latest open (ISO 8601), or null.</param>
/// <param name="OpenCount">Opens so far.</param>
/// <param name="DownloadCount">Downloads so far.</param>
/// <param name="Copy">The recipient's watermarked copy.</param>
public sealed record LinkRecipient(string Email, string Status, string? OpenedAt, string? LastOpenedAt, int OpenCount, int DownloadCount, LinkCopy Copy);

/// <summary>A traceable link. Timestamps are ISO 8601 strings.</summary>
/// <param name="Id">Link ID (<c>lnk_…</c>).</param>
/// <param name="Url">What you send to your recipients.</param>
/// <param name="Name">Shown to recipients.</param>
/// <param name="AssetId">The source asset.</param>
/// <param name="MediaType"><c>image</c>, <c>document</c> or <c>video</c>.</param>
/// <param name="Status">
/// <c>active</c>, <c>revoked</c>, <c>expired</c> or <c>unavailable</c> (the source file was deleted or expired, so no new copies are made).
/// </param>
/// <param name="Prepare">When copies are made, or null when not reported.</param>
/// <param name="AllowedDomains">Email domains that may open the link; empty for a recipient list.</param>
/// <param name="ExpiresAt">When the link expires.</param>
/// <param name="DownloadLimit">Downloads allowed per recipient, or null when unlimited.</param>
/// <param name="RecipientCount">Recipients so far.</param>
/// <param name="CreditsPerCopy">Credits each copy costs: one embed of the source.</param>
/// <param name="EstimatedCredits">For a recipient list, every copy's credits; null for allowed domains.</param>
/// <param name="BatchId">With <see cref="LinkPrepare.OnCreate"/>, the batch making the copies.</param>
/// <param name="WebhookId">Webhook endpoint notified of opens and locks, if any.</param>
/// <param name="CreatedAt">When the link was created.</param>
/// <param name="RevokedAt">When the link was revoked, or null.</param>
/// <param name="Recipients">Each recipient and their copy; empty in <see cref="EtchvClient.ListLinksAsync"/>.</param>
public sealed record Link(string Id, string Url, string Name, string AssetId, string MediaType, string Status, LinkPrepare? Prepare,
    IReadOnlyList<string>? AllowedDomains, string ExpiresAt, int? DownloadLimit, int RecipientCount, int CreditsPerCopy, int? EstimatedCredits,
    string? BatchId, string? WebhookId, string CreatedAt, string? RevokedAt, IReadOnlyList<LinkRecipient>? Recipients = null)
{
    /// <summary>Email domains that may open the link; empty for a recipient list.</summary>
    public IReadOnlyList<string> AllowedDomains { get; init; } = AllowedDomains ?? [];
    /// <summary>Each recipient and their copy (for allowed domains, those who have opened the link); empty in <see cref="EtchvClient.ListLinksAsync"/>.</summary>
    public IReadOnlyList<LinkRecipient> Recipients { get; init; } = Recipients ?? [];
}

/// <summary>A page of links, newest first.</summary>
/// <param name="Data">Links on this page, without recipients.</param>
/// <param name="NextCursor">Pass as <c>before</c> for the next page, or null on the last page.</param>
public sealed record LinkPage(IReadOnlyList<Link> Data, string? NextCursor);

/// <summary>What a link's copies would cost, from <see cref="EtchvClient.EstimateLinkAsync"/>.</summary>
/// <param name="AssetId">The source asset.</param>
/// <param name="MediaType"><c>image</c>, <c>document</c> or <c>video</c>.</param>
/// <param name="Prepare">When copies would be made, or null when not reported.</param>
/// <param name="CreditsPerCopy">Credits each copy costs.</param>
/// <param name="EstimatedCredits">For a recipient list, every copy's credits; null for allowed domains.</param>
/// <param name="AvailableCredits">Credits the workspace has now.</param>
/// <param name="EnoughCredits">Whether creating the link now would pass the credit check.</param>
/// <param name="LatestExpiresAt">The latest <c>ExpiresAt</c> this link could have (ISO 8601).</param>
public sealed record LinkEstimate(string AssetId, string MediaType, LinkPrepare? Prepare, int CreditsPerCopy, int? EstimatedCredits,
    int AvailableCredits, bool EnoughCredits, string LatestExpiresAt);

/// <summary>An open, download or lock of a link.</summary>
/// <param name="Id">Event ID (<c>lev_…</c>).</param>
/// <param name="Type"><c>opened</c>, <c>downloaded</c> or <c>locked</c> (too many wrong codes).</param>
/// <param name="Recipient">The recipient's email; null for <c>locked</c>.</param>
/// <param name="CreatedAt">When it happened (ISO 8601).</param>
/// <param name="VerifiedBy">For an open, how the recipient proved their email (<c>email_code</c>).</param>
/// <param name="AssetId">For a download, the copy downloaded.</param>
public sealed record LinkEvent(string Id, string Type, string? Recipient, string CreatedAt, string? VerifiedBy, string? AssetId);

/// <summary>A page of a link's events, newest first.</summary>
/// <param name="Data">Events on this page.</param>
/// <param name="NextCursor">Pass as <c>before</c> for the next page, or null on the last page.</param>
public sealed record LinkEventPage(IReadOnlyList<LinkEvent> Data, string? NextCursor);

/// <summary>The link a detected copy belongs to.</summary>
/// <param name="Id">Link ID (<c>lnk_…</c>).</param>
/// <param name="Name">The link's name.</param>
public sealed record DetectedLink(string Id, string Name);

/// <summary>The traceable link copy a detected watermark is on.</summary>
/// <param name="AssetId">The recipient's watermarked copy.</param>
/// <param name="Link">The link it was made for.</param>
/// <param name="Recipient">The email the copy was made for.</param>
/// <param name="OpenedAt">When the recipient first opened the link (ISO 8601), or null.</param>
public sealed record DetectedCopy(string? AssetId, DetectedLink Link, string Recipient, string? OpenedAt);

/// <summary>Another of your watermarked assets a detected watermark is on.</summary>
/// <param name="AssetId">Asset ID (<c>ast_…</c>).</param>
/// <param name="Name">The asset's name.</param>
/// <param name="Metadata">The asset's custom JSON metadata, when reported.</param>
/// <param name="CreatedAt">When the asset was created (ISO 8601), when reported.</param>
public sealed record DetectedAsset(string AssetId, string Name, JsonElement? Metadata = null, string? CreatedAt = null);

public sealed partial class EtchvClient
{
    private static string LinkPath(string linkId) => "links/" + Check(linkId, "lnk_[a-f0-9]{32}", "link ID");

    internal static string Wire(LinkPrepare prepare) => prepare switch
    {
        LinkPrepare.OnOpen => "on_open",
        LinkPrepare.OnCreate => "on_create",
        _ => throw new ArgumentException("Prepare must be OnOpen or OnCreate")
    };
    internal static LinkPrepare? ParseLinkPrepare(string? value) => value switch
    {
        "on_open" => LinkPrepare.OnOpen,
        "on_create" => LinkPrepare.OnCreate,
        _ => null
    };

    /// <summary>
    /// Shares a source asset so each recipient downloads their own watermarked copy (<c>POST /links</c>, <c>links:write</c>).
    /// Send the returned <see cref="Link.Url"/> to your recipients yourself. Network errors and HTTP 429, 502, 503 and 504 are
    /// retried with one <c>Idempotency-Key</c> until the client timeout, so a retry returns the same link.
    /// </summary>
    /// <param name="link">The asset, name, recipients or allowed domains, and options.</param>
    /// <param name="idempotencyKey">8–128 letters, digits, hyphens or underscores. Generated when omitted; creating again with the same key returns the same link.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The link, with each recipient.</returns>
    /// <exception cref="ArgumentException">Invalid input. Nothing is sent.</exception>
    /// <exception cref="EtchvException">
    /// HTTP 402 with <see cref="EtchvException.Code"/> <c>insufficient_credits</c> when the workspace lacks the credits for the copies;
    /// 409 when the key was used for a different link; 410 when the source file expired.
    /// </exception>
    public async Task<Link> CreateLinkAsync(LinkRequest link, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        var json = LinkBody(link);
        if (idempotencyKey is not null) Check(idempotencyKey, "[A-Za-z0-9_-]{8,128}", "idempotency key (use 8–128 letters, digits, hyphens or underscores)");
        var key = idempotencyKey ?? Guid.NewGuid().ToString();
        var r = await BatchCallAsync(HttpMethod.Post, "links", () => new StringContent(json, Encoding.UTF8, "application/json"), key, true, cancellationToken).ConfigureAwait(false);
        return Parse<Link>(r.Bytes, r.Status, r.RequestId);
    }

    /// <summary>
    /// Runs every check of <see cref="CreateLinkAsync"/> without creating anything and reports the credits its copies would take
    /// (<c>POST /links/estimate</c>, <c>links:write</c>). Not retried.
    /// </summary>
    /// <param name="link">The same request as for <see cref="CreateLinkAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The estimate.</returns>
    /// <exception cref="ArgumentException">Invalid input. Nothing is sent.</exception>
    public Task<LinkEstimate> EstimateLinkAsync(LinkRequest link, CancellationToken cancellationToken = default) =>
        JsonAsync<LinkEstimate>(HttpMethod.Post, "links/estimate", LinkBody(link), cancellationToken);

    /// <summary>Lists links, newest first, without recipients (<c>GET /links</c>, <c>links:read</c>).</summary>
    /// <param name="limit">Page size, 1–50 (API default 20).</param>
    /// <param name="before">A <see cref="LinkPage.NextCursor"/> (<c>lnk_…</c>) to continue from.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A page of links.</returns>
    public Task<LinkPage> ListLinksAsync(int? limit = null, string? before = null, CancellationToken cancellationToken = default) =>
        JsonAsync<LinkPage>(HttpMethod.Get, "links" + PageQuery(limit, 50, before, "lnk_", "link cursor"), null, cancellationToken);

    /// <summary>Reads a link with each recipient, their opens and downloads, and their copy (<c>GET /links/{id}</c>, <c>links:read</c>).</summary>
    /// <param name="linkId">Link ID (<c>lnk_…</c>).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The link.</returns>
    public Task<Link> GetLinkAsync(string linkId, CancellationToken cancellationToken = default) =>
        JsonAsync<Link>(HttpMethod.Get, LinkPath(linkId), null, cancellationToken);

    /// <summary>Lists a link's opens, downloads and locks, newest first (<c>GET /links/{id}/events</c>, <c>links:read</c>).</summary>
    /// <param name="linkId">Link ID (<c>lnk_…</c>).</param>
    /// <param name="limit">Page size, 1–100 (API default 50).</param>
    /// <param name="before">A <see cref="LinkEventPage.NextCursor"/> (<c>lev_…</c>) to continue from.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A page of events.</returns>
    public Task<LinkEventPage> ListLinkEventsAsync(string linkId, int? limit = null, string? before = null, CancellationToken cancellationToken = default)
    {
        var path = LinkPath(linkId) + "/events" + PageQuery(limit, 100, before, "lev_", "link event cursor");
        return JsonAsync<LinkEventPage>(HttpMethod.Get, path, null, cancellationToken);
    }

    /// <summary>
    /// Stops new opens and downloads at once (<c>POST /links/{id}/revoke</c>, <c>links:write</c>). Copies not yet queued are canceled;
    /// downloaded copies keep their watermark. Revoking again returns the link.
    /// </summary>
    /// <param name="linkId">Link ID (<c>lnk_…</c>).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The revoked link.</returns>
    public Task<Link> RevokeLinkAsync(string linkId, CancellationToken cancellationToken = default) =>
        JsonAsync<Link>(HttpMethod.Post, LinkPath(linkId) + "/revoke", null, cancellationToken);

    private static string PageQuery(int? limit, int most, string? before, string prefix, string label)
    {
        if (limit is { } n && (n < 1 || n > most)) throw new ArgumentException($"limit must be 1–{most}", nameof(limit));
        var query = new List<string>();
        if (limit is { } l) query.Add("limit=" + l.ToString(CultureInfo.InvariantCulture));
        if (before is not null) query.Add("before=" + Check(before, prefix + "[a-f0-9]{32}", label));
        return query.Count == 0 ? "" : "?" + string.Join("&", query);
    }

    private static string LinkBody(LinkRequest link)
    {
        ArgumentNullException.ThrowIfNull(link);
        Check(link.AssetId, "ast_[a-f0-9]{64}", "asset ID");
        // The API counts characters as code points, so a name of 200 emoji is allowed.
        if (string.IsNullOrWhiteSpace(link.Name) || link.Name.EnumerateRunes().Count() > 200) throw new ArgumentException("Name must be 1–200 characters", nameof(link));
        bool recipients = link.Recipients is { Count: > 0 }, domains = link.AllowedDomains is { Count: > 0 };
        if (recipients == domains) throw new ArgumentException("Give either Recipients or AllowedDomains", nameof(link));
        var audience = recipients ? link.Recipients! : link.AllowedDomains!;
        if (audience.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException((recipients ? "Recipients" : "AllowedDomains") + " must not contain empty values", nameof(link));
        var body = new Dictionary<string, object?> { ["asset_id"] = link.AssetId, ["name"] = link.Name, [recipients ? "recipients" : "allowed_domains"] = audience };
        if (link.ExpiresAt is { } expires) body["expires_at"] = expires.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);
        if (link.Prepare is { } prepare) body["prepare"] = Wire(prepare);
        if (link.DownloadLimit is { } limit)
        {
            if (limit is < 1 or > 100) throw new ArgumentException("DownloadLimit must be 1–100", nameof(link));
            body["download_limit"] = limit;
        }
        if (link.WebhookId is not null) body["webhook_id"] = Check(link.WebhookId, "wh_[a-f0-9]{32}", "webhook ID");
        return JsonSerializer.Serialize(body);
    }
}
