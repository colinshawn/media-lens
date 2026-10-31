using System.Net;
using System.Numerics;
using System.Text.Json;
using MediaLens.Http;

namespace MediaLens.Probe.Tests;

public sealed class PublicIpAddressClassifierTests
{
    private const int BitsPerByte = 8;
    private const int Ipv4AddressBits = 32;
    private const int Ipv6AddressBits = 128;
    private const int Ipv4PrefixCount = 26;
    private const int Ipv6PrefixCount = 25;
    private const int EmbeddedIpv4Offset = 12;
    private const string SnapshotDirectory = "Fixtures";
    private const string SnapshotFileName = "iana-special-purpose-addresses.json";
    private const string EntriesField = "entries";
    private const string PrefixField = "prefix";
    private const string DestinationField = "destination";
    private const string ForwardableField = "forwardable";
    private const string GloballyReachableField = "globallyReachable";
    private const string TerminationField = "terminationDate";
    private const string PositiveFlag = "True";
    private const string NoTermination = "N/A";
    private static readonly RegistryRange[] Snapshot = ReadSnapshot();
    private static readonly BigInteger Ipv6UnicastStart = ToNumber(IPAddress.Parse("2000::"));
    private static readonly BigInteger Ipv6UnicastEndExclusive = ToNumber(IPAddress.Parse("4000::"));
    private static readonly BigInteger Ipv4MulticastStart = ToNumber(IPAddress.Parse("224.0.0.0"));
    private static readonly BigInteger Ipv4MulticastEndExclusive = ToNumber(IPAddress.Parse("240.0.0.0"));
    private static readonly BigInteger Nat64Start = ToNumber(IPAddress.Parse("64:ff9b::"));
    private static readonly BigInteger MappedIpv4Start = ToNumber(IPAddress.Parse("::ffff:0:0"));

    [Fact]
    public void AuthoritativeSnapshotContainsBothCompleteRegistries()
    {
        Assert.Equal(Ipv4PrefixCount, Snapshot.Count(range => range.AddressBits == Ipv4AddressBits));
        Assert.Equal(Ipv6PrefixCount, Snapshot.Count(range => range.AddressBits == Ipv6AddressBits));
    }

    [Theory]
    [MemberData(nameof(IanaBoundaryCases))]
    public void SpecialPurposeRangesRespectAuthoritativeFlagsAndSpecificExceptions(
        string address,
        bool expected,
        string sourcePrefix)
    {
        var actual = PublicHttpTargetPolicy.IsPublicAddress(IPAddress.Parse(address));
        Assert.True(actual == expected, $"IANA {sourcePrefix}: {address}, expected {expected}, actual {actual}.");
    }

    [Theory]
    [MemberData(nameof(Ipv4BoundaryCases))]
    public void MappedAndNat64AddressesInheritEveryIpv4BoundaryRule(
        string address,
        bool expected,
        string sourcePrefix)
    {
        var ipv4 = IPAddress.Parse(address);
        var mapped = ipv4.MapToIPv6();
        var translated = IPAddress.Parse("64:ff9b::").GetAddressBytes();
        ipv4.GetAddressBytes().CopyTo(translated, EmbeddedIpv4Offset);

        Assert.True(PublicHttpTargetPolicy.IsPublicAddress(mapped) == expected,
            $"IPv4-mapped {sourcePrefix}: {mapped}.");
        Assert.True(PublicHttpTargetPolicy.IsPublicAddress(new IPAddress(translated)) == expected,
            $"NAT64 {sourcePrefix}: {address}.");
    }

