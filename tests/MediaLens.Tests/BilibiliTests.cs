using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaLens.Contracts;
using MediaLens.Http;
using MediaLens.Platforms;
using MediaLens.Platforms.Bilibili;
using MediaLens.Resolution;

namespace MediaLens.Tests;

public sealed class BilibiliTests
{
    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1Ab2Cd3Ef4", "BV1Ab2Cd3Ef4", null, 1)]
    [InlineData("https://m.bilibili.com/video/av123456/?p=2", null, "123456", 2)]
    [InlineData("https://www.bilibili.com/video/BV1Ab2Cd3Ef4?p=01", "BV1Ab2Cd3Ef4", null, 1)]
    public void LinksPreserveIdentityAndSelectOnePage(string url, string? bvid, string? aid, int page)
    {
        var link = BilibiliWorkLink.Parse(new Uri(url));
        Assert.Equal(bvid, link.Bvid);
        Assert.Equal(aid, link.Aid);
        Assert.Equal(page, link.Page);
    }

    [Theory]
    [InlineData("p=")]
    [InlineData("p=0")]
    [InlineData("p=-1")]
    [InlineData("p=1.2")]
    [InlineData("p=2147483648")]
    [InlineData("p=1&p=2")]
    [InlineData("p=%202")]
    [InlineData("p=+2")]
    public async Task InvalidPageFailsBeforeNetwork(string query)
    {
        var client = new BilibiliTestClient();
        using var adapter = new BilibiliAdapter(client, TimeProvider.System);
        var error = await Assert.ThrowsAsync<ResolutionException>(() => adapter.ResolveAsync(
            new Uri(BilibiliTestClient.Url + '?' + query), CancellationToken.None));
        Assert.Equal(ErrorCodes.InvalidInput, error.Error.Code);
        Assert.Empty(client.Requests);
    }

    [Theory]
    [InlineData("https://www.bilibili.com/")]
    [InlineData("https://space.bilibili.com/123456")]
    [InlineData("https://live.bilibili.com/123456")]
    [InlineData("https://www.bilibili.com/bangumi/play/ep123456")]
    [InlineData("https://www.bilibili.com/opus/123456")]
    public async Task OwnershipDoesNotAuthorizeUnsupportedResources(string url)
    {
        var client = new BilibiliTestClient();
        using var adapter = new BilibiliAdapter(client, TimeProvider.System);
        Assert.True(adapter.CanHandle(new Uri(url)));
        Assert.False(adapter.CanHandle(new Uri("https://bilibili.com.foreign.example/video/x")));
        var error = await Assert.ThrowsAsync<ResolutionException>(() => adapter.ResolveAsync(new Uri(url), CancellationToken.None));
        Assert.Equal(ErrorCodes.UnsupportedResource, error.Error.Code);
        Assert.Empty(client.Requests);
    }

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1Ab2Cd3Ef4?p=2")]
    [InlineData("https://www.bilibili.com/video/av123456?p=2&cid=wrong&w_rid=untrusted")]
    public async Task SelectedPageIsNotTheUploadDurationOrAnotherPage(string url)
    {
        var client = new BilibiliTestClient();
        using var adapter = new BilibiliAdapter(client, TimeProvider.System);
        var work = await adapter.ResolveAsync(new Uri(url), CancellationToken.None);
        Assert.Equal(BilibiliTestClient.Bvid, work.WorkId);
        Assert.Equal(MediaTypes.Video, work.WorkType);
        Assert.EndsWith("?p=2", work.WebUrl);
        var item = Assert.Single(work.Items);
        Assert.Empty(item.ImageSources);
        Assert.Equal(1, item.Index);
        Assert.Equal(2, item.VideoSources.Length);
        Assert.All(item.VideoSources, source =>
        {
            Assert.Equal(24_000, source.DurationMs);
            Assert.Null(source.Width);
            Assert.Null(source.Height);
            Assert.Null(source.ExpiresAt);
            Assert.Contains("_p2_", source.FileName!);
            Assert.Equal(100, source.SizeBytes);
            Assert.Equal(PlatformHttpProfile.Bilibili.Referer, source.Headers["Referer"]);
            Assert.DoesNotContain("Cookie", source.Headers.Keys);
        });
        Assert.NotSame(item.VideoSources[0].Headers, item.VideoSources[1].Headers);
        var play = Assert.Single(client.Requests, uri => uri.AbsolutePath.EndsWith("/playurl", StringComparison.Ordinal));
        Assert.Contains("cid=654322", play.Query);
        Assert.DoesNotContain("wrong", play.Query);
        Assert.DoesNotContain("w_rid", play.Query);
        Assert.DoesNotContain("nav", string.Join(' ', client.Requests));
    }

