using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MediaLens.Http;

namespace MediaLens.Testing;

/// <summary>使用真实 TCP 请求观测处理器自动添加的头部，避免模拟消息处理器遗漏 CookieContainer 行为。</summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private const int EphemeralPort = 0;
    private const int MaximumHeaderCharacters = 16_384;
    private const int RequestLineParts = 3;
    private readonly TcpListener _listener = new(IPAddress.Loopback, EphemeralPort);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Func<ObservedRequest, CancellationToken, Task<ServerReply>> _respond;
    private readonly Task _acceptLoop;

    public LoopbackHttpServer(Func<ObservedRequest, CancellationToken, Task<ServerReply>> respond)
    {
        _respond = respond;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptAsync();
    }

    public int Port { get; }

    public ConcurrentQueue<ObservedRequest> Requests { get; } = [];

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();
        await _acceptLoop;
        await Task.WhenAll(_connections);
        _shutdown.Dispose();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var connection = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                _connections.Add(HandleAsync(connection));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(TcpClient connection)
    {
        using (connection)
        {
            try
            {
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var firstLine = await reader.ReadLineAsync(_shutdown.Token);
                if (firstLine is null)
                {
                    return;
                }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var characters = firstLine.Length;
                while (await reader.ReadLineAsync(_shutdown.Token) is { Length: > 0 } line)
                {
                    characters += line.Length;
                    if (characters > MaximumHeaderCharacters)
                    {
                        throw new InvalidDataException("Test request headers exceeded the fixture limit.");
                    }

                    var separator = line.IndexOf(':');
                    headers.Add(line[..separator], line[(separator + 1)..].Trim());
                }

                var parts = firstLine.Split(' ', RequestLineParts);
                var request = new ObservedRequest(parts[1], headers);
                Requests.Enqueue(request);
                var reply = await _respond(request, _shutdown.Token);
                var body = Encoding.UTF8.GetBytes(reply.Body);
                var output = new StringBuilder()
                    .Append("HTTP/1.1 ").Append((int)reply.StatusCode).Append(" Fixture\r\n")
                    .Append("Connection: close\r\n")
                    .Append("Content-Type: ").Append(reply.ContentType).Append("\r\n");
                if (!reply.OmitContentLength)
                {
                    output.Append("Content-Length: ")
                        .Append((reply.DeclaredContentLength ?? body.Length).ToString(CultureInfo.InvariantCulture))
                        .Append("\r\n");
                }

                if (reply.Headers is not null)
                {
                    foreach (var (name, value) in reply.Headers)
                    {
                        output.Append(name).Append(": ").Append(value).Append("\r\n");
                    }
                }

                output.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(output.ToString()), _shutdown.Token);
                await stream.WriteAsync(body, _shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
                // 读取上限或取消测试会由客户端主动关闭连接。
            }
        }
    }
}

internal sealed record ObservedRequest(string Target, Dictionary<string, string> Headers);

internal sealed record ServerReply(
    HttpStatusCode StatusCode,
    string Body = "{}",
    string ContentType = "application/json",
    IReadOnlyDictionary<string, string>? Headers = null,
    bool OmitContentLength = false,
    long? DeclaredContentLength = null);

/// <summary>仅供测试把合成的平台域名连接到本机替身；生产策略仍禁止环回地址。</summary>
internal sealed class LoopbackTargetPolicy(
    int port,
    PlatformHttpProfile? profile = null,
    bool validatePublicIp = true) : IHttpTargetPolicy
{
    private readonly PublicHttpTargetPolicy _policy = new(profile: profile, validatePublicIp: validatePublicIp);
    public void ValidateUri(Uri uri)
    {
        _policy.ValidateUri(uri);
    }

    public bool IsFinalMediaUri(Uri uri) => _policy.IsFinalMediaUri(uri);

    public async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
