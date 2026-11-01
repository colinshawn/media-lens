using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaLens.Api;

namespace MediaLens.Tests;

public sealed class RuntimeConfigurationTests
{
    private const int ConfigurationFailureExitCode = 1;
    private const string AliasSecret = "synthetic-alias-secret-token-1234567890";
    private const string NativeSecret = "synthetic-native-secret-token-1234567890";
    private const string FileSecret = "synthetic-file-secret-token-1234567890";
    private const string CommandSecret = "synthetic-command-secret-token-1234567890";
    private const string NativeSecretVariable = "MediaLens__Auth__SecretToken";

    public static IEnumerable<object[]> SecretBoundaries()
    {
        foreach (var source in new[] { "alias", "native", "file", "command" })
        {
            foreach (var length in new[] { 31, 32, 256, 257 })
            {
                yield return [source, length];
            }
        }
    }

    [Theory]
    [MemberData(nameof(SecretBoundaries))]
    public async Task EveryConfigurationSourceEnforcesTheSameInclusiveBoundaries(string source, int length)
    {
        var value = new string('A', length);
        var environment = new Dictionary<string, string?>();
        var configuration = new Dictionary<string, string?>();
        string[] arguments = [];
        switch (source)
        {
            case "alias": environment["SECRET_TOKEN"] = value; break;
            case "native": environment[NativeSecretVariable] = value; break;
            case "file": configuration[SecretToken.ConfigurationKey] = value; break;
            case "command": arguments = ["--" + SecretToken.ConfigurationKey, value]; break;
        }

        await using var api = ApiProcess.Start(environment, arguments, configuration);
        if (length is 32 or 256)
        {
            await api.WaitUntilListeningAsync();
            Assert.Equal(HttpStatusCode.OK, await api.DiscoverAsync(value));
            Assert.Equal(HttpStatusCode.Unauthorized, await api.DiscoverAsync(new string('B', length)));
            await api.StopAsync();
        }
        else
        {
            Assert.Equal(ConfigurationFailureExitCode, await api.WaitForExitAsync());
            Assert.Contains("SECRET_TOKEN", api.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Application started", api.Output, StringComparison.Ordinal);
        }

        AssertSecretNotLogged(value, api.Output);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("                                ")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAé")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA#")]
    [InlineData(" synthetic-alias-secret-token-1234567890")]
    [InlineData("synthetic-alias-secret-token-1234567890 ")]
    public async Task MissingOrInvalidSelectedSecretsFailBeforeListeningWithoutLeaks(string? value)
    {
        await using var api = ApiProcess.Start(new Dictionary<string, string?> { ["SECRET_TOKEN"] = value });
        Assert.Equal(ConfigurationFailureExitCode, await api.WaitForExitAsync());
        Assert.Contains("SECRET_TOKEN", api.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", api.Output, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(value))
        {
            AssertSecretNotLogged(value, api.Output);
        }
    }

    [Theory]
    [InlineData("command")]
    [InlineData("alias")]
    [InlineData("native")]
    [InlineData("file")]
    public async Task SecretPrecedenceIsCommandThenAliasThenNativeThenFile(string selected)
    {
        var environment = new Dictionary<string, string?>();
        var configuration = new Dictionary<string, string?> { [SecretToken.ConfigurationKey] = FileSecret };
        if (selected != "file")
        {
            environment[NativeSecretVariable] = NativeSecret;
        }
        if (selected is "command" or "alias")
        {
            environment["SECRET_TOKEN"] = AliasSecret;
        }
        var arguments = selected == "command" ? new[] { "--" + SecretToken.ConfigurationKey, CommandSecret } : [];
        var expected = selected switch
        {
            "command" => CommandSecret,
            "alias" => AliasSecret,
            "native" => NativeSecret,
            _ => FileSecret
        };

        await using var api = ApiProcess.Start(environment, arguments, configuration);
        await api.WaitUntilListeningAsync();
        foreach (var candidate in new[] { CommandSecret, AliasSecret, NativeSecret, FileSecret })
        {
            Assert.Equal(candidate == expected ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, await api.DiscoverAsync(candidate));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("                                ")]
    [InlineData("short")]
    public async Task InvalidActiveAliasDoesNotFallBackToAValidLowerSource(string value)
    {
        await using var api = ApiProcess.Start(
            new Dictionary<string, string?> { ["SECRET_TOKEN"] = value, [NativeSecretVariable] = NativeSecret },
            configuration: new Dictionary<string, string?> { [SecretToken.ConfigurationKey] = FileSecret });
        Assert.Equal(ConfigurationFailureExitCode, await api.WaitForExitAsync());
        AssertSecretNotLogged(NativeSecret, api.Output);
        AssertSecretNotLogged(FileSecret, api.Output);
    }

    [Fact]
    public async Task ExplicitCommandCanOverrideAnInvalidLowerPriorityAlias()
    {
        await using var api = ApiProcess.Start(
            new Dictionary<string, string?> { ["SECRET_TOKEN"] = "short" },
            ["--" + SecretToken.ConfigurationKey, CommandSecret]);
        await api.WaitUntilListeningAsync();
        Assert.Equal(HttpStatusCode.OK, await api.DiscoverAsync(CommandSecret));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("+9000")]
    [InlineData("9000;9001")]
    [InlineData("http://localhost:9000")]
    public async Task InvalidSelectedPortAliasFailsInsteadOfFallingBack(string value)
    {
        await using var api = ApiProcess.Start(new Dictionary<string, string?>
        {
            ["SECRET_TOKEN"] = AliasSecret,
            ["HTTP_PORT"] = value
        });
        Assert.Equal(ConfigurationFailureExitCode, await api.WaitForExitAsync());
        Assert.Contains("HTTP_PORT", api.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", api.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("command-port")]
    [InlineData("command-url")]
    [InlineData("native-url")]
    [InlineData("endpoint")]
    public async Task SelectedPortAndOriginalUrlModesHaveTheirDocumentedPrecedence(string mode)
    {
        var selected = ApiProcess.AvailablePort();
        var environment = new Dictionary<string, string?>
        {
            ["SECRET_TOKEN"] = AliasSecret,
            ["HTTP_PORT"] = mode == "alias" ? selected.ToString(CultureInfo.InvariantCulture) : "not-selected"
        };
        string[] arguments = [];
        var configuration = new Dictionary<string, string?>();
        var url = $"http://127.0.0.1:{selected}";
        switch (mode)
        {
            case "command-port": arguments = ["--HTTP_PORTS", selected.ToString(CultureInfo.InvariantCulture)]; break;
            case "command-url": arguments = ["--urls", url]; break;
            case "native-url": environment["ASPNETCORE_URLS"] = url; break;
            case "endpoint": configuration["Kestrel:Endpoints:Test:Url"] = url; break;
        }

        await using var api = ApiProcess.Start(environment, arguments, configuration, selected);
        await api.WaitUntilListeningAsync();
        Assert.Equal(HttpStatusCode.OK, await api.DiscoverAsync(AliasSecret));
    }

    [Fact]
    public async Task HttpPortEnvironmentOverridesConfigurationFileValues()
    {
        var port = ApiProcess.AvailablePort();
        await using var api = ApiProcess.Start(
            environment: new Dictionary<string, string?> { ["SECRET_TOKEN"] = AliasSecret, ["HTTP_PORT"] = port.ToString(CultureInfo.InvariantCulture) },
            configuration: new Dictionary<string, string?> { ["HTTP_PORTS"] = "not-selected" },
            port: port);
        await api.WaitUntilListeningAsync();
        Assert.Equal(HttpStatusCode.OK, await api.DiscoverAsync(AliasSecret));
    }

    [Fact]
    public async Task RestartWithNewConfigurationRevokesTheOldSecret()
    {
        var port = ApiProcess.AvailablePort();
        await using (var first = ApiProcess.Start(new Dictionary<string, string?> { ["SECRET_TOKEN"] = AliasSecret }, port: port))
        {
            await first.WaitUntilListeningAsync();
            Assert.Equal(HttpStatusCode.OK, await first.DiscoverAsync(AliasSecret));
        }
        await using var second = ApiProcess.Start(new Dictionary<string, string?> { ["SECRET_TOKEN"] = NativeSecret }, port: port);
        await second.WaitUntilListeningAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await second.DiscoverAsync(AliasSecret));
        Assert.Equal(HttpStatusCode.OK, await second.DiscoverAsync(NativeSecret));
        await second.StopAsync();
        AssertSecretNotLogged(AliasSecret, second.Output);
        AssertSecretNotLogged(NativeSecret, second.Output);
    }

    private static void AssertSecretNotLogged(string value, string output)
    {
        Assert.DoesNotContain(value, output, StringComparison.Ordinal);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Assert.DoesNotContain(Convert.ToHexString(digest), output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(digest), output, StringComparison.Ordinal);
    }
}

/// <summary>每个启动用例使用独立进程、配置目录和环境，不修改测试宿主的环境变量。</summary>
internal sealed class ApiProcess : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private readonly Process _process;
    private readonly string _directory;
    private readonly Task<string> _stdout;
    private readonly Task<string> _stderr;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(1) };

    private ApiProcess(Process process, string directory, int port)
    {
        _process = process;
        _directory = directory;
        Port = port;
        _stdout = process.StandardOutput.ReadToEndAsync();
        _stderr = process.StandardError.ReadToEndAsync();
    }

    public int Port { get; }

    public string Output { get; private set; } = "";

    public static ApiProcess Start(
        Dictionary<string, string?> environment,
        string[]? arguments = null,
        Dictionary<string, string?>? configuration = null,
        int? port = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "medialens-api-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (configuration is not null)
        {
            File.WriteAllText(Path.Combine(directory, "appsettings.json"), JsonSerializer.Serialize(configuration));
        }

        var selectedPort = port ?? AvailablePort();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // 清除继承的应用配置；环境只在本子进程内设置，保持并行测试隔离。
        foreach (var key in start.Environment.Keys.Where(key =>
                     key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) ||
                     key.StartsWith("MediaLens", StringComparison.OrdinalIgnoreCase) ||
                     key is "SECRET_TOKEN" or "HTTP_PORT" or "HTTP_PORTS" or "HTTPS_PORTS" or
                         "URLS" or "DOTNET_URLS" or "DOTNET_HTTP_PORTS" or "DOTNET_HTTPS_PORTS").ToArray())
        {
            start.Environment.Remove(key);
        }
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["HTTP_PORT"] = selectedPort.ToString(CultureInfo.InvariantCulture);
        foreach (var (key, value) in environment)
        {
            if (value is null)
            {
                start.Environment.Remove(key);
            }
            else
            {
                start.Environment[key] = value;
            }
        }
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments ?? [])
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start synthetic API process.");
        return new ApiProcess(process, directory, selectedPort);
    }

    public static int AvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task WaitUntilListeningAsync()
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < StartupTimeout)
        {
            if (_process.HasExited)
            {
                await StopAsync();
                throw new InvalidOperationException("Synthetic API exited before listening: " + Output);
            }
            try
            {
                if (await DiscoverAsync(null) == HttpStatusCode.Unauthorized)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
        throw new TimeoutException("Synthetic API did not listen before the startup deadline.");
    }

    public async Task<HttpStatusCode> DiscoverAsync(string? token, int? port = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port ?? Port}/api/v1/platforms");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(SecretToken.Scheme, token);
        }
        using var response = await _client.SendAsync(request);
        return response.StatusCode;
    }

    public async Task<int> WaitForExitAsync()
    {
        await _process.WaitForExitAsync().WaitAsync(StartupTimeout);
        await StopAsync();
        return _process.ExitCode;
    }

    public async Task StopAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(StartupTimeout);
        }
        Output = await _stdout + await _stderr;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _process.Dispose();
        _client.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
