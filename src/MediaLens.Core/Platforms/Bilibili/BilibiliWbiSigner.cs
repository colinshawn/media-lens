using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaLens.Http;
using MediaLens.Resolution;

namespace MediaLens.Platforms.Bilibili;

/// <summary>公开键上的确定性计算，不包含账号、访客标识或随机指纹。</summary>
internal static class BilibiliWbiSigner
{
    private const int PublicKeyLength = 32;
    private const int MixinLength = 32;
    private const string RemovedCharacters = "!'()*";
    private static readonly int[] MixinOrder =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
        37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4,
        22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52
    ];

    internal static string ReadMixin(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("wbi_img", out var images) || images.ValueKind != JsonValueKind.Object)
        {
            throw ResolutionErrors.UpstreamChanged();
        }

        var raw = ReadKey(images, "img_url") + ReadKey(images, "sub_url");
        return new string(MixinOrder.Take(MixinLength).Select(index => raw[index]).ToArray());
    }

    internal static string Sign(IReadOnlyDictionary<string, string> parameters, string mixin, long unixSeconds)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            values.Add(key, value);
        }

        values["wts"] = unixSeconds.ToString(CultureInfo.InvariantCulture);
        values.Remove("w_rid");
        var query = string.Join('&', values.Select(pair =>
            Uri.EscapeDataString(pair.Key) + '=' + Uri.EscapeDataString(
                new string(pair.Value.Where(character => !RemovedCharacters.Contains(character)).ToArray()))));
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(query + mixin));
        // 返回的字符串原样用于请求，不能再次经过表单编码或重新排列。
        return query + "&w_rid=" + Convert.ToHexStringLower(hash);
    }

    private static string ReadKey(JsonElement images, string name)
    {
        if (!images.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri))
        {
            throw ResolutionErrors.UpstreamChanged();
        }

        var key = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        return key.Length == PublicKeyLength && key.All(Uri.IsHexDigit)
            ? key
            : throw ResolutionErrors.UpstreamChanged();
    }
}

/// <summary>缓存的仅是全站公开参数；不读取或保存 nav 返回的身份和 Cookie。</summary>
internal sealed class BilibiliWbiKeys(IUpstreamClient client, TimeProvider timeProvider) : IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
    private static readonly Uri NavUri = new("https://api.bilibili.com/x/web-interface/nav");
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private string? _mixin;
    private DateTimeOffset _expiresAt;

    internal async Task<string> GetAsync(CancellationToken cancellationToken, bool refresh = false)
    {
        // 键和到期时间在同一个异步门内读写，避免刷新时观察到不匹配的一对值。
        await _refresh.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _mixin is not null && timeProvider.GetUtcNow() < _expiresAt)
            {
                return _mixin;
            }

            var page = await client.GetAsync(NavUri, cancellationToken);
            var data = BilibiliProtocol.ReadData(page, publicKeys: true);
            _mixin = BilibiliWbiSigner.ReadMixin(data);
            _expiresAt = timeProvider.GetUtcNow() + CacheLifetime;
            return _mixin;
        }
        finally
        {
            _refresh.Release();
        }
    }

    public void Dispose() => _refresh.Dispose();
}
