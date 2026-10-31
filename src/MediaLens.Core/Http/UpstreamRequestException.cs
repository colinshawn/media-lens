namespace MediaLens.Http;

/// <summary>错误信息只使用固定分类，不包含输入链接、作品 ID 或上游凭据。</summary>
public sealed class UpstreamRequestException(string outcome) : Exception(outcome)
{
    public string Outcome { get; } = outcome;
}
