using System.Globalization;

namespace MediaLens.Api;

/// <summary>将容器友好的变量映射到原生配置键，保留命令行和监听模式优先级。</summary>
internal sealed class RuntimeConfiguration(IConfigurationProvider? portAliasProvider)
{
    private const string SecretEnvironmentVariable = "SECRET_TOKEN";
    private const string PortEnvironmentVariable = "HTTP_PORT";
    private const int MinimumPort = 1;
    private const int MaximumPort = 65_535;

    public static RuntimeConfiguration Configure(ConfigurationManager configuration, string[] args)
    {
        var aliases = new Dictionary<string, string?>();
        if (Environment.GetEnvironmentVariable(SecretEnvironmentVariable) is { } secret)
        {
            aliases[SecretToken.ConfigurationKey] = secret;
        }
        if (Environment.GetEnvironmentVariable(PortEnvironmentVariable) is { } port)
        {
            aliases[WebHostDefaults.HttpPortsKey] = port;
        }

        configuration.AddInMemoryCollection(aliases);
        var provider = aliases.ContainsKey(WebHostDefaults.HttpPortsKey)
            ? ((IConfigurationRoot)configuration).Providers.Last()
            : null;

        // 最后重新应用同一命令行，防止别名或镜像默认值遮蔽显式参数。
        configuration.AddCommandLine(args);
        return new RuntimeConfiguration(provider);
    }

    public void ValidatePortAlias(ConfigurationManager configuration)
    {
        if (portAliasProvider is null || !string.IsNullOrEmpty(configuration[WebHostDefaults.ServerUrlsKey]) ||
            configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
        {
            return;
        }

        foreach (var provider in ((IConfigurationRoot)configuration).Providers.Reverse())
        {
            if (!provider.TryGet(WebHostDefaults.HttpPortsKey, out var value))
            {
                continue;
            }

            // 校验最终选中的 HTTP_PORT；显式命令行监听配置仍交给框架处理。
            if (ReferenceEquals(provider, portAliasProvider) &&
                (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
                 port is < MinimumPort or > MaximumPort))
            {
                throw new InvalidOperationException("HTTP_PORT must be a decimal integer between 1 and 65535.");
            }

            return;
        }
    }
}
