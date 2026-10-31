using System.Net;
using MediaLens.Http;
using MediaLens.Testing;

namespace MediaLens.Probe.Tests;

public sealed class CookieFreeHttpClientTests
{
    private const string FixtureRoot = "http://www.douyin.com/";
    private const string SyntheticCookie = "fixture=value; Path=/";
    private const string LocationHeader = "Location";
    private const int CancellationDelayMilliseconds = 200;
    private const int TestDeadlineSeconds = 10;

    [Fact]
    public async Task SetCookieIsIgnoredAcrossRelativeRedirectsAndRepeatedCalls()
    {
        await using var server = new LoopbackHttpServer((request, _) => Task.FromResult(
            request.Target switch
            {
                "/start" => Redirect("next"),
                "/next" => Redirect("/finish"),
                _ => new ServerReply(HttpStatusCode.OK)
            }));
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(TestDeadlineSeconds));
        var observations = new List<HttpObservation>();

        var first = await client.GetAsync(new Uri(FixtureRoot + "start"), observations.Add, deadline.Token);
        await client.GetAsync(new Uri(FixtureRoot + "again"), observations.Add, deadline.Token);

        Assert.Equal("/finish", first.FinalUri.AbsolutePath);
        Assert.Equal(["/start", "/next", "/finish", "/again"], server.Requests.Select(request => request.Target));
        Assert.Equal(4, observations.Count);
        Assert.Equal(2, observations.Count(observation => observation.SetCookieObserved));
        Assert.All(observations, observation => Assert.False(observation.CookieHeaderObserved));
        Assert.All(server.Requests, request =>
        {
            Assert.False(request.Headers.ContainsKey(ProbeDefaults.CookieHeader));
            Assert.False(request.Headers.ContainsKey("Authorization"));
            Assert.Equal(ProbeDefaults.MobileUserAgent, request.Headers["User-Agent"]);
        });
    }

    [Fact]
    public async Task FixtureWouldExposeAnEnabledCookieContainer()
    {
        await using var server = new LoopbackHttpServer((request, _) => Task.FromResult(
            request.Target == "/start" ? Redirect("/finish") : new ServerReply(HttpStatusCode.OK)));
        var policy = new LoopbackTargetPolicy(server.Port);
        using var handler = new SocketsHttpHandler
        {
            UseCookies = true,
            AllowAutoRedirect = true,
            UseProxy = false,
            ConnectCallback = policy.ConnectAsync
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(TestDeadlineSeconds)
        };

        using var response = await client.GetAsync(FixtureRoot + "start");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fixture=value", server.Requests.Last().Headers[ProbeDefaults.CookieHeader]);
    }

    [Fact]
    public async Task RedirectTargetsAreCheckedBeforeConnecting()
    {
        await using var server = new LoopbackHttpServer((_, _) => Task.FromResult(
            Redirect("http://127.0.0.1/private")));
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));
        var observations = new List<HttpObservation>();

        var error = await Assert.ThrowsAsync<UpstreamRequestException>(() =>
            client.GetAsync(new Uri(FixtureRoot), observations.Add, CancellationToken.None));

        Assert.Equal(ProbeOutcomes.TargetRejected, error.Outcome);
        Assert.Single(server.Requests);
        Assert.Single(observations);
    }

    [Fact]
    public async Task RedirectLoopIsRejected()
    {
        await using var server = new LoopbackHttpServer((_, _) => Task.FromResult(Redirect("/")));
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));

        var error = await Assert.ThrowsAsync<UpstreamRequestException>(() =>
            client.GetAsync(new Uri(FixtureRoot), _ => { }, CancellationToken.None));

        Assert.Equal(ProbeOutcomes.RedirectLoop, error.Outcome);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task RedirectLimitIsEnforcedWithoutAutomaticFollowing()
    {
        await using var server = new LoopbackHttpServer((request, _) => Task.FromResult(
            Redirect(request.Target + "next/")));
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));
        var observations = new List<HttpObservation>();

        var error = await Assert.ThrowsAsync<UpstreamRequestException>(() =>
            client.GetAsync(new Uri(FixtureRoot), observations.Add, CancellationToken.None));

        Assert.Equal(ProbeOutcomes.RedirectLimit, error.Outcome);
        Assert.Equal(ProbeDefaults.MaximumRedirects + 1, server.Requests.Count);
        Assert.Equal(server.Requests.Count, observations.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://[invalid")]
    public async Task MissingOrMalformedLocationIsAnExplicitFailure(string? location)
    {
        var headers = location is null ? null : new Dictionary<string, string> { [LocationHeader] = location };
        await using var server = new LoopbackHttpServer((_, _) => Task.FromResult(
            new ServerReply(StatusCode: HttpStatusCode.Found, Headers: headers)));
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));

        var error = await Assert.ThrowsAsync<UpstreamRequestException>(() =>
            client.GetAsync(new Uri(FixtureRoot), _ => { }, CancellationToken.None));

        Assert.Equal(ProbeOutcomes.InvalidRedirect, error.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataLimitAppliesToDeclaredAndStreamedBodies(bool omitLength)
    {
        var oversizedBody = new string('x', ProbeDefaults.MaximumMetadataBytes + 1);
        await using var server = new LoopbackHttpServer((_, _) => Task.FromResult(
            new ServerReply(
                StatusCode: HttpStatusCode.OK,
                Body: oversizedBody,
                OmitContentLength: omitLength)));
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));

        var error = await Assert.ThrowsAsync<UpstreamRequestException>(() =>
            client.GetAsync(new Uri(FixtureRoot), _ => { }, CancellationToken.None));

        Assert.Equal(ProbeOutcomes.BodyTooLarge, error.Outcome);
    }

    [Fact]
    public async Task BinaryMediaIsNotReadAsMetadata()
    {
        await using var server = new LoopbackHttpServer((_, _) => Task.FromResult(
            new ServerReply(StatusCode: HttpStatusCode.OK, ContentType: "video/mp4")));
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));

        var error = await Assert.ThrowsAsync<UpstreamRequestException>(() =>
            client.GetAsync(new Uri(FixtureRoot), _ => { }, CancellationToken.None));

        Assert.Equal(ProbeOutcomes.UnsupportedContent, error.Outcome);
    }

    [Fact]
    public async Task CancellationInterruptsAnUnresponsiveUpstream()
    {
        await using var server = new LoopbackHttpServer(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new ServerReply(HttpStatusCode.OK);
        });
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));
        using var cancellation = new CancellationTokenSource(CancellationDelayMilliseconds);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetAsync(new Uri(FixtureRoot), _ => { }, cancellation.Token));
    }

    private static ServerReply Redirect(string location) => new(
        StatusCode: HttpStatusCode.Found,
        Headers: new Dictionary<string, string>
        {
            [LocationHeader] = location,
            [ProbeDefaults.SetCookieHeader] = SyntheticCookie
        });
}
