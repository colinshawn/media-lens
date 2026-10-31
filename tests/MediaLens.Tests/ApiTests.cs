using System.Net;
using System.Text;
using System.Text.Json;
using MediaLens.Contracts;
using MediaLens.Platforms;
using MediaLens.Resolution;

namespace MediaLens.Tests;

public sealed class ApiTests
{
    private const string ResolvePath = "/api/v1/resolve";
    private const string ExplicitPath = "/api/v1/platforms/douyin/resolve";
    private const string PlatformsPath = "/api/v1/platforms";
    private const string ValidRequest = "{\"text\":\"https://www.douyin.com/video/1234567890123456789\"}";
    private const string JsonContentType = "application/json";

    [Fact]
    public async Task DiscoveryReportsFourVerifiedMediaTypesWithoutUpstreamCalls()
    {
        var upstream = new StubUpstreamClient(Fixtures.VideoJson);
        await using var factory = new ApiFactory { Upstream = upstream }.WithRealTcp();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(PlatformsPath);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var platform = Assert.Single(json.RootElement.GetProperty("platforms").EnumerateArray());
        Assert.Equal("douyin", platform.GetProperty("id").GetString());
        Assert.True(platform.GetProperty("enabled").GetBoolean());
        Assert.All(platform.GetProperty("capabilities").EnumerateObject(), field =>
            Assert.Equal(CapabilityStates.Supported, field.Value.GetString()));
        AssertRequestId(json.RootElement);
        Assert.Empty(upstream.Requests);
    }

