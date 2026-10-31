namespace MediaLens.Probe;

internal static class ProbeDefaults
{
    public const int MaximumInputCharacters = 8_192;
    public const int MaximumMetadataBytes = 2_097_152;
    public const int MaximumRedirects = 5;
    public const int ReadBufferBytes = 8_192;
    public const int RegexTimeoutMilliseconds = 1_000;
    public const int MaximumJsonDepth = 64;
    public const int MaximumTraversalDepth = 32;
    public const int MaximumTraversalNodes = 50_000;
    public const int ProbeTimeoutSeconds = 30;
    public const int MaximumInputFileBytes = 262_144;
    public const int MaximumSamples = 16;
    public const int MaximumAddressesPerHost = 32;
    public const string CookiePolicy = "none";
    public const int SuccessfulExitCode = 0;
    public const int UnresolvedExitCode = 1;
    public const int InvalidInputExitCode = 2;
    public const string CookieHeader = "Cookie";
    public const string SetCookieHeader = "Set-Cookie";
    public const string RedirectLocationHeader = "Location";
    public const string Referer = "https://www.douyin.com/";
    public const string Accept = "text/html,application/json";
    public const string MobileUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) " +
        "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";
}

internal static class ProbeSampleKinds
{
    public const string Video = "video";
    public const string ImageAlbum = "imageAlbum";
    public const string LivePhoto = "livePhoto";
    public const string MixedAlbum = "mixedAlbum";

    public static bool IsKnown(string value) => value is Video or ImageAlbum or LivePhoto or MixedAlbum;
}

internal static class ProbeOutcomes
{
    public const string MetadataObserved = "metadataObserved";
    public const string NoMediaData = "noMediaData";
    public const string HttpRejected = "httpRejected";
    public const string NetworkFailure = "networkFailure";
    public const string TimedOut = "timedOut";
    public const string InvalidInput = "invalidInput";
    public const string TargetRejected = "targetRejected";
    public const string RedirectLimit = "redirectLimit";
    public const string RedirectLoop = "redirectLoop";
    public const string InvalidRedirect = "invalidRedirect";
    public const string BodyTooLarge = "bodyTooLarge";
    public const string UnsupportedContent = "unsupportedContent";
    public const string UnknownEntryPoint = "unknownEntryPoint";
}

internal static class ProbeEntryPoints
{
    public const string Share = "share";
    public const string Mobile = "mobile";
    public const string Desktop = "desktop";
    public const string Detail = "detail";
    public const string Legacy = "legacy";
    public const string Adapter = "adapter";

    public static bool IsKnown(string value) => value is Share or Mobile or Desktop or Detail or Legacy or Adapter;
}