    [Fact]
    public async Task ShortLinkKeepsExplicitPageAndDoesNotTrustPageShell()
    {
        var client = new BilibiliTestClient();
        using var adapter = new BilibiliAdapter(client, TimeProvider.System);
        var work = await adapter.ResolveAsync(new Uri("https://b23.tv/synthetic?p=2"), CancellationToken.None);
        Assert.EndsWith("?p=2", work.WebUrl);
        Assert.Equal(24_000, work.Items[0].VideoSources[0].DurationMs);
        Assert.Contains(client.Requests, uri => uri.AbsolutePath == "/x/web-interface/view");
    }

    [Fact]
    public async Task OutOfRangePageNeverFallsBackToFirstPage()
    {
        var client = new BilibiliTestClient();
        using var adapter = new BilibiliAdapter(client, TimeProvider.System);
        var error = await Assert.ThrowsAsync<ResolutionException>(() => adapter.ResolveAsync(
            new Uri(BilibiliTestClient.Url + "?p=3"), CancellationToken.None));
        Assert.Equal(ErrorCodes.InvalidInput, error.Error.Code);
        Assert.Single(client.Requests);
    }

    [Theory]
    [InlineData("dash", ErrorCodes.UnsupportedResource)]
    [InlineData("segments", ErrorCodes.UnsupportedResource)]
    [InlineData("flv", ErrorCodes.UnsupportedResource)]
    [InlineData("hls", ErrorCodes.UnsupportedResource)]
    [InlineData("preview", ErrorCodes.UnsupportedResource)]
    [InlineData("short", ErrorCodes.UnsupportedResource)]
    [InlineData("empty", ErrorCodes.MediaIncomplete)]
    [InlineData("unknown", ErrorCodes.UpstreamChanged)]
    [InlineData("wrongType", ErrorCodes.UpstreamChanged)]
    public async Task IncompleteOrCombinedFormatsCannotBeReportedAsWholeVideos(string mutation, string expected)
    {
        var node = JsonNode.Parse(BilibiliTestClient.PlayJson)!.AsObject();
        var data = node["data"]!.AsObject();
        switch (mutation)
        {
            case "dash": data.Remove("durl"); data["dash"] = new JsonObject(); break;
            case "segments": data["durl"]!.AsArray().Add(data["durl"]![0]!.DeepClone()); break;
            case "flv": data["format"] = "flv"; break;
            case "hls": data["format"] = "m3u8"; break;
            case "preview": data["is_preview"] = true; break;
            case "short": data["timelength"] = 1_000; break;
            case "empty": data["durl"] = new JsonArray(); break;
            case "unknown": data.Remove("format"); data.Remove("durl"); break;
            case "wrongType": data["timelength"] = new JsonObject(); break;
        }

        var client = new BilibiliTestClient { Play = node.ToJsonString() };
        using var adapter = new BilibiliAdapter(client, TimeProvider.System);
        var error = await Assert.ThrowsAsync<ResolutionException>(() => adapter.ResolveAsync(
            new Uri(BilibiliTestClient.Url), CancellationToken.None));
        Assert.Equal(expected, error.Error.Code);
        Assert.Equal(0, client.MediaCalls);
    }

