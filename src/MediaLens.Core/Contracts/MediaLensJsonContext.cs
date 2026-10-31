using System.Text.Json.Serialization;

namespace MediaLens.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ResolveRequest))]
[JsonSerializable(typeof(ResolveResponse))]
[JsonSerializable(typeof(PlatformListResponse))]
[JsonSerializable(typeof(ApiErrorResponse))]
public partial class MediaLensJsonContext : JsonSerializerContext;
