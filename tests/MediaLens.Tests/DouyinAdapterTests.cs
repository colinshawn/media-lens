using System.Net;
using MediaLens.Contracts;
using MediaLens.Http;
using MediaLens.Platforms.Douyin;
using MediaLens.Resolution;

namespace MediaLens.Tests;

public sealed class DouyinAdapterTests
{
    private const string Image = """
        {"width":100,"height":200,"url_list":["https://cdn.example/still.jpg"]}
        """;
    private const string Live = """
        {"url_list":["https://cdn.example/live.jpg"],"live_photo_type":1,
         "video":{"duration":3000,"play_addr":{"url_list":["https://cdn.example/live.mp4"]}}}
        """;

    [Fact]
    public async Task FullWorkLinksUseOnlyTheAnonymousDetailBusinessParameters()
    {
        var upstream = new StubUpstreamClient(Fixtures.VideoJson);
        await new DouyinAdapter(upstream, new FixedClock())
            .ResolveAsync(new Uri(Fixtures.WorkUrl), CancellationToken.None);
        var metadataRequest = upstream.Requests.First();
        Assert.Equal(HttpDefaults.DouyinDetailHost, metadataRequest.IdnHost);
        Assert.Equal("?aweme_id=" + Fixtures.WorkId +
            "&aid=6383&device_platform=web&version_code=190500&version_name=19.5.0", metadataRequest.Query);
        Assert.DoesNotContain(upstream.Requests, uri => uri.IdnHost == "www.douyin.com");
    }

    [Fact]
    public async Task AnAlreadyCompleteSharePageAvoidsAdditionalMetadataRequests()
    {
        var upstream = new StubUpstreamClient(Fixtures.VideoJson);
        await new DouyinAdapter(upstream, new FixedClock()).ResolveAsync(
            new Uri("https://v.douyin.com/synthetic/"), CancellationToken.None);
        Assert.DoesNotContain(upstream.Requests, uri => uri.IdnHost == HttpDefaults.DouyinDetailHost);
        Assert.Equal("v.douyin.com", upstream.Requests.First().IdnHost);
    }