    [Theory]
    [InlineData("192.88.99.1", false)]
    [InlineData("192.88.99.2", false)]
    [InlineData("2001::1", false)]
    [InlineData("2001:1::4", false)]
    [InlineData("2001:2::1", false)]
    [InlineData("2001:10::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2002:808:808::1", false)]
    [InlineData("3fff::1", false)]
    [InlineData("3fff:fff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("100:0:0:1::1", false)]
    [InlineData("5f00::1", false)]
    [InlineData("fec0::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("ff0e::1", false)]
    [InlineData("::8.8.8.8", false)]
    [InlineData("4000::1", false)]
    [InlineData("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("192.0.0.9", true)]
    [InlineData("192.0.0.10", true)]
    [InlineData("192.31.196.1", true)]
    [InlineData("192.52.193.1", true)]
    [InlineData("192.175.48.1", true)]
    [InlineData("2001:1::1", true)]
    [InlineData("2001:1::2", true)]
    [InlineData("2001:1::3", true)]
    [InlineData("2001:3::1", true)]
    [InlineData("2001:4:112::1", true)]
    [InlineData("2001:20::1", true)]
    [InlineData("2001:30::1", true)]
    [InlineData("2620:4f:8000::1", true)]
    [InlineData("2001:4860:4860::8888", true)]
    [InlineData("3fff:1000::1", true)]
    [InlineData("64:ff9b::8.8.8.8", true)]
    [InlineData("64:ff9b::192.0.0.9", true)]
    [InlineData("64:ff9b::192.0.0.8", false)]
    [InlineData("64:ff9b::127.0.0.1", false)]
    [InlineData("64:ff9b::169.254.169.254", false)]
    [InlineData("64:ff9b::198.18.0.1", false)]
    [InlineData("64:ff9b:1::808:808", false)]
    public void KnownRegressionsHaveExplicitExpectedResults(string address, bool expected)
    {
        Assert.Equal(expected, PublicHttpTargetPolicy.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("fe80::1%1")]
    [InlineData("2606:4700:4700::1111%3")]
    [InlineData("::ffff:8.8.8.8%3")]
    public void InterfaceScopedAddressesAreRejected(string address)
    {
        Assert.False(PublicHttpTargetPolicy.IsPublicAddress(IPAddress.Parse(address)));
    }

    public static IEnumerable<object[]> IanaBoundaryCases() => BoundaryCases(Snapshot);

    public static IEnumerable<object[]> Ipv4BoundaryCases() =>
        BoundaryCases(Snapshot.Where(range => range.AddressBits == Ipv4AddressBits));

    private static IEnumerable<object[]> BoundaryCases(IEnumerable<RegistryRange> ranges)
    {
        foreach (var range in ranges)
        {
            var length = BigInteger.One << (range.AddressBits - range.PrefixLength);
            var last = range.Start + length - BigInteger.One;
            var maximum = (BigInteger.One << range.AddressBits) - BigInteger.One;
            var addresses = new HashSet<BigInteger> { range.Start, last, range.Start + length / 2 };
            if (length > BigInteger.One)
            {
                addresses.Add(range.Start + BigInteger.One);
                addresses.Add(last - BigInteger.One);
            }
            if (range.Start > BigInteger.Zero)
            {
                addresses.Add(range.Start - BigInteger.One);
            }
            if (last < maximum)
            {
                addresses.Add(last + BigInteger.One);
            }

            foreach (var value in addresses.Order())
            {
                var address = ToAddress(value, range.AddressBits);
                yield return [address.ToString(), ExpectedFromSnapshot(address), range.Prefix];
            }
        }
    }

    private static bool ExpectedFromSnapshot(IPAddress address)
    {
        // 独立使用无符号整数比较注册表区间，不依赖生产规则数组或 IPNetwork.Contains。
        var value = ToNumber(address);
        var bits = address.GetAddressBytes().Length * BitsPerByte;
        if (bits == Ipv6AddressBits && value >> Ipv4AddressBits == MappedIpv4Start >> Ipv4AddressBits)
        {
            return ExpectedFromSnapshot(ToAddress(value - MappedIpv4Start, Ipv4AddressBits));
        }

        if (bits == Ipv4AddressBits && value >= Ipv4MulticastStart && value < Ipv4MulticastEndExclusive)
        {
            return false;
        }

        var matching = Snapshot.Where(range => range.AddressBits == bits &&
                value >= range.Start && value < range.Start + (BigInteger.One << (bits - range.PrefixLength)))
            .OrderByDescending(range => range.PrefixLength).FirstOrDefault();
        var allowed = matching?.Allowed ??
            (bits == Ipv4AddressBits || value >= Ipv6UnicastStart && value < Ipv6UnicastEndExclusive);
        if (allowed && bits == Ipv6AddressBits && value >> Ipv4AddressBits == Nat64Start >> Ipv4AddressBits)
        {
            return ExpectedFromSnapshot(ToAddress(value - Nat64Start, Ipv4AddressBits));
        }

        return allowed;
    }

    private static RegistryRange[] ReadSnapshot()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, SnapshotDirectory, SnapshotFileName)));
        return document.RootElement.GetProperty(EntriesField).EnumerateArray().Select(entry =>
        {
            var prefix = entry.GetProperty(PrefixField).GetString()!;
            var parts = prefix.Split('/');
            var address = IPAddress.Parse(parts[0]);
            var allowed = new[] { DestinationField, ForwardableField, GloballyReachableField }
                .All(field => entry.GetProperty(field).GetString() == PositiveFlag) &&
                entry.GetProperty(TerminationField).GetString() == NoTermination;
            return new RegistryRange(
                Prefix: prefix,
                Start: ToNumber(address),
                PrefixLength: int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                AddressBits: address.GetAddressBytes().Length * BitsPerByte,
                Allowed: allowed);
        }).ToArray();
    }

    private static BigInteger ToNumber(IPAddress address) =>
        new(address.GetAddressBytes(), isUnsigned: true, isBigEndian: true);

    private static IPAddress ToAddress(BigInteger value, int bits)
    {
        var bytes = new byte[bits / BitsPerByte];
        var encoded = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        encoded.CopyTo(bytes, bytes.Length - encoded.Length);
        return new IPAddress(bytes);
    }

    private sealed record RegistryRange(string Prefix, BigInteger Start, int PrefixLength, int AddressBits, bool Allowed);
}
