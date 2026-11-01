using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaLens.Api;
using MediaLens.Http;
using MediaLens.Resolution;
using MediaLens.Testing;

namespace MediaLens.Tests;

public sealed class ApiAccessControlTests
{
    private const string Discovery = "/api/v1/platforms";
    private static readonly string[] ProtectedPaths = [Discovery, "/api/v1/resolve", "/api/v1/platforms/douyin/resolve"];

    [Theory]
    [InlineData(32)]
    [InlineData(256)]
    public async Task ExactBoundarySecretsAuthenticateAndMismatchedValuesDoNot(int length)
    {
        var value = new string('A', length);
        await using var factory = new ApiFactory { Secret = value }.WithRealTcp();
        using var client = factory.CreateClient();
        using var success = await client.GetAsync(Discovery);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(SecretToken.Scheme, new string('B', length));
        using var mismatch = await client.GetAsync(Discovery);
        await AssertUnauthorizedAsync(mismatch);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(257)]
    public async Task OutOfRangeTokensFailOnEveryRouteBeforeBindingOrUpstreamCalls(int length)
    {
        var adapter = new StubAdapter("douyin", "www.douyin.com");
        var upstream = new StubUpstreamClient(Fixtures.VideoJson);
        await using var factory = new ApiFactory(Fixtures.Registry(adapter)) { Upstream = upstream }.WithRealTcp();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(SecretToken.Scheme, new string('A', length));
        foreach (var path in ProtectedPaths)
        {
            using var response = path == Discovery
                ? await client.GetAsync(path)
                : await client.PostAsync(path, Json("{invalid"));
            await AssertUnauthorizedAsync(response);
        }

        Assert.Equal(0, adapter.Calls);
        Assert.Empty(upstream.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer")]
    [InlineData("Basic synthetic-other-credential")]
    [InlineData("Bearer synthetic-wrong-secret-token-1234567890")]
    [InlineData("Bearer synthetic-wrong-secret-token-1234567890, Bearer another")]
    public async Task MissingAndMalformedCredentialsKeepTheSameSafeContract(string? authorization)
    {
        using var logs = new CaptureLogs();
        await using var factory = new ApiFactory { Logs = logs }.WithRealTcp();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        using var request = new HttpRequestMessage(HttpMethod.Get, Discovery);
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await client.SendAsync(request);
        var requestId = await AssertUnauthorizedAsync(response);
        Assert.Contains(logs.Entries, entry => entry.RequestIds.Contains(requestId));
        var output = string.Join('\n', logs.Entries.Select(entry => entry.Message + entry.ExceptionMessage))
            + await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ApiFactory.DefaultSecret, output, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ApiFactory.DefaultSecret))), output, StringComparison.OrdinalIgnoreCase);
        if (authorization is not null)
        {
            Assert.DoesNotContain(authorization, output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DuplicateCredentialsAndAlternateLocationsCannotAuthenticate()
    {
        await using var factory = new ApiFactory().WithRealTcp();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        using var duplicate = new HttpRequestMessage(HttpMethod.Get, Discovery);
        duplicate.Headers.TryAddWithoutValidation("Authorization", ["Bearer " + ApiFactory.DefaultSecret, "Bearer " + ApiFactory.DefaultSecret]);
        using var duplicated = await client.SendAsync(duplicate);
        await AssertUnauthorizedAsync(duplicated);

        client.DefaultRequestHeaders.Add("X-Secret-Token", ApiFactory.DefaultSecret);
        using var query = await client.GetAsync(Discovery + "?token=" + ApiFactory.DefaultSecret);
        await AssertUnauthorizedAsync(query);
        using var body = await client.PostAsync("/api/v1/resolve", Json("{\"token\":\"" + ApiFactory.DefaultSecret + "\"}"));
        await AssertUnauthorizedAsync(body);
    }

    [Fact]
    public async Task AuthenticationPrecedesBindingAndKeepsExistingEarlyGuardsAndRoutingErrors()
    {
        await using var factory = new ApiFactory().WithRealTcp();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        using var unauthenticated = await client.PostAsync("/api/v1/resolve", Json("{invalid"));
        await AssertUnauthorizedAsync(unauthenticated);
        using var cookie = new HttpRequestMessage(HttpMethod.Get, Discovery);
        cookie.Headers.Add("Cookie", "synthetic=ignored");
        using var cookieFailure = await client.SendAsync(cookie);
        Assert.Equal(HttpStatusCode.BadRequest, cookieFailure.StatusCode);
        // Kestrel 可关闭超限正文的连接，不让后续路由断言复用该连接。
        using var oversizedClient = factory.CreateClient();
        oversizedClient.DefaultRequestHeaders.Authorization = null;
        using var oversized = await oversizedClient.PostAsync("/api/v1/resolve", Json(new string('A', 70_000)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using var missing = await client.GetAsync("/api/v1/not-present");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var method = await client.GetAsync("/api/v1/resolve");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, method.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(SecretToken.Scheme, ApiFactory.DefaultSecret);
        using var malformed = await client.PostAsync("/api/v1/resolve", Json("{invalid"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task AuthorizedCallsNeverForwardTokenOrCookiesToEitherPlatformAcrossRedirects()
    {
        await using var server = new LoopbackHttpServer((request, _) => Task.FromResult(new ServerReply(
            StatusCode: request.Target == "/start" ? HttpStatusCode.Found : HttpStatusCode.OK,
            Headers: request.Target == "/start"
                ? new Dictionary<string, string> { ["Location"] = "/end", ["Set-Cookie"] = "synthetic=ignored" }
                : new Dictionary<string, string> { ["Set-Cookie"] = "second=ignored" })));
        using var douyinHttp = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));
        using var bilibiliHttp = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port, PlatformHttpProfile.Bilibili), profile: PlatformHttpProfile.Bilibili);
        var douyin = new StubAdapter("douyin", "www.douyin.com", async (_, cancellation) =>
        {
            await douyinHttp.GetAsync(new Uri("http://www.douyin.com/start"), cancellation);
            return Fixtures.Video();
        });
        var bilibili = new StubAdapter("bilibili", "www.bilibili.com", async (_, cancellation) =>
        {
            await bilibiliHttp.GetAsync(new Uri("http://api.bilibili.com/start"), cancellation);
            return Fixtures.Video("bilibili");
        });
        await using var factory = new ApiFactory(Fixtures.Registry(douyin, bilibili)).WithRealTcp();
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/api/v1/resolve", "/api/v1/platforms/douyin/resolve" })
        {
            using var response = await client.PostAsync(path, Json("{\"text\":\"" + Fixtures.WorkUrl + "\"}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(ApiFactory.DefaultSecret, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        foreach (var path in new[] { "/api/v1/resolve", "/api/v1/platforms/bilibili/resolve" })
        {
            using var response = await client.PostAsync(path, Json("{\"text\":\"https://www.bilibili.com/video/synthetic\"}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(ApiFactory.DefaultSecret, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Equal(8, server.Requests.Count);
        Assert.All(server.Requests, request =>
        {
            Assert.False(request.Headers.ContainsKey("Cookie"));
            Assert.False(request.Headers.ContainsKey("Authorization"));
            Assert.False(request.Headers.ContainsKey("Proxy-Authorization"));
            Assert.DoesNotContain(ApiFactory.DefaultSecret, string.Join(' ', request.Headers.Values), StringComparison.Ordinal);
        });
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private static async Task<string> AssertUnauthorizedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(SecretToken.Scheme, Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(new[] { "error", "requestId" }, root.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
        var requestId = root.GetProperty("requestId").GetString();
        Assert.True(Guid.TryParseExact(requestId, "N", out _));
        var error = root.GetProperty("error");
        Assert.Equal(new[] { "code", "message", "retryAfterSeconds", "retryable" },
            error.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
        Assert.Equal(ErrorCodes.Unauthorized, error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("retryAfterSeconds").ValueKind);
        return requestId!;
    }
}
