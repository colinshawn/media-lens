using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using MediaLens.Http;
using MediaLens.Resolution;

namespace MediaLens.Platforms.Bilibili;

internal sealed partial record BilibiliWorkLink(string? Bvid, string? Aid, int Page)
{
    internal const string ShortHost = "b23.tv";
    private const string VideoSegment = "video";
    private const string LegacyPrefix = "av";
    private const string PageParameter = "p";
    private const int DefaultPage = 1;
    private const int ExpectedSegments = 2;
    private const int MaximumIdentifierDigits = 20;

    internal static BilibiliWorkLink Parse(Uri uri)
    {
        if (!PlatformHttpProfile.Bilibili.IsPlatformUri(uri))
        {
            throw new ResolutionException(
                ErrorCodes.PlatformMismatch, "链接与 Bilibili 平台不一致。", (int)HttpStatusCode.BadRequest);
        }

        var host = uri.IdnHost;
        if (!(host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
              host.Equals("www.bilibili.com", StringComparison.OrdinalIgnoreCase) ||
              host.Equals("m.bilibili.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw Unsupported();
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != ExpectedSegments || segments[0] != VideoSegment)
        {
            throw Unsupported();
        }

        var id = segments[1];
        var page = ReadPage(uri).Page;
        if (BvidPattern().IsMatch(id))
        {
            return new BilibiliWorkLink(id, null, page);
        }

        if (id.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase) && IsPositiveId(id[LegacyPrefix.Length..]))
        {
            return new BilibiliWorkLink(null, id[LegacyPrefix.Length..], page);
        }

        throw ResolutionErrors.InvalidInput();
    }

    internal static (int Page, bool Specified) ReadPage(Uri uri)
    {
        int? page = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]) != PageParameter)
            {
                continue;
            }

            if (page is not null || parts.Length != 2 ||
                !int.TryParse(Uri.UnescapeDataString(parts[1]), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value < DefaultPage)
            {
                throw ResolutionErrors.InvalidInput();
            }

            page = value;
        }

        return (page ?? DefaultPage, page is not null);
    }

    internal static bool IsPositiveId(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaximumIdentifierDigits && value[0] != '0' && value.All(char.IsAsciiDigit);

    internal static bool IsBvid(string value) => BvidPattern().IsMatch(value);

    internal static ResolutionException Unsupported() => new(
        ErrorCodes.UnsupportedResource, "当前仅支持 Bilibili 普通公开视频的完整 MP4。", (int)HttpStatusCode.UnprocessableEntity);

    [GeneratedRegex("^BV[A-Za-z0-9]{10}$", RegexOptions.CultureInvariant)]
    private static partial Regex BvidPattern();
}
