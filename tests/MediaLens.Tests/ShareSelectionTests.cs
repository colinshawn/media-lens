using MediaLens.Platforms.Douyin;
using MediaLens.Resolution;
using MediaLens.Shares;

namespace MediaLens.Tests;

public sealed class ShareSelectionTests
{
    [Theory]
    [InlineData("文案（v.douyin.com/example/），打开观看", "https://v.douyin.com/example/")]
    [InlineData("查看 www.douyin.com/video/123456。", "https://www.douyin.com/video/123456")]
    [InlineData("https://foreign.example/x https://www.douyin.com/video/123456", "https://foreign.example/x")]
    public void CandidatesPreserveOrderAndExcludeChinesePunctuation(string text, string expected)
    {
        Assert.Equal(expected, ShareUrlExtractor.Extract(text)[0].OriginalString);
    }

    [Theory]
    [InlineData("https://www.iesdouyin.com/share/video/1234567890123456789/", "1234567890123456789")]
    [InlineData("https://www.douyin.com/note/1234567890123456789", "1234567890123456789")]
    [InlineData("https://www.iesdouyin.com/share/slides/1234567890123456789/", "1234567890123456789")]
    [InlineData("https://www.douyin.com/user/synthetic?vid=111111&modal_id=1234567890123456789", "1234567890123456789")]
    [InlineData("https://www.douyin.com/video/111111?vid=1234567890123456789", "1234567890123456789")]
    public void WorkIdsStayStringsWithQueryPriority(string value, string expected)
    {
        Assert.Equal(expected, DouyinWorkLink.Parse(new Uri(value)).WorkId);
    }

    [Theory]
    [InlineData("https://www.douyin.com/user/synthetic")]
    [InlineData("https://www.douyin.com/collection/123456")]
    [InlineData("https://www.douyin.com/music/123456")]
    [InlineData("https://live.douyin.com/123456")]
    [InlineData("https://www.douyin.com/video/123456/unsupported-tail")]
    public void NonSingleWorkResourcesAreRejected(string value)
    {
        var error = Assert.Throws<ResolutionException>(() => DouyinWorkLink.Parse(new Uri(value)));
        Assert.Equal(ErrorCodes.UnsupportedResource, error.Error.Code);
    }
}
