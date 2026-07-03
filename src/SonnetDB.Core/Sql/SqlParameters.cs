namespace SonnetDB.Sql;

/// <summary>
/// SQL 执行时的参数值集合（#213）：按序号（位置参数 <c>?</c>）与名称（命名参数 <c>@name</c> / <c>:name</c>）
/// 提供参数值，供 <see cref="SqlParameterBinder"/> 把带占位符的 AST 绑定成含字面量的可执行 AST。
/// </summary>
/// <remarks>
/// 名称匹配大小写不敏感（与 ADO <c>SndbParameterCollection</c> 一致）。命名参数优先按名称解析，
/// 名称缺失时回退到按出现序号解析；位置参数仅按序号解析。
/// </remarks>
public sealed class SqlParameters
{
    /// <summary>无参数的共享空实例。</summary>
    public static SqlParameters Empty { get; } = new();

    private readonly List<object?> _positional = [];
    private readonly Dictionary<string, object?> _named = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>参数个数（位置 + 命名去重后的总数，仅供诊断）。</summary>
    public int Count => _positional.Count + _named.Count;

    /// <summary>按出现顺序追加一个位置参数值（对应一个 <c>?</c>）。</summary>
    /// <param name="value">CLR 参数值（可为 null）。</param>
    /// <returns>当前实例（便于链式调用）。</returns>
    public SqlParameters AddPositional(object? value)
    {
        _positional.Add(value);
        return this;
    }

    /// <summary>设置一个命名参数值（名称去 <c>@</c>/<c>:</c> 前缀，大小写不敏感）。</summary>
    /// <param name="name">参数名（不含前缀）。</param>
    /// <param name="value">CLR 参数值（可为 null）。</param>
    /// <returns>当前实例（便于链式调用）。</returns>
    public SqlParameters AddNamed(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _named[name] = value;
        return this;
    }

    /// <summary>
    /// 解析一个占位符的实际值。命名参数先按名称找，找不到再按序号找；位置参数按序号找。
    /// </summary>
    /// <param name="ordinal">占位符按出现顺序分配的序号（从 0 起）。</param>
    /// <param name="name">命名参数名；位置参数为 null。</param>
    /// <param name="value">解析到的参数值。</param>
    /// <returns>成功解析返回 true。</returns>
    public bool TryResolve(int ordinal, string? name, out object? value)
    {
        if (name is not null && _named.TryGetValue(name, out value))
            return true;

        if (ordinal >= 0 && ordinal < _positional.Count)
        {
            value = _positional[ordinal];
            return true;
        }

        value = null;
        return false;
    }
}
