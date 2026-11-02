using MediaLens.Api;
using MediaLens.Contracts;
using MediaLens.Http;
using Microsoft.Extensions.Logging;

namespace MediaLens.Tests;

public sealed class TimedUpstreamClientTests
{
    [Fact]
    public async Task CompletedRequestsLogStageHostStatusAndElapsedWithoutFullUri()
    {
        const string privateQuery = "synthetic-private-query";
        using var logs = new CaptureLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var client = new TimedUpstreamClient(
            new StubUpstreamClient(Fixtures.VideoJson),
            loggerFactory.CreateLogger<TimedUpstreamClient>());
        var shortLink = new Uri("https://v.douyin.com/synthetic/?private=" + privateQuery);
        var mediaLink = new Uri("https://cdn.example/synthetic.jpg?private=" + privateQuery);

        var page = await client.GetAsync(shortLink, CancellationToken.None);
        var media = await client.ResolveMediaAsync(mediaLink, MediaTypes.Image, CancellationToken.None);

        Assert.Equal((int)System.Net.HttpStatusCode.OK, page.StatusCode);
        Assert.Equal((int)System.Net.HttpStatusCode.OK, media.StatusCode);
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("short-link expansion completed", StringComparison.Ordinal) &&
            entry.Message.Contains("v.douyin.com", StringComparison.Ordinal) &&
            entry.Message.Contains("elapsed", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("media probe completed", StringComparison.Ordinal) &&
            entry.Message.Contains("cdn.example", StringComparison.Ordinal) &&
            entry.Message.Contains(MediaTypes.Image, StringComparison.Ordinal) &&
            entry.Message.Contains("elapsed", StringComparison.Ordinal));
        Assert.All(logs.Entries, entry =>
        {
            Assert.DoesNotContain(shortLink.OriginalString, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(mediaLink.OriginalString, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(privateQuery, entry.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CancellationLogsTheMediaStageAndElapsed()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new StubUpstreamClient(Fixtures.VideoJson)
        {
            MediaHandler = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException();
            }
        };
        using var logs = new CaptureLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var client = new TimedUpstreamClient(upstream, loggerFactory.CreateLogger<TimedUpstreamClient>());
        using var cancellation = new CancellationTokenSource();

        var request = client.ResolveMediaAsync(
            new Uri("https://cdn.example/synthetic.mp4"),
            MediaTypes.Video,
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("media probe canceled", StringComparison.Ordinal) &&
            entry.Message.Contains("cdn.example", StringComparison.Ordinal) &&
            entry.Message.Contains(MediaTypes.Video, StringComparison.Ordinal) &&
            entry.Message.Contains("elapsed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailuresLogOnlyTheExceptionTypeAndElapsed()
    {
        const string secret = "synthetic-upstream-secret";
        var upstream = new StubUpstreamClient(Fixtures.VideoJson)
        {
            MediaHandler = (_, _) => throw new HttpRequestException(secret)
        };
        using var logs = new CaptureLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var client = new TimedUpstreamClient(upstream, loggerFactory.CreateLogger<TimedUpstreamClient>());

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ResolveMediaAsync(
            new Uri("https://cdn.example/synthetic.mp4"),
            MediaTypes.Video,
            CancellationToken.None));

        var entry = Assert.Single(logs.Entries);
        Assert.Contains("media probe failed", entry.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(HttpRequestException), entry.Message, StringComparison.Ordinal);
        Assert.Contains("elapsed", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, entry.Message, StringComparison.Ordinal);
        Assert.Null(entry.ExceptionMessage);
    }
}
