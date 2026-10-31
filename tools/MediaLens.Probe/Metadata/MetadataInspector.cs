using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaLens.Probe.Metadata;

/// <summary>只观察匹配作品的结构化数据，不运行网页脚本，也不输出媒体地址或作品标识。</summary>
internal static partial class MetadataInspector
{
    private const string AwemeId = "aweme_id";
    private const string CamelAwemeId = "awemeId";
    private const string Images = "images";
    private const string ImageList = "image_list";
    private const string ImagePostInfo = "image_post_info";
    private const string Video = "video";
    private const string PlayAddress = "play_addr";
    private const string UrlList = "url_list";
    private const string LivePhotoType = "live_photo_type";
    private const string DataGroup = "data";
    private const string PlainJson = "json";
    private const string RouterJson = "routerData";
    private const string RenderJson = "renderData";
    private const string NoJson = "none";
    private const string MalformedJson = "malformedJson";

    public static MetadataSummary Inspect(string body, string workId)
    {
        var payload = GetJsonPayload(body);
        if (payload.Text is null)
        {
            return MetadataSummary.Empty(NoJson);
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(payload.Text);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                MaxDepth = ProbeDefaults.MaximumJsonDepth
            });
            if (!reader.Read())
            {
                return MetadataSummary.Empty(MalformedJson);
            }

            using var document = JsonDocument.ParseValue(ref reader);
            var remaining = ProbeDefaults.MaximumTraversalNodes;
            var work = FindWork(document.RootElement, workId, depth: 0, ref remaining);
            return work.ValueKind == JsonValueKind.Object
                ? InspectWork(work, payload.Kind)
                : MetadataSummary.Empty(payload.Kind);
        }
        catch (JsonException)
        {
            return MetadataSummary.Empty(MalformedJson);
        }
    }

    private static (string Kind, string? Text) GetJsonPayload(string body)
    {
        var trimmed = body.AsSpan().TrimStart();
        if (!trimmed.IsEmpty && trimmed[0] is '{' or '[')
        {
            return (PlainJson, body);
        }

        var router = RouterAssignmentPattern().Match(body);
        if (router.Success)
        {
            return (RouterJson, body[(router.Index + router.Length)..]);
        }

        var render = RenderScriptPattern().Match(body);
        return render.Success
            ? (RenderJson, Uri.UnescapeDataString(WebUtility.HtmlDecode(render.Groups[DataGroup].Value)))
            : (NoJson, null);
    }

    private static JsonElement FindWork(JsonElement node, string workId, int depth, ref int remaining)
    {
        if (depth > ProbeDefaults.MaximumTraversalDepth || remaining-- <= 0)
        {
            return default;
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            if (ReadId(node, AwemeId) == workId || ReadId(node, CamelAwemeId) == workId)
            {
                return node;
            }

            foreach (var property in node.EnumerateObject())
            {
                var found = FindWork(property.Value, workId, depth + 1, ref remaining);
                if (found.ValueKind == JsonValueKind.Object)
                {
                    return found;
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray())
            {
                var found = FindWork(child, workId, depth + 1, ref remaining);
                if (found.ValueKind == JsonValueKind.Object)
                {
                    return found;
                }
            }
        }

        return default;
    }

    private static MetadataSummary InspectWork(JsonElement work, string payloadKind)
    {
        var images = Child(work, Images);
        if (images.ValueKind != JsonValueKind.Array)
        {
            var imagePost = Child(work, ImagePostInfo);
            images = Child(imagePost, Images);
            if (images.ValueKind != JsonValueKind.Array)
            {
                images = Child(imagePost, ImageList);
            }
            if (images.ValueKind != JsonValueKind.Array)
            {
                images = Child(work, ImageList);
            }
        }

        var imageCount = images.ValueKind == JsonValueKind.Array ? images.GetArrayLength() : 0;
        var liveCount = 0;
        var imageSources = 0;
        var videoSources = 0;
        var complete = true;

        if (imageCount == 0)
        {
            videoSources = CountUrls(Child(Child(work, Video), PlayAddress));
            complete = videoSources > 0;
        }
        else
        {
            foreach (var image in images.EnumerateArray())
            {
                var stillCount = CountUrls(image);
                imageSources += stillCount;
                complete &= stillCount > 0;

                var imageVideo = Child(image, Video);
                var clip = Child(imageVideo, PlayAddress);
                var clipCount = CountUrls(clip);
                var marker = Child(image, LivePhotoType);
                var markedLive = marker.ValueKind == JsonValueKind.Number &&
                                 marker.TryGetInt32(out var markerValue) && markerValue > 0;
                if (markedLive || imageVideo.ValueKind == JsonValueKind.Object)
                {
                    liveCount++;
                    videoSources += clipCount;
                    complete &= clipCount > 0;
                }
            }
        }

        return new MetadataSummary(
            PayloadKind: payloadKind,
            WorkMatched: true,
            CompleteMediaMetadata: complete,
            ImageCount: imageCount,
            LivePhotoCount: liveCount,
            ImageSourceCount: imageSources,
            VideoSourceCount: videoSources);
    }

    private static JsonElement Child(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var child)
            ? child
            : default;

    private static string? ReadId(JsonElement node, string name)
    {
        var id = Child(node, name);
        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null
        };
    }

    private static int CountUrls(JsonElement node)
    {
        var urls = Child(node, UrlList);
        return urls.ValueKind == JsonValueKind.Array
            ? urls.EnumerateArray().Count(value =>
                value.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https")
            : 0;
    }

    [GeneratedRegex("(?:window\\.)?_ROUTER_DATA\\s*=\\s*", RegexOptions.None, ProbeDefaults.RegexTimeoutMilliseconds)]
    private static partial Regex RouterAssignmentPattern();

    [GeneratedRegex(
        "<script\\b(?=[^>]*\\bid\\s*=\\s*[\"']RENDER_DATA[\"'])[^>]*>(?<data>.*?)</script>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase,
        ProbeDefaults.RegexTimeoutMilliseconds)]
    private static partial Regex RenderScriptPattern();
}

internal sealed record MetadataSummary(
    string PayloadKind,
    bool WorkMatched,
    bool CompleteMediaMetadata,
    int ImageCount,
    int LivePhotoCount,
    int ImageSourceCount,
    int VideoSourceCount)
{
    public static MetadataSummary Empty(string payloadKind) => new(
        PayloadKind: payloadKind,
        WorkMatched: false,
        CompleteMediaMetadata: false,
        ImageCount: 0,
        LivePhotoCount: 0,
        ImageSourceCount: 0,
        VideoSourceCount: 0);
}
