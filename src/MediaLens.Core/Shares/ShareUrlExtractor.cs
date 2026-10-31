using System.Text.RegularExpressions;
using MediaLens.Resolution;

namespace MediaLens.Shares;

/// <summary>只提取候选，平台归属与网络目标分别由注册表和 HTTP 策略约束。</summary>
public static partial class ShareUrlExtractor
{
    public const int MaximumTextCharacters = 8_192;
    private const int RegexTimeoutMilliseconds = 1_000;

    public static Uri[] Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextCharacters)
        {
            throw ResolutionErrors.InvalidInput();
        }

        try
        {
            return UrlPattern().Matches(text)
                .Select(match => Parse(match.Value))
                .OfType<Uri>()
                .ToArray();
        }
        catch (RegexMatchTimeoutException)
        {
            throw ResolutionErrors.InvalidInput();
        }
    }

    private static Uri? Parse(string value)
    {
        var candidate = value.TrimEnd('.', ',', '!', ';', ')', ']', '}', '。', '，', '！', '）', '】', '》');
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = Uri.UriSchemeHttps + "://" + candidate;
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
               uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
            ? uri
            : null;
    }

    [GeneratedRegex(
        "https?://[^\\s<>\\\"\\u3000-\\u303f\\u4e00-\\u9fff]+|(?<![\\w.-])(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\\.)+[a-z]{2,63}/[^\\s<>\\\"\\u3000-\\u303f\\u4e00-\\u9fff]+",
        RegexOptions.IgnoreCase,
        RegexTimeoutMilliseconds)]
    private static partial Regex UrlPattern();
}
