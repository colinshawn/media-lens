using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MediaLens.Contracts;
using MediaLens.Http;
using MediaLens.Platforms.Bilibili;
using MediaLens.Platforms.Douyin;

[assembly: InternalsVisibleTo("MediaLens.Probe.Tests")]

namespace MediaLens.Probe;

internal static class Program
{
    private const string PlatformOption = "--platform";
    private const string InputOption = "--input";
    private const string EntryOption = "--entry";
    private const string DnsOverridesOption = "--dns-overrides";
    private const string TimestampFormat = "O";
    private const int OptionPairSize = 2;

    public static async Task<int> Main(string[] args)
    {
        string? inputPath = null;
        string? dnsOverridesPath = null;
        var entryPoint = ProbeEntryPoints.Share;
        var platform = DouyinAdapter.PlatformId;
        var entrySpecified = false;
        for (var index = 0; index < args.Length; index += OptionPairSize)
        {
            if (index + 1 >= args.Length)
            {
                return PrintUsage();
            }

            switch (args[index])
            {
                case PlatformOption:
                    platform = args[index + 1];
                    break;
                case InputOption:
                    inputPath = args[index + 1];
                    break;
                case EntryOption:
                    entryPoint = args[index + 1];
                    entrySpecified = true;
                    break;
                case DnsOverridesOption:
                    dnsOverridesPath = args[index + 1];
                    break;
                default:
                    return PrintUsage();
            }
        }

        if (platform == BilibiliAdapter.PlatformId && !entrySpecified)
        {
            entryPoint = ProbeEntryPoints.Adapter;
        }

        if (inputPath is null || !ProbeEntryPoints.IsKnown(entryPoint) ||
            (platform != DouyinAdapter.PlatformId && platform != BilibiliAdapter.PlatformId) ||
            (platform == BilibiliAdapter.PlatformId && entryPoint != ProbeEntryPoints.Adapter))
        {
            return PrintUsage();
        }

        ProbeInput[]? inputs;
        try
        {
            if (new FileInfo(inputPath).Length > ProbeDefaults.MaximumInputFileBytes)
            {
                return PrintUsage();
            }

            await using var stream = File.OpenRead(inputPath);
            inputs = await JsonSerializer.DeserializeAsync(stream, ProbeJsonContext.Default.ProbeInputArray);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // 不回显文件路径、分享文本、链接或异常中的请求参数。
            return PrintUsage();
        }

        if (inputs is null || inputs.Length is 0 or > ProbeDefaults.MaximumSamples ||
            inputs.Any(input => input is null ||
                                !ProbeSampleKinds.IsKnown(input.Kind) ||
                                string.IsNullOrWhiteSpace(input.Text)))
        {
            return PrintUsage();
        }

        if (platform == BilibiliAdapter.PlatformId && inputs.Any(input => input.Kind != MediaTypes.Video))
        {
            return PrintUsage();
        }

        var profile = platform == BilibiliAdapter.PlatformId
            ? PlatformHttpProfile.Bilibili
            : PlatformHttpProfile.Douyin;
        PublicHttpTargetPolicy targetPolicy;
        try
        {
            var addresses = dnsOverridesPath is null
                ? null
                : await LoadDnsOverridesAsync(dnsOverridesPath);
            targetPolicy = new PublicHttpTargetPolicy(addresses, profile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         JsonException or ArgumentException or FormatException or UpstreamRequestException)
        {
            return PrintUsage();
        }

        using var client = new CookieFreeHttpClient(targetPolicy, profile: profile);
        var runner = new ProbeRunner(client, platform);
        var results = new List<ProbeReport>();
        foreach (var input in inputs)
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(ProbeDefaults.ProbeTimeoutSeconds));
            results.Add(await runner.RunAsync(input, entryPoint, timeout.Token));
        }

        var report = new ProbeBatchReport(
            ObservedAt: DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture),
            CookiePolicy: ProbeDefaults.CookiePolicy,
            Results: results.ToArray());
        Console.WriteLine(JsonSerializer.Serialize(report, ProbeJsonContext.Default.ProbeBatchReport));

        // 元数据观察通过仍不等于 CDN 或调用方下载通过；该退出码只反映本工具的观察范围。
        return results.All(result => result.Outcome == ProbeOutcomes.MetadataObserved)
            ? ProbeDefaults.SuccessfulExitCode
            : ProbeDefaults.UnresolvedExitCode;
    }

    private static int PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: MediaLens.Probe --input <private JSON path> " +
            "[--platform douyin|bilibili] [--entry share|mobile|desktop|detail|legacy|adapter] [--dns-overrides <private JSON path>]");
        return ProbeDefaults.InvalidInputExitCode;
    }

    private static async Task<Dictionary<string, IPAddress[]>> LoadDnsOverridesAsync(string path)
    {
        if (new FileInfo(path).Length > ProbeDefaults.MaximumInputFileBytes)
        {
            throw new ArgumentException("DNS override file is too large.");
        }

        await using var stream = File.OpenRead(path);
        var values = await JsonSerializer.DeserializeAsync(
            stream,
            ProbeJsonContext.Default.DictionaryStringStringArray);
        if (values is null || values.Count > ProbeDefaults.MaximumSamples ||
            values.Any(entry => entry.Value is null ||
                                entry.Value.Length is 0 or > ProbeDefaults.MaximumAddressesPerHost ||
                                entry.Value.Any(string.IsNullOrWhiteSpace)))
        {
            throw new ArgumentException("Invalid DNS overrides.");
        }

        return values.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Select(IPAddress.Parse).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }
}
