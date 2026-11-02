namespace MediaLens.Http;

internal sealed record HttpTimeouts(
    TimeSpan Connect,
    TimeSpan TlsHandshake,
    TimeSpan ResponseHeaders,
    TimeSpan MetadataBody)
{
    internal static HttpTimeouts Default { get; } = new(
        Connect: TimeSpan.FromSeconds(HttpDefaults.ConnectTimeoutSeconds),
        TlsHandshake: TimeSpan.FromSeconds(HttpDefaults.TlsHandshakeTimeoutSeconds),
        ResponseHeaders: TimeSpan.FromSeconds(HttpDefaults.ResponseHeadersTimeoutSeconds),
        MetadataBody: TimeSpan.FromSeconds(HttpDefaults.MetadataBodyTimeoutSeconds));
}
