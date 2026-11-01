using System.Net;
using System.Text;
using System.Text.Json;
using MediaLens.Contracts;
using MediaLens.Http;
using MediaLens.Platforms;
using MediaLens.Platforms.Bilibili;
using MediaLens.Resolution;
using MediaLens.Testing;

namespace MediaLens.Tests;

public sealed class BilibiliIntegrationTests
{
    private const string ResolvePath = "/api/v1/resolve";
    private const string ExplicitPath = "/api/v1/platforms/bilibili/resolve";
    private const string PlatformsPath = "/api/v1/platforms";

    [Fact]
    public async Task ActualAdapterReusesBothRoutesAndDiscoveryWithoutUpstreamHealthChecks()
    {
        var upstream = new BilibiliTestClient();
        using var bilibili = new BilibiliAdapter(upstream, TimeProvider.System);
        var douyin = new StubAdapter("douyin", "www.douyin.com");
        await using var factory = new ApiFactory(Fixtures.Registry(douyin, new SupportedBilibiliTestAdapter(bilibili))).WithRealTcp();
        using var client = factory.CreateClient();
        foreach (var path in new[] { ResolvePath, ExplicitPath })
        {
            using var response = await client.PostAsync(path, Content(BilibiliTestClient.Url + "?p=2"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var value = json.RootElement;
            Assert.Equal("bilibili", value.GetProperty("platform").GetString());
            Assert.Equal(BilibiliTestClient.Bvid, value.GetProperty("workId").GetString());
            Assert.EndsWith("?p=2", value.GetProperty("webUrl").GetString());
            Assert.False(string.IsNullOrWhiteSpace(value.GetProperty("requestId").GetString()));
            var item = Assert.Single(value.GetProperty("items").EnumerateArray());
            Assert.Empty(item.GetProperty("imageSources").EnumerateArray());
            Assert.Equal(24_000, item.GetProperty("videoSources")[0].GetProperty("durationMs").GetInt64());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("videoSources")[0].GetProperty("width").ValueKind);
        }

        var calls = upstream.Requests.Count;
        using var discovery = await client.GetAsync(PlatformsPath);
        using var platforms = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync());
        Assert.Equal(2, platforms.RootElement.GetProperty("platforms").GetArrayLength());
        var found = Assert.Single(platforms.RootElement.GetProperty("platforms").EnumerateArray(), entry => entry.GetProperty("id").GetString() == "bilibili");
        var capabilities = found.GetProperty("capabilities");
        Assert.Equal(CapabilityStates.Supported, capabilities.GetProperty("video").GetString());
        Assert.Equal(CapabilityStates.Unsupported, capabilities.GetProperty("imageAlbum").GetString());
        Assert.Equal(CapabilityStates.Unsupported, capabilities.GetProperty("livePhoto").GetString());
        Assert.Equal(CapabilityStates.Unsupported, capabilities.GetProperty("mixedAlbum").GetString());
        Assert.Equal(calls, upstream.Requests.Count);
    }

    [Theory]
    [InlineData("p")]
    [InlineData("cid")]
    [InlineData("qn")]
    [InlineData("quality")]
    [InlineData("platform")]
    [InlineData("cookie")]
    public async Task JsonInputDoesNotGrowPlatformSpecificFields(string field)
    {
        var upstream = new BilibiliTestClient();
        using var adapter = new BilibiliAdapter(upstream, TimeProvider.System);
        await using var factory = new ApiFactory(Fixtures.Registry(new SupportedBilibiliTestAdapter(adapter))).WithRealTcp();
        using var client = factory.CreateClient();
        using var body = new StringContent("{\"text\":\"" + BilibiliTestClient.Url + "\",\"" + field + "\":2}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(ExplicitPath, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ErrorCodes.InvalidInput, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task SelectionKeepsOwnershipSeparateFromResourceSupport()
    {
        var upstream = new BilibiliTestClient();
        using var adapter = new BilibiliAdapter(upstream, TimeProvider.System);
        var douyin = new StubAdapter("douyin", "www.douyin.com");
        await using var factory = new ApiFactory(Fixtures.Registry(douyin, new SupportedBilibiliTestAdapter(adapter))).WithRealTcp();
        using var client = factory.CreateClient();
        using var unsupported = await client.PostAsync(ResolvePath, Content("https://live.bilibili.com/123456 " + Fixtures.WorkUrl));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unsupported.StatusCode);
        Assert.Equal(0, douyin.Calls);
        Assert.Empty(upstream.Requests);
        using var invalid = await client.PostAsync(ResolvePath, Content(BilibiliTestClient.Url + "?p=0 " + Fixtures.WorkUrl));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(upstream.Requests);
        using var explicitDouyin = await client.PostAsync("/api/v1/platforms/douyin/resolve", Content(BilibiliTestClient.Url + " " + Fixtures.WorkUrl));
        Assert.Equal(HttpStatusCode.OK, explicitDouyin.StatusCode);
        Assert.Equal(1, douyin.Calls);
    }

    [Theory]
    [InlineData("http://www.douyin.com/video/123456", ErrorCodes.PlatformMismatch, HttpStatusCode.BadRequest)]
    [InlineData("http://live.bilibili.com/123456", ErrorCodes.UnsupportedResource, HttpStatusCode.UnprocessableEntity)]
    public async Task ShortLinkRedirectErrorsKeepExistingPublicMeaning(string target, string code, HttpStatusCode status)
    {
        await using var server = new LoopbackHttpServer((_, _) => Task.FromResult(new ServerReply(
            StatusCode: HttpStatusCode.Found,
            Headers: new Dictionary<string, string> { ["Location"] = target, ["Set-Cookie"] = "synthetic=ignored" })));
        using var http = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port, PlatformHttpProfile.Bilibili), profile: PlatformHttpProfile.Bilibili);
        using var adapter = new BilibiliAdapter(http, TimeProvider.System);
        await using var factory = new ApiFactory(Fixtures.Registry(new SupportedBilibiliTestAdapter(adapter))).WithRealTcp();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync(ExplicitPath, Content("http://b23.tv/synthetic"));
        Assert.Equal(status, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Single(server.Requests);
        Assert.All(server.Requests, request => Assert.False(request.Headers.ContainsKey("Cookie")));
    }

