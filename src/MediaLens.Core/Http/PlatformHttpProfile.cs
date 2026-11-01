namespace MediaLens.Http;

/// <summary>平台归属和网络访问权限分开；实例不可变，不能按请求修改共享头部。</summary>
public sealed class PlatformHttpProfile
{
    private const string BilibiliRoot = "bilibili.com";
    private const string BilibiliShortHost = "b23.tv";
    private const string BilibiliApiHost = "api.bilibili.com";
    private const string VideoPathPrefix = "/video/";
    private const string DesktopUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private readonly string[] _platformRoots;
    private readonly string[] _metadataRoots;
    private readonly string[] _metadataHosts;
    private readonly string[] _mediaRoots;

    private PlatformHttpProfile(
        string platformId,
        string userAgent,
        string referer,
        string[] platformRoots,
        string[] metadataRoots,
        string[] metadataHosts,
        string[] mediaRoots)
    {
        PlatformId = platformId;
        UserAgent = userAgent;
        Referer = referer;
        _platformRoots = platformRoots.ToArray();
        _metadataRoots = metadataRoots.ToArray();
        _metadataHosts = metadataHosts.ToArray();
        _mediaRoots = mediaRoots.ToArray();
    }

    public static PlatformHttpProfile Douyin { get; } = new(
        platformId: "douyin",
        userAgent: HttpDefaults.MobileUserAgent,
        referer: HttpDefaults.Referer,
        platformRoots: ["douyin.com", "iesdouyin.com"],
        metadataRoots: ["douyin.com", "iesdouyin.com"],
        metadataHosts: ["aweme.snssdk.com", HttpDefaults.DouyinDetailHost],
        mediaRoots: ["douyinvod.com", "douyinpic.com", "byteimg.com", "ibyteimg.com", "pstatp.com"]);

    public static PlatformHttpProfile Bilibili { get; } = new(
        platformId: "bilibili",
        userAgent: DesktopUserAgent,
        referer: "https://www.bilibili.com/",
        platformRoots: [BilibiliRoot, BilibiliShortHost],
        metadataRoots: [],
        metadataHosts: [BilibiliRoot, "www.bilibili.com", "m.bilibili.com", BilibiliApiHost, BilibiliShortHost],
        mediaRoots: ["bilivideo.com"]);

    private static readonly PlatformHttpProfile[] KnownProfiles = [Douyin, Bilibili];

    public string PlatformId { get; }
    public string UserAgent { get; }
    public string Referer { get; }

    public bool IsPlatformUri(Uri uri) => uri.IsAbsoluteUri &&
        _platformRoots.Any(root => Matches(uri.IdnHost, root));

    internal bool AllowsHost(string host) =>
        _metadataRoots.Any(root => Matches(host, root)) ||
        _metadataHosts.Contains(host, StringComparer.OrdinalIgnoreCase) ||
        _mediaRoots.Any(root => Matches(host, root));

    internal bool IsFinalMediaUri(Uri uri) =>
        _mediaRoots.Any(root => Matches(uri.IdnHost, root));

    internal void ValidateRedirect(Uri next, bool isMedia)
    {
        if (KnownProfiles.Any(profile => profile.PlatformId != PlatformId && profile.IsPlatformUri(next)))
        {
            throw new UpstreamRequestException(HttpFailureCodes.PlatformMismatch);
        }

        // 不支持的自有资源在跳转前本地拒绝；归属规则不授予连接直播、主页等目标的权限。
        if (!isMedia && PlatformId == Bilibili.PlatformId && IsPlatformUri(next) &&
            !next.IdnHost.Equals(BilibiliApiHost, StringComparison.OrdinalIgnoreCase) &&
            !next.IdnHost.Equals(BilibiliShortHost, StringComparison.OrdinalIgnoreCase) &&
            (!_metadataHosts.Contains(next.IdnHost, StringComparer.OrdinalIgnoreCase) ||
             !next.AbsolutePath.StartsWith(VideoPathPrefix, StringComparison.Ordinal)))
        {
            throw new UpstreamRequestException(HttpFailureCodes.UnsupportedResource);
        }
    }

    private static bool Matches(string host, string root) =>
        host.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith('.' + root, StringComparison.OrdinalIgnoreCase);
}
