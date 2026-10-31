using System.Globalization;
using System.Net;
using MediaLens.Contracts;
using MediaLens.Http;
using MediaLens.Platforms;
using MediaLens.Shares;

namespace MediaLens.Resolution;

public sealed class ResolutionService(
    PlatformRegistry registry,
    TimeProvider timeProvider,
    ResolutionSettings settings)
{
    public async Task<ResolveResponse> ResolveAsync(
        string text,
        string? platform,
        string requestId,
        CancellationToken cancellationToken)
    {
        var candidates = ShareUrlExtractor.Extract(text);
        if (candidates.Length == 0)
        {
            throw ResolutionErrors.InvalidInput();
        }

        var (registration, uri) = Select(candidates, platform);
        using var timeout = new CancellationTokenSource(settings.Timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        ResolvedWork work;
        try
        {
            work = await registration.Adapter.ResolveAsync(uri, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ResolutionException(
                ErrorCodes.ResolutionTimeout, "解析超出总时限，请稍后重试。", (int)HttpStatusCode.GatewayTimeout, retryable: true);
        }
        catch (HttpRequestException)
        {
            throw ResolutionErrors.UpstreamUnavailable();
        }
        catch (IOException)
        {
            throw ResolutionErrors.UpstreamUnavailable();
        }
        catch (UpstreamRequestException)
        {
            throw ResolutionErrors.UpstreamChanged();
        }

        if (work.Platform != registration.Adapter.Id)
        {
            throw ResolutionErrors.UpstreamChanged();
        }

        var now = timeProvider.GetUtcNow();
        var items = MediaResultValidator.Validate(work, now);
        var expiry = items.SelectMany(item => item.ImageSources.Concat(item.VideoSources))
            .Select(source => source.ExpiresAt)
            .ToArray();
        return new ResolveResponse(
            RequestId: requestId,
            Platform: work.Platform,
            WorkId: work.WorkId,
            WorkType: work.WorkType,
            WebUrl: work.WebUrl,
            Description: work.Description,
            Items: items,
            ResolvedAt: FormatUtc(now),
            ExpiresAt: expiry.All(value => value is not null)
                ? expiry.MinBy(value => DateTimeOffset.Parse(value!, CultureInfo.InvariantCulture))
                : null);
    }

    public PlatformListResponse GetPlatforms(string requestId) => new(
        requestId,
        registry.Registrations.Select(entry => new PlatformDescription(
            entry.Adapter.Id,
            entry.Adapter.DisplayName,
            entry.Enabled,
            entry.Adapter.Capabilities)).ToArray());

    internal static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private (PlatformRegistration Registration, Uri Uri) Select(Uri[] candidates, string? platform)
    {
        if (platform is not null)
        {
            var entry = registry.Find(platform) ?? throw new ResolutionException(
                ErrorCodes.PlatformNotFound, "指定平台未注册。", (int)HttpStatusCode.NotFound);
            if (!entry.Enabled)
            {
                throw NotSupported();
            }

            var uri = candidates.FirstOrDefault(entry.Adapter.CanHandle) ?? throw new ResolutionException(
                ErrorCodes.PlatformMismatch, "分享文本中没有指定平台的链接。", (int)HttpStatusCode.BadRequest);
            return (entry, uri);
        }

        foreach (var uri in candidates)
        {
            var entry = registry.Registrations.FirstOrDefault(value => value.Enabled && value.Adapter.CanHandle(uri));
            if (entry is not null)
            {
                return (entry, uri);
            }
        }

        throw NotSupported();
    }

    private static ResolutionException NotSupported() => new(
        ErrorCodes.PlatformNotSupported, "当前没有启用并完成匿名验证的平台适配器。", (int)HttpStatusCode.UnprocessableEntity);
}

public sealed record ResolutionSettings(TimeSpan Timeout)
{
    public static ResolutionSettings Default { get; } = new(TimeSpan.FromSeconds(30));
}
