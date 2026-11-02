using System.Globalization;
using System.Net;
using System.Text.Json;
using MediaLens.Contracts;
using MediaLens.Http;
using MediaLens.Resolution;
using static MediaLens.Platforms.Bilibili.BilibiliProtocol;

namespace MediaLens.Platforms.Bilibili;

/// <summary>只返回所选分 P 的完整 MP4；不生成身份，不合并轨道或连续片段。</summary>
public sealed class BilibiliAdapter : IPlatformAdapter, IDisposable
{
    public const string PlatformId = "bilibili";
    private const string ApiRoot = "https://api.bilibili.com";
    private const string DetailPath = "/x/web-interface/view";
    private const string SignedDetailPath = "/x/web-interface/wbi/view";
    private const string PlayPath = "/x/player/playurl";
    private const string SignedPlayPath = "/x/player/wbi/playurl";
    private const string Mp4Mime = "video/mp4";
    private const int MillisecondsPerSecond = 1_000;
    private const int DurationRoundingToleranceMs = MillisecondsPerSecond;
    private const int MaximumCandidateAttempts = 16;
    private const string RequestedQuality = "80";
    private const string ProgressiveMp4Format = "1";
    private const string CurrentStreamVersion = "0";
    private const string DisableFourK = "0";
    private const string Html5Platform = "html5";
    private const string PreferHighQuality = "1";
    private readonly IUpstreamClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly BilibiliWbiKeys _keys;

    public BilibiliAdapter(IUpstreamClient client, TimeProvider timeProvider)
    {
        _client = client;
        _timeProvider = timeProvider;
        _keys = new BilibiliWbiKeys(client, timeProvider);
    }

    public string Id => PlatformId;
    public string DisplayName => "哔哩哔哩";
    // 真实匿名、独立完整下载和原生解码证据见 docs/validation/cookie-free-bilibili.md。
    public PlatformCapabilities Capabilities { get; } = new(
        CapabilityStates.Supported,
        CapabilityStates.Unsupported,
        CapabilityStates.Unsupported,
        CapabilityStates.Unsupported);

    public bool CanHandle(Uri uri) => PlatformHttpProfile.Bilibili.IsPlatformUri(uri);

    public async Task<ResolvedWork> ResolveAsync(Uri uri, CancellationToken cancellationToken)
    {
        var requestedPage = BilibiliWorkLink.ReadPage(uri);
        if (uri.IdnHost.Equals(BilibiliWorkLink.ShortHost, StringComparison.OrdinalIgnoreCase))
        {
            var page = await _client.GetAsync(uri, cancellationToken);
            if (page.StatusCode >= (int)HttpStatusCode.InternalServerError ||
                page.StatusCode == (int)HttpStatusCode.TooManyRequests)
            {
                throw ResolutionErrors.UpstreamUnavailable(page.RetryAfterSeconds);
            }

            if (page.StatusCode >= (int)HttpStatusCode.BadRequest)
            {
                throw ResolutionErrors.AnonymousUnavailable();
            }

            uri = page.FinalUri;
        }

        var link = BilibiliWorkLink.Parse(uri);
        if (requestedPage.Specified)
        {
            // 原分享明确指定 P 时，跳转不能把它悄悄重置为首 P。
            link = link with { Page = requestedPage.Page };
        }

        var parameters = link.Bvid is { } bvid
            ? new Dictionary<string, string> { ["bvid"] = bvid }
            : new Dictionary<string, string> { ["aid"] = link.Aid! };
        var detail = await GetDataAsync(DetailPath, SignedDetailPath, parameters, cancellationToken);
        var part = ReadPart(detail, link);
        var play = await GetDataAsync(PlayPath, SignedPlayPath, new Dictionary<string, string>
        {
            ["bvid"] = part.Bvid,
            ["cid"] = part.Cid,
            ["qn"] = RequestedQuality,
            ["fnval"] = ProgressiveMp4Format,
            ["fnver"] = CurrentStreamVersion,
            ["fourk"] = DisableFourK,
            ["platform"] = Html5Platform,
            ["high_quality"] = PreferHighQuality
        }, cancellationToken);
        var sources = await ReadSourcesAsync(play, part, cancellationToken);
        return new ResolvedWork(
            Platform: PlatformId,
            WorkId: part.Bvid,
            WorkType: MediaTypes.Video,
            WebUrl: "https://www.bilibili.com/video/" + part.Bvid + "?p=" + part.Page.ToString(CultureInfo.InvariantCulture),
            Description: StringValue(detail, "desc"),
            Items: [new MediaItem(1, MediaTypes.Video, [], sources)]);
    }

