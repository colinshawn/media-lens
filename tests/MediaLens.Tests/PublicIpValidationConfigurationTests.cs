using System.Net;
using MediaLens.Api;
using Microsoft.Extensions.Configuration;

namespace MediaLens.Tests;

public sealed class PublicIpValidationConfigurationTests
{
    private const string SyntheticSecret = "synthetic-public-ip-secret-token-1234567890";
    private const string EnvironmentVariable = "VALIDATE_PUBLIC_IP";
    private const string NativeEnvironmentVariable = "MediaLens__Http__ValidatePublicIp";
    private const string DisabledWarning = "Public IP validation is disabled";
    private const string InvalidSentinel = "synthetic-invalid-boolean-do-not-log";
    private const int InvalidConfigurationExitCode = 1;

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public async Task DefaultAndExplicitBooleanValuesApplyBeforeListening(string? value, bool expected)
    {
        await using var api = ApiProcess.Start(new Dictionary<string, string?>
        {
            ["SECRET_TOKEN"] = SyntheticSecret,
            [EnvironmentVariable] = value
        });
        await AssertStartedWithModeAsync(api, expected);
    }

    [Theory]
    [InlineData("alias", "")]
    [InlineData("alias", " ")]
    [InlineData("alias", "0")]
    [InlineData("alias", "1")]
    [InlineData("alias", "yes")]
    [InlineData("alias", InvalidSentinel)]
    [InlineData("native", InvalidSentinel)]
    [InlineData("file", InvalidSentinel)]
    [InlineData("command", InvalidSentinel)]
    public async Task InvalidSelectedValuesFailSafelyWithoutFallback(string source, string value)
    {
        var environment = new Dictionary<string, string?> { ["SECRET_TOKEN"] = SyntheticSecret };
        var configuration = new Dictionary<string, string?> { [RuntimeConfiguration.PublicIpValidationConfigurationKey] = "true" };
        string[] arguments = [];
        switch (source)
        {
            case "alias":
                environment[EnvironmentVariable] = value;
                environment[NativeEnvironmentVariable] = "true";
                break;
            case "native": environment[NativeEnvironmentVariable] = value; break;
            case "file": configuration[RuntimeConfiguration.PublicIpValidationConfigurationKey] = value; break;
            case "command": arguments = ["--" + RuntimeConfiguration.PublicIpValidationConfigurationKey, value]; break;
        }

        await using var api = ApiProcess.Start(environment, arguments, configuration);

        Assert.Equal(InvalidConfigurationExitCode, await api.WaitForExitAsync());
        Assert.Contains(EnvironmentVariable, api.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", api.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(DisabledWarning, api.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(InvalidSentinel, api.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecret, api.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("command", true)]
    [InlineData("command", false)]
    [InlineData("alias", true)]
    [InlineData("alias", false)]
    [InlineData("native", true)]
    [InlineData("native", false)]
    [InlineData("file", true)]
    [InlineData("file", false)]
    public async Task ConfigurationPrecedenceIsCommandThenAliasThenNativeThenFile(string selected, bool expected)
    {
        var value = expected.ToString();
        var lowerValue = (!expected).ToString();
        var environment = new Dictionary<string, string?> { ["SECRET_TOKEN"] = SyntheticSecret };
        var configuration = new Dictionary<string, string?>
        {
            [RuntimeConfiguration.PublicIpValidationConfigurationKey] = selected == "file" ? value : lowerValue
        };
        if (selected != "file")
        {
            environment[NativeEnvironmentVariable] = selected == "native" ? value : lowerValue;
        }
        if (selected is "command" or "alias")
        {
            environment[EnvironmentVariable] = selected == "alias" ? value : lowerValue;
        }
        var arguments = selected == "command"
            ? new[] { "--" + RuntimeConfiguration.PublicIpValidationConfigurationKey, value }
            : [];

        await using var api = ApiProcess.Start(environment, arguments, configuration);
        await AssertStartedWithModeAsync(api, expected);
    }

    [Fact]
    public async Task ExplicitCommandCanOverrideAnInvalidLowerPriorityAlias()
    {
        await using var api = ApiProcess.Start(
            new Dictionary<string, string?> { ["SECRET_TOKEN"] = SyntheticSecret, [EnvironmentVariable] = InvalidSentinel },
            ["--" + RuntimeConfiguration.PublicIpValidationConfigurationKey, "true"]);
        await AssertStartedWithModeAsync(api, expected: true);
        Assert.DoesNotContain(InvalidSentinel, api.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationReloadDoesNotChangeTheValidatedSnapshot()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RuntimeConfiguration.PublicIpValidationConfigurationKey] = "false"
        });
        var runtime = new RuntimeConfiguration(portAliasProvider: null);
        runtime.Validate(configuration);

        configuration[RuntimeConfiguration.PublicIpValidationConfigurationKey] = "true";

        Assert.False(runtime.ValidatePublicIp);
        var restarted = new RuntimeConfiguration(portAliasProvider: null);
        restarted.Validate(configuration);
        Assert.True(restarted.ValidatePublicIp);
    }

    private static async Task AssertStartedWithModeAsync(ApiProcess api, bool expected)
    {
        await api.WaitUntilListeningAsync();
        Assert.Equal(HttpStatusCode.OK, await api.DiscoverAsync(SyntheticSecret));
        Assert.Equal(HttpStatusCode.Unauthorized, await api.DiscoverAsync(null));
        await api.StopAsync();
        Assert.Equal(!expected, api.Output.Contains(DisabledWarning, StringComparison.Ordinal));
        Assert.DoesNotContain(SyntheticSecret, api.Output, StringComparison.Ordinal);
    }
}