    [Theory]
    [InlineData("bvid", ErrorCodes.UpstreamChanged)]
    [InlineData("cid", ErrorCodes.UpstreamChanged)]
    [InlineData("pages", ErrorCodes.UpstreamChanged)]
    [InlineData("rights", ErrorCodes.UpstreamChanged)]
    [InlineData("interactive", ErrorCodes.UnsupportedResource)]
    [InlineData("paid", ErrorCodes.UnsupportedResource)]
    [InlineData("upower", ErrorCodes.UnsupportedResource)]
    public async Task DetailIdentityAndRestrictionsAreNotIgnored(string mutation, string expected)
    {
        var node = JsonNode.Parse(BilibiliTestClient.DetailJson)!;
        var data = node["data"]!;
        switch (mutation)
        {
            case "bvid": data["bvid"] = "BV4Fe3Dc2Ba1"; break;
            case "cid": data["pages"]![0]!["cid"] = 0; break;
            case "pages": data["pages"] = new JsonArray(); break;
            case "rights": data["rights"] = null; break;
            case "interactive": data["rights"]!["is_stein_gate"] = 1; break;
            case "paid": data["rights"]!["ugc_pay"] = 1; break;
            case "upower": data["is_upower_exclusive"] = true; break;
        }

        var client = new BilibiliTestClient { Detail = node.ToJsonString() };
        using var adapter = new BilibiliAdapter(client, TimeProvider.System);
        var error = await Assert.ThrowsAsync<ResolutionException>(() => adapter.ResolveAsync(
            new Uri(BilibiliTestClient.Url), CancellationToken.None));
        Assert.Equal(expected, error.Error.Code);
        Assert.Single(client.Requests);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"code\":\"0\",\"data\":{}}")]
    [InlineData("{\"code\":0,\"data\":[]}")]
    [InlineData("{\"code\":0,\"data\":null}")]
    [InlineData("not json")]
    public async Task UnknownJsonShapesProduceProtocolErrors(string json)
    {
        var client = new BilibiliTestClient { Detail = json };
        using var adapter = new BilibiliAdapter(client, TimeProvider.System);
        var error = await Assert.ThrowsAsync<ResolutionException>(() => adapter.ResolveAsync(new Uri(BilibiliTestClient.Url), CancellationToken.None));
        Assert.Equal(ErrorCodes.UpstreamChanged, error.Error.Code);
    }

    [Theory]
    [InlineData(false, 403, ErrorCodes.MediaIncomplete)]
    [InlineData(false, 503, ErrorCodes.UpstreamUnavailable)]
    [InlineData(true, 403, null)]
    public async Task BackupFailuresNeverBecomeEmptySuccess(bool backupWorks, int status, string? expected)
    {
        var client = new BilibiliTestClient
        {
            MediaHandler = uri => BilibiliTestClient.Location(uri, backupWorks && uri.AbsolutePath.Contains("backup", StringComparison.Ordinal) ? 200 : status)
        };
        using var adapter = new BilibiliAdapter(client, TimeProvider.System);
        if (expected is null)
        {
            var work = await adapter.ResolveAsync(new Uri(BilibiliTestClient.Url), CancellationToken.None);
            Assert.Single(work.Items[0].VideoSources);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ResolutionException>(() => adapter.ResolveAsync(new Uri(BilibiliTestClient.Url), CancellationToken.None));
            Assert.Equal(expected, error.Error.Code);
        }
    }

    [Fact]
    public async Task MimeTypeComparisonIsCaseInsensitiveAndCanonical()
    {
        var client = new BilibiliTestClient
        {
            MediaHandler = uri => BilibiliTestClient.Location(uri, 200) with { MimeType = "Video/MP4" }
        };
        using var adapter = new BilibiliAdapter(client, TimeProvider.System);
        var work = await adapter.ResolveAsync(new Uri(BilibiliTestClient.Url), CancellationToken.None);
        Assert.All(work.Items[0].VideoSources, source =>
        {
            Assert.Equal("video/mp4", source.MimeType);
            Assert.EndsWith(".mp4", source.FileName);
        });
    }

    [Fact]
    public async Task AnUntrustedBackupListCannotCauseUnboundedNetworkAttempts()
    {
        var node = JsonNode.Parse(BilibiliTestClient.PlayJson)!;
        var backups = new JsonArray();
        for (var index = 0; index < 64; index++)
        {
            backups.Add("https://upos-synthetic.bilivideo.com/fail-" + index + ".mp4");
        }

        node["data"]!["durl"]![0]!["backup_url"] = backups;
        var client = new BilibiliTestClient
        {
            Play = node.ToJsonString(),
            MediaHandler = uri => BilibiliTestClient.Location(uri, 403)
        };
        using var adapter = new BilibiliAdapter(client, TimeProvider.System);
        var error = await Assert.ThrowsAsync<ResolutionException>(() => adapter.ResolveAsync(new Uri(BilibiliTestClient.Url), CancellationToken.None));
        Assert.Equal(ErrorCodes.MediaIncomplete, error.Error.Code);
        Assert.Equal(16, client.MediaCalls);
    }

    [Fact]
    public async Task UnsignedRefusalCanUseLocalWbiAndSharedPublicKeysWithoutIdentity()
    {
        var client = new BilibiliTestClient { RequireSignature = true };
        using var adapter = new BilibiliAdapter(client, new BilibiliTestClock());
        await adapter.ResolveAsync(new Uri(BilibiliTestClient.Url), CancellationToken.None);
        await adapter.ResolveAsync(new Uri(BilibiliTestClient.Url), CancellationToken.None);
        Assert.Equal(1, client.NavCalls);
        var signed = client.Requests.Where(uri => uri.AbsolutePath.Contains("/wbi/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, signed.Length);
        Assert.All(signed, uri =>
        {
            Assert.Contains("wts=1700000000", uri.Query);
            Assert.Contains("w_rid=", uri.Query);
            Assert.DoesNotContain("buvid", uri.Query);
            Assert.DoesNotContain("dm_img", uri.Query);
        });
    }

    [Fact]
    public void WbiMatchesIndependentUtf8QueryVector()
    {
        using var nav = JsonDocument.Parse(BilibiliTestClient.NavJson);
        var mixin = BilibiliWbiSigner.ReadMixin(nav.RootElement.GetProperty("data"));
        Assert.Equal("1022a87ffdaf532cb45ee953dce8c96d", mixin);
        var query = BilibiliWbiSigner.Sign(new Dictionary<string, string>
        {
            ["path"] = "/中文/'x*()",
            ["foo"] = "hello world!",
            ["empty"] = ""
        }, mixin, 1_700_000_000);
        Assert.Equal("empty=&foo=hello%20world&path=%2F%E4%B8%AD%E6%96%87%2Fx&wts=1700000000&w_rid=cdd79703fe0d12aa3782f8365b608461", query);
    }

    [Fact]
    public async Task PublicKeyCacheIsBoundedAndCancelledWaitersDoNotPoisonIt()
    {
        var client = new BilibiliTestClient();
        var clock = new BilibiliTestClock();
        using var keys = new BilibiliWbiKeys(client, clock);
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => keys.GetAsync(CancellationToken.None)));
        Assert.Single(results.Distinct());
        Assert.Equal(1, client.NavCalls);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => keys.GetAsync(cancelled.Token));
        clock.Now += TimeSpan.FromMinutes(31);
        await keys.GetAsync(CancellationToken.None);
        Assert.Equal(2, client.NavCalls);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task ExplicitSignatureFailureRefreshesAtMostOnce(int failures, bool fails)
    {
        var client = new BilibiliTestClient { RequireSignature = true, SignedFailures = failures };
        using var adapter = new BilibiliAdapter(client, new BilibiliTestClock());
        if (fails)
        {
            var error = await Assert.ThrowsAsync<ResolutionException>(() => adapter.ResolveAsync(new Uri(BilibiliTestClient.Url), CancellationToken.None));
            Assert.Equal(ErrorCodes.CookieFreeUnavailable, error.Error.Code);
            Assert.Equal(2, client.SignedCalls);
        }
        else
        {
            await adapter.ResolveAsync(new Uri(BilibiliTestClient.Url), CancellationToken.None);
            Assert.Equal(3, client.SignedCalls);
        }

        Assert.Equal(2, client.NavCalls);
    }

    [Theory]
    [InlineData("{\"wbi_img\":[]}")]
    [InlineData("{\"wbi_img\":{\"img_url\":42}}")]
    [InlineData("{\"wbi_img\":{\"img_url\":\"https://images.example/short.png\"}}")]
    [InlineData("[]")]
    public void InvalidPublicKeyShapesDoNotEscapeAsInternalErrors(string data)
    {
        using var document = JsonDocument.Parse(data);
        var error = Assert.Throws<ResolutionException>(() => BilibiliWbiSigner.ReadMixin(document.RootElement));
        Assert.Equal(ErrorCodes.UpstreamChanged, error.Error.Code);
    }

    [Fact]
    public async Task DetailAndPlayNotFoundHaveDifferentMeaning()
    {
        var client = new BilibiliTestClient { Detail = "{\"code\":-404}" };
        using var first = new BilibiliAdapter(client, TimeProvider.System);
        var deleted = await Assert.ThrowsAsync<ResolutionException>(() => first.ResolveAsync(new Uri(BilibiliTestClient.Url), CancellationToken.None));
        Assert.Equal(ErrorCodes.ContentUnavailable, deleted.Error.Code);
        using var second = new BilibiliAdapter(new BilibiliTestClient { Play = "{\"code\":-404}" }, TimeProvider.System);
        var changed = await Assert.ThrowsAsync<ResolutionException>(() => second.ResolveAsync(new Uri(BilibiliTestClient.Url), CancellationToken.None));
        Assert.Equal(ErrorCodes.UpstreamChanged, changed.Error.Code);
    }
}

