using System.Text.Json.Serialization;

namespace MediaLens.Contracts;

/// <summary>分享文本是唯一业务输入；拒绝 Cookie、平台和身份等额外字段。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResolveRequest
{
    public required string Text { get; init; }
}

public sealed record ResolveResponse(
    string RequestId,
    string Platform,
    string WorkId,
    string WorkType,
    string WebUrl,
    string? Description,
    MediaItem[] Items,
    string ResolvedAt,
    string? ExpiresAt);

public sealed record MediaItem(
    int Index,
    string Type,
    MediaSource[] ImageSources,
    MediaSource[] VideoSources);

/// <summary>每个下载候选自带完整头部；候选之间没有继承关系。</summary>
public sealed record MediaSource(
    string Url,
    Dictionary<string, string> Headers,
    string? MimeType,
    string? FileName,
    int? Width,
    int? Height,
    long? DurationMs,
    long? SizeBytes,
    string? ExpiresAt);

public sealed record PlatformListResponse(string RequestId, PlatformDescription[] Platforms);

public sealed record PlatformDescription(
    string Id,
    string DisplayName,
    bool Enabled,
    PlatformCapabilities Capabilities);

public sealed record PlatformCapabilities(string Video, string ImageAlbum, string LivePhoto, string MixedAlbum)
{
    public static PlatformCapabilities Unverified { get; } = new(
        CapabilityStates.Unverified,
        CapabilityStates.Unverified,
        CapabilityStates.Unverified,
        CapabilityStates.Unverified);

    [JsonIgnore]
    public bool HasSupportedMedia => new[] { Video, ImageAlbum, LivePhoto, MixedAlbum }
        .Contains(CapabilityStates.Supported, StringComparer.Ordinal);
}

public sealed record ApiErrorResponse(string RequestId, ApiError Error);

public sealed record ApiError(string Code, string Message, bool Retryable, int? RetryAfterSeconds);

public static class CapabilityStates
{
    public const string Unverified = "unverified";
    public const string Supported = "supported";
    public const string Unsupported = "unsupported";
}

public static class MediaTypes
{
    public const string Video = "video";
    public const string Album = "album";
    public const string Image = "image";
    public const string LivePhoto = "livePhoto";
}