    private async Task<JsonElement> GetDataAsync(
        string path,
        string signedPath,
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var query = string.Join('&', parameters.Select(pair =>
            Uri.EscapeDataString(pair.Key) + '=' + Uri.EscapeDataString(pair.Value)));
        var page = await _client.GetAsync(new Uri(ApiRoot + path + '?' + query), cancellationToken);
        if (!MayNeedSignature(page))
        {
            return ReadData(page, contentLookup: path == DetailPath);
        }

        var mixin = await _keys.GetAsync(cancellationToken);
        page = await SignedRequestAsync(signedPath, parameters, mixin, cancellationToken);
        if (SignatureRejected(page))
        {
            mixin = await _keys.GetAsync(cancellationToken, refresh: true);
            page = await SignedRequestAsync(signedPath, parameters, mixin, cancellationToken);
        }

        return ReadData(page, contentLookup: path == DetailPath);
    }

    private Task<MetadataPage> SignedRequestAsync(
        string path,
        Dictionary<string, string> parameters,
        string mixin,
        CancellationToken cancellationToken) =>
        _client.GetAsync(new Uri(ApiRoot + path + '?' + BilibiliWbiSigner.Sign(
            parameters, mixin, _timeProvider.GetUtcNow().ToUnixTimeSeconds())), cancellationToken);

    private static SelectedPart ReadPart(JsonElement detail, BilibiliWorkLink link)
    {
        var bvid = Text(detail, "bvid");
        var aid = Text(detail, "aid");
        if (bvid is null || !BilibiliWorkLink.IsBvid(bvid) || !BilibiliWorkLink.IsPositiveId(aid) ||
            (link.Bvid is not null && link.Bvid != bvid) || (link.Aid is not null && link.Aid != aid))
        {
            throw ResolutionErrors.UpstreamChanged();
        }

        if (!detail.TryGetProperty("rights", out var rights) || rights.ValueKind != JsonValueKind.Object)
        {
            throw ResolutionErrors.UpstreamChanged();
        }

        if (!string.IsNullOrEmpty(Text(detail, "redirect_url")) ||
            Flag(detail, "is_upower_exclusive") || Flag(detail, "is_upower_preview") ||
            Flag(detail, "is_upower_exclusive_with_qa") || Flag(rights, "is_stein_gate") ||
            Flag(rights, "pay") || Flag(rights, "ugc_pay") || Flag(rights, "ugc_pay_preview") ||
            Flag(rights, "arc_pay") || Flag(rights, "movie"))
        {
            throw BilibiliWorkLink.Unsupported();
        }

        if (!detail.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array ||
            pages.GetArrayLength() == 0)
        {
            throw ResolutionErrors.UpstreamChanged();
        }

        if (link.Page > pages.GetArrayLength())
        {
            throw ResolutionErrors.InvalidInput();
        }

        var selected = pages[link.Page - 1];
        var cid = Text(selected, "cid");
        var duration = PositiveNumber(selected, "duration");
        if (!BilibiliWorkLink.IsPositiveId(cid) || PositiveNumber(selected, "page") != link.Page ||
            duration is null || duration > long.MaxValue / MillisecondsPerSecond)
        {
            throw ResolutionErrors.UpstreamChanged();
        }

        return new SelectedPart(bvid, cid!, link.Page, duration.Value * MillisecondsPerSecond);
    }

