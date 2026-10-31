using System.Collections.Concurrent;
using System.Net;
using MediaLens.Api;
using MediaLens.Contracts;
using MediaLens.Http;
using MediaLens.Platforms;
using MediaLens.Resolution;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MediaLens.Tests;

internal sealed class ApiFactory(PlatformRegistry? registry = null, TimeSpan? timeout = null)
    : WebApplicationFactory<Program>
{
    public IUpstreamClient? Upstream { get; init; }

    public CaptureLogs? Logs { get; init; }

    public ApiFactory WithRealTcp()
    {
        const int ephemeralPort = 0;
        UseKestrel(ephemeralPort);
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (Logs is { } logs)
        {
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        }

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedClock());
            if (Upstream is { } upstream)
            {
                services.RemoveAll<IUpstreamClient>();
                services.AddSingleton(upstream);
            }
            if (registry is not null)
            {
                services.RemoveAll<PlatformRegistry>();
                services.AddSingleton(registry);
            }

            if (timeout is { } value)
            {
                services.RemoveAll<ResolutionSettings>();
                services.AddSingleton(new ResolutionSettings(value));
            }
        });
    }
}

internal sealed class FixedClock : TimeProvider
{
    public static DateTimeOffset Now { get; } = new(
        year: 2026,
        month: 1,
        day: 1,
        hour: 0,
        minute: 0,
        second: 0,
        offset: TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class StubAdapter(
    string id,
    string host,
    Func<Uri, CancellationToken, Task<ResolvedWork>>? resolve = null) : IPlatformAdapter
{
    public string Id => id;

    public string DisplayName => "合成平台";

    public PlatformCapabilities Capabilities { get; init; } = new(
        CapabilityStates.Supported, CapabilityStates.Supported, CapabilityStates.Supported, CapabilityStates.Supported);

    public int Calls { get; private set; }

    public Uri? LastUri { get; private set; }

    public bool CanHandle(Uri uri) => uri.IdnHost == host;

    public Task<ResolvedWork> ResolveAsync(Uri uri, CancellationToken cancellationToken)
    {
        Calls++;
        LastUri = uri;
        return resolve?.Invoke(uri, cancellationToken) ??
               Task.FromResult(Fixtures.Video(id) with { WebUrl = uri.OriginalString });
    }
}

internal sealed class StubUpstreamClient(string metadata) : IUpstreamClient
{
    public ConcurrentQueue<Uri> Requests { get; } = [];

    public Func<Uri, CancellationToken, Task<MediaLocation>>? MediaHandler { get; init; }

    public Func<Uri, MetadataPage>? MetadataHandler { get; init; }

    public int MetadataStatus { get; init; } = (int)HttpStatusCode.OK;

    public int? RetryAfterSeconds { get; init; }

    public Task<MetadataPage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Enqueue(uri);
        if (MetadataHandler is { } handler)
        {
            return Task.FromResult(handler(uri));
        }

        var final = uri.IdnHost == "v.douyin.com" ? new Uri(Fixtures.MobileUrl) : uri;
        return Task.FromResult(new MetadataPage(
            FinalUri: final,
            StatusCode: MetadataStatus,
            ContentType: "application/json",
            Body: metadata,
            BytesRead: System.Text.Encoding.UTF8.GetByteCount(metadata),
            RetryAfterSeconds: RetryAfterSeconds));
    }

    public Task<MediaLocation> ResolveMediaAsync(Uri uri, string expectedType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Enqueue(uri);
        return MediaHandler?.Invoke(uri, cancellationToken) ?? Task.FromResult(new MediaLocation(
            FinalUri: uri,
            StatusCode: (int)HttpStatusCode.OK,
            MimeType: expectedType == MediaTypes.Image ? "image/jpeg" : "video/mp4",
            SizeBytes: null,
            RetryAfterSeconds: null));
    }

    public Dictionary<string, string> CreateDownloadHeaders() => new() { ["User-Agent"] = "synthetic-client" };
}

internal static class Fixtures
{
    public const string WorkId = "1234567890123456789";
    public const string WorkUrl = "https://www.douyin.com/video/1234567890123456789";
    public const string MobileUrl = "https://www.iesdouyin.com/share/video/1234567890123456789/";
    public const string VideoJson = """
        {"aweme_detail":{"aweme_id":"1234567890123456789","desc":"合成描述","video":{
          "duration":12000,"width":1080,"height":1920,
          "play_addr":{"url_list":["https://cdn.example/default.mp4?sig=A%2FB%2B"]},
          "bit_rate":[{"play_addr":{"width":640,"height":480,"url_list":["https://cdn.example/alternative.webm"]}}]
        }}}
        """;

    public static PlatformRegistry Registry(params IPlatformAdapter[] adapters) =>
        new(adapters.Select(adapter => new PlatformRegistration(adapter, Enabled: true)));

    public static MediaSource Source(string url = "https://cdn.example/synthetic.mp4", string? expiresAt = null) => new(
        Url: url,
        Headers: [],
        MimeType: null,
        FileName: null,
        Width: null,
        Height: null,
        DurationMs: null,
        SizeBytes: null,
        ExpiresAt: expiresAt);

    public static ResolvedWork Video(string platform = "douyin") => new(
        Platform: platform,
        WorkId: WorkId,
        WorkType: MediaTypes.Video,
        WebUrl: WorkUrl,
        Description: null,
        Items: [new MediaItem(1, MediaTypes.Video, [], [Source()])]);
}
