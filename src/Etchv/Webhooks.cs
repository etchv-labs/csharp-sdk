using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Etchv;

/// <summary>A webhook endpoint. <see cref="ToString"/> redacts the signing secret.</summary>
/// <param name="Id">Endpoint ID (<c>wh_…</c>); pass it as <c>webhookId</c> when submitting async jobs.</param>
/// <param name="Url">Public HTTPS URL receiving events.</param>
/// <param name="Enabled">Whether deliveries are sent.</param>
/// <param name="CreatedAt">ISO 8601 creation time.</param>
/// <param name="SigningSecret">One-time signing secret (<c>whsec_…</c>), returned only on creation. Store it securely.</param>
public sealed record WebhookEndpoint(string Id, string Url, bool Enabled, string CreatedAt, string? SigningSecret = null)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Id = {Id}, Url = {Url}, Enabled = {Enabled}, CreatedAt = {CreatedAt}, SigningSecret = {(SigningSecret is null ? "null" : "[redacted]")}");
        return true;
    }
}

/// <summary>One webhook delivery attempt.</summary>
/// <param name="At">ISO 8601 attempt time.</param>
/// <param name="StatusCode">HTTP status returned by the endpoint, if any.</param>
/// <param name="Error">Delivery error code, if any.</param>
public sealed record WebhookAttempt(string At, int? StatusCode, string? Error);

/// <summary>A webhook event delivery.</summary>
/// <param name="Id">Event ID (<c>evt_…</c>).</param>
/// <param name="RequestId">Job that produced the event.</param>
/// <param name="Status"><c>queued</c>, <c>delivering</c>, <c>retrying</c>, <c>delivered</c>, <c>exhausted</c> or <c>cancelled</c>.</param>
/// <param name="Attempts">Attempts made.</param>
/// <param name="CreatedAt">ISO 8601 creation time.</param>
/// <param name="History">Most recent attempts (up to 30).</param>
/// <param name="NextAttemptAt">ISO 8601 time of the next attempt.</param>
/// <param name="Payload">The event body.</param>
public sealed record WebhookDelivery(string Id, string RequestId, string Status, int Attempts, string CreatedAt,
    IReadOnlyList<WebhookAttempt> History, string? NextAttemptAt, JsonElement Payload);

/// <summary>A page of webhook deliveries, newest first.</summary>
/// <param name="Data">Deliveries on this page (up to 50).</param>
/// <param name="NextCursor">Pass as <c>after</c> for the next page, or null.</param>
public sealed record WebhookDeliveryPage(IReadOnlyList<WebhookDelivery> Data, string? NextCursor);

