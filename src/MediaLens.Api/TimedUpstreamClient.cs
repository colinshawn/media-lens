using System.Diagnostics;
using MediaLens.Http;

namespace MediaLens.Api;

internal sealed class TimedUpstreamClient(IUpstreamClient inner, ILogger<TimedUpstreamClient> logger) : IUpstreamClient
{
    public async Task<MetadataPage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        var stage = GetMetadataStage(uri);
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var page = await inner.GetAsync(uri, cancellationToken);
            logger.LogInformation(
                "Upstream {Stage} completed: host {Host}, status {StatusCode}, elapsed {ElapsedMilliseconds} ms",
                stage,
                uri.IdnHost,
                page.StatusCode,
                ElapsedMilliseconds(startedAt));
            return page;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Upstream {Stage} canceled: host {Host}, elapsed {ElapsedMilliseconds} ms",
                stage,
                uri.IdnHost,
                ElapsedMilliseconds(startedAt));
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Upstream {Stage} failed: host {Host}, exception {ExceptionType}, elapsed {ElapsedMilliseconds} ms",
                stage,
                uri.IdnHost,
                exception.GetType().Name,
                ElapsedMilliseconds(startedAt));
            throw;
        }
    }

    public async Task<MediaLocation> ResolveMediaAsync(Uri uri, string expectedType, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var location = await inner.ResolveMediaAsync(uri, expectedType, cancellationToken);
            logger.LogInformation(
                "Upstream media probe completed: host {Host}, type {MediaType}, status {StatusCode}, elapsed {ElapsedMilliseconds} ms",
                uri.IdnHost,
                expectedType,
                location.StatusCode,
                ElapsedMilliseconds(startedAt));
            return location;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Upstream media probe canceled: host {Host}, type {MediaType}, elapsed {ElapsedMilliseconds} ms",
                uri.IdnHost,
                expectedType,
                ElapsedMilliseconds(startedAt));
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Upstream media probe failed: host {Host}, type {MediaType}, exception {ExceptionType}, elapsed {ElapsedMilliseconds} ms",
                uri.IdnHost,
                expectedType,
                exception.GetType().Name,
                ElapsedMilliseconds(startedAt));
            throw;
        }
    }

    public Dictionary<string, string> CreateDownloadHeaders() => inner.CreateDownloadHeaders();

    private static string GetMetadataStage(Uri uri) => uri.IdnHost switch
    {
        "v.douyin.com" or "b23.tv" => "short-link expansion",
        "api.amemv.com" or "aweme.snssdk.com" or "api.bilibili.com" => "detail retrieval",
        "jingxuan.douyin.com" => "featured page read",
        _ => "share page read"
    };

    private static long ElapsedMilliseconds(long startedAt) =>
        (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
}
