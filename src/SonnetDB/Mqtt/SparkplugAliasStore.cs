using System.Collections.Concurrent;

namespace SonnetDB.Mqtt;

/// <summary>
/// 保存 BIRTH 建立的 Sparkplug 实体和 metric alias 快照。
/// </summary>
internal sealed class SparkplugAliasStore
{
    private readonly ConcurrentDictionary<EntityKey, AliasSnapshot> _entities = new();

    /// <summary>当前已通过 BIRTH 注册的节点或设备数量。</summary>
    public int RegisteredEntityCount => _entities.Count;

    /// <summary>
    /// 用一次完整 BIRTH 快照原子替换指定实体的 alias 表。
    /// </summary>
    public void ReplaceBirthAliases(
        in SparkplugTopicRoute route,
        IEnumerable<(ulong Alias, string Name)> aliases)
    {
        var aliasToName = new Dictionary<ulong, string>();
        var nameToAlias = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var (alias, name) in aliases)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            aliasToName[alias] = name;
            nameToAlias[name] = alias;
        }

        _entities[EntityKey.From(route)] = new AliasSnapshot(aliasToName, nameToAlias);
    }

    /// <summary>
    /// 按当前实体的 alias 查找 metric 名称。
    /// </summary>
    public bool TryResolveAlias(in SparkplugTopicRoute route, ulong alias, out string name)
    {
        if (_entities.TryGetValue(EntityKey.From(route), out var snapshot)
            && snapshot.AliasToName.TryGetValue(alias, out var resolved))
        {
            name = resolved;
            return true;
        }

        name = string.Empty;
        return false;
    }

    /// <summary>
    /// 按当前实体的 metric 名称反查 alias。
    /// </summary>
    public bool TryResolveName(in SparkplugTopicRoute route, string name, out ulong alias)
    {
        if (_entities.TryGetValue(EntityKey.From(route), out var snapshot)
            && snapshot.NameToAlias.TryGetValue(name, out alias))
        {
            return true;
        }

        alias = 0;
        return false;
    }

    private readonly record struct EntityKey(string GroupId, string EdgeNodeId, string? DeviceId)
    {
        public static EntityKey From(in SparkplugTopicRoute route)
            => new(route.GroupId, route.EdgeNodeId, route.DeviceId);
    }

    private sealed record AliasSnapshot(
        IReadOnlyDictionary<ulong, string> AliasToName,
        IReadOnlyDictionary<string, ulong> NameToAlias);
}
