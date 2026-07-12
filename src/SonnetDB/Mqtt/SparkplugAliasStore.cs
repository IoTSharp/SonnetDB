using System.Text;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Diagnostics;

namespace SonnetDB.Mqtt;

/// <summary>
/// 保存 BIRTH 建立的 Sparkplug 实体和 metric alias 快照，并在服务重启后恢复。
/// </summary>
internal sealed class SparkplugAliasStore
{
    private const int FormatVersion = 1;
    private const int MaxEntities = 100_000;
    private const int MaxAliasesPerEntity = 100_000;
    private const int MaxStringBytes = 4096;
    private static ReadOnlySpan<byte> Magic => "SNDBSPB1"u8;

    private readonly object _syncRoot = new();
    private readonly Dictionary<EntityKey, AliasSnapshot> _entities = new();
    private readonly string? _path;
    private readonly ILogger<SparkplugAliasStore>? _logger;

    /// <summary>创建不持久化的 alias store，供嵌入式测试使用。</summary>
    public SparkplugAliasStore()
    {
    }

    /// <summary>创建绑定服务器数据目录的持久化 alias store。</summary>
    public SparkplugAliasStore(
        IOptions<ServerOptions> options,
        ILogger<SparkplugAliasStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        string directory = Path.Combine(options.Value.DataRoot, ".system");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "sparkplug-aliases-v1.bin");
        Load();
    }

    /// <summary>当前已通过 BIRTH 注册的节点或设备数量。</summary>
    public int RegisteredEntityCount
    {
        get
        {
            lock (_syncRoot)
                return _entities.Count;
        }
    }

    /// <summary>
    /// 用一次完整 BIRTH 快照原子替换指定实体的 alias 表并持久化。
    /// NBIRTH 会同时淘汰该 edge node 上一会话遗留的设备 alias。
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

        lock (_syncRoot)
        {
            if (route.MessageType == SparkplugMessageType.NBirth)
            {
                string groupId = route.GroupId;
                string edgeNodeId = route.EdgeNodeId;
                foreach (EntityKey key in _entities.Keys
                    .Where(key => string.Equals(key.GroupId, groupId, StringComparison.Ordinal)
                        && string.Equals(key.EdgeNodeId, edgeNodeId, StringComparison.Ordinal))
                    .ToArray())
                {
                    _entities.Remove(key);
                }
            }

            _entities[EntityKey.From(route)] = new AliasSnapshot(aliasToName, nameToAlias);
            Save();
        }
    }

    /// <summary>
    /// 按当前实体的 alias 查找 metric 名称。
    /// </summary>
    public bool TryResolveAlias(in SparkplugTopicRoute route, ulong alias, out string name)
    {
        lock (_syncRoot)
        {
            if (_entities.TryGetValue(EntityKey.From(route), out AliasSnapshot? snapshot)
                && snapshot.AliasToName.TryGetValue(alias, out string? resolved))
            {
                name = resolved;
                return true;
            }
        }

        name = string.Empty;
        return false;
    }

    /// <summary>
    /// 按当前实体的 metric 名称反查 alias。
    /// </summary>
    public bool TryResolveName(in SparkplugTopicRoute route, string name, out ulong alias)
    {
        lock (_syncRoot)
        {
            if (_entities.TryGetValue(EntityKey.From(route), out AliasSnapshot? snapshot)
                && snapshot.NameToAlias.TryGetValue(name, out alias))
            {
                return true;
            }
        }

        alias = 0;
        return false;
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
            return;

        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            byte[] magic = reader.ReadBytes(Magic.Length);
            if (!magic.AsSpan().SequenceEqual(Magic))
                throw new InvalidDataException("Sparkplug alias 快照 magic 无效。");
            if (reader.ReadInt32() != FormatVersion)
                throw new InvalidDataException("Sparkplug alias 快照版本不受支持。");

            int entityCount = ReadCount(reader, MaxEntities, "entity");
            for (int i = 0; i < entityCount; i++)
            {
                string groupId = ReadString(reader);
                string edgeNodeId = ReadString(reader);
                string? deviceId = reader.ReadBoolean() ? ReadString(reader) : null;
                int aliasCount = ReadCount(reader, MaxAliasesPerEntity, "alias");
                var aliasToName = new Dictionary<ulong, string>(aliasCount);
                var nameToAlias = new Dictionary<string, ulong>(aliasCount, StringComparer.Ordinal);
                for (int j = 0; j < aliasCount; j++)
                {
                    ulong alias = reader.ReadUInt64();
                    string name = ReadString(reader);
                    aliasToName[alias] = name;
                    nameToAlias[name] = alias;
                }

                _entities[new EntityKey(groupId, edgeNodeId, deviceId)] = new(aliasToName, nameToAlias);
            }

            if (stream.Position != stream.Length)
                throw new InvalidDataException("Sparkplug alias 快照包含尾随数据。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            _entities.Clear();
            _logger?.SparkplugAliasLoadFailed(ex, _path);
        }
    }

    private void Save()
    {
        if (_path is null)
            return;

        string temporaryPath = _path + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.WriteThrough))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(_entities.Count);
                foreach (var (key, snapshot) in _entities)
                {
                    WriteString(writer, key.GroupId);
                    WriteString(writer, key.EdgeNodeId);
                    writer.Write(key.DeviceId is not null);
                    if (key.DeviceId is not null)
                        WriteString(writer, key.DeviceId);
                    writer.Write(snapshot.AliasToName.Count);
                    foreach (var (alias, name) in snapshot.AliasToName)
                    {
                        writer.Write(alias);
                        WriteString(writer, name);
                    }
                }
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (IOException ex)
        {
            _logger?.SparkplugAliasSaveFailed(ex, _path);
            try { File.Delete(temporaryPath); } catch (IOException) { }
        }
    }

    private static int ReadCount(BinaryReader reader, int maximum, string scope)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum)
            throw new InvalidDataException($"Sparkplug alias 快照 {scope} 数量无效。");
        return count;
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaxStringBytes)
            throw new InvalidDataException("Sparkplug alias 快照字符串长度无效。");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException("Sparkplug alias 快照字符串被截断。");
        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxStringBytes)
            throw new InvalidDataException("Sparkplug alias 快照字符串超过长度限制。");
        writer.Write(bytes.Length);
        writer.Write(bytes);
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
