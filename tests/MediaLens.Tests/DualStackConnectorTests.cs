using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MediaLens.Http;

namespace MediaLens.Tests;

public sealed class DualStackConnectorTests
{
    private const int HttpsPort = 443;
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShortDelay = TimeSpan.FromMilliseconds(20);
    private static readonly IPAddress FirstV6 = IPAddress.Parse("2001:db8::1");
    private static readonly IPAddress SecondV6 = IPAddress.Parse("2001:db8::2");
    private static readonly IPAddress FirstV4 = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress SecondV4 = IPAddress.Parse("192.0.2.2");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStalledAddressDoesNotBlockTheOtherFamily(bool ipv6First)
    {
        var first = ipv6First ? FirstV6 : FirstV4;
        var secondInFamily = ipv6First ? SecondV6 : SecondV4;
        var alternate = ipv6First ? FirstV4 : FirstV6;
        var attempted = new ConcurrentQueue<IPAddress>();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var winner = new TrackingStream();
        var connector = new DualStackConnector(async (endpoint, token) =>
        {
            attempted.Enqueue(endpoint.Address);
            if (endpoint.Address.Equals(first))
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    canceled.TrySetResult();
                }
            }

            return winner;
        }, ShortDelay);
        using var deadline = new CancellationTokenSource(TestDeadline);

        var result = await connector.ConnectAsync([first, secondInFamily, alternate], HttpsPort, deadline.Token);

        Assert.Same(winner, result);
        Assert.Equal([first, alternate], attempted);
        Assert.True(canceled.Task.IsCompletedSuccessfully);
        Assert.False(winner.IsDisposed);
    }

    [Fact]
    public async Task LaterAddressesAreTriedWhenBothFamiliesHaveAStalledAddress()
    {
        using var winner = new TrackingStream();
        var attempted = new ConcurrentQueue<IPAddress>();
        var connector = new DualStackConnector(async (endpoint, token) =>
        {
            attempted.Enqueue(endpoint.Address);
            if (!endpoint.Address.Equals(SecondV6))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return winner;
        }, ShortDelay);
        using var deadline = new CancellationTokenSource(TestDeadline);

        var result = await connector.ConnectAsync([FirstV6, SecondV6, FirstV4], HttpsPort, deadline.Token);

        Assert.Same(winner, result);
        Assert.Equal([FirstV6, FirstV4, SecondV6], attempted);
    }

    [Fact]
    public async Task AHealthyFirstAddressDoesNotOpenExtraConnections()
    {
        var attempted = new List<IPEndPoint>();
        using var winner = new TrackingStream();
        var connector = new DualStackConnector((endpoint, _) =>
        {
            attempted.Add(endpoint);
            return ValueTask.FromResult<Stream>(winner);
        }, ShortDelay);

        var result = await connector.ConnectAsync([FirstV6, FirstV4], HttpsPort, CancellationToken.None);

        Assert.Same(winner, result);
        Assert.Equal(FirstV6, Assert.Single(attempted).Address);
        Assert.Equal(HttpsPort, attempted[0].Port);
    }

    [Fact]
    public async Task ExplicitFailuresImmediatelyTryDistinctAddressesInAlternatingOrder()
    {
        var attempted = new List<IPAddress>();
        var connector = new DualStackConnector((endpoint, _) =>
        {
            attempted.Add(endpoint.Address);
            return ValueTask.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused));
        }, TestDeadline + TestDeadline);
        using var deadline = new CancellationTokenSource(TestDeadline);

        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await connector.ConnectAsync([FirstV6, FirstV6, SecondV6, FirstV4, SecondV4], HttpsPort, deadline.Token));

        Assert.IsType<SocketException>(error.InnerException);
        Assert.Equal([FirstV6, FirstV4, SecondV6, SecondV4], attempted);
        Assert.False(deadline.IsCancellationRequested);
    }

    [Fact]
    public async Task CallerCancellationStopsAllPendingAttempts()
    {
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var connector = new DualStackConnector(async (_, token) =>
        {
            if (Interlocked.Increment(ref active) == 2)
            {
                bothStarted.TrySetResult();
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Stream.Null;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }, ShortDelay);
        using var cancellation = new CancellationTokenSource(TestDeadline);
        var request = connector.ConnectAsync([FirstV6, FirstV4], HttpsPort, cancellation.Token).AsTask();
        await bothStarted.Task.WaitAsync(TestDeadline);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        Assert.Equal(0, active);
    }

    [Fact]
    public async Task ALosingAttemptThatCompletesDuringCancellationIsDisposed()
    {
        using var loser = new TrackingStream();
        using var winner = new TrackingStream();
        var connector = new DualStackConnector(async (endpoint, token) =>
        {
            if (endpoint.Address.Equals(FirstV6))
            {
                var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = token.Register(() => released.TrySetResult());
                await released.Task;
                return loser;
            }

            return winner;
        }, ShortDelay);
        using var deadline = new CancellationTokenSource(TestDeadline);

        var result = await connector.ConnectAsync([FirstV6, FirstV4], HttpsPort, deadline.Token);

        Assert.Same(winner, result);
        Assert.True(loser.IsDisposed);
        Assert.False(winner.IsDisposed);
    }

    private sealed class TrackingStream : MemoryStream
    {
        internal bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
