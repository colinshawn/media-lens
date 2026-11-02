using System.Net;
using System.Text;
using System.Text.Json;
using MediaLens.Contracts;
using MediaLens.Http;
using MediaLens.Platforms.Douyin;
using MediaLens.Resolution;

namespace MediaLens.Tests;

public sealed class JingxuanMetadataTests
{
    private const string DefaultVariant = """
        {"main_url":"https://v3.douyinvod.com/synthetic.mp4?sig=A%2FB%2B",
         "backup_url":"https://v4.douyinvod.com/synthetic.mp4?sig=backup",
         "video_meta":{"vtype":"mp4","codec_type":"h264","audio_channels":"2.0",
           "vwidth":1080,"vheight":1920,"bitrate":2000000}}
        """;
    private const string DefaultModel = """
        {"status":10,"media_type":"video","video_duration":8.134,"url_expire":1798761600,
         "video_list":[
           {"main_url":"https://v3.douyinvod.com/synthetic.mp4?sig=A%2FB%2B",
            "backup_url":"https://v4.douyinvod.com/synthetic.mp4?sig=backup",
            "video_meta":{"vtype":"mp4","codec_type":"h264","audio_channels":"2.0",
              "vwidth":1080,"vheight":1920,"bitrate":2000000}}
         ]}
        """;

    internal static string Page(string model = DefaultModel, string workId = Fixtures.WorkId) =>
        "window._SSR_DATA = {\"data\":{\"storeState\":{\"detail\":{\"videoData\":{\"result\":{" +
        "\"gid\":\"" + workId + "\",\"title\":\"合成精选描述\",\"video_model\":" +
        JsonSerializer.Serialize(model) + "}}}}}}; throw 'must-not-run';";

    [Fact]
    public async Task FallbackReadsOnlyMatchingSsrModelAndStopsAtTheFirstUsableCdn()
    {
        var upstream = new StubUpstreamClient(string.Empty)
        {
            MetadataHandler = uri => new MetadataPage(
                FinalUri: uri,
                StatusCode: (int)HttpStatusCode.OK,
                ContentType: "text/html",
                Body: uri.IdnHost == "jingxuan.douyin.com" ? Page() : "window._ROUTER_DATA = {};",
                BytesRead: 0,
                RetryAfterSeconds: null)
        };
        var work = await new DouyinAdapter(upstream, new FixedClock()).ResolveAsync(
            new Uri(Fixtures.WorkUrl), CancellationToken.None);

        Assert.Equal("合成精选描述", work.Description);
        Assert.Equal(MediaTypes.Video, work.WorkType);
        var item = Assert.Single(work.Items);
        Assert.Empty(item.ImageSources);
        var source = Assert.Single(item.VideoSources);
        Assert.EndsWith("?sig=A%2FB%2B", source.Url);
        Assert.Equal(1080, source.Width);
        Assert.Equal(1920, source.Height);
        Assert.Equal(8134, source.DurationMs);
        Assert.Equal("2027-01-01T00:00:00.0000000Z", source.ExpiresAt);
        Assert.DoesNotContain(upstream.Requests, uri => uri.IdnHost == "v4.douyinvod.com");
        Assert.Equal(["api.amemv.com", "www.iesdouyin.com", "jingxuan.douyin.com"],
            upstream.Requests.Take(3).Select(uri => uri.IdnHost));
    }

    [Fact]
    public void AnotherWorkOrAnEmptyCatalogCannotBecomeTheRequestedVideo()
    {
        Assert.Null(JingxuanMetadataParser.Read(Page(workId: "999999"), Fixtures.WorkId));
        Assert.Null(JingxuanMetadataParser.Read("window._SSR_DATA = {\"data\":{}};", Fixtures.WorkId));
    }

    [Fact]
    public async Task RefusedDetailAndShareRoutesCanUseAnIndependentAnonymousReader()
    {
        var upstream = new StubUpstreamClient(string.Empty)
        {
            MetadataHandler = uri => new MetadataPage(
                FinalUri: uri,
                StatusCode: uri.IdnHost == "jingxuan.douyin.com"
                    ? (int)HttpStatusCode.OK
                    : (int)HttpStatusCode.Forbidden,
                ContentType: "text/html",
                Body: uri.IdnHost == "jingxuan.douyin.com" ? Page() : string.Empty,
                BytesRead: 0,
                RetryAfterSeconds: null)
        };
        var work = await new DouyinAdapter(upstream, new FixedClock()).ResolveAsync(
            new Uri(Fixtures.WorkUrl), CancellationToken.None);
        Assert.Equal(MediaTypes.Video, work.WorkType);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("{\"media_type\":\"album\",\"video_list\":[]}")]
    public void InvalidOrUnsupportedModelsProduceProtocolErrors(string model)
    {
        var error = Assert.Throws<ResolutionException>(() => JingxuanMetadataParser.Read(Page(model), Fixtures.WorkId));
        Assert.Equal(ErrorCodes.UpstreamChanged, error.Error.Code);
    }

