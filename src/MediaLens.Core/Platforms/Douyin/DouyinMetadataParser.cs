using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaLens.Contracts;
using MediaLens.Resolution;

namespace MediaLens.Platforms.Douyin;

/// <summary>只读取目标作品的数据；不运行脚本，也不把推荐作品或图集音乐当作媒体项。</summary>
internal static partial class DouyinMetadataParser
{
    private const int MaximumJsonDepth = 64;
    private const int MaximumTraversalDepth = 32;
    private const int MaximumTraversalNodes = 50_000;
    private const int MaximumAlbumItems = 128;
    private const int MaximumCandidates = 16;
    private const int ImageAlbumAwemeType = 68;
    private const int RegexTimeoutMilliseconds = 1_000;
    private const string DataGroup = "data";
    private const string IdField = "aweme_id";
    private const string CamelIdField = "awemeId";
    private const string ImagesField = "images";
    private const string ImageListField = "image_list";
    private const string ImagePostField = "image_post_info";
    private const string VideoField = "video";
    private const string PlayAddressField = "play_addr";
    private const string UrlListField = "url_list";
    private const string WidthField = "width";
    private const string HeightField = "height";
    private const string DurationField = "duration";

    public static MetadataWork? Read(string body, string workId)
    {
        var payload = ExtractPayload(body);
        if (payload is null)
        {
            return JingxuanMetadataParser.Read(body, workId);
        }

        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(payload), new JsonReaderOptions
            {
                MaxDepth = MaximumJsonDepth
            });
            if (!reader.Read())
            {
                return null;
            }

            using var document = JsonDocument.ParseValue(ref reader);
            var remaining = MaximumTraversalNodes;
            var work = FindWork(document.RootElement, workId, depth: 0, ref remaining);
            if (work.ValueKind != JsonValueKind.Object)
            {
                var statusMessage = ReadString(Child(document.RootElement, "status_msg"));
                if (statusMessage is "作品已删除" or "视频已删除" or "作品不存在")
                {
                    throw new ResolutionException(
                        ErrorCodes.ContentUnavailable, "上游明确报告该作品不可用。", (int)HttpStatusCode.NotFound);
                }

                // 空的公开页 loader 或空列表不等于作品被删除，也不触发身份回退。
                return null;
            }

