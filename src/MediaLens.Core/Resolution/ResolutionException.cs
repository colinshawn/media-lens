using MediaLens.Contracts;

namespace MediaLens.Resolution;

/// <summary>只携带安全、固定的错误契约，不保存上游响应和原始异常。</summary>
public sealed class ResolutionException(
    string code,
    string message,
    int statusCode,
    bool retryable = false,
    int? retryAfterSeconds = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public ApiError Error { get; } = new(
        code, message, retryable, retryable && retryAfterSeconds is >= 0 ? retryAfterSeconds : null);
}

public static class ErrorCodes
{
    public const string InvalidInput = "INVALID_INPUT";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string PlatformMismatch = "PLATFORM_MISMATCH";
    public const string PlatformNotFound = "PLATFORM_NOT_FOUND";
    public const string PlatformNotSupported = "PLATFORM_NOT_SUPPORTED";
    public const string UnsupportedResource = "UNSUPPORTED_RESOURCE";
    public const string CookieFreeUnavailable = "COOKIE_FREE_UNAVAILABLE";
    public const string MediaIncomplete = "MEDIA_INCOMPLETE";
    public const string ContentUnavailable = "CONTENT_UNAVAILABLE";
    public const string UpstreamChanged = "UPSTREAM_CHANGED";
    public const string UpstreamUnavailable = "UPSTREAM_UNAVAILABLE";
    public const string ResolutionTimeout = "RESOLUTION_TIMEOUT";
    public const string EndpointNotFound = "ENDPOINT_NOT_FOUND";
    public const string MethodNotAllowed = "METHOD_NOT_ALLOWED";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string InternalError = "INTERNAL_ERROR";
}

internal static class ResolutionErrors
{
    public static ResolutionException InvalidInput() => new(
        ErrorCodes.InvalidInput, "请提供包含公开分享链接的有效文本。", (int)System.Net.HttpStatusCode.BadRequest);

    public static ResolutionException MediaIncomplete() => new(
        ErrorCodes.MediaIncomplete, "作品的必需媒体或实况配对不完整。", (int)System.Net.HttpStatusCode.UnprocessableEntity);

    public static ResolutionException AnonymousUnavailable() => new(
        ErrorCodes.CookieFreeUnavailable, "当前匿名解析路线无法取得该作品的完整媒体。", (int)System.Net.HttpStatusCode.UnprocessableEntity);

    public static ResolutionException UpstreamChanged() => new(
        ErrorCodes.UpstreamChanged, "上游返回了无法识别的作品数据。", (int)System.Net.HttpStatusCode.BadGateway);

    public static ResolutionException UpstreamUnavailable(int? retryAfterSeconds = null) => new(
        code: ErrorCodes.UpstreamUnavailable,
        message: "上游暂时不可用，请稍后重试。",
        statusCode: (int)System.Net.HttpStatusCode.ServiceUnavailable,
        retryable: true,
        retryAfterSeconds: retryAfterSeconds);
}
