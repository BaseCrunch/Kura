using Kura.Core.Models;

namespace Kura.Core.Security;

public interface ICredentialStore
{
    ServerCredentials Get(long serverId);
    void Set(long serverId, ServerCredentials credentials);
    void Delete(long serverId);
}

/// <summary>Non-persistent store for tests.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<long, ServerCredentials> _items = [];
    public ServerCredentials Get(long serverId) { lock (_items) return _items.GetValueOrDefault(serverId) ?? ServerCredentials.Empty; }
    public void Set(long serverId, ServerCredentials credentials) { lock (_items) _items[serverId] = credentials; }
    public void Delete(long serverId) { lock (_items) _items.Remove(serverId); }
}
