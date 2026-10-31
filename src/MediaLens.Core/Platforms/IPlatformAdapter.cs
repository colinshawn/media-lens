using MediaLens.Contracts;

namespace MediaLens.Platforms;

public interface IPlatformAdapter
{
    string Id { get; }

    string DisplayName { get; }

    PlatformCapabilities Capabilities { get; }

    bool CanHandle(Uri uri);

    Task<ResolvedWork> ResolveAsync(Uri uri, CancellationToken cancellationToken);
}

public sealed record ResolvedWork(
    string Platform,
    string WorkId,
    string WorkType,
    string WebUrl,
    string? Description,
    MediaItem[] Items);

public sealed record PlatformRegistration(IPlatformAdapter Adapter, bool Enabled);