/// <summary>Verifies Etchv webhook signatures (<c>X-Etchv-Signature</c>: <c>v1=</c> HMAC-SHA256 of <c>timestamp + "." + raw body</c>).</summary>
public static class WebhookSignature
{
    /// <summary>Default maximum clock difference (five minutes).</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Returns true when <paramref name="signature"/> is a valid signature of the exact raw request body.
    /// Verify before parsing JSON, then check that the body <c>id</c> matches <c>X-Etchv-Event-ID</c> and deduplicate by it.
    /// </summary>
    /// <param name="rawBody">Unmodified request body bytes.</param>
    /// <param name="timestamp">The <c>X-Etchv-Timestamp</c> header (Unix seconds).</param>
    /// <param name="signature">The <c>X-Etchv-Signature</c> header.</param>
    /// <param name="signingSecret">The endpoint's full signing secret, including <c>whsec_</c>.</param>
    /// <param name="tolerance">Maximum clock difference; defaults to five minutes.</param>
    /// <param name="now">Current time, for testing; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <returns>True when the signature and timestamp are valid.</returns>
    public static bool Verify(ReadOnlySpan<byte> rawBody, string? timestamp, string? signature, string signingSecret, TimeSpan? tolerance = null, DateTimeOffset? now = null)
    {
        if (string.IsNullOrEmpty(signingSecret)) throw new ArgumentException("Signing secret required", nameof(signingSecret));
        if (timestamp is null || signature is null || timestamp.Length is 0 or > 20 || !timestamp.All(char.IsAsciiDigit)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return false;
        long current = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        if (Math.Abs((decimal)current - seconds) > (decimal)(tolerance ?? DefaultTolerance).TotalSeconds) return false;
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var message = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(message, 0); rawBody.CopyTo(message.AsSpan(prefix.Length));
        var expected = Encoding.ASCII.GetBytes("v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), message)).ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(signature));
    }
}

public sealed partial class EtchvClient
{
    private static string WebhookPath(string id) => "webhooks/" + Check(id, "wh_[a-f0-9]{32}", "webhook ID");

    /// <summary>Lists webhook endpoints (<c>GET /webhooks</c>). Requires <c>webhooks:read</c>.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Up to ten endpoints, newest first.</returns>
    public Task<IReadOnlyList<WebhookEndpoint>> ListWebhooksAsync(CancellationToken cancellationToken = default) => JsonAsync<IReadOnlyList<WebhookEndpoint>>(HttpMethod.Get, "webhooks", null, cancellationToken);

    /// <summary>Creates a webhook endpoint (<c>POST /webhooks</c>). Requires <c>webhooks:write</c> and owner/admin membership.</summary>
    /// <param name="url">Public HTTPS URL on port 443.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The endpoint, including its one-time <see cref="WebhookEndpoint.SigningSecret"/>.</returns>
    public Task<WebhookEndpoint> CreateWebhookAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048) throw new ArgumentException("A public HTTPS URL is required", nameof(url));
        return JsonAsync<WebhookEndpoint>(HttpMethod.Post, "webhooks", Body(new { url }), cancellationToken);
    }

    /// <summary>Enables or disables a webhook endpoint (<c>PATCH /webhooks/{id}</c>).</summary>
    /// <param name="id">Endpoint ID (<c>wh_…</c>).</param>
    /// <param name="enabled">Whether to send deliveries.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The updated endpoint.</returns>
    public Task<WebhookEndpoint> UpdateWebhookAsync(string id, bool enabled, CancellationToken cancellationToken = default) => JsonAsync<WebhookEndpoint>(HttpMethod.Patch, WebhookPath(id), Body(new { enabled }), cancellationToken);

    /// <summary>Permanently deletes a webhook endpoint (<c>DELETE /webhooks/{id}</c>).</summary>
    /// <param name="id">Endpoint ID (<c>wh_…</c>).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task DeleteWebhookAsync(string id, CancellationToken cancellationToken = default) => await ManageAsync(HttpMethod.Delete, WebhookPath(id), null, cancellationToken).ConfigureAwait(false);

    /// <summary>Lists an endpoint's deliveries, newest first (<c>GET /webhooks/{id}/deliveries</c>). Requires <c>webhooks:read</c>.</summary>
    /// <param name="id">Endpoint ID (<c>wh_…</c>).</param>
    /// <param name="after"><see cref="WebhookDeliveryPage.NextCursor"/> from the previous page.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Up to 50 deliveries.</returns>
    public Task<WebhookDeliveryPage> ListWebhookDeliveriesAsync(string id, string? after = null, CancellationToken cancellationToken = default)
    {
        var path = WebhookPath(id) + "/deliveries";
        if (after is not null) path += "?after=" + Check(after, "evt_[a-f0-9]{64}", "delivery cursor");
        return JsonAsync<WebhookDeliveryPage>(HttpMethod.Get, path, null, cancellationToken);
    }

    /// <summary>Queues a delivered, exhausted or cancelled event for redelivery (<c>POST /webhooks/{id}/deliveries/{event_id}/redeliver</c>).</summary>
    /// <param name="id">Enabled endpoint ID (<c>wh_…</c>).</param>
    /// <param name="eventId">Event ID (<c>evt_…</c>).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="EtchvException">HTTP 409 when the event is pending, expired or reached its redelivery limit.</exception>
    public async Task RedeliverWebhookAsync(string id, string eventId, CancellationToken cancellationToken = default) =>
        await ManageAsync(HttpMethod.Post, WebhookPath(id) + "/deliveries/" + Check(eventId, "evt_[a-f0-9]{64}", "event ID") + "/redeliver", null, cancellationToken).ConfigureAwait(false);
}