            return Map(work);
        }
        catch (JsonException)
        {
            throw ResolutionErrors.UpstreamChanged();
        }
    }

    private static string? ExtractPayload(string body)
    {
        var trimmed = body.AsSpan().TrimStart();
        if (!trimmed.IsEmpty && trimmed[0] is '{' or '[')
        {
            return body;
        }

        var router = RouterPattern().Match(body);
        if (router.Success)
        {
            return body[(router.Index + router.Length)..];
        }

        var render = RenderPattern().Match(body);
        return render.Success
            ? Uri.UnescapeDataString(WebUtility.HtmlDecode(render.Groups[DataGroup].Value))
            : null;
    }

    private static JsonElement FindWork(JsonElement node, string id, int depth, ref int remaining)
    {
        if (depth > MaximumTraversalDepth || remaining-- <= 0)
        {
            return default;
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            if (ReadId(Child(node, IdField)) == id || ReadId(Child(node, CamelIdField)) == id)
            {
                return node;
            }

            foreach (var property in node.EnumerateObject())
            {
                var result = FindWork(property.Value, id, depth + 1, ref remaining);
                if (result.ValueKind == JsonValueKind.Object)
                {
                    return result;
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in node.EnumerateArray())
            {
                var result = FindWork(value, id, depth + 1, ref remaining);
                if (result.ValueKind == JsonValueKind.Object)
                {
                    return result;
                }
            }
        }

        return default;
    }

    private static MetadataWork Map(JsonElement work)
    {
        var images = Child(work, ImagesField);
        if (images.ValueKind != JsonValueKind.Array)
        {
            var post = Child(work, ImagePostField);
            images = Child(post, ImagesField);
            if (images.ValueKind != JsonValueKind.Array)
            {
                images = Child(post, ImageListField);
            }
            if (images.ValueKind != JsonValueKind.Array)
            {
                images = Child(work, ImageListField);
            }
        }

        var items = new List<MetadataItem>();
        var isAlbum = (images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0) ||
                      ReadPositiveLong(Child(work, "aweme_type")) == ImageAlbumAwemeType;
        if (isAlbum)
        {
            if (images.ValueKind != JsonValueKind.Array || images.GetArrayLength() is 0 or > MaximumAlbumItems)
            {
                throw ResolutionErrors.MediaIncomplete();
            }

            foreach (var image in images.EnumerateArray())
            {
                var stills = ReadAddress(image, image, isVideo: false).ToArray();
                var clip = Child(image, VideoField);
                var live = clip.ValueKind == JsonValueKind.Object ||
                           ReadPositiveLong(Child(image, "live_photo_type")) is > 0 ||
                           Child(image, "is_live_photo").ValueKind == JsonValueKind.True;
                var videos = live ? ReadVideo(clip) : [];
                if (stills.Length == 0 || (live && videos.Length == 0))
                {
                    throw ResolutionErrors.MediaIncomplete();
                }

                items.Add(new MetadataItem(live ? MediaTypes.LivePhoto : MediaTypes.Image, stills, videos));
            }
        }
        else
        {
            var video = Child(work, VideoField);
            if (video.ValueKind != JsonValueKind.Object)
            {
                throw ResolutionErrors.UpstreamChanged();
            }

            var candidates = ReadVideo(video);
            if (candidates.Length == 0)
            {
                throw ResolutionErrors.MediaIncomplete();
            }

            items.Add(new MetadataItem(MediaTypes.Video, [], candidates));
        }

        return new MetadataWork(
            isAlbum ? MediaTypes.Album : MediaTypes.Video,
            ReadString(Child(work, "desc")),
            items.ToArray());
    }

    private static MetadataCandidate[] ReadVideo(JsonElement video)
    {
        var addresses = new List<MetadataCandidate>();
        addresses.AddRange(ReadAddress(Child(video, PlayAddressField), video, isVideo: true));
        var bitRates = Child(video, "bit_rate");
        if (bitRates.ValueKind == JsonValueKind.Array)
        {
            foreach (var variant in bitRates.EnumerateArray())
            {
                // 每种码率使用自身地址上的尺寸，不能套用另一候选的尺寸。
                var address = Child(variant, PlayAddressField);
                addresses.AddRange(ReadAddress(address, address, isVideo: true));
            }
        }

        return addresses.DistinctBy(candidate => candidate.Uri.OriginalString)
            .Take(MaximumCandidates).ToArray();
    }

    private static IEnumerable<MetadataCandidate> ReadAddress(JsonElement address, JsonElement parent, bool isVideo)
    {
        var urls = Child(address, UrlListField);
        if (urls.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var value in urls.EnumerateArray().Take(MaximumCandidates))
        {
            var text = ReadString(value);
            if (text is null || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            yield return new MetadataCandidate(
                Uri: uri,
                Width: ReadPositiveInt(Child(address, WidthField)) ?? ReadPositiveInt(Child(parent, WidthField)),
                Height: ReadPositiveInt(Child(address, HeightField)) ?? ReadPositiveInt(Child(parent, HeightField)),
                DurationMs: isVideo
                    ? ReadPositiveLong(Child(address, DurationField)) ?? ReadPositiveLong(Child(parent, DurationField))
                    : null,
                ExpiresAt: null);
        }
    }

    private static JsonElement Child(JsonElement node, string key) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value) ? value : default;

    private static string? ReadString(JsonElement node) => node.ValueKind == JsonValueKind.String ? node.GetString() : null;

    private static string? ReadId(JsonElement node) =>
        node.ValueKind == JsonValueKind.Number ? node.GetRawText() : ReadString(node);

    private static long? ReadPositiveLong(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var value) && value > 0)
        {
            return value;
        }

        return node.ValueKind == JsonValueKind.String && long.TryParse(
            node.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0 ? value : null;
    }

    private static int? ReadPositiveInt(JsonElement node) =>
        ReadPositiveLong(node) is { } value and <= int.MaxValue ? (int)value : null;

    [GeneratedRegex("(?:window\\.)?_ROUTER_DATA\\s*=\\s*", RegexOptions.None, RegexTimeoutMilliseconds)]
    private static partial Regex RouterPattern();

    [GeneratedRegex(
        "<script\\b(?=[^>]*\\bid\\s*=\\s*[\"']RENDER_DATA[\"'])[^>]*>(?<data>.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        RegexTimeoutMilliseconds)]
    private static partial Regex RenderPattern();
}

internal sealed record MetadataWork(string WorkType, string? Description, MetadataItem[] Items);

internal sealed record MetadataItem(string Type, MetadataCandidate[] Images, MetadataCandidate[] Videos);

internal sealed record MetadataCandidate(Uri Uri, int? Width, int? Height, long? DurationMs, DateTimeOffset? ExpiresAt);
