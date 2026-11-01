using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using MediaLens.Http;
using MediaLens.Platforms;
using MediaLens.Platforms.Bilibili;
using MediaLens.Platforms.Douyin;
using MediaLens.Probe.Links;
using MediaLens.Probe.Metadata;
using MediaLens.Resolution;
using MediaLens.Shares;

namespace MediaLens.Probe;

internal sealed class ProbeRunner(CookieFreeHttpClient client, string platform = DouyinAdapter.PlatformId)
{
    public async Task<ProbeReport> RunAsync(
        ProbeInput input,
        string entryPoint,
        CancellationToken cancellationToken)
    {
        if (entryPoint == ProbeEntryPoints.Adapter)
        {
            return await RunAdapterAsync(input, cancellationToken);
        }

        var hops = new List<HttpObservation>();
        MetadataPage? page = null;
        var metadata = MetadataSummary.Empty("none");
        var outcome = ProbeOutcomes.NoMediaData;

        try
        {
            var uri = ShareLinks.Extract(input.Text);
            if (ShareLinks.IsShortLink(uri))
            {
                page = await client.GetAsync(uri, hops.Add, cancellationToken);
                uri = page.FinalUri;
            }

            var work = ShareLinks.Identify(uri);
            var target = ShareLinks.GetEntryPoint(work, entryPoint);
            if (page is null || target != page.FinalUri)
            {
                page = await client.GetAsync(target, hops.Add, cancellationToken);
            }

            metadata = MetadataInspector.Inspect(page.Body, work.WorkId);
            outcome = page.StatusCode >= (int)HttpStatusCode.BadRequest
                ? ProbeOutcomes.HttpRejected
                : metadata.CompleteMediaMetadata
                    ? ProbeOutcomes.MetadataObserved
                    : ProbeOutcomes.NoMediaData;
        }
        catch (UpstreamRequestException exception)
        {
            outcome = exception.Outcome;
        }
        catch (OperationCanceledException)
        {
            outcome = ProbeOutcomes.TimedOut;
        }
        catch (HttpRequestException exception)
        {
            outcome = exception.InnerException is UpstreamRequestException policyException
                ? policyException.Outcome
                : ProbeOutcomes.NetworkFailure;
        }
        catch (RegexMatchTimeoutException)
        {
            outcome = ProbeOutcomes.InvalidInput;
        }

        return new ProbeReport(
            SampleKind: input.Kind,
            EntryPoint: entryPoint,
            Outcome: outcome,
            HttpStatusCode: page?.StatusCode ?? hops.LastOrDefault()?.StatusCode,
            ContentType: page?.ContentType,
            MetadataBytes: page?.BytesRead ?? 0,
            Metadata: metadata,
            Hops: hops.ToArray());
    }

    private async Task<ProbeReport> RunAdapterAsync(ProbeInput input, CancellationToken cancellationToken)
    {
        var hops = new ConcurrentQueue<HttpObservation>();
        var observed = new ObservedUpstreamClient(client, hops.Enqueue);
        var outcome = ProbeOutcomes.NoMediaData;
        var metadata = MetadataSummary.Empty(ProbeEntryPoints.Adapter);
        try
        {
            using var bilibili = platform == BilibiliAdapter.PlatformId
                ? new BilibiliAdapter(observed, TimeProvider.System)
                : null;
            IPlatformAdapter adapter = bilibili is not null
                ? bilibili
                : new DouyinAdapter(observed, TimeProvider.System);
            var uri = ShareUrlExtractor.Extract(input.Text).FirstOrDefault(adapter.CanHandle)
                ?? throw new ResolutionException(ErrorCodes.PlatformMismatch, "分享链接与探测平台不一致。", (int)HttpStatusCode.BadRequest);
            var work = await adapter.ResolveAsync(uri, cancellationToken);
            metadata = new MetadataSummary(
                PayloadKind: ProbeEntryPoints.Adapter,
                WorkMatched: true,
                CompleteMediaMetadata: true,
                ImageCount: work.Items.Count(item => item.ImageSources.Length > 0),
                LivePhotoCount: work.Items.Count(item => item.Type == "livePhoto"),
                ImageSourceCount: work.Items.Sum(item => item.ImageSources.Length),
                VideoSourceCount: work.Items.Sum(item => item.VideoSources.Length));
            outcome = ProbeOutcomes.MetadataObserved;
        }
        catch (ResolutionException exception)
        {
            outcome = exception.Error.Code;
        }
        catch (UpstreamRequestException exception)
        {
            outcome = exception.Outcome;
        }
        catch (OperationCanceledException)
        {
            outcome = ProbeOutcomes.TimedOut;
        }
        catch (HttpRequestException exception)
        {
            outcome = exception.InnerException is UpstreamRequestException policyException
                ? policyException.Outcome
                : ProbeOutcomes.NetworkFailure;
        }
        catch (IOException)
        {
            outcome = ProbeOutcomes.NetworkFailure;
        }
        catch (RegexMatchTimeoutException)
        {
            outcome = ProbeOutcomes.InvalidInput;
        }

        return new ProbeReport(
            SampleKind: input.Kind,
            EntryPoint: ProbeEntryPoints.Adapter,
            Outcome: outcome,
            HttpStatusCode: observed.LastPage?.StatusCode ?? hops.LastOrDefault()?.StatusCode,
            ContentType: observed.LastPage?.ContentType,
            MetadataBytes: observed.LastPage?.BytesRead ?? 0,
            Metadata: metadata,
            Hops: hops.ToArray());
    }
}

internal sealed class ObservedUpstreamClient(CookieFreeHttpClient client, Action<HttpObservation> observe) : IUpstreamClient
{
    public MetadataPage? LastPage { get; private set; }

    public async Task<MetadataPage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        LastPage = await client.GetAsync(uri, observe, cancellationToken);
        return LastPage;
    }

    public Task<MediaLocation> ResolveMediaAsync(Uri uri, string expectedType, CancellationToken cancellationToken) =>
        client.ResolveMediaAsync(uri, expectedType, observe, cancellationToken);

    public Dictionary<string, string> CreateDownloadHeaders() => client.CreateDownloadHeaders();
}
