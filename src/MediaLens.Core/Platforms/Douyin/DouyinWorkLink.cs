using System.Net;
using System.Text.RegularExpressions;
using MediaLens.Resolution;

namespace MediaLens.Platforms.Douyin;

internal sealed partial record DouyinWorkLink(string WorkId, string PathKind)
{
    private const string IdGroup = "id";
    private const string KindGroup = "kind";
    private const string VideoPath = "video";
    private const string NotePath = "note";
    private const string AnonymousDetailEndpoint = "https://api.amemv.com/aweme/v1/aweme/detail/";
    private const string AnonymousDetailParameters =
        "aid=6383&device_platform=web&version_code=190500&version_name=19.5.0";
    private const int RegexTimeoutMilliseconds = 1_000;

    public string WebUrl => "https://www.douyin.com/" + PathKind + '/' + WorkId;

    public Uri MobileUri => new("https://www.iesdouyin.com/share/video/" + WorkId + '/');

    public Uri JingxuanUri => new("https://jingxuan.douyin.com/m/video/" + WorkId);

    // 应用编号和版本是固定协议参数，不能附加访客、账号或设备标识。
    public Uri DetailUri => new(AnonymousDetailEndpoint + "?aweme_id=" + WorkId + '&' + AnonymousDetailParameters);

    public static bool IsDouyinUri(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        new[] { "douyin.com", "iesdouyin.com" }.Any(root =>
            uri.IdnHost.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.EndsWith('.' + root, StringComparison.OrdinalIgnoreCase));

    public static DouyinWorkLink Parse(Uri uri)
    {
        if (!IsDouyinUri(uri))
        {
            throw new ResolutionException(
                ErrorCodes.PlatformMismatch, "展开后的链接不属于指定平台。", (int)HttpStatusCode.BadRequest);
        }

        var id = ReadQueryId(uri, "modal_id") ?? ReadQueryId(uri, "vid");
        if (id is not null)
        {
            return new DouyinWorkLink(id, VideoPath);
        }

        var match = WorkPathPattern().Match(uri.AbsolutePath);
        if (!match.Success)
        {
            throw new ResolutionException(
                ErrorCodes.UnsupportedResource, "仅支持单个作品的公开分享链接。", (int)HttpStatusCode.UnprocessableEntity);
        }

        return new DouyinWorkLink(
            match.Groups[IdGroup].Value,
            match.Groups[KindGroup].Value is "note" or "slides" ? NotePath : VideoPath);
    }

    private static string? ReadQueryId(Uri uri, string key)
    {
        foreach (var field in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = field.IndexOf('=');
            if (separator < 0 || field[..separator] != key)
            {
                continue;
            }

            var value = Uri.UnescapeDataString(field[(separator + 1)..]);
            if (IdPattern().IsMatch(value))
            {
                return value;
            }
        }

        return null;
    }

    [GeneratedRegex("^[0-9]{1,30}$", RegexOptions.None, RegexTimeoutMilliseconds)]
    private static partial Regex IdPattern();

    [GeneratedRegex("^/(?:(?:share|m)/)?(?<kind>video|note|slides)/(?<id>[0-9]{1,30})/?$", RegexOptions.None, RegexTimeoutMilliseconds)]
    private static partial Regex WorkPathPattern();
}