    private async Task<MediaSource[]> ReadSourcesAsync(
        JsonElement play,
        SelectedPart part,
        CancellationToken cancellationToken)
    {
        var format = Text(play, "format");
        if (format is null)
        {
            throw play.TryGetProperty("dash", out _)
                ? BilibiliWorkLink.Unsupported()
                : ResolutionErrors.UpstreamChanged();
        }

        if (!(format.StartsWith("mp4", StringComparison.Ordinal) || format == "hdmp4"))
        {
            throw BilibiliWorkLink.Unsupported();
        }

        if (!play.TryGetProperty("durl", out var segments) || segments.ValueKind != JsonValueKind.Array)
        {
            throw play.TryGetProperty("dash", out _)
                ? BilibiliWorkLink.Unsupported()
                : ResolutionErrors.UpstreamChanged();
        }

        if (segments.GetArrayLength() == 0)
        {
            throw ResolutionErrors.MediaIncomplete();
        }

        if (segments.GetArrayLength() != 1)
        {
            throw BilibiliWorkLink.Unsupported();
        }

        var segment = segments[0];
        var duration = PositiveNumber(play, "timelength");
        var segmentDuration = PositiveNumber(segment, "length");
        if (duration is null || segmentDuration is null)
        {
            throw ResolutionErrors.UpstreamChanged();
        }

        if (Math.Abs(duration.Value - part.DurationMs) > DurationRoundingToleranceMs ||
            Math.Abs(duration.Value - segmentDuration.Value) > DurationRoundingToleranceMs ||
            Flag(play, "is_preview") || Flag(play, "is_upower_preview") ||
            Flag(play, "is_ugc_pay_preview") || Flag(play, "is_pay_preview"))
        {
            throw BilibiliWorkLink.Unsupported();
        }

        var addresses = new List<string?> { Text(segment, "url") };
        if (segment.TryGetProperty("backup_url", out var backups) && backups.ValueKind == JsonValueKind.Array)
        {
            addresses.AddRange(backups.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()));
        }

        var result = new List<MediaSource>();
        var transient = false;
        int? retryAfter = null;
        foreach (var address in addresses.Distinct(StringComparer.Ordinal).Take(MaximumCandidateAttempts))
        {
            if (address is null || !Uri.TryCreate(address.StartsWith("//", StringComparison.Ordinal)
                    ? "https:" + address
                    : address, UriKind.Absolute, out var uri))
            {
                continue;
            }

            try
            {
                var location = await _client.ResolveMediaAsync(uri, MediaTypes.Video, cancellationToken);
                if (location.StatusCode >= (int)HttpStatusCode.InternalServerError ||
                    location.StatusCode == (int)HttpStatusCode.TooManyRequests)
                {
                    transient = true;
                    retryAfter ??= location.RetryAfterSeconds;
                    continue;
                }

                if (location.StatusCode is < (int)HttpStatusCode.OK or >= (int)HttpStatusCode.MultipleChoices ||
                    (location.MimeType is not null && !string.Equals(location.MimeType, Mp4Mime, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // 原始详情尺寸可能属于更高清晰度；没有候选尺寸时保持 null。
                result.Add(new MediaSource(
                    Url: location.FinalUri.OriginalString,
                    Headers: _client.CreateDownloadHeaders(),
                    MimeType: location.MimeType is null ? null : Mp4Mime,
                    FileName: string.Equals(location.MimeType, Mp4Mime, StringComparison.OrdinalIgnoreCase)
                        ? "bilibili_" + part.Bvid + "_p" + part.Page.ToString(CultureInfo.InvariantCulture) + "_01_video.mp4"
                        : null,
                    Width: AsDimension(PositiveNumber(segment, "width")),
                    Height: AsDimension(PositiveNumber(segment, "height")),
                    DurationMs: duration,
                    SizeBytes: location.SizeBytes ?? PositiveNumber(segment, "size"),
                    ExpiresAt: null));
                // 首个完整 MP4 已确认可用，后续镜像只在前面的候选失败时探测
                break;
            }
            catch (UpstreamRequestException)
            {
                // 单个不可信或不可用备用来源可以排除；不会删除必需媒体后返回空成功。
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                transient = true;
            }
            catch (HttpRequestException)
            {
                transient = true;
            }
            catch (IOException)
            {
                transient = true;
            }
        }

        if (result.Count == 0)
        {
            throw transient
                ? ResolutionErrors.UpstreamUnavailable(retryAfter)
                : ResolutionErrors.MediaIncomplete();
        }

        return result.ToArray();
    }

    private static int? AsDimension(long? value) => value is > 0 and <= int.MaxValue ? (int)value : null;

    public void Dispose() => _keys.Dispose();

    private sealed record SelectedPart(string Bvid, string Cid, int Page, long DurationMs);
}
