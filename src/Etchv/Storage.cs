using System.Text;
using System.Text.Json;
namespace Etchv;

/// <summary>A customer storage destination. Credentials are never returned.</summary>
/// <param name="Id">Destination ID (<c>dst_…</c>).</param>
/// <param name="Name">Display name.</param>
/// <param name="Provider"><c>s3</c>, <c>gcs</c> or <c>azure</c>.</param>
/// <param name="Bucket">Bucket or container name.</param>
/// <param name="Prefix">Object key prefix.</param>
/// <param name="Visibility"><c>private</c> or <c>public</c>.</param>
/// <param name="Region">AWS region (S3).</param>
/// <param name="RoleArn">IAM role ARN (S3).</param>
/// <param name="Account">Storage account name (Azure).</param>
/// <param name="ExternalId">External ID for the S3 role trust policy.</param>
/// <param name="Enabled">Whether the destination accepts deliveries.</param>
/// <param name="VerifiedAt">ISO 8601 time of the last successful connection check, or null.</param>
/// <param name="CreatedAt">ISO 8601 creation time.</param>
/// <param name="LastError">Last connection error code, if any.</param>
/// <param name="CredentialExpiresAt">Expiry of the stored Azure SAS, if any.</param>
/// <param name="GcsAuth"><c>service_account_key</c> or <c>workload_identity</c> (GCS).</param>
/// <param name="GcsWorkloadIdentityProvider">Workload identity provider resource name (keyless GCS).</param>
/// <param name="GcsServiceAccount">Service account email (keyless GCS).</param>
/// <param name="AwsPrincipalArn">Etchv AWS principal to trust (S3 and keyless GCS).</param>
/// <param name="GcsSubject">Federated subject to grant (keyless GCS).</param>
public sealed record StorageDestination(string Id, string Name, string Provider, string Bucket, string? Prefix, string Visibility,
    string? Region, string? RoleArn, string? Account, string? ExternalId, bool Enabled, string? VerifiedAt, string CreatedAt,
    string? LastError, string? CredentialExpiresAt, string? GcsAuth, string? GcsWorkloadIdentityProvider, string? GcsServiceAccount,
    string? AwsPrincipalArn, string? GcsSubject);

/// <summary>Settings for a new storage destination. <see cref="ToString"/> redacts <paramref name="Credentials"/>.</summary>
/// <param name="Name">Display name (1–80 characters).</param>
/// <param name="Provider"><c>s3</c>, <c>gcs</c> or <c>azure</c>.</param>
/// <param name="Bucket">Bucket or container name.</param>
/// <param name="Prefix">Object key prefix (default <c>etchv</c>).</param>
/// <param name="Visibility"><c>private</c> (default) or <c>public</c>.</param>
/// <param name="Region">AWS region (S3 only).</param>
/// <param name="RoleArn">IAM role ARN named <c>etchv-storage-…</c> (S3 only).</param>
/// <param name="Account">Storage account name (Azure only).</param>
/// <param name="Credentials">GCS service account JSON key or Azure container SAS. Not used for S3 or keyless GCS.</param>
/// <param name="GcsAuth"><c>service_account_key</c> or <c>workload_identity</c> (GCS only).</param>
/// <param name="GcsWorkloadIdentityProvider">Workload identity provider resource name (keyless GCS).</param>
/// <param name="GcsServiceAccount">Service account email (keyless GCS).</param>
public sealed record StorageDestinationCreate(string Name, string Provider, string Bucket, string? Prefix = null, string? Visibility = null,
    string? Region = null, string? RoleArn = null, string? Account = null, string? Credentials = null, string? GcsAuth = null,
    string? GcsWorkloadIdentityProvider = null, string? GcsServiceAccount = null)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Name = {Name}, Provider = {Provider}, Bucket = {Bucket}, Prefix = {Prefix}, Visibility = {Visibility}, Region = {Region}, RoleArn = {RoleArn}, Account = {Account}, ")
            .Append($"Credentials = {(Credentials is null ? "null" : "[redacted]")}, GcsAuth = {GcsAuth}, GcsWorkloadIdentityProvider = {GcsWorkloadIdentityProvider}, GcsServiceAccount = {GcsServiceAccount}");
        return true;
    }
}

