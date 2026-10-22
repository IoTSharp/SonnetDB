namespace SonnetDB.Tables;

/// <summary>
/// 关系表的持久目录身份，由 schema 名与表名两个独立部分组成。
/// </summary>
public readonly struct TableObjectName : IEquatable<TableObjectName>
{
    /// <summary>未限定关系表使用的默认 schema。</summary>
    public const string DefaultSchemaName = "main";

    /// <summary>使用默认 schema 构造关系表身份。</summary>
    /// <param name="name">表名。</param>
    public TableObjectName(string name)
        : this(DefaultSchemaName, name)
    {
    }

    /// <summary>使用独立的 schema 名与表名构造关系表身份。</summary>
    /// <param name="schemaName">schema 名。</param>
    /// <param name="name">表名。</param>
    public TableObjectName(string schemaName, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        SchemaName = schemaName;
        Name = name;
    }

    /// <summary>schema 的原始保存拼写。</summary>
    public string SchemaName { get; }

    /// <summary>表的原始保存拼写。</summary>
    public string Name { get; }

    /// <summary>是否位于规范默认 schema。</summary>
    public bool IsDefaultSchema => string.Equals(SchemaName, DefaultSchemaName, StringComparison.Ordinal);

    /// <summary>逐段使用 Ordinal 比较的身份比较器。</summary>
    public static IEqualityComparer<TableObjectName> ExactComparer { get; } = new ObjectNameComparer(
        StringComparer.Ordinal,
        StringComparer.Ordinal);

    /// <summary>逐段使用 OrdinalIgnoreCase 比较的未加引号身份比较器。</summary>
    public static IEqualityComparer<TableObjectName> UnquotedComparer { get; } = new ObjectNameComparer(
        StringComparer.OrdinalIgnoreCase,
        StringComparer.OrdinalIgnoreCase);

    /// <summary>按两个 SQL 名称部分各自的引号状态创建比较器。</summary>
    /// <param name="schemaIsQuoted">schema 名是否加双引号。</param>
    /// <param name="nameIsQuoted">表名是否加双引号。</param>
    /// <returns>逐段遵循 SQL 标识符大小写合同的比较器。</returns>
    public static IEqualityComparer<TableObjectName> CreateComparer(bool schemaIsQuoted, bool nameIsQuoted)
        => new ObjectNameComparer(
            schemaIsQuoted ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase,
            nameIsQuoted ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool Equals(TableObjectName other)
        => string.Equals(SchemaName ?? string.Empty, other.SchemaName ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(Name ?? string.Empty, other.Name ?? string.Empty, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TableObjectName other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(SchemaName ?? string.Empty),
        StringComparer.Ordinal.GetHashCode(Name ?? string.Empty));

    /// <summary>返回仅供诊断显示的逐段引用名称。</summary>
    public override string ToString() => Quote(SchemaName ?? string.Empty) + "." + Quote(Name ?? string.Empty);

    /// <summary>判断两个身份是否相等。</summary>
    public static bool operator ==(TableObjectName left, TableObjectName right) => left.Equals(right);

    /// <summary>判断两个身份是否不相等。</summary>
    public static bool operator !=(TableObjectName left, TableObjectName right) => !left.Equals(right);

    /// <summary>兼容历史 string API，将未限定名称映射到 main。</summary>
    public static implicit operator TableObjectName(string name) => new(name);

    /// <summary>兼容历史诊断 API，返回未限定表名或完整限定名。</summary>
    public static implicit operator string(TableObjectName value)
        => value.IsDefaultSchema ? value.Name ?? string.Empty : value.ToString();

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed class ObjectNameComparer(
        StringComparer schemaComparer,
        StringComparer nameComparer) : IEqualityComparer<TableObjectName>
    {
        public bool Equals(TableObjectName x, TableObjectName y)
            => schemaComparer.Equals(x.SchemaName, y.SchemaName)
                && nameComparer.Equals(x.Name, y.Name);

        public int GetHashCode(TableObjectName obj)
        {
            var hash = new HashCode();
            hash.Add(obj.SchemaName, schemaComparer);
            hash.Add(obj.Name, nameComparer);
            return hash.ToHashCode();
        }
    }
}
