using System.Net;
using MediaLens.Http;
using MediaLens.Testing;

namespace MediaLens.Probe.Tests;

public sealed class PublicHttpTargetPolicyTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(10);

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyDnsOverridesAreRejected(bool validatePublicIp)
    {
        var values = new Dictionary<string, IPAddress[]> { ["www.douyin.com"] = [] };
        Assert.Throws<UpstreamRequestException>(() => new PublicHttpTargetPolicy(values, validatePublicIp: validatePublicIp));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("198.18.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void ExplicitlyDisablingValidationAllowsRestrictedAndMixedAddressMappings(string address)
    {
        var values = new Dictionary<string, IPAddress[]>
        {
            ["www.douyin.com"] = [IPAddress.Parse("8.8.8.8"), IPAddress.Parse(address)]
        };

        _ = new PublicHttpTargetPolicy(values, validatePublicIp: false);
        Assert.False(PublicHttpTargetPolicy.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledValidationConnectsToTheCopiedResolvedAddressForBothPlatforms(bool bilibili)
    {
        var profile = bilibili ? PlatformHttpProfile.Bilibili : PlatformHttpProfile.Douyin;
        var host = bilibili ? "www.bilibili.com" : "www.douyin.com";
        await using var server = new LoopbackHttpServer((_, _) => Task.FromResult(new ServerReply(HttpStatusCode.OK)));
        IPAddress[] addresses = [IPAddress.Loopback];
        var values = new Dictionary<string, IPAddress[]> { [host] = addresses };
        var policy = new PublicHttpTargetPolicy(values, profile, validatePublicIp: false);
        // 原映射被修改后仍连接构造时的副本，且走生产 ConnectAsync 的地址检查分支。
        addresses[0] = IPAddress.Parse("192.0.2.1");
        using var handler = new SocketsHttpHandler { UseProxy = false, ConnectCallback = policy.ConnectAsync };
        using var client = new HttpClient(handler) { Timeout = TestDeadline };

        using var response = await client.GetAsync($"http://{host}:{server.Port}/fixture");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledValidationKeepsEveryOtherUriRestriction(bool bilibili)
    {
        var profile = bilibili ? PlatformHttpProfile.Bilibili : PlatformHttpProfile.Douyin;
        var host = bilibili ? "www.bilibili.com" : "www.douyin.com";
        var foreignHost = bilibili ? "www.douyin.com" : "www.bilibili.com";
        var policy = new PublicHttpTargetPolicy(profile: profile, validatePublicIp: false);
        policy.ValidateUri(new Uri($"https://{host}/"));
        foreach (var uri in new[]
                 {
                     $"https://{host}:8443/", $"https://user:password@{host}/",
                     $"ftp://{host}/", $"https://{host}.evil.example/",
                     $"https://{foreignHost}/", "http://127.0.0.1/", "file:///private/data"
                 })
        {
            Assert.Throws<UpstreamRequestException>(() => policy.ValidateUri(new Uri(uri)));
        }

        var values = new Dictionary<string, IPAddress[]> { [foreignHost] = [IPAddress.Loopback] };
        Assert.Throws<UpstreamRequestException>(() => new PublicHttpTargetPolicy(values, profile, validatePublicIp: false));
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
