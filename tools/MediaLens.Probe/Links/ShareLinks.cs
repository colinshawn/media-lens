using System.Text.RegularExpressions;
using MediaLens.Http;
using MediaLens.Platforms.Douyin;

namespace MediaLens.Probe.Links;

internal static partial class ShareLinks
{
    private const string ShortLinkHost = "v.douyin.com";
    private const string IdGroup = "id";
    private const string KindGroup = "kind";
    private const string NotePath = "note";
    private const string SlidesPath = "slides";
    private const string DefaultWorkPath = "video";
    private const string MobileWorkRoot = "https://www.iesdouyin.com/share/video/";
    private const string DesktopWorkRoot = "https://www.douyin.com/";
    private const string DetailRoot =
        "https://www.douyin.com/aweme/v1/web/aweme/detail/?aid=6383&device_platform=webapp&aweme_id=";
    private const string LegacyRoot =
        "https://www.iesdouyin.com/web/api/v2/aweme/iteminfo/?item_ids=";

    public static Uri Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > ProbeDefaults.MaximumInputCharacters)
        {
            throw new UpstreamRequestException(ProbeOutcomes.InvalidInput);
        }

        foreach (Match match in UrlPattern().Matches(text))
        {
            var candidate = match.Value.TrimEnd('.', ',', '!', ';', ')', ']', '}', '。', '，', '！', '）', '】', '》');
            if (!candidate.Contains("://", StringComparison.Ordinal))
            {
                candidate = Uri.UriSchemeHttps + "://" + candidate;
            }

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (!DouyinAdapter.IsSupportedUri(uri))
            {
                continue;
            }

            try
            {
                new PublicHttpTargetPolicy().ValidateUri(uri);
                return uri;
            }
            catch (UpstreamRequestException)
            {
                // 文案中不受支持的链接不能扩大网络目标范围。
            }
        }

        throw new UpstreamRequestException(ProbeOutcomes.InvalidInput);
    }

    public static bool IsShortLink(Uri uri) =>
        uri.IdnHost.Equals(ShortLinkHost, StringComparison.OrdinalIgnoreCase);

    public static WorkLink Identify(Uri uri)
    {
        if (!DouyinAdapter.IsSupportedUri(uri))
        {
            throw new UpstreamRequestException(ProbeOutcomes.InvalidInput);
        }

        new PublicHttpTargetPolicy().ValidateUri(uri);
        var modal = ModalIdPattern().Match(uri.Query);
        var selectedId = modal.Success ? modal : VideoIdQueryPattern().Match(uri.Query);
        if (selectedId.Success)
        {
            return new WorkLink(uri, selectedId.Groups[IdGroup].Value, DefaultWorkPath);
        }

        var path = WorkPathPattern().Match(uri.AbsolutePath);
        if (!path.Success)
        {
            throw new UpstreamRequestException(ProbeOutcomes.InvalidInput);
        }

        var kind = path.Groups[KindGroup].Value;
        var canonicalKind = kind is NotePath or SlidesPath ? NotePath : DefaultWorkPath;
        return new WorkLink(uri, path.Groups[IdGroup].Value, canonicalKind);
    }

    public static Uri GetEntryPoint(WorkLink work, string entryPoint) => entryPoint switch
    {
        ProbeEntryPoints.Share => work.Uri,
        ProbeEntryPoints.Mobile => new Uri(MobileWorkRoot + work.WorkId + '/'),
        ProbeEntryPoints.Desktop => new Uri(DesktopWorkRoot + work.PathKind + '/' + work.WorkId),
        ProbeEntryPoints.Detail => new Uri(DetailRoot + work.WorkId),
        ProbeEntryPoints.Legacy => new Uri(LegacyRoot + work.WorkId),
        _ => throw new UpstreamRequestException(ProbeOutcomes.UnknownEntryPoint)
    };

    [GeneratedRegex(
        "https?://[^\\s<>\\\"\\u3000-\\u303f\\u4e00-\\u9fff]+|(?<![\\w.-])(?:v\\.douyin\\.com|(?:www\\.)?(?:douyin|iesdouyin)\\.com)/[^\\s<>\\\"\\u3000-\\u303f\\u4e00-\\u9fff]+",
        RegexOptions.IgnoreCase,
        ProbeDefaults.RegexTimeoutMilliseconds)]
    private static partial Regex UrlPattern();

    [GeneratedRegex("[?&]modal_id=(?<id>[0-9]+)(?:&|$)", RegexOptions.None, ProbeDefaults.RegexTimeoutMilliseconds)]
    private static partial Regex ModalIdPattern();

    [GeneratedRegex("[?&]vid=(?<id>[0-9]+)(?:&|$)", RegexOptions.None, ProbeDefaults.RegexTimeoutMilliseconds)]
    private static partial Regex VideoIdQueryPattern();

    [GeneratedRegex("^/(?:share/)?(?<kind>video|note|slides)/(?<id>[0-9]+)(?:/|$)", RegexOptions.None, ProbeDefaults.RegexTimeoutMilliseconds)]
    private static partial Regex WorkPathPattern();
}

internal sealed record WorkLink(Uri Uri, string WorkId, string PathKind);
