using MediaLens.Probe.Metadata;

namespace MediaLens.Probe.Tests;

public sealed class MetadataInspectorTests
{
    private const string SyntheticWorkId = "1234567890123456789";
    private const string VideoPayload = """
        {"aweme_detail":{"aweme_id":"1234567890123456789","video":{
          "play_addr":{"url_list":["https://cdn.example/video.mp4"]}}}}
        """;
    private const string MixedPayload = """
        {"aweme_detail":{"aweme_id":1234567890123456789,"images":[
          {"url_list":["https://cdn.example/01.jpg"]},
          {"url_list":["https://cdn.example/02.jpg"],"live_photo_type":1,
           "video":{"play_addr":{"url_list":["https://cdn.example/02.mp4"]}}}
        ]}}
        """;

    [Theory]
    [InlineData("json")]
    [InlineData("routerData")]
    [InlineData("renderData")]
    public void StructuredPayloadsAreReadWithoutExecutingScripts(string kind)
    {
        var body = kind switch
        {
            "routerData" => "<script>window._ROUTER_DATA = " + VideoPayload + "; throw 'do not run';</script>",
            "renderData" => "<script type='application/json' id='RENDER_DATA'>" +
                            Uri.EscapeDataString(VideoPayload) + "</script>",
            _ => VideoPayload
        };

        var summary = MetadataInspector.Inspect(body, SyntheticWorkId);

        Assert.Equal(kind, summary.PayloadKind);
        Assert.True(summary.WorkMatched);
        Assert.True(summary.CompleteMediaMetadata);
        Assert.Equal(1, summary.VideoSourceCount);
    }

    [Fact]
    public void MixedAlbumNeedsStillAndClipOnTheSameItem()
    {
        var summary = MetadataInspector.Inspect(MixedPayload, SyntheticWorkId);

        Assert.True(summary.CompleteMediaMetadata);
        Assert.Equal(2, summary.ImageCount);
        Assert.Equal(1, summary.LivePhotoCount);
        Assert.Equal(2, summary.ImageSourceCount);
        Assert.Equal(1, summary.VideoSourceCount);
    }

    [Fact]
    public void MissingLiveClipCannotUseAlbumMusicOrTopLevelVideo()
    {
        const string payload = """
            {"aweme_id":"1234567890123456789",
             "video":{"play_addr":{"url_list":["https://cdn.example/album.mp4"]}},
             "images":[{"url_list":["https://cdn.example/still.jpg"],"live_photo_type":1}]}
            """;

        var summary = MetadataInspector.Inspect(payload, SyntheticWorkId);

        Assert.True(summary.WorkMatched);
        Assert.False(summary.CompleteMediaMetadata);
        Assert.Equal(1, summary.LivePhotoCount);
        Assert.Equal(0, summary.VideoSourceCount);
    }

    [Fact]
    public void MissingStillCannotBePartialSuccess()
    {
        var payload = MixedPayload.Replace("https://cdn.example/01.jpg", "invalid", StringComparison.Ordinal);

        Assert.False(MetadataInspector.Inspect(payload, SyntheticWorkId).CompleteMediaMetadata);
    }

    [Fact]
    public void IncompleteVideoObjectStillRequiresALiveClip()
    {
        const string payload = """
            {"aweme_id":"1234567890123456789",
             "images":[{"url_list":["https://cdn.example/still.jpg"],"video":{}}]}
            """;

        var summary = MetadataInspector.Inspect(payload, SyntheticWorkId);

        Assert.Equal(1, summary.LivePhotoCount);
        Assert.False(summary.CompleteMediaMetadata);
    }

    [Fact]
    public void RecommendationFromAnotherWorkIsNotTheRequestedWork()
    {
        Assert.False(MetadataInspector.Inspect(VideoPayload, "999999").WorkMatched);
    }

    [Theory]
    [InlineData("<html>public shell only</html>", "none")]
    [InlineData("window._ROUTER_DATA = {malformed", "malformedJson")]
    public void MissingOrMalformedDataDoesNotImplySuccess(string body, string expectedKind)
    {
        var summary = MetadataInspector.Inspect(body, SyntheticWorkId);

        Assert.Equal(expectedKind, summary.PayloadKind);
        Assert.False(summary.CompleteMediaMetadata);
    }
}
