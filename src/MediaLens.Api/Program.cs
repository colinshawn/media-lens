using MediaLens.Contracts;
using MediaLens.Http;
using MediaLens.Platforms;
using MediaLens.Platforms.Douyin;
using MediaLens.Resolution;

namespace MediaLens.Api;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(options =>
            options.Limits.MaxRequestBodySize = ApiDefaults.MaximumRequestBodyBytes);
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, MediaLensJsonContext.Default));
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(ReadSettings(builder.Configuration));
        builder.Services.AddSingleton<IUpstreamClient>(_ => new CookieFreeHttpClient(new PublicHttpTargetPolicy()));
        builder.Services.AddSingleton(provider => new DouyinAdapter(
            provider.GetRequiredService<IUpstreamClient>(),
            provider.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(provider =>
        {
            var adapter = provider.GetRequiredService<DouyinAdapter>();
            // 至少一种媒体通过真实匿名验证才启用；逐类型验证状态来自适配器。
            return new PlatformRegistry([new PlatformRegistration(adapter, adapter.Capabilities.HasSupportedMedia)]);
        });
        builder.Services.AddSingleton(provider => new ResolutionService(
            provider.GetRequiredService<PlatformRegistry>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ResolutionSettings>()));

        var app = builder.Build();
        app.UseApiErrors();
        app.MapMediaResolutionApi();
        await app.RunAsync();
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
    public const int MaximumRequestBodyBytes = 65_536;
    public const int MinimumTimeoutSeconds = 1;
    public const int MaximumTimeoutSeconds = 120;
    public const string TimeoutConfigurationKey = "MediaLens:ResolutionTimeoutSeconds";
    public const string ApiPrefix = "/api/v1";
}
