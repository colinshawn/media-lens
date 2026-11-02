namespace MediaLens.Http;

internal enum HttpTimeoutPhase
{
    Connect,
    TlsHandshake,
    ResponseHeaders,
    MetadataBody
}

internal sealed class UpstreamTimeoutException(HttpTimeoutPhase phase, Exception innerException)
    : HttpRequestException("An upstream HTTP phase exceeded its time limit.", innerException)
{
    internal HttpTimeoutPhase Phase { get; } = phase;

    internal static HttpTimeoutPhase? FindPhase(Exception exception)
    {
        // HttpClient 可能包装连接回调的异常，日志只提取固定阶段，不输出异常中的 URL
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is UpstreamTimeoutException timeout)
            {
                return timeout.Phase;
            }
        }

        return null;
    }
}