    [Theory]
    [InlineData("\"vtype\":\"mp4\"", "\"vtype\":\"dash\"")]
    [InlineData("\"audio_channels\":\"2.0\"", "\"audio_channels\":\"0\"")]
    [InlineData("\"codec_type\":\"h264\"", "\"codec_type\":\"unknown\"")]
    [InlineData("\"main_url\":\"https://v3.douyinvod.com/synthetic.mp4?sig=A%2FB%2B\"", "\"main_url\":\"file:///private/synthetic\"")]
    public void IncompleteStreamsAndInvalidAddressesCannotBeDownloadSuccess(string original, string replacement)
    {
        var model = DefaultModel.Replace(original, replacement, StringComparison.Ordinal)
            .Replace("https://v4.douyinvod.com/synthetic.mp4?sig=backup", "invalid-base64", StringComparison.Ordinal);
        var error = Assert.Throws<ResolutionException>(() => JingxuanMetadataParser.Read(Page(model), Fixtures.WorkId));
        Assert.Equal(ErrorCodes.MediaIncomplete, error.Error.Code);
    }

    [Fact]
    public void Base64AddressesAreDecodedWithoutRewritingTheQuery()
    {
        const string url = "https://v3.douyinvod.com/synthetic.mp4?sig=A%2FB%2B";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(url));
        var model = DefaultModel.Replace(url, encoded, StringComparison.Ordinal);
        var parsed = JingxuanMetadataParser.Read(Page(model), Fixtures.WorkId);
        Assert.Equal(url, parsed!.Items[0].Videos[0].Uri.OriginalString);
    }

    [Fact]
    public void SelectionPrefersCompatibleHighQualityVariantsAndKeepsTheirOwnDimensions()
    {
        var hevc = DefaultVariant.Replace("h264", "h265", StringComparison.Ordinal)
            .Replace("1080", "2160", StringComparison.Ordinal).Replace("1920", "3840", StringComparison.Ordinal)
            .Replace("synthetic.mp4", "hevc.mp4", StringComparison.Ordinal);
        var low = DefaultVariant.Replace("1080", "540", StringComparison.Ordinal)
            .Replace("1920", "960", StringComparison.Ordinal).Replace("synthetic.mp4", "low.mp4", StringComparison.Ordinal);
        var parsed = JingxuanMetadataParser.Read(Page(Model(hevc + ',' + low + ',' + DefaultVariant)), Fixtures.WorkId);
        var candidates = parsed!.Items[0].Videos;
        Assert.Equal(4, candidates.Length);
        Assert.Equal([1080, 1080, 540, 540], candidates.Select(candidate => candidate.Width));
        Assert.DoesNotContain(candidates, candidate => candidate.Uri.AbsolutePath.Contains("hevc", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("null", "null")]
    [InlineData("-1", "-1")]
    [InlineData("1e100", "999999999999999999")]
    public void UnknownOrInvalidTechnicalMetadataRemainsNull(string duration, string expiry)
    {
        var model = DefaultModel.Replace("8.134", duration, StringComparison.Ordinal)
            .Replace("1798761600", expiry, StringComparison.Ordinal);
        var source = JingxuanMetadataParser.Read(Page(model), Fixtures.WorkId)!.Items[0].Videos[0];
        Assert.Null(source.DurationMs);
        Assert.Null(source.ExpiresAt);
    }

    [Fact]
    public async Task ExpiredModelCandidatesAreExcludedBeforeMediaRequests()
    {
        var upstream = new StubUpstreamClient(Page(DefaultModel.Replace("1798761600", "1", StringComparison.Ordinal)));
        var error = await Assert.ThrowsAsync<ResolutionException>(() => new DouyinAdapter(upstream, new FixedClock())
            .ResolveAsync(new Uri(Fixtures.WorkUrl), CancellationToken.None));
        Assert.Equal(ErrorCodes.MediaIncomplete, error.Error.Code);
        Assert.Single(upstream.Requests);
    }

    [Fact]
    public async Task JingxuanReaderLinkIsAcceptedAndCanonicalizedToTheWorkPage()
    {
        var upstream = new StubUpstreamClient(Page());
        var work = await new DouyinAdapter(upstream, new FixedClock()).ResolveAsync(
            new Uri("https://jingxuan.douyin.com/m/video/" + Fixtures.WorkId), CancellationToken.None);
        Assert.Equal(Fixtures.WorkUrl, work.WebUrl);
    }

    private static string Model(string variants) =>
        "{\"media_type\":\"video\",\"video_duration\":8.134,\"video_list\":[" + variants + "]}";
}
