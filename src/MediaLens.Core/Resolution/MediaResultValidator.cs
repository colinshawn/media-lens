using System.Globalization;
using MediaLens.Contracts;
using MediaLens.Platforms;

namespace MediaLens.Resolution;

internal static class MediaResultValidator
{
    private static readonly HashSet<string> SafeHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "User-Agent", "Referer", "Accept", "Origin"
    };

    public static MediaItem[] Validate(ResolvedWork work, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(work.WorkId) || work.Items is null || work.Items.Length == 0 ||
            work.WorkType is not (MediaTypes.Video or MediaTypes.Album) || !IsHttpUri(work.WebUrl))
        {
            throw ResolutionErrors.UpstreamChanged();
        }

        var items = new List<MediaItem>();
        foreach (var item in work.Items)
        {
            if (item is null || item.ImageSources is null || item.VideoSources is null ||
                item.Index != items.Count + 1 ||
                item.Type is not (MediaTypes.Video or MediaTypes.Image or MediaTypes.LivePhoto))
            {
                throw ResolutionErrors.UpstreamChanged();
            }

            if ((item.Type == MediaTypes.Video && item.ImageSources.Length > 0) ||
                (item.Type == MediaTypes.Image && item.VideoSources.Length > 0))
            {
                throw ResolutionErrors.UpstreamChanged();
            }

            var images = Filter(item.ImageSources, now, isImage: true);
            var videos = Filter(item.VideoSources, now, isImage: false);
            var complete = item.Type switch
            {
                MediaTypes.Video => images.Length == 0 && videos.Length > 0,
                MediaTypes.Image => images.Length > 0 && videos.Length == 0,
                _ => images.Length > 0 && videos.Length > 0
            };
            if (!complete)
            {
                throw ResolutionErrors.MediaIncomplete();
            }

            items.Add(item with { ImageSources = images, VideoSources = videos });
        }

        if ((work.WorkType == MediaTypes.Video && (items.Count != 1 || items[0].Type != MediaTypes.Video)) ||
            (work.WorkType == MediaTypes.Album && items.Any(item => item.Type == MediaTypes.Video)))
        {
            throw ResolutionErrors.UpstreamChanged();
        }

        return items.ToArray();
    }

    private static MediaSource[] Filter(MediaSource[] sources, DateTimeOffset now, bool isImage)
    {
        var result = new List<MediaSource>();
        foreach (var source in sources)
        {
            if (source is null || source.Headers is null ||
                !IsHttpUri(source.Url) || source.Width is <= 0 || source.Height is <= 0 ||
                source.DurationMs is <= 0 || source.SizeBytes is <= 0 ||
                (source.MimeType is { } mime && !mime.StartsWith(
                    (isImage ? MediaTypes.Image : MediaTypes.Video) + '/', StringComparison.OrdinalIgnoreCase)) ||
                (source.FileName is { } name &&
                 (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\'))))
            {
                throw ResolutionErrors.UpstreamChanged();
            }

            if (source.ExpiresAt is { } expiry)
            {
                if (!expiry.EndsWith('Z') || !DateTimeOffset.TryParse(
                        expiry, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                {
                    throw ResolutionErrors.UpstreamChanged();
                }

                if (parsed <= now)
                {
                    continue;
                }
            }

            var headers = source.Headers.Where(pair => SafeHeaderNames.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            if (headers.Values.Any(value => value.Contains('\r') || value.Contains('\n')))
            {
                throw ResolutionErrors.UpstreamChanged();
            }

            result.Add(source with { Headers = headers, DurationMs = isImage ? null : source.DurationMs });
        }

        return result.ToArray();
    }

    private static bool IsHttpUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        string.IsNullOrEmpty(uri.UserInfo);
}
