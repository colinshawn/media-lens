using MediaLens.Contracts;
using MediaLens.Resolution;

namespace MediaLens.Api;

internal static class MediaResolutionEndpoints
{
    public static void MapMediaResolutionApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup(ApiDefaults.ApiPrefix);
        api.MapPost("/resolve", async (
            ResolveRequest request,
            ResolutionService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
            TypedResults.Ok(await service.ResolveAsync(
                request.Text, platform: null, context.TraceIdentifier, cancellationToken)));
        api.MapPost("/platforms/{platform}/resolve", async (
            string platform,
            ResolveRequest request,
            ResolutionService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
            TypedResults.Ok(await service.ResolveAsync(
                request.Text, platform, context.TraceIdentifier, cancellationToken)));
        api.MapGet("/platforms", (ResolutionService service, HttpContext context) =>
            TypedResults.Ok(service.GetPlatforms(context.TraceIdentifier)));
    }
}
