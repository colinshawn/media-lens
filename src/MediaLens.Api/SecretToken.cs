using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace MediaLens.Api;

/// <summary>本服务的静态访问凭证；只保留摘要，不向解析服务传递密钥。</summary>
internal sealed class SecretToken
{
    internal const int MinimumLength = 32;
    internal const int MaximumLength = 256;
    internal const string Scheme = "Bearer";
    internal const string ConfigurationKey = "MediaLens:Auth:SecretToken";
    private readonly byte[] _digest;

    public SecretToken(string? value)
    {
        if (value is null || !IsValid(value.AsSpan()))
        {
            // 不包含配置原值，避免启动异常和容器日志泄露凭证。
            throw new InvalidOperationException(
                "SECRET_TOKEN (MediaLens:Auth:SecretToken) must contain 32 to 256 ASCII characters using Bearer token syntax.");
        }

        _digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }

    public bool Authenticate(StringValues authorization)
    {
        if (authorization.Count != 1 || authorization[0] is not { } header)
        {
            return false;
        }

        var value = header.AsSpan();
        var separator = value.IndexOf(' ');
        if (separator < 0 || !value[..separator].Equals(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Bearer 允许一个或多个分隔空格；不裁剪 token 本体的尾部或内容。
        var token = value[(separator + 1)..];
        while (!token.IsEmpty && token[0] == ' ')
        {
            token = token[1..];
        }

        if (!IsValid(token))
        {
            return false;
        }

        // 先限制长度与字符，再编码和散列；摘要长度固定，避免内容比较提前退出。
        Span<byte> bytes = stackalloc byte[MaximumLength];
        var length = Encoding.UTF8.GetBytes(token, bytes);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes[..length], digest);
        return CryptographicOperations.FixedTimeEquals(_digest, digest);
    }

    private static bool IsValid(ReadOnlySpan<char> token)
    {
        if (token.Length is < MinimumLength or > MaximumLength || !IsTokenCharacter(token[0]))
        {
            return false;
        }

        var padding = false;
        foreach (var character in token)
        {
            if (character == '=')
            {
                padding = true;
            }
            else if (padding || !IsTokenCharacter(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTokenCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '-' or '.' or '_' or '~' or '+' or '/';
}
