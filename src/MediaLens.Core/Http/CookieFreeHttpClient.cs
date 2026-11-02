using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Text;

namespace MediaLens.Http;

/// <summary>只读取有限元数据，逐跳展开地址；所有请求均使用相同的无 Cookie 策略。</summary>
public sealed class CookieFreeHttpClient : IUpstreamClient, IDisposable
{
    private static readonly HttpRequestOptionsKey<ResponseHeadersDeadline> HeadersDeadlineKey = new("MediaLens.ResponseHeadersDeadline");
    private readonly IHttpTargetPolicy _targetPolicy;
    private readonly HttpClient _client;
    private readonly string _userAgent;
    private readonly PlatformHttpProfile _profile;
    private readonly HttpTimeouts _timeouts;
    private readonly RemoteCertificateValidationCallback? _certificateValidation;

    public CookieFreeHttpClient(
        IHttpTargetPolicy targetPolicy,
        string? userAgent = null,
        PlatformHttpProfile? profile = null)
        : this(targetPolicy, HttpTimeouts.Default, userAgent, profile)
    {
    }

    internal CookieFreeHttpClient(
        IHttpTargetPolicy targetPolicy,
        HttpTimeouts timeouts,
        string? userAgent = null,
        PlatformHttpProfile? profile = null,
        RemoteCertificateValidationCallback? certificateValidation = null)
    {
        _targetPolicy = targetPolicy;
        _timeouts = timeouts;
        _certificateValidation = certificateValidation;
        _profile = profile ?? PlatformHttpProfile.Douyin;
        _userAgent = userAgent ?? _profile.UserAgent;
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.Brotli,
            // DNS/TCP 与 TLS 各有独立预算，避免处理器用一个连接时限截断两个阶段
            ConnectTimeout = Timeout.InfiniteTimeSpan,
            MaxResponseDrainSize = HttpDefaults.NoResponseDrainBytes,
            ConnectCallback = ConnectAsync
        };
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public Task<MetadataPage> GetAsync(Uri uri, CancellationToken cancellationToken) =>
        GetAsync(uri, static _ => { }, cancellationToken);

