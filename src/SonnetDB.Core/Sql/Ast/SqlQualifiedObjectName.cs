using SonnetDB.Tables;

namespace SonnetDB.Sql.Ast;

/// <summary>SQL 中由 schema 与对象名组成、并保留逐段引号状态的关系对象名称。</summary>
public readonly struct SqlQualifiedObjectName : IEquatable<SqlQualifiedObjectName>
{
    /// <summary>构造未限定对象名；绑定时仅解析到默认 schema <c>main</c>。</summary>
    /// <param name="name">对象名。</param>
    /// <param name="nameIsQuoted">对象名是否使用双引号。</param>
    public SqlQualifiedObjectName(string name, bool nameIsQuoted = false)
        : this(TableObjectName.DefaultSchemaName, false, name, nameIsQuoted, hasExplicitSchema: false)
    {
    }

    /// <summary>构造二段限定对象名。</summary>
    /// <param name="schemaName">schema 名。</param>
    /// <param name="schemaIsQuoted">schema 名是否使用双引号。</param>
    /// <param name="name">对象名。</param>
    /// <param name="nameIsQuoted">对象名是否使用双引号。</param>
    public SqlQualifiedObjectName(string schemaName, bool schemaIsQuoted, string name, bool nameIsQuoted)
        : this(schemaName, schemaIsQuoted, name, nameIsQuoted, hasExplicitSchema: true)
    {
    }

    private SqlQualifiedObjectName(
        string schemaName,
        bool schemaIsQuoted,
        string name,
        bool nameIsQuoted,
        bool hasExplicitSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        SchemaName = schemaName;
        SchemaIsQuoted = schemaIsQuoted;
        Name = name;
        NameIsQuoted = nameIsQuoted;
        HasExplicitSchema = hasExplicitSchema;
    }

    /// <summary>schema 文本；未限定名称固定为 <c>main</c>。</summary>
    public string SchemaName { get; }

    /// <summary>schema 部分是否使用双引号。</summary>
    public bool SchemaIsQuoted { get; }

    /// <summary>对象名文本。</summary>
    public string Name { get; }

    /// <summary>对象名部分是否使用双引号。</summary>
    public bool NameIsQuoted { get; }

    /// <summary>SQL 是否显式提供 schema 部分。</summary>
    public bool HasExplicitSchema { get; }

    /// <summary>转换为保留输入拼写的关系表身份。</summary>
    public TableObjectName ObjectName => new(SchemaName, Name);

    /// <summary>按每段引号状态判断已保存身份是否匹配。</summary>
    /// <param name="candidate">catalog 中已保存的身份。</param>
    public bool Matches(TableObjectName candidate)
        => Compare(SchemaName, candidate.SchemaName, SchemaIsQuoted)
            && Compare(Name, candidate.Name, NameIsQuoted);

    /// <inheritdoc />
    public bool Equals(SqlQualifiedObjectName other)
        => string.Equals(SchemaName, other.SchemaName, StringComparison.Ordinal)
            && SchemaIsQuoted == other.SchemaIsQuoted
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && NameIsQuoted == other.NameIsQuoted
            && HasExplicitSchema == other.HasExplicitSchema;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SqlQualifiedObjectName other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(SchemaName),
        SchemaIsQuoted,
        StringComparer.Ordinal.GetHashCode(Name),
        NameIsQuoted,
        HasExplicitSchema);

    /// <summary>按原引号状态返回可重新解析的 SQL 名称。</summary>
    public override string ToString()
    {
        string name = FormatPart(Name, NameIsQuoted);
        return HasExplicitSchema ? FormatPart(SchemaName, SchemaIsQuoted) + "." + name : name;
    }

    /// <summary>判断两个 SQL 名称值是否相等。</summary>
    public static bool operator ==(SqlQualifiedObjectName left, SqlQualifiedObjectName right) => left.Equals(right);

    /// <summary>判断两个 SQL 名称值是否不相等。</summary>
    public static bool operator !=(SqlQualifiedObjectName left, SqlQualifiedObjectName right) => !left.Equals(right);

    private static bool Compare(string requested, string stored, bool quoted)
        => string.Equals(requested, stored, quoted ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static string FormatPart(string value, bool quoted)
        => quoted ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}
