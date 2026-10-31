using System.Net;
using System.Net.Sockets;

namespace MediaLens.Http;

/// <summary>按固定的 IANA 特殊用途快照判断可作为出站目标的公网单播地址。</summary>
internal static class PublicIpAddressClassifier
{
    private const int NoMatchingPrefix = -1;
    private const int UnscopedAddress = 0;
    private const int Ipv6AddressBytes = 16;
    private const int EmbeddedIpv4Offset = 12;
    private static readonly IPNetwork Ipv4Multicast = IPNetwork.Parse("224.0.0.0/4");
    private static readonly IPNetwork Ipv6GlobalUnicast = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork Nat64WellKnownPrefix = IPNetwork.Parse("64:ff9b::/96");

    public static bool IsPublicAddress(IPAddress address)
    {
        // 区域编号依赖本机接口，不作为独立、可验证的公网目标接受。
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != UnscopedAddress)
        {
            return false;
        }

        // ::ffff:127.0.0.1 等映射形式必须继承对应 IPv4 的限制。
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // 多播在另一张 IANA 注册表中定义，不能因未命中特殊用途表而放行。
            return !Ipv4Multicast.Contains(address) &&
                   MatchMostSpecificRange(address, Ipv4SpecialPurposeRanges, defaultAllowed: true);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 ||
            !MatchMostSpecificRange(address, Ipv6SpecialPurposeRanges, Ipv6GlobalUnicast.Contains(address)))
        {
            return false;
        }

        if (Nat64WellKnownPrefix.Contains(address))
        {
            // RFC 6052 §3.1：标准 NAT64 前缀不得用于非全球可达 IPv4。
            // 单看外层前缀会错误放行 64:ff9b::127.0.0.1 或云元数据地址。
            Span<byte> bytes = stackalloc byte[Ipv6AddressBytes];
            if (!address.TryWriteBytes(bytes, out var written) || written != Ipv6AddressBytes)
            {
                return false;
            }

            return IsPublicAddress(new IPAddress(bytes[EmbeddedIpv4Offset..]));
        }

        return true;
    }

    private static bool MatchMostSpecificRange(
        IPAddress address,
        SpecialPurposeRange[] ranges,
        bool defaultAllowed)
    {
        var longestPrefix = NoMatchingPrefix;
        var allowed = defaultAllowed;
        foreach (var range in ranges)
        {
            if (range.Network.PrefixLength > longestPrefix && range.Network.Contains(address))
            {
                longestPrefix = range.Network.PrefixLength;
                allowed = range.IsAllowedDestination;
            }
        }

        // 不依赖表顺序；例如 192.0.0.9/32 的允许规则覆盖父 /24 的拒绝规则。
        return allowed;
    }

    // IANA IPv4 / IPv6 Special-Purpose Address Registries，获取于 2026-10-31。
    // 目的地址有效、可转发且 Globally Reachable 明确为 True 的活动条目才允许。
    // N/A、空值、False 及已废弃条目均拒绝；来源、摘要及完整字段保存在离线测试快照。
    private static readonly SpecialPurposeRange[] Ipv4SpecialPurposeRanges =
    [
        // "This network"
        new(IPNetwork.Parse("0.0.0.0/8"), IsAllowedDestination: false),
        // "This host on this network"
        new(IPNetwork.Parse("0.0.0.0/32"), IsAllowedDestination: false),
        // Private-Use
        new(IPNetwork.Parse("10.0.0.0/8"), IsAllowedDestination: false),
        // Shared Address Space
        new(IPNetwork.Parse("100.64.0.0/10"), IsAllowedDestination: false),
        // Loopback
        new(IPNetwork.Parse("127.0.0.0/8"), IsAllowedDestination: false),
        // Link Local
        new(IPNetwork.Parse("169.254.0.0/16"), IsAllowedDestination: false),
        // Private-Use
        new(IPNetwork.Parse("172.16.0.0/12"), IsAllowedDestination: false),
        // IETF Protocol Assignments
        new(IPNetwork.Parse("192.0.0.0/24"), IsAllowedDestination: false),
        // IPv4 Service Continuity Prefix
        new(IPNetwork.Parse("192.0.0.0/29"), IsAllowedDestination: false),
        // IPv4 dummy address
        new(IPNetwork.Parse("192.0.0.8/32"), IsAllowedDestination: false),
        // Port Control Protocol Anycast
        new(IPNetwork.Parse("192.0.0.9/32"), IsAllowedDestination: true),
        // Traversal Using Relays around NAT Anycast
        new(IPNetwork.Parse("192.0.0.10/32"), IsAllowedDestination: true),
        // NAT64/DNS64 Discovery
        new(IPNetwork.Parse("192.0.0.170/32"), IsAllowedDestination: false),
        // NAT64/DNS64 Discovery
        new(IPNetwork.Parse("192.0.0.171/32"), IsAllowedDestination: false),
        // Documentation (TEST-NET-1)
        new(IPNetwork.Parse("192.0.2.0/24"), IsAllowedDestination: false),
        // AS112-v4
        new(IPNetwork.Parse("192.31.196.0/24"), IsAllowedDestination: true),
        // AMT
        new(IPNetwork.Parse("192.52.193.0/24"), IsAllowedDestination: true),
        // Deprecated (6to4 Relay Anycast)
        new(IPNetwork.Parse("192.88.99.0/24"), IsAllowedDestination: false),
        // 6a44-relay anycast address
        new(IPNetwork.Parse("192.88.99.2/32"), IsAllowedDestination: false),
        // Private-Use
        new(IPNetwork.Parse("192.168.0.0/16"), IsAllowedDestination: false),
        // Direct Delegation AS112 Service
        new(IPNetwork.Parse("192.175.48.0/24"), IsAllowedDestination: true),
        // Benchmarking
        new(IPNetwork.Parse("198.18.0.0/15"), IsAllowedDestination: false),
        // Documentation (TEST-NET-2)
        new(IPNetwork.Parse("198.51.100.0/24"), IsAllowedDestination: false),
        // Documentation (TEST-NET-3)
        new(IPNetwork.Parse("203.0.113.0/24"), IsAllowedDestination: false),
        // Reserved
        new(IPNetwork.Parse("240.0.0.0/4"), IsAllowedDestination: false),
        // Limited Broadcast
        new(IPNetwork.Parse("255.255.255.255/32"), IsAllowedDestination: false),
    ];

    private static readonly SpecialPurposeRange[] Ipv6SpecialPurposeRanges =
    [
        // Loopback Address
        new(IPNetwork.Parse("::1/128"), IsAllowedDestination: false),
        // Unspecified Address
        new(IPNetwork.Parse("::/128"), IsAllowedDestination: false),
        // IPv4-mapped Address
        new(IPNetwork.Parse("::ffff:0:0/96"), IsAllowedDestination: false),
        // IPv4-IPv6 Translat.
        new(IPNetwork.Parse("64:ff9b::/96"), IsAllowedDestination: true),
        // IPv4-IPv6 Translat.
        new(IPNetwork.Parse("64:ff9b:1::/48"), IsAllowedDestination: false),
        // Discard-Only Address Block
        new(IPNetwork.Parse("100::/64"), IsAllowedDestination: false),
        // Dummy IPv6 Prefix
        new(IPNetwork.Parse("100:0:0:1::/64"), IsAllowedDestination: false),
        // IETF Protocol Assignments
        new(IPNetwork.Parse("2001::/23"), IsAllowedDestination: false),
        // TEREDO
        new(IPNetwork.Parse("2001::/32"), IsAllowedDestination: false),
        // Port Control Protocol Anycast
        new(IPNetwork.Parse("2001:1::1/128"), IsAllowedDestination: true),
        // Traversal Using Relays around NAT Anycast
        new(IPNetwork.Parse("2001:1::2/128"), IsAllowedDestination: true),
        // DNS-SD Service Registration Protocol Anycast
        new(IPNetwork.Parse("2001:1::3/128"), IsAllowedDestination: true),
        // Benchmarking
        new(IPNetwork.Parse("2001:2::/48"), IsAllowedDestination: false),
        // AMT
        new(IPNetwork.Parse("2001:3::/32"), IsAllowedDestination: true),
        // AS112-v6
        new(IPNetwork.Parse("2001:4:112::/48"), IsAllowedDestination: true),
        // Deprecated (previously ORCHID)
        new(IPNetwork.Parse("2001:10::/28"), IsAllowedDestination: false),
        // ORCHIDv2
        new(IPNetwork.Parse("2001:20::/28"), IsAllowedDestination: true),
        // Drone Remote ID Protocol Entity Tags (DETs) Prefix
        new(IPNetwork.Parse("2001:30::/28"), IsAllowedDestination: true),
        // Documentation
        new(IPNetwork.Parse("2001:db8::/32"), IsAllowedDestination: false),
        // 6to4
        new(IPNetwork.Parse("2002::/16"), IsAllowedDestination: false),
        // Direct Delegation AS112 Service
        new(IPNetwork.Parse("2620:4f:8000::/48"), IsAllowedDestination: true),
        // Documentation
        new(IPNetwork.Parse("3fff::/20"), IsAllowedDestination: false),
        // Segment Routing (SRv6) SIDs
        new(IPNetwork.Parse("5f00::/16"), IsAllowedDestination: false),
        // Unique-Local
        new(IPNetwork.Parse("fc00::/7"), IsAllowedDestination: false),
        // Link-Local Unicast
        new(IPNetwork.Parse("fe80::/10"), IsAllowedDestination: false),
    ];

    private readonly record struct SpecialPurposeRange(IPNetwork Network, bool IsAllowedDestination);
}
