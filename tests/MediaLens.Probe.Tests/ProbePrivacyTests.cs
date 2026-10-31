using System.Net;
using System.Text.Json;
using MediaLens.Http;
using MediaLens.Resolution;
using MediaLens.Testing;

namespace MediaLens.Probe.Tests;

public sealed class ProbePrivacyTests
{
    [Theory]
    [InlineData(ProbeEntryPoints.Share)]
    [InlineData(ProbeEntryPoints.Adapter)]
    public async Task ReportOmitsShareTextWorkIdMediaUrlsAndRawResponse(string entryPoint)
    {
        const string privateCaption = "Synthetic private caption";
        const string syntheticId = "1234567890123456789";
        const string mediaUrl = "https://cdn.example/synthetic.mp4?token=private-example";
        const string body = """
            {"aweme_id":"1234567890123456789","video":{
              "play_addr":{"url_list":["https://cdn.example/synthetic.mp4?token=private-example"]}}}
            """;
        await using var server = new LoopbackHttpServer((request, _) => Task.FromResult(
            request.Target == "/synthetic/"
                ? new ServerReply(StatusCode: HttpStatusCode.Found,
                    Headers: new Dictionary<string, string>
                    {
                        [HttpDefaults.RedirectLocationHeader] = "http://www.douyin.com/video/" + syntheticId
                    })
                : new ServerReply(StatusCode: HttpStatusCode.OK, Body: body)));
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));
        var input = new ProbeInput
        {
            Kind = ProbeSampleKinds.Video,
            Text = privateCaption + " http://v.douyin.com/synthetic/"
        };

        var report = await new ProbeRunner(client).RunAsync(input, entryPoint, CancellationToken.None);
        var batch = new ProbeBatchReport("2026-01-01T00:00:00Z", ProbeDefaults.CookiePolicy, [report]);
        var json = JsonSerializer.Serialize(batch, ProbeJsonContext.Default.ProbeBatchReport);

        Assert.Equal(entryPoint == ProbeEntryPoints.Share ? ProbeOutcomes.MetadataObserved : ErrorCodes.MediaIncomplete, report.Outcome);
        Assert.DoesNotContain(privateCaption, json, StringComparison.Ordinal);
        Assert.DoesNotContain(syntheticId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(mediaUrl, json, StringComparison.Ordinal);
        Assert.DoesNotContain(body, json, StringComparison.Ordinal);
        Assert.DoesNotContain(input.Text, json, StringComparison.Ordinal);
    }

    [Fact]
    public void InputCannotCarryCookieFields()
    {
        const string input = """
            [{"kind":"video","text":"synthetic share","cookie":"fixture=value"}]
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(input, ProbeJsonContext.Default.ProbeInputArray));
    }

    [Fact]
    public async Task TransportFailureKeepsObservedStatusWithoutExposingRequestParameters()
    {
        await using var server = new LoopbackHttpServer((_, _) => Task.FromResult(
            new ServerReply(StatusCode: HttpStatusCode.Found)));
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port));
        var input = new ProbeInput
        {
            Kind = ProbeSampleKinds.Video,
            Text = "http://www.douyin.com/video/123456?private=synthetic"
        };

        var report = await new ProbeRunner(client).RunAsync(input, ProbeEntryPoints.Share, CancellationToken.None);

        Assert.Equal(ProbeOutcomes.InvalidRedirect, report.Outcome);
        Assert.Equal((int)HttpStatusCode.Found, report.HttpStatusCode);
    }
}
