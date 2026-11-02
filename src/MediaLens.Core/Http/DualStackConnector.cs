using System.Net;
using System.Net.Sockets;

namespace MediaLens.Http;

internal sealed class DualStackConnector(
    Func<IPEndPoint, CancellationToken, ValueTask<Stream>>? connect = null,
    TimeSpan? attemptDelay = null)
{
    private readonly Func<IPEndPoint, CancellationToken, ValueTask<Stream>> _connect = connect ?? ConnectSocketAsync;
    private readonly TimeSpan _attemptDelay = attemptDelay ?? TimeSpan.FromMilliseconds(HttpDefaults.ConnectionAttemptDelayMilliseconds);

    internal async ValueTask<Stream> ConnectAsync(IPAddress[] addresses, int port, CancellationToken cancellationToken)
    {
        var ordered = Interleave(addresses);
        using var attempts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task<Stream>>();
        var next = 0;
        Exception? lastFailure = null;
        Task delay = Task.CompletedTask;

        try
        {
            StartNext();
            while (pending.Count > 0)
            {
                var completed = next < ordered.Length
                    ? await Task.WhenAny(pending.Cast<Task>().Append(delay))
                    : await Task.WhenAny(pending);
                cancellationToken.ThrowIfCancellationRequested();
                if (completed == delay)
                {
                    StartNext();
                    continue;
                }

                var connection = (Task<Stream>)completed;
                pending.Remove(connection);
                try
                {
                    var stream = await connection;
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await stream.DisposeAsync();
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    return stream;
                }
                catch (Exception exception) when (exception is SocketException or IOException or HttpRequestException ||
                                                   exception is OperationCanceledException && !attempts.IsCancellationRequested)
                {
                    lastFailure = exception;
                    // 明确失败时立即尝试后续地址，挂起的连接则允许错峰并行
                    StartNext();
                }
            }

            throw new HttpRequestException("No resolved target address was reachable.", lastFailure);
        }
        finally
        {
            await attempts.CancelAsync();
            foreach (var connection in pending)
            {
                try
                {
                    var unused = await connection;
                    await unused.DisposeAsync();
                }
                catch (Exception)
                {
                    // 观察并回收所有落选连接，包括取消时恰好已经连接成功的流
                }
            }
        }

        void StartNext()
        {
            if (next < ordered.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pending.Add(ConnectOneAsync(new IPEndPoint(ordered[next++], port)));
                delay = Task.Delay(_attemptDelay, attempts.Token);
            }
        }

        async Task<Stream> ConnectOneAsync(IPEndPoint endpoint) =>
            await _connect(endpoint, attempts.Token);
    }

    private static IPAddress[] Interleave(IPAddress[] addresses)
    {
        if (addresses.Length == 0)
        {
            return [];
        }

        var family = addresses[0].AddressFamily;
        var primary = addresses.Where(address => address.AddressFamily == family).Distinct().ToArray();
        var alternate = addresses.Where(address => address.AddressFamily != family).Distinct().ToArray();
        var result = new List<IPAddress>(primary.Length + alternate.Length);
        for (var index = 0; index < Math.Max(primary.Length, alternate.Length); index++)
        {
            if (index < primary.Length)
            {
                result.Add(primary[index]);
            }

            if (index < alternate.Length)
            {
                result.Add(alternate[index]);
            }
        }

        return result.ToArray();
    }

    private static async ValueTask<Stream> ConnectSocketAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
