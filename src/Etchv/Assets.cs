using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Etchv;

public sealed record AssetRecord(string Id, string Name, string Kind, string MediaType, string Format, string ContentType,
    long SizeBytes, string Sha256, string? ParentAssetId, string RequestId, string? WatermarkId, string CreatedAt,
    string UpdatedAt, string? FileExpiresAt, bool FileAvailable, int Version, JsonElement? Metadata, string? DownloadUrl,
    string? StorageProvider = null, string? StorageStatus = null, string? StorageDestinationId = null, string? StorageDeliveryId = null,
    string? StagingExpiresAt = null, string? StagingDeletedAt = null);
public sealed record AssetPage(IReadOnlyList<AssetRecord> Items, string? NextCursor);
public sealed record AssetListOptions(int Limit = 25, string? Cursor = null, string? Kind = null, string? MediaType = null, string? WatermarkId = null);
public sealed partial class EtchvClient
{
    private static readonly JsonSerializerOptions AssetJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static string AssetPath(string id)
    {
        if (id is null || !Regex.IsMatch(id, "\\Aast_[a-f0-9]{64}\\z")) throw new ArgumentException("Invalid asset ID");
        return "assets/" + id;
    }
    private async Task<byte[]> AssetRequestAsync(string path, HttpMethod method, object? body, CancellationToken caller)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller); deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(method, baseUrl + "/" + path);
        request.Headers.Add("X-API-Key", key);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream(); var chunk = new byte[8192]; int n;
        while ((n = await stream.ReadAsync(chunk, deadline.Token)) > 0)
        {
            if (buffer.Length + n > MaxFileSize) throw new EtchvException((int)response.StatusCode, "Response exceeds 20 MB");
            buffer.Write(chunk, 0, n);
        }
        var bytes = buffer.ToArray(); int status = (int)response.StatusCode;
        if (status is not 200 and not 204) throw new EtchvException(status, Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 10000))));
        return bytes;
    }
    public async Task<AssetPage> ListAssetsAsync(AssetListOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new(); var query = new Dictionary<string, string?> { ["limit"] = options.Limit.ToString(), ["cursor"] = options.Cursor, ["kind"] = options.Kind, ["media_type"] = options.MediaType, ["watermark_id"] = options.WatermarkId };
        var path = "assets?" + string.Join("&", query.Where(p => p.Value is not null).Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value!)));
        return JsonSerializer.Deserialize<AssetPage>(await AssetRequestAsync(path, HttpMethod.Get, null, cancellationToken), AssetJson)!;
    }
    public async Task<AssetRecord> GetAssetAsync(string id, CancellationToken cancellationToken = default) => JsonSerializer.Deserialize<AssetRecord>(await AssetRequestAsync(AssetPath(id), HttpMethod.Get, null, cancellationToken), AssetJson)!;
    public async Task<AssetRecord> UpdateAssetAsync(string id, int version, IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default)
    {
        var body = changes.ToDictionary(p => p.Key, p => p.Value); body["version"] = version;
        return JsonSerializer.Deserialize<AssetRecord>(await AssetRequestAsync(AssetPath(id), HttpMethod.Patch, body, cancellationToken), AssetJson)!;
    }
    public async Task DeleteAssetAsync(string id, CancellationToken cancellationToken = default) => _ = await AssetRequestAsync(AssetPath(id), HttpMethod.Delete, null, cancellationToken);
    public async Task DeleteAssetsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count is < 1 or > 50) throw new ArgumentException("Provide 1–50 asset IDs"); foreach (var id in ids) AssetPath(id);
        await AssetRequestAsync("assets/bulk-delete", HttpMethod.Post, new { asset_ids = ids }, cancellationToken);
    }
    public Task<byte[]> DownloadAssetAsync(string id, CancellationToken cancellationToken = default) => AssetRequestAsync(AssetPath(id) + "/content", HttpMethod.Get, null, cancellationToken);
}
