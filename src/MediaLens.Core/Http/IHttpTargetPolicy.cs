using System.Net.Http;

namespace MediaLens.Http;

/// <summary>将目标校验与实际连接绑定，避免校验后再次进行不同的 DNS 解析。</summary>
public interface IHttpTargetPolicy
{
    void ValidateUri(Uri uri);

    bool IsFinalMediaUri(Uri uri);

    ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken);
}
