using System.Globalization;
using System.Net;
using MediaLens.Contracts;
using MediaLens.Http;
using MediaLens.Resolution;

namespace MediaLens.Platforms.Douyin;

internal sealed class DouyinMediaResolver(IUpstreamClient client, TimeProvider timeProvider)
{
    private const int MaximumResolvedCandidates = 2;
    private const int MaximumConcurrentItems = 4;

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/mp4"] = "mp4",
        ["video/webm"] = "webm",
        ["video/quicktime"] = "mov",
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
        ["image/webp"] = "webp",
        ["image/heic"] = "heic",
        ["image/heif"] = "heif",
        ["image/avif"] = "avif"
    };

    public async Task<MediaItem[]> ResolveAsync(MetadataWork metadata, string workId, CancellationToken cancellationToken)
    {
        using var concurrency = new SemaphoreSlim(MaximumConcurrentItems);
        // WhenAll 按输入任务顺序返回，不按上游完成时间重排图集或实况归属。
        var tasks = metadata.Items.Select((item, offset) => ResolveItemAsync(item, offset + 1)).ToArray();
        return await Task.WhenAll(tasks);

        async Task<MediaItem> ResolveItemAsync(MetadataItem item, int index)
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                var images = item.Images.Length == 0
                    ? []
                    : await ResolveSourcesAsync(item.Images, workId, index, MediaTypes.Image, cancellationToken);
                var videos = item.Videos.Length == 0
                    ? []
                    : await ResolveSourcesAsync(item.Videos, workId, index, MediaTypes.Video, cancellationToken);
                return new MediaItem(index, item.Type, images, videos);
            }
            finally
            {
                concurrency.Release();
            }
        }
    }

    private async Task<MediaSource[]> ResolveSourcesAsync(
        MetadataCandidate[] candidates,
        string workId,
        int index,
        string type,
        CancellationToken cancellationToken)
    {
        var result = new List<MediaSource>();
        var temporaryFailure = false;
        int? retryAfterSeconds = null;
        foreach (var candidate in candidates)
        {
            if (candidate.ExpiresAt is { } expiry && expiry <= timeProvider.GetUtcNow())
            {
                continue;
            }

            try
            {
                var location = await client.ResolveMediaAsync(candidate.Uri, type, cancellationToken);
                if (location.StatusCode >= (int)HttpStatusCode.InternalServerError ||
                    location.StatusCode == (int)HttpStatusCode.TooManyRequests)
                {
                    temporaryFailure = true;
                    retryAfterSeconds ??= location.RetryAfterSeconds;
                    continue;
                }

                if (location.StatusCode is < (int)HttpStatusCode.OK or >= (int)HttpStatusCode.MultipleChoices)
                {
                    continue;
                }

                // 文件名只依据响应确认的 MIME，未知格式时不猜测后缀。
                var extension = location.MimeType is null ? null : Extensions.GetValueOrDefault(location.MimeType);
                var fileName = extension is null
                    ? null
                    : "douyin_" + workId + '_' + index.ToString("D2", CultureInfo.InvariantCulture) + '_' + type + '.' + extension;
                result.Add(new MediaSource(
                    Url: location.FinalUri.OriginalString,
                    Headers: client.CreateDownloadHeaders(),
                    MimeType: location.MimeType,
                    FileName: fileName,
                    Width: candidate.Width,
                    Height: candidate.Height,
                    DurationMs: type == MediaTypes.Image ? null : candidate.DurationMs,
                    SizeBytes: location.SizeBytes,
                    ExpiresAt: candidate.ExpiresAt is { } knownExpiry ? ResolutionService.FormatUtc(knownExpiry) : null));
                if (result.Count == MaximumResolvedCandidates)
                {
                    // 调用方只需下载一个候选；保留可用备用后停止无收益的逐档网络探测。
                    break;
                }
            }
            catch (UpstreamRequestException)
            {
                // 无法确认的备用地址可排除，但不能删除必需图片或把实况降级成普通图。
            }
            catch (HttpRequestException)
            {
                temporaryFailure = true;
            }
            catch (IOException)
            {
                temporaryFailure = true;
            }
        }

        if (result.Count == 0)
        {
            throw temporaryFailure ? ResolutionErrors.UpstreamUnavailable(retryAfterSeconds) : ResolutionErrors.MediaIncomplete();
        }

        return result.ToArray();
    }
}
