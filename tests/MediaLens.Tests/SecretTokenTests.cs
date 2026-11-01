using MediaLens.Api;
using Microsoft.Extensions.Primitives;

namespace MediaLens.Tests;

public sealed class SecretTokenTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(256)]
    public void ValidLengthsCompareOriginalCharactersWithoutDecoding(int length)
    {
        var value = new string('A', length);
        var secret = new SecretToken(value);

        Assert.True(secret.Authenticate("Bearer " + value));
        Assert.True(secret.Authenticate("bEaReR   " + value));
        Assert.False(secret.Authenticate("Bearer " + new string('a', length)));
    }

    public static IEnumerable<object?[]> InvalidValues()
    {
        yield return [null];
        yield return [""];
        yield return [new string(' ', 32)];
        yield return [new string('A', 31)];
        yield return [new string('A', 257)];
        yield return [new string('A', 31) + "é"];
        yield return [new string('A', 31) + "中"];
        yield return [new string('A', 31) + "#"];
        yield return [new string('A', 31) + "\t"];
        yield return [new string('A', 31) + "\n"];
        yield return [" " + new string('A', 32)];
        yield return [new string('A', 32) + " "];
        yield return [new string('=', 32)];
        yield return [new string('A', 30) + "=A"];
    }

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void InvalidConfigurationFailsWithoutDisclosingValue(string? value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => new SecretToken(value));
        Assert.Contains("SECRET_TOKEN", error.Message, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(value))
        {
            Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void InvalidTokenBodiesAreRejectedBeforeDigestComparison(string? value)
    {
        var secret = new SecretToken(ApiFactory.DefaultSecret);
        Assert.False(secret.Authenticate("Bearer " + value));
    }

    [Theory]
    [InlineData("Az09-._~+/Az09-._~+/Az09-._~+/Az09-._~+/")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    public void BearerAlphabetAndTrailingPaddingAreAccepted(string value)
    {
        var secret = new SecretToken(value);
        Assert.True(secret.Authenticate("Bearer " + value));
    }

    [Fact]
    public void MissingMalformedAndMultipleHeadersAreRejected()
    {
        var secret = new SecretToken(ApiFactory.DefaultSecret);
        Assert.False(secret.Authenticate(StringValues.Empty));
        Assert.False(secret.Authenticate("Bearer"));
        Assert.False(secret.Authenticate("Bearer\t" + ApiFactory.DefaultSecret));
        Assert.False(secret.Authenticate("Basic " + ApiFactory.DefaultSecret));
        Assert.False(secret.Authenticate("Bearer " + ApiFactory.DefaultSecret + ", Bearer " + ApiFactory.DefaultSecret));
        Assert.False(secret.Authenticate(new StringValues(["Bearer " + ApiFactory.DefaultSecret, "Bearer " + ApiFactory.DefaultSecret])));
    }

    [Fact]
    public void MaximumLengthTokenCannotBeExtendedAndTruncatedBackToTheSecret()
    {
        var value = new string('A', 256);
        var secret = new SecretToken(value);
        Assert.False(secret.Authenticate("Bearer " + value + "A"));
        Assert.False(secret.Authenticate("Bearer " + value + " "));
    }
}
