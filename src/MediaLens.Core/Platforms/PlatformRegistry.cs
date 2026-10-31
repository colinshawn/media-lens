namespace MediaLens.Platforms;

/// <summary>发现与分发读取同一个只读注册快照；不维护业务数据或平台健康缓存。</summary>
public sealed class PlatformRegistry
{
    private readonly Dictionary<string, PlatformRegistration> _byId;

    public PlatformRegistry(IEnumerable<PlatformRegistration> registrations)
    {
        var entries = registrations.ToArray();
        if (entries.Any(entry => string.IsNullOrWhiteSpace(entry.Adapter.Id) ||
                                 entry.Adapter.Id != entry.Adapter.Id.ToLowerInvariant() ||
                                 (entry.Enabled && !entry.Adapter.Capabilities.HasSupportedMedia)))
        {
            throw new ArgumentException("Enabled platforms must have verified media support.", nameof(registrations));
        }

        _byId = entries.ToDictionary(entry => entry.Adapter.Id, StringComparer.Ordinal);
        Registrations = Array.AsReadOnly(entries);
    }

    public IReadOnlyList<PlatformRegistration> Registrations { get; }

    public PlatformRegistration? Find(string id) => _byId.GetValueOrDefault(id);
}
