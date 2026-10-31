using System.Text.Json;
using MediaLens.Contracts;
using MediaLens.Platforms;
using MediaLens.Resolution;

namespace MediaLens.Tests;

public sealed class ContractTests
{
    [Theory]
    [InlineData("video")]
    [InlineData("image")]
    [InlineData("livePhoto")]
    [InlineData("mixedAlbum")]
    public async Task EveryMediaShapeHasFixedKeysNullsAndIndependentCandidateHeaders(string kind)
    {
        var jpeg = Fixtures.Source("https://cdn.example/still") with
        {
            MimeType = "image/jpeg",
            Headers = new Dictionary<string, string> { ["Referer"] = "https://image.example/" }
        };
        var webp = Fixtures.Source("https://cdn.example/alternative") with
        {
            MimeType = "image/webp",
            Width = 640,
            Height = 480,
            Headers = new Dictionary<string, string> { ["User-Agent"] = "synthetic-image" }
        };
        var video = Fixtures.Source() with
        {
            MimeType = "video/mp4",
            DurationMs = 3000,
            Headers = new Dictionary<string, string> { ["User-Agent"] = "synthetic-video" }
        };
        MediaItem[] items = kind switch
        {
            "video" => [new MediaItem(1, MediaTypes.Video, [], [video])],
            "image" => [new MediaItem(1, MediaTypes.Image, [jpeg, webp], [])],
            "livePhoto" => [new MediaItem(1, MediaTypes.LivePhoto, [jpeg, webp], [video])],
            _ => [new MediaItem(1, MediaTypes.Image, [jpeg], []), new MediaItem(2, MediaTypes.LivePhoto, [webp], [video])]
        };
        var work = Fixtures.Video() with { WorkType = kind == "video" ? MediaTypes.Video : MediaTypes.Album, Items = items };
        var result = await ResolveAsync(work);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, MediaLensJsonContext.Default.ResolveResponse));

        var sourceKeys = new[] { "durationMs", "expiresAt", "fileName", "headers", "height", "mimeType", "sizeBytes", "url", "width" };
        foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
        {
            Assert.Equal(new[] { "imageSources", "index", "type", "videoSources" },
                item.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
            foreach (var source in item.GetProperty("imageSources").EnumerateArray()
                         .Concat(item.GetProperty("videoSources").EnumerateArray()))
            {
                Assert.Equal(sourceKeys, source.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
                Assert.Equal(JsonValueKind.Null, source.GetProperty("sizeBytes").ValueKind);
                Assert.Equal(JsonValueKind.Null, source.GetProperty("expiresAt").ValueKind);
                Assert.Equal(JsonValueKind.Object, source.GetProperty("headers").ValueKind);
            }
        }

        if (kind == "livePhoto")
        {
            Assert.NotEqual(result.Items[0].ImageSources[0].Headers, result.Items[0].VideoSources[0].Headers);
            Assert.Equal("image/webp", result.Items[0].ImageSources[1].MimeType);
            Assert.Equal(640, result.Items[0].ImageSources[1].Width);
        }
    }

    [Fact]
    public async Task UnknownTechnicalFieldsAndUnusedCollectionsStayPresent()
    {
        var result = await ResolveAsync(Fixtures.Video());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, MediaLensJsonContext.Default.ResolveResponse));
        var item = json.RootElement.GetProperty("items")[0];
        Assert.Empty(item.GetProperty("imageSources").EnumerateArray());
        var source = item.GetProperty("videoSources")[0];
        Assert.Empty(source.GetProperty("headers").EnumerateObject());
        foreach (var name in new[] { "mimeType", "fileName", "width", "height", "durationMs", "sizeBytes", "expiresAt" })
        {
            Assert.Equal(JsonValueKind.Null, source.GetProperty(name).ValueKind);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("2026-01-02T00:00:00Z", "2026-01-02T00:00:00Z")]
    public async Task TopLevelExpiryRequiresEverySourceToHaveAKnownExpiry(string? secondExpiry, string? expected)
    {
        var first = Fixtures.Source(expiresAt: "2026-01-03T00:00:00Z");
        var second = Fixtures.Source("https://cdn.example/second", secondExpiry);
        var work = Fixtures.Video() with { Items = [new MediaItem(1, MediaTypes.Video, [], [first, second])] };

        var result = await ResolveAsync(work);

        Assert.Equal(expected, result.ExpiresAt);
        Assert.Equal(first.ExpiresAt, result.Items[0].VideoSources[0].ExpiresAt);
        Assert.Equal(secondExpiry, result.Items[0].VideoSources[1].ExpiresAt);
    }

    [Fact]
    public async Task ExpiredBackupsAreExcludedAndMissingRequiredMediaFails()
    {
        var expired = Fixtures.Source(expiresAt: "2025-12-31T00:00:00Z");
        var work = Fixtures.Video() with { Items = [new MediaItem(1, MediaTypes.Video, [], [expired, Fixtures.Source()])] };
        var result = await ResolveAsync(work);
        Assert.Single(result.Items[0].VideoSources);

        var incomplete = work with { Items = [new MediaItem(1, MediaTypes.Video, [], [expired])] };
        var error = await Assert.ThrowsAsync<ResolutionException>(() => ResolveAsync(incomplete));
        Assert.Equal(ErrorCodes.MediaIncomplete, error.Error.Code);
    }

    [Fact]
    public async Task SensitiveHeadersAreNeverReturned()
    {
        var source = Fixtures.Source() with
        {
            Headers = new Dictionary<string, string>
            {
                ["Cookie"] = "synthetic=value",
                ["Set-Cookie"] = "synthetic=value",
                ["Authorization"] = "synthetic-token",
                ["Proxy-Authorization"] = "synthetic-token",
                ["User-Agent"] = "synthetic-download-client"
            }
        };
        var result = await ResolveAsync(Fixtures.Video() with { Items = [new MediaItem(1, MediaTypes.Video, [], [source])] });

        Assert.Equal("User-Agent", Assert.Single(result.Items[0].VideoSources[0].Headers).Key);
    }

    [Fact]
    public async Task MissingLivePairAndNonContinuousIndexesAreRejected()
    {
        var live = Fixtures.Video() with
        {
            WorkType = MediaTypes.Album,
            Items = [new MediaItem(1, MediaTypes.LivePhoto, [Fixtures.Source()], [])]
        };
        var missing = await Assert.ThrowsAsync<ResolutionException>(() => ResolveAsync(live));
        Assert.Equal(ErrorCodes.MediaIncomplete, missing.Error.Code);

        var wrongIndex = Fixtures.Video() with { Items = [new MediaItem(2, MediaTypes.Video, [], [Fixtures.Source()])] };
        var invalid = await Assert.ThrowsAsync<ResolutionException>(() => ResolveAsync(wrongIndex));
        Assert.Equal(ErrorCodes.UpstreamChanged, invalid.Error.Code);
    }

    private static Task<ResolveResponse> ResolveAsync(ResolvedWork work)
    {
        var adapter = new StubAdapter("douyin", "www.douyin.com", (_, _) => Task.FromResult(work));
        var service = new ResolutionService(Fixtures.Registry(adapter), new FixedClock(), ResolutionSettings.Default);
        return service.ResolveAsync(Fixtures.WorkUrl, null, "synthetic-request", CancellationToken.None);
    }
}