internal sealed class BilibiliTestClock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class BilibiliTestClient : IUpstreamClient
{
    internal const string Bvid = "BV1Ab2Cd3Ef4";
    internal const string Url = "https://www.bilibili.com/video/" + Bvid;
    internal const string DetailJson = """
        {"code":0,"data":{"bvid":"BV1Ab2Cd3Ef4","aid":123456,"desc":"合成 Bilibili 描述",
        "duration":36,"dimension":{"width":1920,"height":1080},"rights":{},"pages":[
        {"cid":654321,"page":1,"duration":12},{"cid":654322,"page":2,"duration":24}]}}
        """;
    internal const string PlayJson = """
        {"code":0,"data":{"quality":64,"format":"mp4720","timelength":12000,"durl":[
        {"length":12000,"size":100,"url":"https://upos-synthetic.bilivideo.com/main.mp4?sig=A%2FB%2B",
        "backup_url":["https://upos-synthetic.bilivideo.com/backup.mp4?sig=C%2FD"]}]}}
        """;
    internal const string NavJson = """
        {"code":-101,"data":{"isLogin":false,"wbi_img":{
        "img_url":"https://images.example/0123456789abcdef0123456789abcdef.png",
        "sub_url":"https://images.example/fedcba9876543210fedcba9876543210.png"}}}
        """;
    internal ConcurrentQueue<Uri> Requests { get; } = [];
    internal string Detail { get; init; } = DetailJson;
    internal string Play { get; init; } = PlayJson;
    internal bool RequireSignature { get; init; }
    internal int SignedFailures { get; init; }
    internal int SignedCalls;
    internal Func<Uri, MediaLocation>? MediaHandler { get; init; }
    internal int NavCalls;
    internal int MediaCalls;

