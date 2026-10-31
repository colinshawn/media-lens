using System.Net;
using MediaLens.Http;

namespace MediaLens.Probe.Tests;

public sealed class PublicHttpTargetPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("https://douyin.com.evil.example/")]
    [InlineData("https://evildouyin.com/")]
    [InlineData("https://api.amemv.com.evil.example/")]
    [InlineData("https://unverified.amemv.com/")]
    [InlineData("https://user:password@www.douyin.com/video/123456")]
    [InlineData("https://www.douyin.com:8443/video/123456")]
    [InlineData("file:///private/data")]
    public void OutOfScopeUrisAreRejected(string value)
    {
        var error = Assert.Throws<UpstreamRequestException>(() =>
            new PublicHttpTargetPolicy().ValidateUri(new Uri(value)));

        Assert.Equal(ProbeOutcomes.TargetRejected, error.Outcome);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    public void NonPublicAddressesAreRejected(string address)
    {
        Assert.False(PublicHttpTargetPolicy.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void PublicAddressesAreRecognized(string address)
    {
        Assert.True(PublicHttpTargetPolicy.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public void DnsOverrideCannotDisablePublicAddressChecks()
    {
        var values = new Dictionary<string, IPAddress[]>
        {
            ["www.douyin.com"] = [IPAddress.Parse("8.8.8.8"), IPAddress.Loopback]
        };

        var error = Assert.Throws<UpstreamRequestException>(() => new PublicHttpTargetPolicy(values));

        Assert.Equal(ProbeOutcomes.TargetRejected, error.Outcome);
    }

    [Theory]
    [InlineData("2001:2::1")]
    [InlineData("3fff::1")]
    [InlineData("64:ff9b::169.254.169.254")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("192.88.99.2")]
    public void MixedPublicAndSpecialDnsAnswersRejectTheEntireHost(string specialAddress)
    {
        var values = new Dictionary<string, IPAddress[]>
        {
            ["www.douyin.com"] =
                [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2606:4700::1111"), IPAddress.Parse(specialAddress)]
        };
        var error = Assert.Throws<UpstreamRequestException>(() => new PublicHttpTargetPolicy(values));
        Assert.Equal(ProbeOutcomes.TargetRejected, error.Outcome);
    }

    [Fact]
    public void EmptyDnsOverridesAreRejected()
    {
        var values = new Dictionary<string, IPAddress[]> { ["www.douyin.com"] = [] };
        Assert.Throws<UpstreamRequestException>(() => new PublicHttpTargetPolicy(values));
    }

    [Fact]
    public void GloballyReachableExceptionsAndBothAddressFamiliesCanBeConfigured()
    {
        var values = new Dictionary<string, IPAddress[]>
        {
            ["www.douyin.com"] =
                [IPAddress.Parse("192.0.0.9"), IPAddress.Parse("2001:1::1"), IPAddress.Parse("64:ff9b::8.8.8.8")]
        };
        _ = new PublicHttpTargetPolicy(values);
    }

    [Fact]
    public void AnonymousDetailHostIsAllowedAsMetadataButNeverAsFinalCdn()
    {
        var uri = new Uri("https://api.amemv.com/aweme/v1/aweme/detail/?aweme_id=123456");
        var policy = new PublicHttpTargetPolicy();
        policy.ValidateUri(uri);
        Assert.False(policy.IsFinalMediaUri(uri));
    }
}