    [Fact]
    public async Task TwoPlatformTransportsKeepHeadersAndCookiesIsolatedAcrossRedirectsAndCalls()
    {
        await using var server = new LoopbackHttpServer((request, _) => Task.FromResult(
            request.Target == "/start"
                ? new ServerReply(StatusCode: HttpStatusCode.Found, Headers: new Dictionary<string, string>
                {
                    ["Location"] = "/end",
                    ["Set-Cookie"] = "synthetic=ignored; Path=/"
                })
                : new ServerReply(StatusCode: HttpStatusCode.OK, Headers: new Dictionary<string, string>
                {
                    ["Set-Cookie"] = "second=ignored; Path=/"
                })));
        using var bilibili = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port, PlatformHttpProfile.Bilibili), profile: PlatformHttpProfile.Bilibili);
        using var douyin = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));
        await Task.WhenAll(Enumerable.Range(0, 2).SelectMany(_ => new[]
        {
            bilibili.GetAsync(new Uri("http://api.bilibili.com/start"), CancellationToken.None),
            douyin.GetAsync(new Uri("http://www.douyin.com/start"), CancellationToken.None)
        }));
        Assert.Equal(8, server.Requests.Count);
        Assert.All(server.Requests, request =>
        {
            Assert.False(request.Headers.ContainsKey("Cookie"));
            var expected = request.Headers["Host"] == "api.bilibili.com" ? PlatformHttpProfile.Bilibili : PlatformHttpProfile.Douyin;
            Assert.Equal(expected.Referer, request.Headers["Referer"]);
            Assert.Equal(expected.UserAgent, request.Headers["User-Agent"]);
        });
    }

    [Theory]
    [InlineData("https://api.bilibili.com/x/web-interface/view", false)]
    [InlineData("https://b23.tv/synthetic", false)]
    [InlineData("https://upos-synthetic.bilivideo.com/video.mp4", true)]
    public void BilibiliTargetsDoNotExpandDouyinPermissions(string url, bool finalMedia)
    {
        var uri = new Uri(url);
        var bilibili = new PublicHttpTargetPolicy(profile: PlatformHttpProfile.Bilibili);
        bilibili.ValidateUri(uri);
        Assert.Equal(finalMedia, bilibili.IsFinalMediaUri(uri));
        Assert.Throws<UpstreamRequestException>(() => new PublicHttpTargetPolicy().ValidateUri(uri));
        Assert.Throws<UpstreamRequestException>(() => bilibili.ValidateUri(new Uri(Fixtures.WorkUrl)));
        Assert.Throws<UpstreamRequestException>(() => bilibili.ValidateUri(new Uri("https://bilibili.com.foreign.example/x")));
    }

    [Fact]
    public async Task WbiQueryIsSentByteForByteWithoutFormReencoding()
    {
        await using var server = new LoopbackHttpServer((_, _) => Task.FromResult(new ServerReply(HttpStatusCode.OK)));
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port, PlatformHttpProfile.Bilibili), profile: PlatformHttpProfile.Bilibili);
        using var nav = JsonDocument.Parse(BilibiliTestClient.NavJson);
        var query = BilibiliWbiSigner.Sign(new Dictionary<string, string>
        {
            ["path"] = "/中文/'x*()",
            ["foo"] = "hello world!",
            ["empty"] = ""
        }, BilibiliWbiSigner.ReadMixin(nav.RootElement.GetProperty("data")), 1_700_000_000);
        const string path = "/x/player/wbi/playurl?";
        await client.GetAsync(new Uri("http://api.bilibili.com" + path + query), CancellationToken.None);
        Assert.Equal(path + query, Assert.Single(server.Requests).Target);
        Assert.False(Assert.Single(server.Requests).Headers.ContainsKey("Cookie"));
    }

    private static StringContent Content(string text) => new(
        JsonSerializer.Serialize(new ResolveRequest { Text = text }, MediaLensJsonContext.Default.ResolveRequest), Encoding.UTF8, "application/json");
}

/// <summary>合成上游的受控支持声明；不改变生产适配器的真实验收门槛。</summary>
internal sealed class SupportedBilibiliTestAdapter(BilibiliAdapter adapter) : IPlatformAdapter
{
    public string Id => adapter.Id;
    public string DisplayName => adapter.DisplayName;
    public PlatformCapabilities Capabilities { get; } = new(
        CapabilityStates.Supported, CapabilityStates.Unsupported, CapabilityStates.Unsupported, CapabilityStates.Unsupported);
    public bool CanHandle(Uri uri) => adapter.CanHandle(uri);
    public Task<ResolvedWork> ResolveAsync(Uri uri, CancellationToken cancellationToken) => adapter.ResolveAsync(uri, cancellationToken);
}
