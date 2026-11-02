using MediaLens.Contracts;
using MediaLens.Http;
using MediaLens.Platforms;
using MediaLens.Platforms.Bilibili;
using MediaLens.Platforms.Douyin;
using MediaLens.Resolution;

namespace MediaLens.Api;

public partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var runtimeConfiguration = RuntimeConfiguration.Configure(builder.Configuration, args);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options =>
            options.Limits.MaxRequestBodySize = ApiDefaults.MaximumRequestBodyBytes);
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, MediaLensJsonContext.Default));
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(ReadSettings(builder.Configuration));
        builder.Services.AddSingleton<IUpstreamClient>(provider => new TimedUpstreamClient(
            new CookieFreeHttpClient(
                new PublicHttpTargetPolicy(validatePublicIp: runtimeConfiguration.ValidatePublicIp)),
            provider.GetRequiredService<ILogger<TimedUpstreamClient>>()));
        builder.Services.AddKeyedSingleton<IUpstreamClient>(BilibiliAdapter.PlatformId, (provider, _) =>
            new TimedUpstreamClient(
                new CookieFreeHttpClient(
                    new PublicHttpTargetPolicy(
                        profile: PlatformHttpProfile.Bilibili,
                        validatePublicIp: runtimeConfiguration.ValidatePublicIp),
                    profile: PlatformHttpProfile.Bilibili),
                provider.GetRequiredService<ILogger<TimedUpstreamClient>>()));
        builder.Services.AddSingleton(provider => new BilibiliAdapter(
            provider.GetRequiredKeyedService<IUpstreamClient>(BilibiliAdapter.PlatformId),
            provider.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(provider => new DouyinAdapter(
            provider.GetRequiredService<IUpstreamClient>(),
            provider.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(provider =>
        {
            var douyin = provider.GetRequiredService<DouyinAdapter>();
            var bilibili = provider.GetRequiredService<BilibiliAdapter>();
            // 两个平台均已取得严格匿名与独立下载证据，发现接口只读取这些注册信息。
            return new PlatformRegistry(
            [
                new PlatformRegistration(douyin, douyin.Capabilities.HasSupportedMedia),
                new PlatformRegistration(bilibili, bilibili.Capabilities.HasSupportedMedia)
            ]);
        });
        builder.Services.AddSingleton(provider => new ResolutionService(
            provider.GetRequiredService<PlatformRegistry>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ResolutionSettings>()));

        await using var app = builder.Build();
        SecretToken secret;
        try
        {
            secret = new SecretToken(app.Configuration[SecretToken.ConfigurationKey]);
            runtimeConfiguration.Validate(builder.Configuration);
        }
        catch (InvalidOperationException exception)
        {
            // 启动配置校验器只生成固定的安全消息；受控退出避免进入崩溃转储路径。
            app.Logger.LogCritical("{ConfigurationError}", exception.Message);
            return ApiDefaults.InvalidConfigurationExitCode;
        }
        if (!runtimeConfiguration.ValidatePublicIp)
        {
            app.Logger.LogWarning(
                "Public IP validation is disabled (VALIDATE_PUBLIC_IP=false). Allowed hosts may resolve to restricted addresses.");
        }
        app.UseApiErrors();
        app.UseRouting();
        app.UseApiAccessControl(secret);
        app.MapMediaResolutionApi();
        await app.RunAsync();
        return ApiDefaults.SuccessExitCode;
    }

    private static ResolutionSettings ReadSettings(IConfiguration configuration)
    {
        var value = configuration[ApiDefaults.TimeoutConfigurationKey];
        if (value is null)
        {
            return ResolutionSettings.Default;
        }

        if (!int.TryParse(value, out var seconds) || seconds is < ApiDefaults.MinimumTimeoutSeconds or > ApiDefaults.MaximumTimeoutSeconds)
        {
            throw new InvalidOperationException("Resolution timeout must be between 1 and 120 seconds.");
        }

        return new ResolutionSettings(TimeSpan.FromSeconds(seconds));
    }
}

internal static class ApiDefaults
{
    public const int SuccessExitCode = 0;
    public const int InvalidConfigurationExitCode = 1;
    public const int MaximumRequestBodyBytes = 65_536;
    public const int MinimumTimeoutSeconds = 1;
    public const int MaximumTimeoutSeconds = 120;
    public const string TimeoutConfigurationKey = "MediaLens:ResolutionTimeoutSeconds";
    public const string ApiPrefix = "/api/v1";
}
