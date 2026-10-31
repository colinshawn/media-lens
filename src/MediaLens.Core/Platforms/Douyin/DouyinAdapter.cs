using System.Net;
using MediaLens.Contracts;
using MediaLens.Http;
using MediaLens.Resolution;

namespace MediaLens.Platforms.Douyin;

/// <summary>使用已验证的匿名详情入口与公开阅读页；不补齐 Cookie、游客标识或签名。</summary>
public sealed class DouyinAdapter(IUpstreamClient client, TimeProvider timeProvider) : IPlatformAdapter
{
    public const string PlatformId = "douyin";
    private const string ShortLinkHost = "v.douyin.com";

    public string Id => PlatformId;

    public string DisplayName => "抖音";

    public PlatformCapabilities Capabilities { get; } = new(
        CapabilityStates.Supported,
        CapabilityStates.Supported,
        CapabilityStates.Supported,
        CapabilityStates.Supported);

    public bool CanHandle(Uri uri) => IsSupportedUri(uri);

    public static bool IsSupportedUri(Uri uri) => DouyinWorkLink.IsDouyinUri(uri);

    public async Task<ResolvedWork> ResolveAsync(Uri uri, CancellationToken cancellationToken)
    {
        var shortLink = uri.IdnHost.Equals(ShortLinkHost, StringComparison.OrdinalIgnoreCase);
        MetadataPage? sharePage = null;
        if (shortLink)
        {
            sharePage = await client.GetAsync(uri, cancellationToken);
            EnsureNotTransientFailure(sharePage);
        }

        var link = DouyinWorkLink.Parse(sharePage?.FinalUri ?? uri);
        // 展开短链时已取得完整数据则直接复用，否则通过本地 ID 请求匿名详情。
        var metadata = sharePage is { StatusCode: < (int)HttpStatusCode.BadRequest }
            ? DouyinMetadataParser.Read(sharePage.Body, link.WorkId)
            : null;
        MetadataPage page;
        if (metadata is null)
        {
            page = await client.GetAsync(link.DetailUri, cancellationToken);
            EnsureNotTransientFailure(page);
            metadata = page.StatusCode < (int)HttpStatusCode.BadRequest
                ? DouyinMetadataParser.Read(page.Body, link.WorkId)
                : null;
        }

        if (metadata is null)
        {
            page = sharePage ?? await client.GetAsync(link.MobileUri, cancellationToken);
            EnsureNotTransientFailure(page);
            var finalLink = DouyinWorkLink.Parse(page.FinalUri);
            if (finalLink.WorkId != link.WorkId)
            {
                throw ResolutionErrors.UpstreamChanged();
            }

            metadata = page.StatusCode < (int)HttpStatusCode.BadRequest
                ? DouyinMetadataParser.Read(page.Body, link.WorkId)
                : null;
        }

        if (metadata is null)
        {
            // 精选公开阅读页直接提供服务端 video_model，无需 Cookie 或本地签名。
            page = await client.GetAsync(link.JingxuanUri, cancellationToken);
            EnsureAccessible(page);
            if (DouyinWorkLink.Parse(page.FinalUri).WorkId != link.WorkId)
            {
                throw ResolutionErrors.UpstreamChanged();
            }

            metadata = JingxuanMetadataParser.Read(page.Body, link.WorkId);
        }

        if (metadata is null)
        {
            throw ResolutionErrors.AnonymousUnavailable();
        }

        var items = await new DouyinMediaResolver(client, timeProvider)
            .ResolveAsync(metadata, link.WorkId, cancellationToken);
        return new ResolvedWork(
            Platform: Id,
            WorkId: link.WorkId,
            WorkType: metadata.WorkType,
            WebUrl: "https://www.douyin.com/" + (metadata.WorkType == MediaTypes.Album ? "note/" : "video/") + link.WorkId,
            Description: metadata.Description,
            Items: items);
    }

    private static void EnsureAccessible(MetadataPage page)
    {
        EnsureNotTransientFailure(page);
        if (page.StatusCode >= (int)HttpStatusCode.BadRequest)
        {
            // HTTP 拒绝不足以断言删除；不读取或补齐 Cookie、UIFID 等身份材料。
            throw ResolutionErrors.AnonymousUnavailable();
        }
    }

    private static void EnsureNotTransientFailure(MetadataPage page)
    {
        if (page.StatusCode >= (int)HttpStatusCode.InternalServerError ||
            page.StatusCode == (int)HttpStatusCode.TooManyRequests)
        {
            throw ResolutionErrors.UpstreamUnavailable(page.RetryAfterSeconds);
        }
    }
}
