using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaLens.Contracts;
using MediaLens.Resolution;

namespace MediaLens.Platforms.Douyin;

/// <summary>读取精选公开阅读页的服务端数据，不执行页面脚本或获取身份材料。</summary>
internal static partial class JingxuanMetadataParser
{
    private const int MaximumJsonDepth = 64;
    private const int MaximumVariants = 128;
    private const int MaximumSelectedVariants = 2;
    private const int MaximumUrlCharacters = 16_384;
    private const int MaximumAudioChannels = 32;
    private const int RegexTimeoutMilliseconds = 1_000;
    private const long MillisecondsPerSecond = 1_000;
    private const long MaximumUnixSeconds = 253_402_300_799;
    private const string DataField = "data";
    private const string StoreStateField = "storeState";
    private const string DetailField = "detail";
    private const string VideoDataField = "videoData";
    private const string ResultField = "result";
    private const string WorkIdField = "gid";
    private const string TitleField = "title";
    private const string VideoModelField = "video_model";
    private const string MediaTypeField = "media_type";
    private const string DurationField = "video_duration";
    private const string ExpiryField = "url_expire";
    private const string VideoListField = "video_list";
    private const string VideoMetadataField = "video_meta";
    private const string FormatField = "vtype";
    private const string CodecField = "codec_type";
    private const string AudioChannelsField = "audio_channels";
    private const string WidthField = "vwidth";
    private const string HeightField = "vheight";
    private const string BitrateField = "bitrate";
    private const string PrimaryUrlField = "main_url";
    private const string BackupUrlField = "backup_url";
    private const string Mp4Format = "mp4";
    private const string H264Codec = "h264";
    private const string H265Codec = "h265";

    public static MetadataWork? Read(string body, string workId)
    {
        var match = SsrDataPattern().Match(body);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            // ParseValue 只读取赋值右侧的 JSON；后面的 JavaScript 不会被执行。
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(body[(match.Index + match.Length)..]),
                new JsonReaderOptions { MaxDepth = MaximumJsonDepth });
            if (!reader.Read())
            {
                return null;
            }

            using var document = JsonDocument.ParseValue(ref reader);
            var result = Child(Child(Child(Child(Child(document.RootElement, DataField), StoreStateField),
                DetailField), VideoDataField), ResultField);
            var id = Child(result, WorkIdField);
            var actualId = id.ValueKind == JsonValueKind.Number ? id.GetRawText() : ReadString(id);
            if (actualId != workId)
            {
                // 推荐列表和默认页面数据均不能替代目标作品，也不能据此断言作品删除。
                return null;
            }

            var modelText = ReadString(Child(result, VideoModelField));
            if (string.IsNullOrEmpty(modelText))
            {
                throw ResolutionErrors.UpstreamChanged();
            }

            using var model = JsonDocument.Parse(modelText, new JsonDocumentOptions { MaxDepth = MaximumJsonDepth });
            return Map(model.RootElement, ReadString(Child(result, TitleField)));
        }
        catch (JsonException)
        {
            throw ResolutionErrors.UpstreamChanged();
        }
    }

    private static MetadataWork Map(JsonElement model, string? description)
    {
        var entries = Child(model, VideoListField);
        if (ReadString(Child(model, MediaTypeField)) != MediaTypes.Video ||
            entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > MaximumVariants)
        {
            throw ResolutionErrors.UpstreamChanged();
        }

        var duration = ReadDuration(Child(model, DurationField));
        var expirySeconds = ReadPositiveLong(Child(model, ExpiryField));
        var expiry = expirySeconds is <= MaximumUnixSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(expirySeconds.Value)
            : (DateTimeOffset?)null;

        // 仅选已知 MP4 且带音轨的独立文件；不拼接 DASH 片段或独立音视频流。
        // 同等条件优先 H.264，再按尺寸和码率排序；限制候选数以控制上游请求量。
        var variants = entries.EnumerateArray()
            .Where(IsCompleteMp4)
            .OrderBy(variant => ReadString(Child(Child(variant, VideoMetadataField), CodecField)) == H264Codec ? 0 : 1)
            .ThenByDescending(variant => ReadPositiveLong(Child(Child(variant, VideoMetadataField), HeightField)))
            .ThenByDescending(variant => ReadPositiveLong(Child(Child(variant, VideoMetadataField), BitrateField)));
        var candidates = new List<MetadataCandidate>();
        var selectedVariants = 0;
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            var meta = Child(variant, VideoMetadataField);
            var previousCount = candidates.Count;
            foreach (var field in new[] { PrimaryUrlField, BackupUrlField })
            {
                var uri = ReadUrl(Child(variant, field));
                if (uri is null || !urls.Add(uri.OriginalString))
                {
                    continue;
                }

                candidates.Add(new MetadataCandidate(
                    Uri: uri,
                    Width: ReadPositiveInt(Child(meta, WidthField)),
                    Height: ReadPositiveInt(Child(meta, HeightField)),
                    DurationMs: duration,
                    ExpiresAt: expiry));
            }

            if (candidates.Count > previousCount && ++selectedVariants == MaximumSelectedVariants)
            {
                break;
            }
        }

        if (candidates.Count == 0)
        {
            throw ResolutionErrors.MediaIncomplete();
        }

        return new MetadataWork(MediaTypes.Video, description,
            [new MetadataItem(MediaTypes.Video, [], candidates.ToArray())]);
    }

    private static bool IsCompleteMp4(JsonElement variant)
    {
        var meta = Child(variant, VideoMetadataField);
        return ReadString(Child(meta, FormatField)) == Mp4Format &&
               ReadString(Child(meta, CodecField)) is H264Codec or H265Codec &&
               HasAudioChannels(Child(meta, AudioChannelsField));
    }

    private static bool HasAudioChannels(JsonElement node)
    {
        // 当前公开模型把双声道表示为字符串 "2.0"，不能按整数文本解析后误判为空。
        decimal channels;
        var parsed = node.ValueKind == JsonValueKind.Number
            ? node.TryGetDecimal(out channels)
            : decimal.TryParse(ReadString(node), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out channels);
        return parsed && channels is > 0 and <= MaximumAudioChannels && decimal.Truncate(channels) == channels;
    }

    private static Uri? ReadUrl(JsonElement node)
    {
        var value = ReadString(node);
        if (string.IsNullOrEmpty(value) || value.Length > MaximumUrlCharacters)
        {
            return null;
        }

        if (!value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                value = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // 这里只解码地址；真正请求前仍由共享 HTTP 边界校验域名、端口及每跳 DNS。
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;
    }

    private static long? ReadDuration(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Number || !node.TryGetDecimal(out var seconds) ||
            seconds <= 0 || seconds > long.MaxValue / MillisecondsPerSecond)
        {
            return null;
        }

        var milliseconds = decimal.Round(seconds * MillisecondsPerSecond, MidpointRounding.AwayFromZero);
        return milliseconds is > 0 and <= long.MaxValue ? (long)milliseconds : null;
    }

    private static JsonElement Child(JsonElement node, string field) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(field, out var value) ? value : default;

    private static string? ReadString(JsonElement node) => node.ValueKind == JsonValueKind.String ? node.GetString() : null;

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

    [GeneratedRegex("(?:window\\.)?_SSR_DATA\\s*=\\s*", RegexOptions.None, RegexTimeoutMilliseconds)]
    private static partial Regex SsrDataPattern();
}