    public async Task<MetadataPage> GetAsync(
        Uri uri,
        Action<HttpObservation> observe,
        CancellationToken cancellationToken)
    {
        var current = uri;
        var visited = new HashSet<string>(StringComparer.Ordinal);

        for (var hop = 0; hop <= HttpDefaults.MaximumRedirects; hop++)
        {
            _targetPolicy.ValidateUri(current);
            if (!visited.Add(current.AbsoluteUri))
            {
                throw new UpstreamRequestException(HttpFailureCodes.RedirectLoop);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd(_userAgent);
            request.Headers.Accept.ParseAdd(HttpDefaults.Accept);
            request.Headers.Referrer = new Uri(_profile.Referer);

            using var response = await SendForHeadersAsync(request, cancellationToken);

            observe(new HttpObservation(
                Host: current.IdnHost,
                StatusCode: (int)response.StatusCode,
                CookieHeaderObserved: request.Headers.Contains(HttpDefaults.CookieHeader),
                SetCookieObserved: response.Headers.Contains(HttpDefaults.SetCookieHeader)));

            if (IsRedirect(response.StatusCode))
            {
                if (hop == HttpDefaults.MaximumRedirects)
                {
                    throw new UpstreamRequestException(HttpFailureCodes.RedirectLimit);
                }

                if (!response.Headers.TryGetValues(HttpDefaults.RedirectLocationHeader, out var locations) ||
                    !Uri.TryCreate(current, locations.FirstOrDefault(), out var next))
                {
                    throw new UpstreamRequestException(HttpFailureCodes.InvalidRedirect);
                }

                _profile.ValidateRedirect(next, isMedia: false);
                current = next;
                // 不复制响应头，也不从 Set-Cookie 提取任何后续请求材料。
                continue;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (response.StatusCode >= HttpStatusCode.InternalServerError ||
                response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // 临时故障优先按状态与 Retry-After 分类，不能让错误正文的格式或体积掩盖它。
                return new MetadataPage(
                    FinalUri: current,
                    StatusCode: (int)response.StatusCode,
                    ContentType: mediaType,
                    Body: string.Empty,
                    BytesRead: 0,
                    RetryAfterSeconds: GetRetryAfterSeconds(response));
            }

            if (!IsMetadataType(mediaType))
            {
                throw new UpstreamRequestException(HttpFailureCodes.UnsupportedContent);
            }

            if (response.Content.Headers.ContentLength > HttpDefaults.MaximumMetadataBytes)
            {
                throw new UpstreamRequestException(HttpFailureCodes.BodyTooLarge);
            }

            var bytes = await RunPhaseAsync(
                token => ReadBoundedAsync(response.Content, token),
                _timeouts.MetadataBody,
                HttpTimeoutPhase.MetadataBody,
                cancellationToken);
            return new MetadataPage(
                FinalUri: current,
                StatusCode: (int)response.StatusCode,
                ContentType: mediaType,
                Body: Encoding.UTF8.GetString(bytes),
                BytesRead: bytes.Length,
                RetryAfterSeconds: GetRetryAfterSeconds(response));
        }

        throw new UpstreamRequestException(HttpFailureCodes.RedirectLimit);
    }

    /// <summary>只取得响应头，释放时禁止处理器为复用连接而排空媒体正文。</summary>
    public Task<MediaLocation> ResolveMediaAsync(Uri uri, string expectedType, CancellationToken cancellationToken) =>
        ResolveMediaAsync(uri, expectedType, static _ => { }, cancellationToken);

    public async Task<MediaLocation> ResolveMediaAsync(
        Uri uri,
        string expectedType,
        Action<HttpObservation> observe,
        CancellationToken cancellationToken)
    {
        var current = uri;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var hop = 0; hop <= HttpDefaults.MaximumRedirects; hop++)
        {
            _targetPolicy.ValidateUri(current);
            if (!visited.Add(current.AbsoluteUri))
            {
                throw new UpstreamRequestException(HttpFailureCodes.RedirectLoop);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd(_userAgent);
            request.Headers.Referrer = new Uri(_profile.Referer);
            request.Headers.Accept.ParseAdd(HttpDefaults.MediaAccept);
            using var response = await SendForHeadersAsync(request, cancellationToken);
            observe(new HttpObservation(
                Host: current.IdnHost,
                StatusCode: (int)response.StatusCode,
                CookieHeaderObserved: request.Headers.Contains(HttpDefaults.CookieHeader),
                SetCookieObserved: response.Headers.Contains(HttpDefaults.SetCookieHeader)));
            if (IsRedirect(response.StatusCode))
            {
                if (hop == HttpDefaults.MaximumRedirects)
                {
                    throw new UpstreamRequestException(HttpFailureCodes.RedirectLimit);
                }

                if (!response.Headers.TryGetValues(HttpDefaults.RedirectLocationHeader, out var values) ||
                    !Uri.TryCreate(current, values.FirstOrDefault(), out var next))
                {
                    throw new UpstreamRequestException(HttpFailureCodes.InvalidRedirect);
                }

                _profile.ValidateRedirect(next, isMedia: true);
                current = next;
                continue;
            }

            var mimeType = response.Content.Headers.ContentType?.MediaType;
            if (response.IsSuccessStatusCode &&
                !string.Equals(mimeType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) &&
                (mimeType is null || !mimeType.StartsWith(expectedType + '/', StringComparison.OrdinalIgnoreCase)))
            {
                throw new UpstreamRequestException(HttpFailureCodes.UnsupportedContent);
            }

            if (response.IsSuccessStatusCode && !_targetPolicy.IsFinalMediaUri(current))
            {
                throw new UpstreamRequestException(HttpFailureCodes.UnsupportedContent);
            }

            var knownMime = mimeType?.StartsWith(expectedType + '/', StringComparison.OrdinalIgnoreCase) == true
                ? mimeType
                : null;
            return new MediaLocation(
                FinalUri: current,
                StatusCode: (int)response.StatusCode,
                MimeType: knownMime,
                SizeBytes: response.Content.Headers.ContentLength is > 0 ? response.Content.Headers.ContentLength : null,
                RetryAfterSeconds: GetRetryAfterSeconds(response));
        }

        throw new UpstreamRequestException(HttpFailureCodes.RedirectLimit);
    }

    public Dictionary<string, string> CreateDownloadHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["User-Agent"] = _userAgent,
        ["Referer"] = _profile.Referer,
        ["Accept"] = HttpDefaults.MediaAccept
    };

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    private async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        context.InitialRequestMessage.Options.TryGetValue(HeadersDeadlineKey, out var headersDeadline);
        headersDeadline?.Pause();
        var stream = await RunPhaseAsync(
            token => _targetPolicy.ConnectAsync(context, token).AsTask(),
            _timeouts.Connect,
            HttpTimeoutPhase.Connect,
            cancellationToken);
        if (context.InitialRequestMessage.RequestUri?.Scheme != Uri.UriSchemeHttps)
        {
            headersDeadline?.Resume();
            return stream;
        }

        // .NET 支持从连接回调返回已认证的 SslStream，保留域名校验与 SNI
        var tls = new SslStream(stream, leaveInnerStreamOpen: false, _certificateValidation);
        try
        {
            await RunPhaseAsync(async token =>
            {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = context.DnsEndPoint.Host,
                    ApplicationProtocols = [SslApplicationProtocol.Http11]
                }, token);
                return tls;
            }, _timeouts.TlsHandshake, HttpTimeoutPhase.TlsHandshake, cancellationToken);
            headersDeadline?.Resume();
            return tls;
        }
        catch
        {
            await tls.DisposeAsync();
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendForHeadersAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 复用连接时立即计时；新建连接时暂停，由连接回调在握手完成后重新计时
        // 连接池等待与发送 GET 请求也受此预算限制，防止排队无限等待
        using var deadline = new ResponseHeadersDeadline(_timeouts.ResponseHeaders, cancellationToken);
        request.Options.Set(HeadersDeadlineKey, deadline);
        try
        {
            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        }
        catch (OperationCanceledException exception) when (deadline.IsExpired && !cancellationToken.IsCancellationRequested)
        {
            throw new UpstreamTimeoutException(HttpTimeoutPhase.ResponseHeaders, exception);
        }
    }

    private static async Task<T> RunPhaseAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan duration,
        HttpTimeoutPhase phase,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(duration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await operation(linked.Token);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new UpstreamTimeoutException(phase, exception);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static int? GetRetryAfterSeconds(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        var seconds = retry?.Delta?.TotalSeconds;
        if (seconds is null && retry?.Date is { } date && response.Headers.Date is { } serverDate)
        {
            seconds = (date - serverDate).TotalSeconds;
        }

        return seconds is >= 0 and <= int.MaxValue ? (int)Math.Ceiling(seconds.Value) : null;
    }

    private static bool IsMetadataType(string? mediaType) =>
        mediaType is null ||
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
        mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(HttpDefaults.ReadBufferBytes);

        try
        {
            while (true)
            {
                var remaining = HttpDefaults.MaximumMetadataBytes - (int)output.Length;
                var length = Math.Min(buffer.Length, remaining + 1);
                var read = await input.ReadAsync(buffer.AsMemory(0, length), cancellationToken);
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (read > remaining)
                {
                    throw new UpstreamRequestException(HttpFailureCodes.BodyTooLarge);
                }

                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

public sealed record MetadataPage(
    Uri FinalUri,
    int StatusCode,
    string? ContentType,
    string Body,
    int BytesRead,
    int? RetryAfterSeconds = null);

public sealed record HttpObservation(
    string Host,
    int StatusCode,
    bool CookieHeaderObserved,
    bool SetCookieObserved);
