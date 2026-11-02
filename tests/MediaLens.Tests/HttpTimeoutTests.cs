using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MediaLens.Http;
using MediaLens.Testing;

namespace MediaLens.Tests;

public sealed class HttpTimeoutTests
{
    private const string MediaHost = "v3-dy-o.douyinvod.com";
    private static readonly Uri HttpUri = new("http://" + MediaHost + '/');
    private static readonly Uri HttpsUri = new("https://" + MediaHost + '/');
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StageTimeout = TimeSpan.FromMilliseconds(75);
    private static readonly HttpTimeouts TestTimeouts = new(TestDeadline, TestDeadline, TestDeadline, TestDeadline);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionTimeoutIsAnUpstreamFailure(bool media)
    {
        var policy = new DelegateTargetPolicy(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Stream.Null;
        });
        using var client = new CookieFreeHttpClient(policy, TestTimeouts with { Connect = StageTimeout });
        using var deadline = new CancellationTokenSource(TestDeadline);

        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => RequestAsync(client, HttpUri, media, deadline.Token));

        Assert.Equal(HttpTimeoutPhase.Connect, UpstreamTimeoutException.FindPhase(error));
        Assert.False(deadline.IsCancellationRequested);
    }

    [Fact]
    public async Task TlsHandshakeHasItsOwnTimeout()
    {
        using var certificate = CreateCertificate();
        await using var server = new LoopbackHttpServer(
            (_, _) => Task.FromResult(new ServerReply(HttpStatusCode.OK)),
            certificate,
            Timeout.InfiniteTimeSpan);
        using var client = new CookieFreeHttpClient(
            new LoopbackTargetPolicy(server.Port), TestTimeouts with { TlsHandshake = StageTimeout });
        using var deadline = new CancellationTokenSource(TestDeadline);

        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(HttpsUri, deadline.Token));

        Assert.Equal(HttpTimeoutPhase.TlsHandshake, UpstreamTimeoutException.FindPhase(error));
        Assert.Empty(server.Requests);
        Assert.False(deadline.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseHeadersCannotWaitIndefinitely(bool media)
    {
        await using var server = new LoopbackHttpServer(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new ServerReply(HttpStatusCode.OK);
        });
        using var client = new CookieFreeHttpClient(
            new LoopbackTargetPolicy(server.Port), TestTimeouts with { ResponseHeaders = StageTimeout });
        using var deadline = new CancellationTokenSource(TestDeadline);

        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => RequestAsync(client, HttpUri, media, deadline.Token));

        Assert.Equal(HttpTimeoutPhase.ResponseHeaders, UpstreamTimeoutException.FindPhase(error));
        Assert.Single(server.Requests);
        Assert.False(deadline.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeaderBudgetStartsAfterConnectionAndTlsHandshake(bool https)
    {
        var connectionDelay = TimeSpan.FromMilliseconds(300);
        var headerBudget = TimeSpan.FromMilliseconds(150);
        using var certificate = CreateCertificate();
        await using var server = new LoopbackHttpServer(
            (_, _) => Task.FromResult(new ServerReply(HttpStatusCode.OK)),
            https ? certificate : null,
            connectionDelay);
        var loopback = new LoopbackTargetPolicy(server.Port);
        var policy = new DelegateTargetPolicy(async (context, token) =>
        {
            await Task.Delay(connectionDelay, token);
            return await loopback.ConnectAsync(context, token);
        });
        using var client = new CookieFreeHttpClient(
            policy,
            TestTimeouts with { ResponseHeaders = headerBudget },
            certificateValidation: (_, remote, _, errors) =>
                errors == SslPolicyErrors.RemoteCertificateChainErrors && remote?.GetCertHashString() == certificate.Thumbprint);
        using var deadline = new CancellationTokenSource(TestDeadline);

        var page = await client.GetAsync(https ? HttpsUri : HttpUri, deadline.Token);

        Assert.Equal((int)HttpStatusCode.OK, page.StatusCode);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task ManualTlsStillRejectsAnUntrustedCertificate()
    {
        using var certificate = CreateCertificate();
        await using var server = new LoopbackHttpServer(
            (_, _) => Task.FromResult(new ServerReply(HttpStatusCode.OK)), certificate);
        using var client = new CookieFreeHttpClient(new LoopbackTargetPolicy(server.Port), TestTimeouts);
        using var deadline = new CancellationTokenSource(TestDeadline);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(HttpsUri, deadline.Token));

        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyMetadataReadsTheBodyAndUsesItsOwnBudget(bool media)
    {
        await using var server = new LoopbackHttpServer((_, _) => Task.FromResult(new ServerReply(
            StatusCode: HttpStatusCode.OK,
            ContentType: media ? "video/mp4" : "application/json",
            BodyDelay: Timeout.InfiniteTimeSpan)));
        using var client = new CookieFreeHttpClient(
            new LoopbackTargetPolicy(server.Port), TestTimeouts with { MetadataBody = StageTimeout });
        using var deadline = new CancellationTokenSource(TestDeadline);

        if (media)
        {
            var location = await client.ResolveMediaAsync(HttpUri, "video", deadline.Token);
            Assert.Equal((int)HttpStatusCode.OK, location.StatusCode);
        }
        else
        {
            var error = await Assert.ThrowsAsync<UpstreamTimeoutException>(() => client.GetAsync(HttpUri, deadline.Token));
            Assert.Equal(HttpTimeoutPhase.MetadataBody, error.Phase);
        }

        Assert.False(deadline.IsCancellationRequested);
    }

    [Fact]
    public async Task AReusedConnectionStillHasAResponseHeaderDeadline()
    {
        const int ephemeralPort = 0;
        var listener = new TcpListener(IPAddress.Loopback, ephemeralPort);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var shutdown = new CancellationTokenSource(TestDeadline);
        var server = ServeAsync();
        var connects = 0;
        var loopback = new LoopbackTargetPolicy(port);
        var policy = new DelegateTargetPolicy((context, token) =>
        {
            Interlocked.Increment(ref connects);
            return loopback.ConnectAsync(context, token);
        });
        using var client = new CookieFreeHttpClient(policy, TestTimeouts with { ResponseHeaders = StageTimeout });
        try
        {
            var first = await client.GetAsync(HttpUri, shutdown.Token);
            Assert.Equal((int)HttpStatusCode.OK, first.StatusCode);

            var error = await Assert.ThrowsAsync<UpstreamTimeoutException>(() => client.GetAsync(HttpUri, shutdown.Token));

            Assert.Equal(HttpTimeoutPhase.ResponseHeaders, error.Phase);
            Assert.Equal(1, connects);
        }
        finally
        {
            await shutdown.CancelAsync();
            listener.Stop();
            try
            {
                await server;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
        }

        async Task ServeAsync()
        {
            using var connection = await listener.AcceptTcpClientAsync(shutdown.Token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await ReadRequestAsync();
            const string response = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), shutdown.Token);
            await ReadRequestAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);

            async Task ReadRequestAsync()
            {
                while (await reader.ReadLineAsync(shutdown.Token) is { Length: > 0 })
                {
                }
            }
        }
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedIntoAPhaseTimeout()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new DelegateTargetPolicy(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Stream.Null;
        });
        using var client = new CookieFreeHttpClient(policy, TestTimeouts);
        using var cancellation = new CancellationTokenSource(TestDeadline);
        var request = client.GetAsync(HttpUri, cancellation.Token);
        await started.Task.WaitAsync(TestDeadline);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    private static async Task RequestAsync(CookieFreeHttpClient client, Uri uri, bool media, CancellationToken cancellationToken)
    {
        if (media)
        {
            await client.ResolveMediaAsync(uri, "video", cancellationToken);
        }
        else
        {
            await client.GetAsync(uri, cancellationToken);
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + MediaHost, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(MediaHost);
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class DelegateTargetPolicy(
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> connect) : IHttpTargetPolicy
    {
        private readonly PublicHttpTargetPolicy _validation = new();

        public void ValidateUri(Uri uri) => _validation.ValidateUri(uri);

        public bool IsFinalMediaUri(Uri uri) => _validation.IsFinalMediaUri(uri);

        public ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken) =>
            connect(context, cancellationToken);
    }
}
