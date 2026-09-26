using System.Text.Json;
namespace Etchv;

/// <summary>An original or watermarked file in the organization's asset library.</summary>
/// <param name="Id">Asset ID (<c>ast_…</c>).</param>
/// <param name="Name">Display name.</param>
/// <param name="Kind"><c>source</c> or <c>watermarked</c>.</param>
/// <param name="MediaType"><c>image</c>, <c>document</c> or <c>video</c>.</param>
/// <param name="Format">File format, such as <c>PNG</c>.</param>
/// <param name="ContentType">MIME type.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="Sha256">SHA-256 of the file, hexadecimal.</param>
/// <param name="ParentAssetId">Source asset of a watermarked output.</param>
/// <param name="RequestId">Job that created the asset.</param>
/// <param name="WatermarkId">Embedded watermark ID, for watermarked assets.</param>
/// <param name="CreatedAt">ISO 8601 creation time.</param>
/// <param name="UpdatedAt">ISO 8601 last update time.</param>
/// <param name="FileExpiresAt">When the Etchv-hosted file expires; null for customer storage.</param>
/// <param name="FileAvailable">Whether the file can currently be downloaded.</param>
/// <param name="Version">Current version, required for updates.</param>
/// <param name="Metadata">Custom JSON metadata.</param>
/// <param name="DownloadUrl">Relative, authenticated download path.</param>
/// <param name="StorageProvider"><c>etchv</c>, <c>s3</c>, <c>gcs</c> or <c>azure</c>.</param>
/// <param name="StorageStatus">Customer storage delivery state.</param>
/// <param name="StorageDestinationId">Customer storage destination, if any.</param>
/// <param name="StorageDeliveryId">Customer storage delivery, if any.</param>
/// <param name="StagingExpiresAt">When the temporary Etchv copy expires.</param>
/// <param name="StagingDeletedAt">When the temporary Etchv copy was removed.</param>
public sealed record AssetRecord(string Id, string Name, string Kind, string MediaType, string Format, string ContentType,
    long SizeBytes, string Sha256, string? ParentAssetId, string RequestId, string? WatermarkId, string CreatedAt,
    string UpdatedAt, string? FileExpiresAt, bool FileAvailable, int Version, JsonElement? Metadata, string? DownloadUrl,
    string? StorageProvider = null, string? StorageStatus = null, string? StorageDestinationId = null, string? StorageDeliveryId = null,
    string? StagingExpiresAt = null, string? StagingDeletedAt = null);

/// <summary>A page of assets.</summary>
/// <param name="Items">Assets on this page.</param>
/// <param name="NextCursor">Cursor for the next page with the same filters, or null.</param>
public sealed record AssetPage(IReadOnlyList<AssetRecord> Items, string? NextCursor);

/// <summary>Filters for <see cref="EtchvClient.ListAssetsAsync"/>.</summary>
/// <param name="Limit">Page size, 1–100.</param>
/// <param name="Cursor">Cursor from a previous page.</param>
/// <param name="Kind"><c>source</c> or <c>watermarked</c>.</param>
/// <param name="MediaType"><c>image</c>, <c>document</c> or <c>video</c>.</param>
/// <param name="WatermarkId">64-character hexadecimal watermark ID.</param>
public sealed record AssetListOptions(int Limit = 25, string? Cursor = null, string? Kind = null, string? MediaType = null, string? WatermarkId = null);

public sealed partial class EtchvClient
{
    private static string AssetPath(string id) => "assets/" + Check(id, "ast_[a-f0-9]{64}", "asset ID");

    /// <summary>Lists assets, newest first (<c>GET /assets</c>). Requires <c>assets:read</c>.</summary>
    /// <param name="options">Filters and pagination.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A page of assets.</returns>
    public Task<AssetPage> ListAssetsAsync(AssetListOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (options.Limit is < 1 or > 100) throw new ArgumentException("Limit must be 1–100", nameof(options));
        var query = new Dictionary<string, string?> { ["limit"] = options.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture), ["cursor"] = options.Cursor, ["kind"] = options.Kind, ["media_type"] = options.MediaType, ["watermark_id"] = options.WatermarkId };
        var path = "assets?" + string.Join("&", query.Where(p => p.Value is not null).Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value!)));
        return JsonAsync<AssetPage>(HttpMethod.Get, path, null, cancellationToken);
    }

    /// <summary>Reads an asset record (<c>GET /assets/{id}</c>). Requires <c>assets:read</c>.</summary>
    /// <param name="id">Asset ID (<c>ast_…</c>).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The asset.</returns>
    public Task<AssetRecord> GetAssetAsync(string id, CancellationToken cancellationToken = default) => JsonAsync<AssetRecord>(HttpMethod.Get, AssetPath(id), null, cancellationToken);

    /// <summary>Renames an asset or replaces its metadata (<c>PATCH /assets/{id}</c>). Requires <c>assets:write</c>.</summary>
    /// <param name="id">Asset ID (<c>ast_…</c>).</param>
    /// <param name="version">Current <see cref="AssetRecord.Version"/>; HTTP 409 when stale.</param>
    /// <param name="changes"><c>name</c> and/or <c>metadata</c>. Metadata is replaced, not merged.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The updated asset.</returns>
    public Task<AssetRecord> UpdateAssetAsync(string id, int version, IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var body = changes.ToDictionary(p => p.Key, p => p.Value); body["version"] = version;
        return JsonAsync<AssetRecord>(HttpMethod.Patch, AssetPath(id), JsonSerializer.Serialize(body), cancellationToken);
    }

    /// <summary>Deletes an asset (<c>DELETE /assets/{id}</c>). Requires <c>assets:delete</c> and owner/admin membership.</summary>
    /// <param name="id">Asset ID (<c>ast_…</c>).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task DeleteAssetAsync(string id, CancellationToken cancellationToken = default) => await ManageAsync(HttpMethod.Delete, AssetPath(id), null, cancellationToken).ConfigureAwait(false);

    /// <summary>Deletes 1–50 assets atomically (<c>POST /assets/bulk-delete</c>).</summary>
    /// <param name="ids">Asset IDs.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task DeleteAssetsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        if (ids is null || ids.Count is < 1 or > 50) throw new ArgumentException("Provide 1–50 asset IDs", nameof(ids));
        foreach (var id in ids) AssetPath(id);
        await ManageAsync(HttpMethod.Post, "assets/bulk-delete", Body(new { asset_ids = ids }), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Downloads an asset file (<c>GET /assets/{id}/content</c>). Requires <c>assets:read</c>.</summary>
    /// <param name="id">Asset ID (<c>ast_…</c>).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The file bytes in the original format.</returns>
    /// <exception cref="EtchvException">HTTP 410 when the file availability window ended.</exception>
    public async Task<byte[]> DownloadAssetAsync(string id, CancellationToken cancellationToken = default) => (await ManageAsync(HttpMethod.Get, AssetPath(id) + "/content", null, cancellationToken).ConfigureAwait(false)).Bytes;
}
