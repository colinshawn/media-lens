using MediaLens.Contracts;
using MediaLens.Platforms;

namespace MediaLens.Tests;

public sealed class RegistryTests
{
    [Fact]
    public void UnverifiedAdapterCannotBeEnabled()
    {
        var adapter = new StubAdapter("synthetic", "synthetic.example")
        {
            Capabilities = PlatformCapabilities.Unverified
        };

        Assert.Throws<ArgumentException>(() => new PlatformRegistry([new PlatformRegistration(adapter, Enabled: true)]));
        var registry = new PlatformRegistry([new PlatformRegistration(adapter, Enabled: false)]);
        Assert.False(Assert.Single(registry.Registrations).Enabled);
    }
}
