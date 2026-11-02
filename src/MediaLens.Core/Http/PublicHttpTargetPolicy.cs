using System.Net;

namespace MediaLens.Http;

public sealed class PublicHttpTargetPolicy : IHttpTargetPolicy
{
    private static readonly DualStackConnector Connector = new();
    private readonly PlatformHttpProfile _profile;
    private readonly bool _validatePublicIp;
    private readonly Dictionary<string, IPAddress[]> _addressOverrides = new(StringComparer.OrdinalIgnoreCase);

    public PublicHttpTargetPolicy(
        IReadOnlyDictionary<string, IPAddress[]>? addressOverrides = null,
        PlatformHttpProfile? profile = null,
        bool validatePublicIp = true)
    {
        _profile = profile ?? PlatformHttpProfile.Douyin;
        _validatePublicIp = validatePublicIp;
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
            ValidateAddresses(addresses);

            // 诊断映射遵循同一公网检查开关，且复制后不受调用方修改影响。
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
            !_profile.AllowsHost(uri.IdnHost))
        {
            throw new UpstreamRequestException(HttpFailureCodes.TargetRejected);
        }
    }

    // 播放接口即便直接返回视频头，也不是本契约要求的最终 CDN 地址。
    public bool IsFinalMediaUri(Uri uri) => _profile.IsFinalMediaUri(uri);

    public async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = _addressOverrides.TryGetValue(context.DnsEndPoint.Host, out var configuredAddresses)
            ? configuredAddresses
            : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);

        // 开关只控制公网属性检查；始终固定本次解析结果建连，避免再次解析域名。
        ValidateAddresses(addresses);

        return await Connector.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
    }

    private void ValidateAddresses(IPAddress[] addresses)
    {
        if (addresses.Length == 0 || (_validatePublicIp && addresses.Any(address => !IsPublicAddress(address))))
        {
            throw new UpstreamRequestException(HttpFailureCodes.TargetRejected);
        }
    }

    internal static bool IsPublicAddress(IPAddress address) =>
        PublicIpAddressClassifier.IsPublicAddress(address);
}