    [Theory]
    [InlineData(ResolvePath)]
    [InlineData(ExplicitPath)]
    public async Task DefaultRegistrationRunsDouyinWithoutCookieOrRegistryOverride(string path)
    {
        var upstream = new StubUpstreamClient(JingxuanMetadataTests.Page());
        await using var factory = new ApiFactory { Upstream = upstream }.WithRealTcp();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync(path, Content(ValidRequest));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Fixtures.WorkId, json.RootElement.GetProperty("workId").GetString());
        Assert.Equal(MediaTypes.Video, json.RootElement.GetProperty("workType").GetString());
        Assert.Equal(8134, json.RootElement.GetProperty("items")[0].GetProperty("videoSources")[0]
            .GetProperty("durationMs").GetInt64());
        Assert.NotEmpty(upstream.Requests);
    }

    [Fact]
    public async Task UnifiedAndExplicitRoutesUseTheSameContractAndService()
    {
        var adapter = new StubAdapter("douyin", "www.douyin.com");
        await using var factory = new ApiFactory(Fixtures.Registry(adapter)).WithRealTcp();
        using var client = factory.CreateClient();
        var responses = new List<JsonDocument>();
        try
        {
            foreach (var path in new[] { ResolvePath, ExplicitPath })
            {
                using var response = await client.PostAsync(path, Content(ValidRequest));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                responses.Add(JsonDocument.Parse(await response.Content.ReadAsStringAsync()));
            }

            var first = responses[0].RootElement;
            var second = responses[1].RootElement;
            Assert.Equal(2, adapter.Calls);
            Assert.Equal(first.GetProperty("items").GetRawText(), second.GetProperty("items").GetRawText());
            Assert.Equal(Fixtures.WorkId, first.GetProperty("workId").GetString());
            Assert.Equal(JsonValueKind.Null, first.GetProperty("description").ValueKind);
            Assert.Equal(JsonValueKind.Null, first.GetProperty("expiresAt").ValueKind);
            Assert.EndsWith("Z", first.GetProperty("resolvedAt").GetString());
            Assert.NotEqual(first.GetProperty("requestId").GetString(), second.GetProperty("requestId").GetString());
            AssertRequestId(first);
            Assert.Equal(new[] { "description", "expiresAt", "items", "platform", "requestId", "resolvedAt", "webUrl", "workId", "workType" },
                first.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"text\":null}")]
    [InlineData("{\"text\":\"\"}")]
    [InlineData("{\"text\":\"no URL\"}")]
    [InlineData("{invalid")]
    [InlineData("{\"text\":\"https://www.douyin.com/video/123456\",\"cookie\":\"fixture\"}")]
    [InlineData("{\"text\":\"https://www.douyin.com/video/123456\",\"identity\":{}}")]
    [InlineData("{\"text\":\"https://www.douyin.com/video/123456\",\"platform\":\"douyin\"}")]
    public async Task InvalidBindingsUseTheErrorContractBeforeCallingAdapters(string body)
    {
        var adapter = new StubAdapter("douyin", "www.douyin.com");
        await using var factory = new ApiFactory(Fixtures.Registry(adapter)).WithRealTcp();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync(ResolvePath, Content(body));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.InvalidInput);
        Assert.Equal(0, adapter.Calls);
    }

    [Fact]
    public async Task IncomingCookieIsRejectedAndNotReflected()
    {
        var adapter = new StubAdapter("douyin", "www.douyin.com");
        await using var factory = new ApiFactory(Fixtures.Registry(adapter)).WithRealTcp();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, ResolvePath)
        {
            Content = Content(ValidRequest)
        };
        request.Headers.Add("Cookie", "synthetic-private=value");
        request.Headers.Add("X-Request-Id", "untrusted-request-id");
        using var response = await client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.InvalidInput);
        Assert.DoesNotContain("synthetic-private", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("untrusted-request-id", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, adapter.Calls);
    }

    [Theory]
    [InlineData("GET", "/api/v1/resolve", 405, ErrorCodes.MethodNotAllowed)]
    [InlineData("GET", "/api/v1/not-present", 404, ErrorCodes.EndpointNotFound)]
    [InlineData("POST", "/api/v1/resolve", 415, ErrorCodes.UnsupportedMediaType)]
    public async Task FrameworkFailuresHaveRequestIdsAndFixedErrorKeys(string method, string path, int status, string code)
    {
        await using var factory = new ApiFactory().WithRealTcp();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = new StringContent("synthetic", Encoding.UTF8, "text/plain");
        }

        using var response = await client.SendAsync(request);
        await AssertErrorAsync(response, (HttpStatusCode)status, code);
    }

    [Fact]
    public async Task UnknownPlatformAndMismatchedInputAreDistinguished()
    {
        var adapter = new StubAdapter("douyin", "www.douyin.com");
        await using var factory = new ApiFactory(Fixtures.Registry(adapter)).WithRealTcp();
        using var client = factory.CreateClient();
        using var unknown = await client.PostAsync("/api/v1/platforms/absent/resolve", Content(ValidRequest));
        await AssertErrorAsync(unknown, HttpStatusCode.NotFound, ErrorCodes.PlatformNotFound);
        using var mismatch = await client.PostAsync(ExplicitPath, Content("{\"text\":\"https://foreign.example/item/123456\"}"));
        await AssertErrorAsync(mismatch, HttpStatusCode.BadRequest, ErrorCodes.PlatformMismatch);
        Assert.Equal(0, adapter.Calls);
    }

    [Theory]
    [InlineData(ErrorCodes.CookieFreeUnavailable, 422, false)]
    [InlineData(ErrorCodes.ContentUnavailable, 404, false)]
    [InlineData(ErrorCodes.MediaIncomplete, 422, false)]
    [InlineData(ErrorCodes.UpstreamChanged, 502, false)]
    [InlineData(ErrorCodes.UpstreamUnavailable, 503, true)]
    [InlineData(ErrorCodes.UnsupportedResource, 422, false)]
    public async Task AdapterFailuresKeepStatusAndRetrySemantics(string code, int status, bool retryable)
    {
        var adapter = new StubAdapter("douyin", "www.douyin.com", (_, _) => throw new ResolutionException(
            code: code,
            message: "合成错误",
            statusCode: status,
            retryable: retryable,
            retryAfterSeconds: retryable ? 7 : null));
        await using var factory = new ApiFactory(Fixtures.Registry(adapter)).WithRealTcp();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync(ResolvePath, Content(ValidRequest));

        await AssertErrorAsync(response, (HttpStatusCode)status, code, retryable);
        if (retryable)
        {
            Assert.Equal(TimeSpan.FromSeconds(7), response.Headers.RetryAfter?.Delta);
        }
    }

    [Fact]
    public async Task InternalExceptionsDoNotLeakDetails()
    {
        const string syntheticSecret = "synthetic-secret-must-not-escape";
        var adapter = new StubAdapter("douyin", "www.douyin.com", (_, _) =>
            throw new InvalidOperationException(syntheticSecret));
        await using var factory = new ApiFactory(Fixtures.Registry(adapter)).WithRealTcp();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync(ResolvePath, Content(ValidRequest));

        await AssertErrorAsync(response, HttpStatusCode.InternalServerError, ErrorCodes.InternalError);
        Assert.DoesNotContain(syntheticSecret, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TotalDeadlineCancelsTheAdapterAndReturnsTimeout()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new StubAdapter("douyin", "www.douyin.com", async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Fixtures.Video();
            }
            finally
            {
                canceled.TrySetResult();
            }
        });
        const int timeoutMilliseconds = 100;
        await using var factory = new ApiFactory(Fixtures.Registry(adapter), TimeSpan.FromMilliseconds(timeoutMilliseconds)).WithRealTcp();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync(ResolvePath, Content(ValidRequest));

        await AssertErrorAsync(response, HttpStatusCode.GatewayTimeout, ErrorCodes.ResolutionTimeout, retryable: true);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ClientCancellationReachesTheAdapter()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new StubAdapter("douyin", "www.douyin.com", async (_, token) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Fixtures.Video();
            }
            finally
            {
                if (token.IsCancellationRequested)
                {
                    canceled.TrySetResult();
                }
            }
        });
        await using var factory = new ApiFactory(Fixtures.Registry(adapter)).WithRealTcp();
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var request = client.PostAsync(ResolvePath, Content(ValidRequest), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SecondPlatformUsesExistingRoutesAndDiscovery()
    {
        var douyin = new StubAdapter("douyin", "www.douyin.com");
        var second = new StubAdapter("example", "video.example");
        await using var factory = new ApiFactory(Fixtures.Registry(douyin, second)).WithRealTcp();
        using var client = factory.CreateClient();
        const string multiple = "{\"text\":\"https://foreign.example/x https://video.example/item/123456 https://www.douyin.com/video/123456\"}";
        foreach (var path in new[] { ResolvePath, "/api/v1/platforms/example/resolve" })
        {
            using var response = await client.PostAsync(path, Content(multiple));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("example", json.RootElement.GetProperty("platform").GetString());
        }

        using var explicitDouyin = await client.PostAsync(ExplicitPath, Content(multiple));
        Assert.Equal(HttpStatusCode.OK, explicitDouyin.StatusCode);
        Assert.Equal(1, douyin.Calls);
        Assert.Equal(2, second.Calls);
        using var discovery = await client.GetAsync(PlatformsPath);
        using var platforms = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync());
        Assert.Equal(2, platforms.RootElement.GetProperty("platforms").GetArrayLength());
        Assert.Equal(1, douyin.Calls);
        Assert.Equal(2, second.Calls);
    }

    [Fact]
    public async Task LogsCorrelateWithTheResponseAndDoNotContainShareTextOrExceptionSecrets()
    {
        const string caption = "synthetic-private-caption";
        const string secret = "synthetic-exception-secret";
        using var logs = new CaptureLogs();
        var adapter = new StubAdapter("douyin", "www.douyin.com", (_, _) => throw new InvalidOperationException(secret));
        await using var factory = new ApiFactory(Fixtures.Registry(adapter)) { Logs = logs }.WithRealTcp();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync(ResolvePath,
            Content("{\"text\":\"" + caption + " " + Fixtures.WorkUrl + "\"}"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var requestId = json.RootElement.GetProperty("requestId").GetString();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(logs.Entries, entry => entry.RequestIds.Contains(requestId) &&
            entry.Message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.All(logs.Entries, entry =>
        {
            Assert.DoesNotContain(caption, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Fixtures.WorkUrl, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, entry.Message, StringComparison.Ordinal);
            Assert.Null(entry.ExceptionMessage);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclaredAndChunkedOversizedBodiesUseTheErrorContract(bool chunked)
    {
        const int oversizedCharacters = 70_000;
        await using var factory = new ApiFactory().WithRealTcp();
        using var client = factory.CreateClient();
        var bytes = Encoding.UTF8.GetBytes("{\"text\":\"" + new string('x', oversizedCharacters) + "\"}");
        using HttpContent content = chunked ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(JsonContentType);
        using var response = await client.PostAsync(ResolvePath, content);

        await AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, ErrorCodes.InvalidInput);
    }

    private static StringContent Content(string body) => new(body, Encoding.UTF8, JsonContentType);

    private static void AssertRequestId(JsonElement value) =>
        Assert.True(Guid.TryParseExact(value.GetProperty("requestId").GetString(), "N", out _));

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code, bool retryable = false)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(JsonContentType, response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(new[] { "error", "requestId" }, root.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
        AssertRequestId(root);
        var error = root.GetProperty("error");
        Assert.Equal(new[] { "code", "message", "retryAfterSeconds", "retryable" },
            error.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(retryable, error.GetProperty("retryable").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        if (!retryable)
        {
            Assert.Equal(JsonValueKind.Null, error.GetProperty("retryAfterSeconds").ValueKind);
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
