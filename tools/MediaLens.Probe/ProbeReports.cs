using System.Text.Json.Serialization;
using MediaLens.Http;
using MediaLens.Probe.Metadata;

namespace MediaLens.Probe;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProbeInput
{
    public required string Kind { get; init; }

    public required string Text { get; init; }
}

internal sealed record ProbeReport(
    string SampleKind,
    string EntryPoint,
    string Outcome,
    int? HttpStatusCode,
    string? ContentType,
    int MetadataBytes,
    MetadataSummary Metadata,
    HttpObservation[] Hops);

internal sealed record ProbeBatchReport(
    string ObservedAt,
    string CookiePolicy,
    ProbeReport[] Results);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = true)]
[JsonSerializable(typeof(ProbeInput[]))]
[JsonSerializable(typeof(ProbeBatchReport))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
internal partial class ProbeJsonContext : JsonSerializerContext;