/// <summary>One storage upload attempt.</summary>
/// <param name="At">ISO 8601 attempt time.</param>
/// <param name="Status">Resulting delivery status.</param>
/// <param name="ErrorCode">Error code, if any.</param>
public sealed record StorageDeliveryAttempt(string At, string Status, string? ErrorCode);

/// <summary>A watermarked result delivery to customer storage.</summary>
/// <param name="Id">Delivery ID (<c>std_…</c>).</param>
/// <param name="AssetId">Delivered asset.</param>
/// <param name="RequestId">Job that produced the asset.</param>
/// <param name="DestinationId">Destination ID (<c>dst_…</c>).</param>
/// <param name="Provider"><c>s3</c>, <c>gcs</c> or <c>azure</c>.</param>
/// <param name="Key">Full object key.</param>
/// <param name="Uri">Provider URI of the object.</param>
/// <param name="PublicUrl">Public URL for public destinations, otherwise null.</param>
/// <param name="Status"><c>queued</c>, <c>uploading</c>, <c>retrying</c>, <c>stored</c>, <c>failed</c> or <c>cancelled</c>.</param>
/// <param name="Attempts">Upload attempts made.</param>
/// <param name="CreatedAt">ISO 8601 creation time.</param>
/// <param name="NextAttemptAt">ISO 8601 time of the next attempt.</param>
/// <param name="CompletedAt">ISO 8601 completion time, if terminal.</param>
/// <param name="ErrorCode">Failure reason, if any.</param>
/// <param name="History">Most recent attempts.</param>
/// <param name="ExpiresAt">When the staged Etchv copy expires.</param>
public sealed record StorageDelivery(string Id, string AssetId, string RequestId, string DestinationId, string Provider, string Key,
    string? Uri, string? PublicUrl, string Status, int Attempts, string CreatedAt, string? NextAttemptAt, string? CompletedAt,
    string? ErrorCode, IReadOnlyList<StorageDeliveryAttempt>? History, string? ExpiresAt);

/// <summary>A page of storage deliveries, newest first.</summary>
/// <param name="Items">Deliveries on this page (up to 50).</param>
/// <param name="NextCursor">Pass as <c>after</c> for the next page, or null.</param>
public sealed record StorageDeliveryPage(IReadOnlyList<StorageDelivery> Items, string? NextCursor);

public sealed partial class EtchvClient
{
    private static string DestinationPath(string id) => "storage/destinations/" + Check(id, "dst_[a-f0-9]{32}", "storage destination ID");
    private static string DeliveryPath(string id) => "storage/deliveries/" + Check(id, "std_[a-f0-9]{64}", "storage delivery ID");

    /// <summary>Lists storage destinations (<c>GET /storage/destinations</c>). Requires <c>storage:read</c>.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Up to ten destinations, newest first.</returns>
    public Task<IReadOnlyList<StorageDestination>> ListStorageDestinationsAsync(CancellationToken cancellationToken = default) => JsonAsync<IReadOnlyList<StorageDestination>>(HttpMethod.Get, "storage/destinations", null, cancellationToken);

    /// <summary>Creates a storage destination (<c>POST /storage/destinations</c>). Requires <c>storage:write</c> and owner/admin membership. Verify it before use.</summary>
    /// <param name="destination">Destination settings.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The unverified destination.</returns>
    public Task<StorageDestination> CreateStorageDestinationAsync(StorageDestinationCreate destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return JsonAsync<StorageDestination>(HttpMethod.Post, "storage/destinations", Body(destination), cancellationToken);
    }

    /// <summary>Enables/disables a destination or replaces its credentials (<c>PATCH /storage/destinations/{id}</c>). Replacing credentials requires re-verification.</summary>
    /// <param name="id">Destination ID (<c>dst_…</c>).</param>
    /// <param name="enabled">New enabled state, or null to keep it.</param>
    /// <param name="credentials">Replacement GCS JSON key or Azure SAS, or null to keep it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The updated destination.</returns>
    public Task<StorageDestination> UpdateStorageDestinationAsync(string id, bool? enabled = null, string? credentials = null, CancellationToken cancellationToken = default)
    {
        var path = DestinationPath(id);
        if (enabled is null && credentials is null) throw new ArgumentException("Provide enabled and/or credentials");
        return JsonAsync<StorageDestination>(HttpMethod.Patch, path, Body(new { enabled, credentials }), cancellationToken);
    }