    [Fact]
    public async Task VideoPreservesVariantsAndFullUrlsWithPerCandidateMetadata()
    {
        var upstream = new StubUpstreamClient(Fixtures.VideoJson)
        {
            MediaHandler = (uri, _) => Task.FromResult(new MediaLocation(
                FinalUri: uri,
                StatusCode: (int)HttpStatusCode.OK,
                MimeType: uri.AbsolutePath == "/alternative.webm" ? "video/webm" : "video/mp4",
                SizeBytes: uri.AbsolutePath == "/alternative.webm" ? 321 : 654,
                RetryAfterSeconds: null))
        };
        var work = await new DouyinAdapter(upstream, new FixedClock()).ResolveAsync(
            new Uri(Fixtures.WorkUrl), CancellationToken.None);

        Assert.Equal(MediaTypes.Video, work.WorkType);
        var item = Assert.Single(work.Items);
        Assert.Empty(item.ImageSources);
        Assert.Equal(2, item.VideoSources.Length);
        var first = item.VideoSources[0];
        var second = item.VideoSources[1];
        Assert.EndsWith("?sig=A%2FB%2B", first.Url);
        Assert.Equal(1080, first.Width);
        Assert.Equal(1920, first.Height);
        Assert.Equal(640, second.Width);
        Assert.Equal(480, second.Height);
        Assert.Equal(12000, first.DurationMs);
        Assert.Null(second.DurationMs);
        Assert.Equal("video/webm", second.MimeType);
        Assert.EndsWith(".webm", second.FileName);
        Assert.Equal(321, second.SizeBytes);
        Assert.NotSame(first.Headers, second.Headers);
        Assert.Null(first.ExpiresAt);
        Assert.Equal("合成描述", work.Description);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("livePhoto")]
    [InlineData("mixedAlbum")]
    public async Task AlbumsPreserveOrderAndPairEachLiveClip(string kind)
    {
        var images = kind switch
        {
            "livePhoto" => Live + ',' + Live,
            "mixedAlbum" => Image + ',' + Live + ',' + Image,
            _ => Image + ',' + Image
        };
        var upstream = new StubUpstreamClient(Album(images));
        var work = await new DouyinAdapter(upstream, new FixedClock()).ResolveAsync(
            new Uri(Fixtures.WorkUrl), CancellationToken.None);

        Assert.Equal(MediaTypes.Album, work.WorkType);
        Assert.Contains("/note/", work.WebUrl, StringComparison.Ordinal);
        Assert.Equal(Enumerable.Range(1, work.Items.Length), work.Items.Select(item => item.Index));
        Assert.All(work.Items, item =>
        {
            Assert.NotEmpty(item.ImageSources);
            Assert.All(item.ImageSources, source => Assert.Null(source.DurationMs));
            if (item.Type == MediaTypes.LivePhoto)
            {
                Assert.NotEmpty(item.VideoSources);
                Assert.All(item.VideoSources, source => Assert.Contains("live.mp4", source.Url, StringComparison.Ordinal));
            }
            else
            {
                Assert.Empty(item.VideoSources);
            }
        });
        if (kind == "mixedAlbum")
        {
            Assert.Equal([MediaTypes.Image, MediaTypes.LivePhoto, MediaTypes.Image], work.Items.Select(item => item.Type));
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("router")]
    [InlineData("render")]
    public async Task MetadataEncodingsAreReadWithoutRunningScripts(string encoding)
    {
        var body = encoding switch
        {
            "router" => "window._ROUTER_DATA = " + Fixtures.VideoJson + "; throw 'synthetic';",
            "render" => "<script id='RENDER_DATA'>" + Uri.EscapeDataString(Fixtures.VideoJson) + "</script>",
            _ => Fixtures.VideoJson
        };
        var upstream = new StubUpstreamClient(body);
        var result = await new DouyinAdapter(upstream, new FixedClock()).ResolveAsync(
            new Uri("https://v.douyin.com/example/"), CancellationToken.None);

        Assert.Equal(Fixtures.WorkId, result.WorkId);
        Assert.Equal(MediaTypes.Video, result.WorkType);
    }

    [Fact]
    public async Task MissingLiveClipCannotFallBackToAlbumVideo()
    {
        const string incomplete = """
            {"url_list":["https://cdn.example/still.jpg"],"live_photo_type":1}
            """;
        var upstream = new StubUpstreamClient(Album(incomplete));
        var error = await Assert.ThrowsAsync<ResolutionException>(() =>
            new DouyinAdapter(upstream, new FixedClock()).ResolveAsync(new Uri(Fixtures.WorkUrl), CancellationToken.None));

        Assert.Equal(ErrorCodes.MediaIncomplete, error.Error.Code);
        Assert.Single(upstream.Requests);
    }

    [Fact]
    public async Task AFailedBackupIsExcludedWithoutDroppingTheLogicalItem()
    {
        var upstream = new StubUpstreamClient(Fixtures.VideoJson)
        {
            MediaHandler = (uri, _) => Task.FromResult(new MediaLocation(
                FinalUri: uri,
                StatusCode: uri.AbsolutePath == "/alternative.webm" ? (int)HttpStatusCode.Forbidden : (int)HttpStatusCode.OK,
                MimeType: "video/mp4",
                SizeBytes: null,
                RetryAfterSeconds: null))
        };
        var result = await new DouyinAdapter(upstream, new FixedClock()).ResolveAsync(new Uri(Fixtures.WorkUrl), CancellationToken.None);

        Assert.Single(result.Items);
        Assert.Single(result.Items[0].VideoSources);
    }

    [Fact]
    public async Task AllFailedRequiredSourcesRejectTheWholeWork()
    {
        var upstream = new StubUpstreamClient(Album(Image + ',' + Live))
        {
            MediaHandler = (uri, _) => Task.FromResult(new MediaLocation(
                FinalUri: uri,
                StatusCode: uri.AbsolutePath == "/live.mp4" ? (int)HttpStatusCode.Forbidden : (int)HttpStatusCode.OK,
                MimeType: "image/jpeg",
                SizeBytes: null,
                RetryAfterSeconds: null))
        };

        var error = await Assert.ThrowsAsync<ResolutionException>(() => new DouyinAdapter(upstream, new FixedClock())
            .ResolveAsync(new Uri(Fixtures.WorkUrl), CancellationToken.None));

        Assert.Equal(ErrorCodes.MediaIncomplete, error.Error.Code);
    }

    [Theory]
    [InlineData(403, ErrorCodes.CookieFreeUnavailable)]
    [InlineData(429, ErrorCodes.UpstreamUnavailable)]
    [InlineData(503, ErrorCodes.UpstreamUnavailable)]
    public async Task UpstreamRefusalsAreNotMisreportedAsDeletion(int status, string expectedCode)
    {
        var upstream = new StubUpstreamClient("{}") { MetadataStatus = status, RetryAfterSeconds = 5 };
        var error = await Assert.ThrowsAsync<ResolutionException>(() => new DouyinAdapter(upstream, new FixedClock())
            .ResolveAsync(new Uri(Fixtures.WorkUrl), CancellationToken.None));

        Assert.Equal(expectedCode, error.Error.Code);
        if (status != (int)HttpStatusCode.Forbidden)
        {
            Assert.Equal(5, error.Error.RetryAfterSeconds);
            Assert.True(error.Error.Retryable);
        }
    }

    [Fact]
    public async Task EmptyOrWrongWorkMetadataDoesNotUseRecommendations()
    {
        var upstream = new StubUpstreamClient(Fixtures.VideoJson.Replace(Fixtures.WorkId, "999999", StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<ResolutionException>(() => new DouyinAdapter(upstream, new FixedClock())
            .ResolveAsync(new Uri(Fixtures.WorkUrl), CancellationToken.None));

        Assert.Equal(ErrorCodes.CookieFreeUnavailable, error.Error.Code);
        Assert.Equal(3, upstream.Requests.Count);
    }

    [Theory]
    [InlineData("window._ROUTER_DATA = {broken", ErrorCodes.UpstreamChanged)]
    [InlineData("{\"status_msg\":\"作品已删除\"}", ErrorCodes.ContentUnavailable)]
    [InlineData("{\"aweme_id\":\"1234567890123456789\",\"unrecognized_media\":{}}", ErrorCodes.UpstreamChanged)]
    public async Task ExplicitDeletionAndUnknownStructuresRemainDistinct(string metadata, string code)
    {
        var upstream = new StubUpstreamClient(metadata);
        var error = await Assert.ThrowsAsync<ResolutionException>(() => new DouyinAdapter(upstream, new FixedClock())
            .ResolveAsync(new Uri(Fixtures.WorkUrl), CancellationToken.None));

        Assert.Equal(code, error.Error.Code);
    }

    [Fact]
    public async Task NonWorkUrlsAreRejectedBeforeUpstreamAccess()
    {
        var upstream = new StubUpstreamClient(Fixtures.VideoJson);
        var error = await Assert.ThrowsAsync<ResolutionException>(() => new DouyinAdapter(upstream, new FixedClock())
            .ResolveAsync(new Uri("https://www.douyin.com/user/synthetic"), CancellationToken.None));

        Assert.Equal(ErrorCodes.UnsupportedResource, error.Error.Code);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task ConcurrentAlbumRequestsKeepOriginalOrderAndStayBounded()
    {
        const int itemCount = 8;
        const int expectedConcurrencyLimit = 4;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var counterLock = new Lock();
        var imageItems = string.Join(',', Enumerable.Range(1, itemCount).Select(index =>
            "{\"url_list\":[\"https://cdn.example/still-" + index + ".jpg\"]}"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var upstream = new StubUpstreamClient(Album(imageItems))
        {
            MediaHandler = async (uri, token) =>
            {
                int current;
                lock (counterLock)
                {
                    current = ++active;
                    maximumActive = Math.Max(maximumActive, current);
                }
                if (current == expectedConcurrencyLimit)
                {
                    gate.TrySetResult();
                }
                await gate.Task.WaitAsync(token);
                if (uri.AbsolutePath.EndsWith("1.jpg", StringComparison.Ordinal))
                {
                    await Task.Yield();
                }
                lock (counterLock)
                {
                    active--;
                }
                return new MediaLocation(
                    FinalUri: uri,
                    StatusCode: (int)HttpStatusCode.OK,
                    MimeType: "image/jpeg",
                    SizeBytes: null,
                    RetryAfterSeconds: null);
            }
        };
        var work = await new DouyinAdapter(upstream, new FixedClock())
            .ResolveAsync(new Uri(Fixtures.WorkUrl), timeout.Token);

        Assert.Equal(expectedConcurrencyLimit, maximumActive);
        Assert.Equal(Enumerable.Range(1, itemCount), work.Items.Select(item => item.Index));
        Assert.All(work.Items, item => Assert.EndsWith("still-" + item.Index + ".jpg", item.ImageSources[0].Url));
    }

    private static string Album(string items) =>
        "{\"aweme_id\":\"" + Fixtures.WorkId + "\",\"aweme_type\":68,\"images\":[" + items +
        "],\"video\":{\"play_addr\":{\"url_list\":[\"https://cdn.example/album-music.mp4\"]}}}";
}
