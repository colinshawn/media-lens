namespace MediaLens.Http;

/// <summary>元数据和最终媒体地址使用同一个严格无 Cookie 的传输边界。</summary>
public interface IUpstreamClient
{
    Task<MetadataPage> GetAsync(Uri uri, CancellationToken cancellationToken);

    Task<MediaLocation> ResolveMediaAsync(Uri uri, string expectedType, CancellationToken cancellationToken);

    Dictionary<string, string> CreateDownloadHeaders();
}

public sealed record MediaLocation(
    Uri FinalUri,
    int StatusCode,
    string? MimeType,
    long? SizeBytes,
    int? RetryAfterSeconds);