    /// <summary>Disconnects a destination and discards its stored credentials (<c>DELETE /storage/destinations/{id}</c>).</summary>
    /// <param name="id">Destination ID (<c>dst_…</c>).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task DeleteStorageDestinationAsync(string id, CancellationToken cancellationToken = default) => await ManageAsync(HttpMethod.Delete, DestinationPath(id), null, cancellationToken).ConfigureAwait(false);

    /// <summary>Writes and reads a connection probe (<c>POST /storage/destinations/{id}/verify</c>).</summary>
    /// <param name="id">Destination ID (<c>dst_…</c>).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The verified destination.</returns>
    /// <exception cref="EtchvException">HTTP 422 when the connection check fails.</exception>
    public Task<StorageDestination> VerifyStorageDestinationAsync(string id, CancellationToken cancellationToken = default) => JsonAsync<StorageDestination>(HttpMethod.Post, DestinationPath(id) + "/verify", null, cancellationToken);

    /// <summary>Lists a destination's deliveries, newest first (<c>GET /storage/destinations/{id}/deliveries</c>).</summary>
    /// <param name="destinationId">Destination ID (<c>dst_…</c>).</param>
    /// <param name="after"><see cref="StorageDeliveryPage.NextCursor"/> from the previous page.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Up to 50 deliveries.</returns>
    public Task<StorageDeliveryPage> ListStorageDeliveriesAsync(string destinationId, string? after = null, CancellationToken cancellationToken = default)
    {
        var path = DestinationPath(destinationId) + "/deliveries";
        if (after is not null) path += "?after=" + Check(after, "std_[a-f0-9]{64}", "delivery cursor");
        return JsonAsync<StorageDeliveryPage>(HttpMethod.Get, path, null, cancellationToken);
    }

    /// <summary>Moves an Etchv-hosted watermarked asset to a verified destination (<c>POST /storage/destinations/{id}/deliveries</c>). Idempotent per asset and destination.</summary>
    /// <param name="destinationId">Destination ID (<c>dst_…</c>).</param>
    /// <param name="assetId">Watermarked asset ID (<c>ast_…</c>).</param>
    /// <param name="key">Optional relative object key beneath the destination prefix.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The queued (or existing) delivery.</returns>
    public Task<StorageDelivery> CreateStorageDeliveryAsync(string destinationId, string assetId, string? key = null, CancellationToken cancellationToken = default)
    {
        var path = DestinationPath(destinationId) + "/deliveries";
        return JsonAsync<StorageDelivery>(HttpMethod.Post, path, Body(new { asset_id = Check(assetId, "ast_[a-f0-9]{64}", "asset ID"), key }), cancellationToken);
    }

    /// <summary>Reads a storage delivery (<c>GET /storage/deliveries/{id}</c>). Requires <c>storage:read</c>. Poll until <c>stored</c> or a terminal failure.</summary>
    /// <param name="id">Delivery ID (<c>std_…</c>).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The delivery.</returns>
    /// <exception cref="EtchvException">HTTP 404 while the watermark job has not yet queued the upload.</exception>
    public Task<StorageDelivery> GetStorageDeliveryAsync(string id, CancellationToken cancellationToken = default) => JsonAsync<StorageDelivery>(HttpMethod.Get, DeliveryPath(id), null, cancellationToken);

    /// <summary>Retries a failed or cancelled upload without another charge (<c>POST /storage/deliveries/{id}/retry</c>).</summary>
    /// <param name="id">Delivery ID (<c>std_…</c>).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The requeued delivery.</returns>
    public Task<StorageDelivery> RetryStorageDeliveryAsync(string id, CancellationToken cancellationToken = default) => JsonAsync<StorageDelivery>(HttpMethod.Post, DeliveryPath(id) + "/retry", null, cancellationToken);

    /// <summary>Downloads a stored object through Etchv (<c>GET /storage/deliveries/{id}/content</c>). Requires a <c>stored</c> delivery.</summary>
    /// <param name="id">Delivery ID (<c>std_…</c>).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The file bytes.</returns>
    public async Task<byte[]> DownloadStorageDeliveryAsync(string id, CancellationToken cancellationToken = default) => (await ManageAsync(HttpMethod.Get, DeliveryPath(id) + "/content", null, cancellationToken).ConfigureAwait(false)).Bytes;
}