    public Task<MetadataPage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Enqueue(uri);
        if (uri.IdnHost == "b23.tv")
        {
            return Task.FromResult(Reply(new Uri(Url), "<html>shell</html>"));
        }

        if (uri.AbsolutePath.EndsWith("/nav", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref NavCalls);
            return Task.FromResult(Reply(uri, NavJson));
        }

        if (RequireSignature && !uri.AbsolutePath.Contains("/wbi/", StringComparison.Ordinal))
        {
            return Task.FromResult(Reply(uri, "{\"code\":-403}", HttpStatusCode.Forbidden));
        }

        if (uri.AbsolutePath.Contains("/wbi/", StringComparison.Ordinal) &&
            Interlocked.Increment(ref SignedCalls) <= SignedFailures)
        {
            return Task.FromResult(Reply(uri, "{\"code\":-403,\"message\":\"w_rid signature invalid\"}"));
        }

        var body = uri.AbsolutePath.EndsWith("/view", StringComparison.Ordinal) ? Detail : Play;
        if (uri.Query.Contains("cid=654322", StringComparison.Ordinal))
        {
            body = body.Replace("12000", "24000", StringComparison.Ordinal);
        }

        return Task.FromResult(Reply(uri, body));
    }

    public Task<MediaLocation> ResolveMediaAsync(Uri uri, string expectedType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref MediaCalls);
        return Task.FromResult(MediaHandler?.Invoke(uri) ?? Location(uri, 200));
    }

    public Dictionary<string, string> CreateDownloadHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["User-Agent"] = "synthetic-client",
        ["Referer"] = PlatformHttpProfile.Bilibili.Referer,
        ["Accept"] = "*/*"
    };

    internal static MetadataPage Reply(Uri uri, string body, HttpStatusCode status = HttpStatusCode.OK) => new(
        FinalUri: uri,
        StatusCode: (int)status,
        ContentType: "application/json",
        Body: body,
        BytesRead: Encoding.UTF8.GetByteCount(body),
        RetryAfterSeconds: null);

    internal static MediaLocation Location(Uri uri, int status) => new(
        FinalUri: uri,
        StatusCode: status,
        MimeType: "video/mp4",
        SizeBytes: 100,
        RetryAfterSeconds: null);
}
