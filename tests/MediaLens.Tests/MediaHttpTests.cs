using System.Net;
using MediaLens.Http;
using MediaLens.Testing;

namespace MediaLens.Tests;

public sealed class MediaHttpTests
{
    private const string MediaRoot = "http://v3-dy-o.douyinvod.com/";
    private const string PlaybackRoot = "http://www.douyin.com/";
    private const long SyntheticMediaSize = 8_388_608;

    [Fact]
    public async Task MediaRedirectsIgnoreCookiesPreserveQueryAndNeverReadTheDeclaredBody()
    {
        await using var server = new LoopbackHttpServer((request, _) => Task.FromResult(
            request.Target == "/play"
                ? new ServerReply(
                    StatusCode: HttpStatusCode.Found,
                    Headers: new Dictionary<string, string>
                    {
                        ["Location"] = MediaRoot + "cdn.mp4?signature=A%2FB%2B%3D",
                        ["Set-Cookie"] = "synthetic=value; Path=/"
                    })
                : new ServerReply(
                    StatusCode: HttpStatusCode.OK,
                    Body: string.Empty,
                    ContentType: "video/mp4",
                    Headers: new Dictionary<string, string> { ["Set-Cookie"] = "cdn-synthetic=value; Path=/" },
                    DeclaredContentLength: SyntheticMediaSize)));
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var location = await client.ResolveMediaAsync(new Uri(PlaybackRoot + "play"), "video", deadline.Token);
        await client.ResolveMediaAsync(new Uri(MediaRoot + "again"), "video", deadline.Token);

        Assert.Equal((int)HttpStatusCode.OK, location.StatusCode);
        Assert.Equal(SyntheticMediaSize, location.SizeBytes);
        Assert.Equal("video/mp4", location.MimeType);
        Assert.EndsWith("signature=A%2FB%2B%3D", location.FinalUri.OriginalString);
        Assert.Equal(3, server.Requests.Count);
        Assert.All(server.Requests, request => Assert.False(request.Headers.ContainsKey("Cookie")));
        var headers = client.CreateDownloadHeaders();
        Assert.All(server.Requests, request =>
        {
            foreach (var (name, value) in headers)
            {
                Assert.Equal(value, request.Headers[name]);
            }
        });
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData("image/jpeg")]
    public async Task NonVideoResponsesCannotBecomeVideoSources(string mime)
    {
        await using var server = new LoopbackHttpServer((_, _) => Task.FromResult(
            new ServerReply(StatusCode: HttpStatusCode.OK, ContentType: mime)));
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));

        var error = await Assert.ThrowsAsync<UpstreamRequestException>(() =>
            client.ResolveMediaAsync(new Uri(MediaRoot), "video", CancellationToken.None));

        Assert.Equal(HttpFailureCodes.UnsupportedContent, error.Outcome);
    }

    [Fact]
    public async Task OctetStreamDoesNotInventAMimeType()
    {
        await using var server = new LoopbackHttpServer((_, _) => Task.FromResult(
            new ServerReply(StatusCode: HttpStatusCode.OK, ContentType: "application/octet-stream")));
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));

        var location = await client.ResolveMediaAsync(new Uri(MediaRoot), "video", CancellationToken.None);

        Assert.Null(location.MimeType);
    }

    [Fact]
    public async Task MediaRedirectCannotLeaveAllowedPublicTargets()
    {
        await using var server = new LoopbackHttpServer((_, _) => Task.FromResult(
            new ServerReply(StatusCode: HttpStatusCode.Found, Headers: new Dictionary<string, string>
            {
                ["Location"] = "http://127.0.0.1/private"
            })));
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));

        var error = await Assert.ThrowsAsync<UpstreamRequestException>(() =>
            client.ResolveMediaAsync(new Uri(MediaRoot), "video", CancellationToken.None));

        Assert.Equal(HttpFailureCodes.TargetRejected, error.Outcome);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task TemporaryMetadataErrorsKeepStatusAndRetryDelayWithoutReadingBinaryBodies()
    {
        await using var server = new LoopbackHttpServer((_, _) => Task.FromResult(new ServerReply(
            StatusCode: HttpStatusCode.ServiceUnavailable,
            Body: string.Empty,
            ContentType: "application/octet-stream",
            Headers: new Dictionary<string, string> { ["Retry-After"] = "9" },
            DeclaredContentLength: SyntheticMediaSize)));
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));

        var page = await client.GetAsync(new Uri(MediaRoot), CancellationToken.None);

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, page.StatusCode);
        Assert.Equal(9, page.RetryAfterSeconds);
        Assert.Equal(0, page.BytesRead);
    }

    [Theory]
    [InlineData("https://v3-dy-o.douyinvod.com/file")]
    [InlineData("https://p3-sign.douyinpic.com/file")]
    [InlineData("https://aweme.snssdk.com/aweme/v1/play/?video_id=synthetic")]
    public void KnownPlaybackAndCdnHostsPassDomainValidation(string value)
    {
        new PublicHttpTargetPolicy().ValidateUri(new Uri(value));
    }

    [Fact]
    public async Task PlaybackInterfaceWithoutACdnRedirectIsNotReturnedAsAFinalSource()
    {
        await using var server = new LoopbackHttpServer((_, _) => Task.FromResult(
            new ServerReply(StatusCode: HttpStatusCode.OK, ContentType: "video/mp4")));
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));

        var error = await Assert.ThrowsAsync<UpstreamRequestException>(() => client.ResolveMediaAsync(
            new Uri(PlaybackRoot + "aweme/v1/play/?video_id=synthetic"), "video", CancellationToken.None));

        Assert.Equal(HttpFailureCodes.UnsupportedContent, error.Outcome);
    }
}
