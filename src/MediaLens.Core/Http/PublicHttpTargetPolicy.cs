using System.Net;
using System.Net.Sockets;

namespace MediaLens.Http;

public sealed class PublicHttpTargetPolicy : IHttpTargetPolicy
{
    private static readonly string[] PlatformDomainRoots = ["douyin.com", "iesdouyin.com"];
    private static readonly string[] MediaDomainRoots =
        ["douyinvod.com", "douyinpic.com", "byteimg.com", "ibyteimg.com", "pstatp.com"];
    private static readonly string[] AllowedDomainRoots = [.. PlatformDomainRoots, .. MediaDomainRoots];
    private const string DouyinPlaybackHost = "aweme.snssdk.com";
    private readonly Dictionary<string, IPAddress[]> _addressOverrides = new(StringComparer.OrdinalIgnoreCase);

    public PublicHttpTargetPolicy(IReadOnlyDictionary<string, IPAddress[]>? addressOverrides = null)
    {
        if (addressOverrides is null)
        {
            return;
        }

        foreach (var (host, addresses) in addressOverrides)
        {
            if (Uri.CheckHostName(host) != UriHostNameType.Dns)
            {
                throw new UpstreamRequestException(HttpFailureCodes.TargetRejected);
            }

            ValidateUri(new Uri($"https://{host}/"));
            if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            {
                throw new UpstreamRequestException(HttpFailureCodes.TargetRejected);
            }

            // 诊断用公网 DNS 映射同样不能引入私网目标，且复制后不受调用方修改影响。
            _addressOverrides.Add(host, addresses.ToArray());
        }
    }

    public void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.IsDefaultPort ||
            !(uri.IdnHost.Equals(DouyinPlaybackHost, StringComparison.OrdinalIgnoreCase) ||
              uri.IdnHost.Equals(HttpDefaults.DouyinDetailHost, StringComparison.OrdinalIgnoreCase) ||
              AllowedDomainRoots.Any(root =>
                uri.IdnHost.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                uri.IdnHost.EndsWith('.' + root, StringComparison.OrdinalIgnoreCase))))
        {
            throw new UpstreamRequestException(HttpFailureCodes.TargetRejected);
        }
    }

    // 播放接口即便直接返回视频头，也不是本契约要求的最终 CDN 地址。
    public bool IsFinalMediaUri(Uri uri) => MediaDomainRoots.Any(root =>
        uri.IdnHost.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.EndsWith('.' + root, StringComparison.OrdinalIgnoreCase));

    public async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = _addressOverrides.TryGetValue(context.DnsEndPoint.Host, out var configuredAddresses)
            ? configuredAddresses
            : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);

        // 整个解析结果必须为公网地址，连接时使用刚校验过的地址而非再次解析域名。
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
        {
            throw new UpstreamRequestException(HttpFailureCodes.TargetRejected);
        }

        SocketException? lastFailure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                lastFailure = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("No validated public address was reachable.", lastFailure);
    }

    internal static bool IsPublicAddress(IPAddress address) =>
        PublicIpAddressClassifier.IsPublicAddress(address);
}
