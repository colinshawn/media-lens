using System.Globalization;
using System.Net;
using System.Text.Json;
using MediaLens.Http;
using MediaLens.Resolution;

namespace MediaLens.Platforms.Bilibili;

internal static class BilibiliProtocol
{
    private const int NotLoggedIn = -101;
    private const int NotFound = -404;
    private const int RateLimited = -509;
    private const int AccessDenied = -403;
    private const int RequestRejected = -401;
    private const int RiskRejected = -352;

    internal static JsonElement ReadData(MetadataPage page, bool publicKeys = false, bool contentLookup = false)
    {
        if (page.StatusCode >= (int)HttpStatusCode.InternalServerError ||
            page.StatusCode == (int)HttpStatusCode.TooManyRequests)
        {
            throw ResolutionErrors.UpstreamUnavailable(page.RetryAfterSeconds);
        }

        if (page.StatusCode >= (int)HttpStatusCode.BadRequest)
        {
            throw ResolutionErrors.AnonymousUnavailable();
        }

        try
        {
            using var document = JsonDocument.Parse(page.Body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("code", out var value) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out var code))
            {
                throw ResolutionErrors.UpstreamChanged();
            }

            if (code == NotFound && contentLookup)
            {
                throw new ResolutionException(
                    ErrorCodes.ContentUnavailable, "作品已删除或不可访问。", (int)HttpStatusCode.NotFound);
            }

            if (code == RateLimited)
            {
                throw ResolutionErrors.UpstreamUnavailable();
            }

            if (code != 0 && !(publicKeys && code == NotLoggedIn))
            {
                if (code == NotLoggedIn || code == AccessDenied || code == RequestRejected || code == RiskRejected)
                {
                    throw ResolutionErrors.AnonymousUnavailable();
                }

                throw ResolutionErrors.UpstreamChanged();
            }

            return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                ? data.Clone()
                : throw ResolutionErrors.UpstreamChanged();
        }
        catch (JsonException)
        {
            throw ResolutionErrors.UpstreamChanged();
        }
    }

    internal static bool MayNeedSignature(MetadataPage page)
    {
        if (page.StatusCode >= (int)HttpStatusCode.InternalServerError ||
            page.StatusCode == (int)HttpStatusCode.TooManyRequests)
        {
            return false;
        }

        if (page.StatusCode is (int)HttpStatusCode.Forbidden or (int)HttpStatusCode.PreconditionFailed)
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(page.Body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) &&
                (value == AccessDenied || value == RequestRejected || value == RiskRejected);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool SignatureRejected(MetadataPage page)
    {
        try
        {
            using var document = JsonDocument.Parse(page.Body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number &&
                code.TryGetInt32(out var result) && result != 0 &&
                document.RootElement.TryGetProperty("message", out var value) &&
                value.ValueKind == JsonValueKind.String && value.GetString() is { } message &&
                (message.Contains("签名", StringComparison.Ordinal) ||
                 message.Contains("w_rid", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string? Text(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    internal static string? StringValue(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static long? PositiveNumber(JsonElement data, string name) =>
        long.TryParse(Text(data, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;

    internal static bool Flag(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True ||
         (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number != 0));
}
