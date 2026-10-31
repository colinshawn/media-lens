using MediaLens.Http;
using MediaLens.Probe.Links;

namespace MediaLens.Probe.Tests;

public sealed class ShareLinksTests
{
    private const string SyntheticWorkId = "1234567890123456789";

    [Theory]
    [InlineData("复制这个分享（https://v.douyin.com/example/），打开观看", "https://v.douyin.com/example/")]
    [InlineData("文案 v.douyin.com/example/。", "https://v.douyin.com/example/")]
    [InlineData("https://unrelated.example/ https://www.douyin.com/video/123456", "https://www.douyin.com/video/123456")]
    public void ShareTextSelectsTheFirstAllowedUrl(string text, string expected)
    {
        Assert.Equal(expected, ShareLinks.Extract(text).AbsoluteUri);
    }

    [Theory]
    [InlineData("https://www.douyin.com/video/1234567890123456789", "video")]
    [InlineData("https://www.iesdouyin.com/share/video/1234567890123456789/", "video")]
    [InlineData("https://www.douyin.com/note/1234567890123456789", "note")]
    [InlineData("https://www.iesdouyin.com/share/slides/1234567890123456789/", "note")]
    public void WorkPathsPreserveIdPrecision(string value, string kind)
    {
        var work = ShareLinks.Identify(new Uri(value));

        Assert.Equal(SyntheticWorkId, work.WorkId);
        Assert.Equal(kind, work.PathKind);
    }

    [Fact]
    public void ModalIdTakesPriorityOverVidAndPath()
    {
        var work = ShareLinks.Identify(new Uri(
            "https://www.douyin.com/video/111111?vid=222222&modal_id=" + SyntheticWorkId));

        Assert.Equal(SyntheticWorkId, work.WorkId);
    }

    [Theory]
    [InlineData("https://www.douyin.com/user/example")]
    [InlineData("https://www.douyin.com/music/123456")]
    [InlineData("https://www.douyin.com/collection/123456")]
    [InlineData("https://live.douyin.com/123456")]
    public void NonWorkPathsAreRejected(string value)
    {
        Assert.Throws<UpstreamRequestException>(() => ShareLinks.Identify(new Uri(value)));
    }

    [Fact]
    public void InvalidAndOversizedInputsDoNotProduceARequestTarget()
    {
        Assert.Throws<UpstreamRequestException>(() => ShareLinks.Extract("没有链接"));
        Assert.Throws<UpstreamRequestException>(() => ShareLinks.Extract(
            new string('x', ProbeDefaults.MaximumInputCharacters + 1)));
    }
}
