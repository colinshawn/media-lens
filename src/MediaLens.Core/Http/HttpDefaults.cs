namespace MediaLens.Http;

/// <summary>验证工具与解析服务共同使用的网络边界。</summary>
public static class HttpDefaults
{
    public const int MaximumMetadataBytes = 2_097_152;
    public const int MaximumRedirects = 5;
    public const int ReadBufferBytes = 8_192;
    public const int ConnectTimeoutSeconds = 15;
    public const int TlsHandshakeTimeoutSeconds = 15;
    public const int ResponseHeadersTimeoutSeconds = 30;
    public const int MetadataBodyTimeoutSeconds = 30;
    public const int ConnectionAttemptDelayMilliseconds = 300;
    public const int NoResponseDrainBytes = 0;
    public const string CookieHeader = "Cookie";
    public const string SetCookieHeader = "Set-Cookie";
    public const string RedirectLocationHeader = "Location";
    public const string Referer = "https://www.douyin.com/";
    public const string DouyinDetailHost = "api.amemv.com";
    public const string Accept = "text/html,application/json";
    public const string MediaAccept = "*/*";
    public const string MobileUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) " +
        "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";
}

public static class HttpFailureCodes
{
    public const string PlatformMismatch = "platformMismatch";
    public const string UnsupportedResource = "unsupportedResource";
    public const string TargetRejected = "targetRejected";
    public const string RedirectLimit = "redirectLimit";
    public const string RedirectLoop = "redirectLoop";
    public const string InvalidRedirect = "invalidRedirect";
    public const string BodyTooLarge = "bodyTooLarge";
    public const string UnsupportedContent = "unsupportedContent";
}
